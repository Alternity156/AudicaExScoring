using MelonLoader;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        public static GameObject heatmapObject;

        // Grid dimensions: 12 columns x 7 rows (pitch 0-83)
        public const int HeatmapGridColumns = 12;
        public const int HeatmapGridRows = 7;

        // Bounds for "on grid" detection (half-cell tolerance beyond grid edges)
        public const float HeatmapGridMinX = -0.5f;
        public const float HeatmapGridMaxX = 11.5f;
        public const float HeatmapGridMinY = -0.5f;
        public const float HeatmapGridMaxY = 6.5f;

        // Dot sizing
        public const float HeatmapDotRadius = 0.12f;        // Fixed radius in local units
        public const float HeatmapDotAlpha = 0.7f;          // Fixed alpha for all dots
        public const int HeatmapDotSegments = 8;             // Segments per circle (octagon)

        public class HeatmapData
        {
            public List<Vector2> positions;  // Exact grid coordinates per target
            public bool hasOffGridLeft;
            public bool hasOffGridRight;
            public bool hasOffGridTop;
            public bool hasOffGridBottom;
        }

        public static HeatmapData CalculateHeatmapData(string songId, KataConfig.Difficulty difficulty)
        {
            SongCues.Cue[] cues = SongCues.GetCues(SongList.I.GetSong(songId), difficulty).ToArray();

            // Filter: Standard, Vertical, Horizontal, Hold, ChainStart, Chain
            var relevant = cues.Where(c =>
                c.behavior == Target.TargetBehavior.Standard ||
                c.behavior == Target.TargetBehavior.Vertical ||
                c.behavior == Target.TargetBehavior.Horizontal ||
                c.behavior == Target.TargetBehavior.Hold ||
                c.behavior == Target.TargetBehavior.ChainStart ||
                c.behavior == Target.TargetBehavior.Chain).ToArray();

            HeatmapData data = new HeatmapData();
            data.positions = new List<Vector2>();
            data.hasOffGridLeft = false;
            data.hasOffGridRight = false;
            data.hasOffGridTop = false;
            data.hasOffGridBottom = false;

            if (relevant.Length == 0) return data;

            foreach (var cue in relevant)
            {
                int col = cue.pitch % 12;
                int row = cue.pitch / 12;

                float finalX = col + cue.gridOffset.x;
                float finalY = row + cue.gridOffset.y;

                // Off-grid detection
                if (finalX < HeatmapGridMinX) data.hasOffGridLeft = true;
                if (finalX > HeatmapGridMaxX) data.hasOffGridRight = true;
                if (finalY < HeatmapGridMinY) data.hasOffGridBottom = true;
                if (finalY > HeatmapGridMaxY) data.hasOffGridTop = true;

                // Only plot on-grid targets
                if (finalX < HeatmapGridMinX || finalX > HeatmapGridMaxX ||
                    finalY < HeatmapGridMinY || finalY > HeatmapGridMaxY)
                    continue;

                data.positions.Add(new Vector2(finalX, finalY));
            }

            return data;
        }

        public static void CreateHeatmap(Transform parent, string songId, KataConfig.Difficulty difficulty)
        {
            MelonLogger.Log($"CreateHeatmap called, songId: {songId}");

            DestroyHeatmap();

            HeatmapData data = CalculateHeatmapData(songId, difficulty);

            heatmapObject = new GameObject("Heatmap");
            heatmapObject.transform.SetParent(parent, false);
            heatmapObject.layer = parent.gameObject.layer;

            // Position above the intensity graph
            heatmapObject.transform.localPosition = new Vector3(-3.75f, 0.25f, -0.05f);
            heatmapObject.transform.localScale = new Vector3(0.4f, 0.4f, 0.4f);

            // Heatmap visual dimensions in local units
            float heatmapWidth = 14f;
            float heatmapHeight = 8.17f; // Proportional to 7/12 aspect ratio

            if (data.positions.Count > 0)
            {
                CreateDotsMesh(data, heatmapWidth, heatmapHeight);
            }

            // Create off-grid indicators
            CreateOffGridIndicators(data, heatmapWidth, heatmapHeight);
        }

        private static void CreateDotsMesh(HeatmapData data, float heatmapWidth, float heatmapHeight)
        {
            int seg = HeatmapDotSegments;
            int dotCount = data.positions.Count;

            // Each dot: 1 center vertex + seg outer vertices
            // Each dot: seg triangles
            int vertsPerDot = seg + 1;
            int trisPerDot = seg * 3;

            Vector3[] vertices = new Vector3[dotCount * vertsPerDot];
            Color[] colors = new Color[dotCount * vertsPerDot];
            int[] triangles = new int[dotCount * trisPerDot];

            Color centerColor = new Color(1f, 1f, 1f, HeatmapDotAlpha);
            Color edgeColor = new Color(1f, 1f, 1f, 0f);

            for (int p = 0; p < dotCount; p++)
            {
                Vector2 pos = data.positions[p];

                // Map grid coords to local space
                float localX = (pos.x / (HeatmapGridColumns - 1)) * heatmapWidth - (heatmapWidth / 2f);
                float localY = (pos.y / (HeatmapGridRows - 1)) * heatmapHeight;

                int vBase = p * vertsPerDot;
                int tBase = p * trisPerDot;

                // Center vertex
                vertices[vBase] = new Vector3(localX, localY, 0f);
                colors[vBase] = centerColor;

                // Outer ring vertices
                for (int s = 0; s < seg; s++)
                {
                    float angle = (s / (float)seg) * (float)System.Math.PI * 2f;
                    float ox = localX + Mathf.Cos(angle) * HeatmapDotRadius;
                    float oy = localY + Mathf.Sin(angle) * HeatmapDotRadius;

                    vertices[vBase + 1 + s] = new Vector3(ox, oy, 0f);
                    colors[vBase + 1 + s] = edgeColor;
                }

                // Triangles (fan from center)
                for (int s = 0; s < seg; s++)
                {
                    int next = (s + 1) % seg;
                    triangles[tBase + s * 3] = vBase;
                    triangles[tBase + s * 3 + 1] = vBase + 1 + s;
                    triangles[tBase + s * 3 + 2] = vBase + 1 + next;
                }
            }

            Mesh mesh = new Mesh();
            mesh.vertices = vertices;
            mesh.colors = colors;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();

            MeshFilter meshFilter = heatmapObject.AddComponent<MeshFilter>();
            meshFilter.mesh = mesh;

            MeshRenderer meshRenderer = heatmapObject.AddComponent<MeshRenderer>();
            meshRenderer.material = new Material(Shader.Find("Sprites/Default"));
            meshRenderer.material.renderQueue = 3100;
        }

        private static void CreateOffGridIndicators(HeatmapData data, float heatmapWidth, float heatmapHeight)
        {
            float arrowLength = 0.4f;
            float arrowWidth = 1.6f;
            // Half a cell in local units: width covers 11 gaps over heatmapWidth
            float halfCellX = (heatmapWidth / (HeatmapGridColumns - 1)) * 0.5f;
            float halfCellY = (heatmapHeight / (HeatmapGridRows - 1)) * 0.5f;
            float margin = 0.3f;
            Color arrowColor = new Color(1f, 1f, 1f, 0.6f);

            if (data.hasOffGridLeft)
            {
                CreateArrowIndicator("HeatmapArrowLeft", heatmapObject.transform,
                    new Vector3(-heatmapWidth / 2f - margin - halfCellX, heatmapHeight / 2f, 0f),
                    arrowLength, arrowWidth, arrowColor, Direction.Left);
            }

            if (data.hasOffGridRight)
            {
                CreateArrowIndicator("HeatmapArrowRight", heatmapObject.transform,
                    new Vector3(heatmapWidth / 2f + margin + halfCellX, heatmapHeight / 2f, 0f),
                    arrowLength, arrowWidth, arrowColor, Direction.Right);
            }

            if (data.hasOffGridTop)
            {
                CreateArrowIndicator("HeatmapArrowTop", heatmapObject.transform,
                    new Vector3(0f, heatmapHeight + margin + halfCellY, 0f),
                    arrowLength, arrowWidth, arrowColor, Direction.Up);
            }

            if (data.hasOffGridBottom)
            {
                CreateArrowIndicator("HeatmapArrowBottom", heatmapObject.transform,
                    new Vector3(0f, -margin - halfCellY, 0f),
                    arrowLength, arrowWidth, arrowColor, Direction.Down);
            }
        }

        private enum Direction { Left, Right, Up, Down }

        private static void CreateArrowIndicator(string name, Transform parent, Vector3 localPos, float length, float width, Color color, Direction dir)
        {
            GameObject arrow = new GameObject(name);
            arrow.transform.SetParent(parent, false);
            arrow.layer = parent.gameObject.layer;
            arrow.transform.localPosition = localPos;

            float halfWidth = width * 0.5f;
            Vector3[] verts = new Vector3[3];

            switch (dir)
            {
                case Direction.Left:
                    verts[0] = new Vector3(-length, 0f, 0f);
                    verts[1] = new Vector3(0f, halfWidth, 0f);
                    verts[2] = new Vector3(0f, -halfWidth, 0f);
                    break;
                case Direction.Right:
                    verts[0] = new Vector3(length, 0f, 0f);
                    verts[1] = new Vector3(0f, -halfWidth, 0f);
                    verts[2] = new Vector3(0f, halfWidth, 0f);
                    break;
                case Direction.Up:
                    verts[0] = new Vector3(0f, length, 0f);
                    verts[1] = new Vector3(-halfWidth, 0f, 0f);
                    verts[2] = new Vector3(halfWidth, 0f, 0f);
                    break;
                case Direction.Down:
                    verts[0] = new Vector3(0f, -length, 0f);
                    verts[1] = new Vector3(halfWidth, 0f, 0f);
                    verts[2] = new Vector3(-halfWidth, 0f, 0f);
                    break;
            }

            Color[] colors = new Color[] { color, color, color };
            int[] tris = new int[] { 0, 1, 2 };

            Mesh mesh = new Mesh();
            mesh.vertices = verts;
            mesh.colors = colors;
            mesh.triangles = tris;
            mesh.RecalculateNormals();

            MeshFilter mf = arrow.AddComponent<MeshFilter>();
            mf.mesh = mesh;

            MeshRenderer mr = arrow.AddComponent<MeshRenderer>();
            mr.material = new Material(Shader.Find("Sprites/Default"));
            mr.material.renderQueue = 3100;
        }

        public static void DestroyHeatmap()
        {
            if (heatmapObject != null)
            {
                GameObject.Destroy(heatmapObject);
                heatmapObject = null;
            }
        }

        public static void RefreshHeatmap()
        {
            if (selectedSong == null) return;

            MelonLogger.Log("RefreshHeatmap called");

            GameObject launchPanelCenter = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center");
            if (launchPanelCenter == null) return;

            CreateHeatmap(
                launchPanelCenter.transform,
                selectedSong,
                KataConfig.I.GetDifficulty()
            );
        }
    }
}