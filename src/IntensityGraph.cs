using MelonLoader;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        public static GameObject intensityGraphObject;
        public static int ticksPerMeasure = 1920; // 480 ticks per beat * 4 beats

        public static float[] CalculateIntensityData(string songId, KataConfig.Difficulty difficulty)
        {
            SongCues.Cue[] cues = SongCues.GetCues(SongList.I.GetSong(songId), difficulty).ToArray();

            // Filter to relevant behaviors
            var relevant = cues.Where(c =>
                c.behavior == Target.TargetBehavior.Standard ||
                c.behavior == Target.TargetBehavior.Vertical ||
                c.behavior == Target.TargetBehavior.Horizontal ||
                c.behavior == Target.TargetBehavior.Hold ||
                c.behavior == Target.TargetBehavior.ChainStart ||
                c.behavior == Target.TargetBehavior.Melee).ToArray();

            if (relevant.Length == 0) return new float[] { 0f };

            // Find the last tick to determine number of measures
            int lastTick = 0;
            foreach (var cue in relevant)
            {
                if (cue.tick > lastTick) lastTick = cue.tick;
            }

            int measureCount = (lastTick / ticksPerMeasure) + 1;
            float[] density = new float[measureCount];

            // Count cues per measure
            foreach (var cue in relevant)
            {
                int measure = cue.tick / ticksPerMeasure;
                if (measure < measureCount)
                    density[measure]++;
            }

            // Normalize to 0-1
            float max = 0f;
            foreach (float d in density)
            {
                if (d > max) max = d;
            }

            if (max > 0f)
            {
                for (int i = 0; i < density.Length; i++)
                {
                    density[i] /= max;
                }
            }

            return density;
        }

        public static void CreateIntensityGraph(Transform parent, string songId, KataConfig.Difficulty difficulty)
        {
            MelonLogger.Log($"CreateIntensityGraph called, songId: {songId}");

            // Destroy existing graph if any
            DestroyIntensityGraph();

            float[] density = CalculateIntensityData(songId, difficulty);
            if (density.Length < 2) return;

            intensityGraphObject = new GameObject("IntensityGraph");
            intensityGraphObject.transform.SetParent(parent, false);
            intensityGraphObject.layer = parent.gameObject.layer;

            // Position below the title/artist, above the difficulty buttons
            intensityGraphObject.transform.localPosition = new Vector3(0f, -3.4f, -0.05f);
            intensityGraphObject.transform.localScale = new Vector3(1f, 1f, 1f);

            // Graph dimensions in local units
            float graphWidth = 14f;
            float graphHeight = 3f;

            int pointCount = density.Length;

            // Build mesh: 2 vertices per data point (top and bottom)
            Vector3[] vertices = new Vector3[pointCount * 2];
            Color[] colors = new Color[pointCount * 2];
            int[] triangles = new int[(pointCount - 1) * 6];

            Color topColor = new Color(1f, 1f, 1f, 0.85f);
            Color bottomColor = new Color(1f, 1f, 1f, 0f);

            for (int i = 0; i < pointCount; i++)
            {
                float x = (i / (float)(pointCount - 1)) * graphWidth - (graphWidth / 2f);
                float y = density[i] * graphHeight;

                // Bottom vertex
                vertices[i * 2] = new Vector3(x, 0f, 0f);
                colors[i * 2] = bottomColor;

                // Top vertex
                vertices[i * 2 + 1] = new Vector3(x, y, 0f);
                colors[i * 2 + 1] = Color.Lerp(bottomColor, topColor, density[i]);
            }

            // Build triangles (two per quad between adjacent data points)
            for (int i = 0; i < pointCount - 1; i++)
            {
                int bl = i * 2;       // bottom-left
                int tl = i * 2 + 1;   // top-left
                int br = (i + 1) * 2;  // bottom-right
                int tr = (i + 1) * 2 + 1; // top-right

                int ti = i * 6;
                // Triangle 1
                triangles[ti] = bl;
                triangles[ti + 1] = tl;
                triangles[ti + 2] = tr;
                // Triangle 2
                triangles[ti + 3] = bl;
                triangles[ti + 4] = tr;
                triangles[ti + 5] = br;
            }

            Mesh mesh = new Mesh();
            mesh.vertices = vertices;
            mesh.colors = colors;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();

            MeshFilter meshFilter = intensityGraphObject.AddComponent<MeshFilter>();
            meshFilter.mesh = mesh;

            MeshRenderer meshRenderer = intensityGraphObject.AddComponent<MeshRenderer>();
            meshRenderer.material = new Material(Shader.Find("Sprites/Default"));
            meshRenderer.material.renderQueue = 3100;
        }

        public static void DestroyIntensityGraph()
        {
            if (intensityGraphObject != null)
            {
                GameObject.Destroy(intensityGraphObject);
                intensityGraphObject = null;
            }
        }

        public static void RefreshIntensityGraph()
        {
            if (selectedSong == null) return;

            MelonLogger.Log("RefreshIntensityGraph called");
            MelonLogger.Log($"selectedSong: {selectedSong}");

            GameObject launchPanelCenter = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center");
            if (launchPanelCenter == null) return;

            CreateIntensityGraph(
                launchPanelCenter.transform,
                selectedSong,
                KataConfig.I.GetDifficulty()
            );

            RefreshHeatmap();
        }
    }
}