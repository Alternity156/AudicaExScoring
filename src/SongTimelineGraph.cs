using System.Collections.Generic;
using System.Linq;
using MelonLoader;
using TMPro;
using UnityEngine;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        public static GameObject songTimelineGraphObject;

        // Graph dimensions in local units — same conventions as the other graphs.
        private static readonly Vector3 SongTimelineLocalPosition = new Vector3(-1.5f, -20.5f, -0.05f);
        private static readonly Vector3 SongTimelineLocalScale = new Vector3(1f, 1f, 1f);
        private static readonly float SongTimelineWidth = 14f;
        private static readonly float SongTimelineHalfHeight = 1.5f;

        // Confirmed from real saved data: cue.health is 0-1 normalized (1.0 = full health), not 0-100.
        private const float SongTimelineMaxHealth = 1f;

        // Small strip above the main graph for Melee cues, which have no timing offset to plot on
        // the Y-axis at all — they just sit in a row at their tick position instead. Two rows since
        // simultaneous two-handed melees share the same tick and would otherwise overlap; the bottom
        // row is used first, falling back to the top row only for a simultaneous second melee.
        private static readonly float SongTimelineMeleeStripGap = 0.15f;
        private static readonly float SongTimelineMeleeStripHeight = 0.5f;
        private static float SongTimelineMeleeStripBottomRowY => SongTimelineHalfHeight + SongTimelineMeleeStripGap + SongTimelineMeleeStripHeight * 0.25f;
        private static float SongTimelineMeleeStripTopRowY => SongTimelineHalfHeight + SongTimelineMeleeStripGap + SongTimelineMeleeStripHeight * 0.75f;

        /// <summary>Maps a cue's tick into local X across the full song's tick span (min/max tick
        /// across every saved cue, since Dodge cues — the only ones excluded from saved data — are
        /// a reasonable approximation of the full playable span).</summary>
        private static float TickToX(float tick, float minTick, float maxTick)
        {
            float t = maxTick > minTick ? Mathf.InverseLerp(minTick, maxTick, tick) : 0.5f;
            return t * SongTimelineWidth - SongTimelineWidth / 2f;
        }

        private static void CreateHealthLine(Transform parent, List<ExCue> exCues, float minTick, float maxTick)
        {
            var ordered = exCues.OrderBy(c => c.tick).ToList();
            if (ordered.Count == 0) return;

            GameObject go = new GameObject("HealthLine");
            go.transform.SetParent(parent, false);
            go.layer = parent.gameObject.layer;

            LineRenderer lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.positionCount = ordered.Count;
            for (int i = 0; i < ordered.Count; i++)
            {
                float x = TickToX(ordered[i].tick, minTick, maxTick);
                float healthNorm = Mathf.Clamp01(ordered[i].health / SongTimelineMaxHealth);
                float y = Mathf.Lerp(-SongTimelineHalfHeight, SongTimelineHalfHeight, healthNorm);
                lr.SetPosition(i, new Vector3(x, y, 0f));
            }
            lr.startWidth = 0.04f;
            lr.endWidth = 0.04f;
            lr.material = new Material(Shader.Find("Sprites/Default"));
            lr.startColor = Color.white;
            lr.endColor = Color.white;
            lr.sortingOrder = 0;
        }

        // 50% bigger than AimGraph's own base dot size — these sit on a stretched horizontal
        // timeline and want a bit more visual weight to stay legible individually.
        private const float TimingDotSizeMultiplier = 1.5f;

        private static void CreateTimingDot(Transform parent, Vector3 localPos, Color color, Target.TargetBehavior behavior)
        {
            // Black outline: same shape, slightly larger, drawn first/behind — same treatment as
            // AimGraph's dots (shared BuildDotMesh helper).
            GameObject outlineGo = new GameObject("TimingDotOutline");
            outlineGo.transform.SetParent(parent, false);
            outlineGo.layer = parent.gameObject.layer;
            outlineGo.transform.localPosition = localPos;

            MeshFilter outlineMf = outlineGo.AddComponent<MeshFilter>();
            outlineMf.mesh = BuildDotMesh(behavior, Color.black, TimingDotSizeMultiplier * 1.35f);

            MeshRenderer outlineMr = outlineGo.AddComponent<MeshRenderer>();
            outlineMr.material = new Material(Shader.Find("Sprites/Default"));
            outlineMr.material.renderQueue = 3100;

            // Colored fill on top.
            GameObject go = new GameObject("TimingDot");
            go.transform.SetParent(parent, false);
            go.layer = parent.gameObject.layer;
            go.transform.localPosition = localPos;

            MeshFilter mf = go.AddComponent<MeshFilter>();
            mf.mesh = BuildDotMesh(behavior, color, TimingDotSizeMultiplier);

            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.material = new Material(Shader.Find("Sprites/Default"));
            mr.material.renderQueue = 3101;
        }

        private const int SongTimelineIntensityBinCount = 60;

        /// <summary>Buckets the run's own cues into a density histogram across the same tick span
        /// used for the rest of the timeline (minTick..maxTick), using the same behavior set as the
        /// song-select IntensityGraph. Normalized 0-1 against the busiest bucket.</summary>
        private static float[] BuildSongTimelineIntensityDensity(List<ExCue> exCues, float minTick, float maxTick, int binCount)
        {
            float[] counts = new float[binCount];
            if (maxTick <= minTick) return counts;

            foreach (var cue in exCues)
            {
                bool relevant = cue.behavior == Target.TargetBehavior.Standard ||
                                cue.behavior == Target.TargetBehavior.Vertical ||
                                cue.behavior == Target.TargetBehavior.Horizontal ||
                                cue.behavior == Target.TargetBehavior.Hold ||
                                cue.behavior == Target.TargetBehavior.ChainStart ||
                                cue.behavior == Target.TargetBehavior.Melee;
                if (!relevant) continue;

                float t = Mathf.InverseLerp(minTick, maxTick, cue.tick);
                int bin = Mathf.Clamp(Mathf.FloorToInt(t * binCount), 0, binCount - 1);
                counts[bin]++;
            }

            float max = 0f;
            foreach (float c in counts) if (c > max) max = c;
            if (max > 0f)
            {
                for (int i = 0; i < binCount; i++) counts[i] /= max;
            }
            return counts;
        }

        /// <summary>Same bottom-anchored translucent mountain-range visual as the song-select
        /// IntensityGraph, but scaled to the timeline's own width/height and rendered behind
        /// everything else (judgement bands, health line, dots) as background context only.</summary>
        private static void CreateSongTimelineIntensityOverlay(Transform parent, float[] density)
        {
            int pointCount = density.Length;
            if (pointCount < 2) return;

            GameObject go = new GameObject("IntensityOverlay");
            go.transform.SetParent(parent, false);
            go.layer = parent.gameObject.layer;

            float fullHeight = SongTimelineHalfHeight * 2f;
            Color fillColor = new Color(1f, 1f, 1f, 0.32f); // solid — density is already conveyed by height, no need to also fade opacity

            Vector3[] vertices = new Vector3[pointCount * 2];
            Color[] colors = new Color[pointCount * 2];
            int[] triangles = new int[(pointCount - 1) * 6];

            for (int i = 0; i < pointCount; i++)
            {
                float x = (i / (float)(pointCount - 1)) * SongTimelineWidth - SongTimelineWidth / 2f;
                float y = -SongTimelineHalfHeight + density[i] * fullHeight;

                vertices[i * 2] = new Vector3(x, -SongTimelineHalfHeight, 0f);
                colors[i * 2] = fillColor;

                vertices[i * 2 + 1] = new Vector3(x, y, 0f);
                colors[i * 2 + 1] = fillColor;
            }

            for (int i = 0; i < pointCount - 1; i++)
            {
                int bl = i * 2;
                int tl = i * 2 + 1;
                int br = (i + 1) * 2;
                int tr = (i + 1) * 2 + 1;

                int ti = i * 6;
                triangles[ti] = bl;
                triangles[ti + 1] = tl;
                triangles[ti + 2] = tr;
                triangles[ti + 3] = bl;
                triangles[ti + 4] = tr;
                triangles[ti + 5] = br;
            }

            Mesh mesh = new Mesh();
            mesh.vertices = vertices;
            mesh.colors = colors;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();

            MeshFilter mf = go.AddComponent<MeshFilter>();
            mf.mesh = mesh;

            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.material = new Material(Shader.Find("Sprites/Default"));
            mr.material.renderQueue = 2800; // behind judgement bands (2900), health line, dots, labels
        }

        private static void CreateSongTimelineMeleeStripBackground(Transform parent)
        {
            GameObject go = new GameObject("MeleeStripBackground");
            go.transform.SetParent(parent, false);
            go.layer = parent.gameObject.layer;

            float yFrom = SongTimelineHalfHeight + SongTimelineMeleeStripGap;
            float yTo = yFrom + SongTimelineMeleeStripHeight;
            Color color = new Color(0.5f, 0.5f, 0.5f, 0.5f);

            Mesh mesh = new Mesh();
            mesh.vertices = new Vector3[]
            {
                new Vector3(-SongTimelineWidth / 2f, yFrom, 0f),
                new Vector3(-SongTimelineWidth / 2f, yTo, 0f),
                new Vector3(SongTimelineWidth / 2f, yTo, 0f),
                new Vector3(SongTimelineWidth / 2f, yFrom, 0f),
            };
            mesh.colors = new Color[] { color, color, color, color };
            mesh.triangles = new int[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateNormals();

            MeshFilter mf = go.AddComponent<MeshFilter>();
            mf.mesh = mesh;

            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.material = new Material(Shader.Find("Sprites/Default"));
            mr.material.renderQueue = 2900;
        }

        private static void CreateSongTimelineCenterLine(Transform parent)
        {
            GameObject go = new GameObject("CenterLine_0ms");
            go.transform.SetParent(parent, false);
            go.layer = parent.gameObject.layer;

            LineRenderer lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.positionCount = 2;
            lr.SetPosition(0, new Vector3(-SongTimelineWidth / 2f, 0f, 0f));
            lr.SetPosition(1, new Vector3(SongTimelineWidth / 2f, 0f, 0f));
            lr.startWidth = 0.03f;
            lr.endWidth = 0.03f;
            lr.material = new Material(Shader.Find("Sprites/Default"));
            lr.startColor = Color.white;
            lr.endColor = Color.white;
            lr.sortingOrder = 2;

            CreateTimingLabel(parent, "CenterLineLabel", new Vector3(SongTimelineWidth / 2f + 1f, 0f, 0f), Color.white)
                .text = "0ms";
        }

        /// <summary>Fills the region between each judgement's timing window boundary (mirrored +/-)
        /// with a translucent horizontal band colored the same as that judgement, up to the dynamic
        /// range — same convention as CreateTimingJudgementBands, just rotated 90 degrees.</summary>
        private static void CreateSongTimelineJudgementBands(Transform parent, float rangeMs)
        {
            float prevMs = 0f;
            foreach (var tier in TimingJudgementTiersAscending)
            {
                float curMs = GetJudgementTimingWindowMs(tier);
                if (curMs > rangeMs + 0.01f) break;

                Color color = GetJudgementColor(tier);
                Color bandColor = new Color(color.r, color.g, color.b, 0.36f);

                CreateSongTimelineBandQuad(parent, prevMs, curMs, rangeMs, bandColor);
                CreateSongTimelineBandQuad(parent, -curMs, -prevMs, rangeMs, bandColor);

                prevMs = curMs;
            }
        }

        private static void CreateSongTimelineBandQuad(Transform parent, float msFrom, float msTo, float rangeMs, Color color)
        {
            float yFrom = (msFrom / rangeMs) * SongTimelineHalfHeight;
            float yTo = (msTo / rangeMs) * SongTimelineHalfHeight;

            GameObject go = new GameObject($"JudgementBand_{msFrom:0}_{msTo:0}");
            go.transform.SetParent(parent, false);
            go.layer = parent.gameObject.layer;

            Mesh mesh = new Mesh();
            mesh.vertices = new Vector3[]
            {
                new Vector3(-SongTimelineWidth / 2f, yFrom, 0f),
                new Vector3(-SongTimelineWidth / 2f, yTo, 0f),
                new Vector3(SongTimelineWidth / 2f, yTo, 0f),
                new Vector3(SongTimelineWidth / 2f, yFrom, 0f),
            };
            mesh.colors = new Color[] { color, color, color, color };
            mesh.triangles = new int[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateNormals();

            MeshFilter mf = go.AddComponent<MeshFilter>();
            mf.mesh = mesh;

            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.material = new Material(Shader.Find("Sprites/Default"));
            mr.material.renderQueue = 2900; // behind the health line, dots, center line, and labels
        }

        /// <summary>
        /// Builds the song timeline graph: a translucent note-density mountain overlay in the
        /// background (same visual language as the song-select IntensityGraph, built from this run's
        /// own cues), a continuous health line across the whole run, plus one dot per non-miss
        /// aim-behavior shot at its actual song position (X = tick) and timing offset (Y = ms,
        /// centered on 0), colored by hand. Shares the same dynamic judgement-based Y-scale as
        /// TimingGraph, and the same horizontal judgement-threshold-line convention (rotated 90
        /// degrees from TimingGraph's vertical ones). Position/scale are placeholder defaults — tune
        /// live in UnityExplorer.
        /// </summary>
        public static void CreateSongTimelineGraph(Transform parent, List<ExCue> exCues)
        {
            DestroySongTimelineGraph();
            if (parent == null || exCues == null || exCues.Count == 0) return;

            float minTick = exCues.Min(c => c.tick);
            float maxTick = exCues.Max(c => c.tick);
            float rangeMs = GetWorstTimingJudgementRangeMs(exCues);

            songTimelineGraphObject = new GameObject("SongTimelineGraph (Clone)");
            songTimelineGraphObject.transform.SetParent(parent, false);
            songTimelineGraphObject.layer = parent.gameObject.layer;
            songTimelineGraphObject.transform.localPosition = SongTimelineLocalPosition;
            songTimelineGraphObject.transform.localScale = SongTimelineLocalScale;

            CreateSongTimelineIntensityOverlay(songTimelineGraphObject.transform,
                BuildSongTimelineIntensityDensity(exCues, minTick, maxTick, SongTimelineIntensityBinCount));

            CreateHealthLine(songTimelineGraphObject.transform, exCues, minTick, maxTick);

            CreateSongTimelineCenterLine(songTimelineGraphObject.transform);

            CreateSongTimelineJudgementBands(songTimelineGraphObject.transform, rangeMs);

            CreateSongTimelineMeleeStripBackground(songTimelineGraphObject.transform);

            foreach (var cue in exCues)
            {
                if (cue.miss) continue;
                if (!aimBehaviors.Contains(cue.behavior)) continue;

                float x = TickToX(cue.tick, minTick, maxTick);
                float y = (cue.timingMs / rangeMs) * SongTimelineHalfHeight;
                y = Mathf.Clamp(y, -SongTimelineHalfHeight, SongTimelineHalfHeight);

                Color color = ChainArrow.GetHandColor(cue.handType);
                CreateTimingDot(songTimelineGraphObject.transform, new Vector3(x, y, 0f), color, cue.behavior);
            }

            // Melee cues have no timing offset to plot at all — they just sit in a row in the small
            // grey strip above the main graph, at their tick position. Shown for both hits and
            // misses (darkened, same treatment as AimGraph's misses), since Melee isn't part of
            // aimBehaviors and would otherwise never appear on the timeline. Two-row layout: bottom
            // row first, top row only if another melee shares the exact same tick (simultaneous
            // two-handed melee).
            var meleeTickRowUsage = new Dictionary<float, int>();
            foreach (var cue in exCues)
            {
                if (cue.behavior != Target.TargetBehavior.Melee) continue;

                float x = TickToX(cue.tick, minTick, maxTick);
                Color color = ChainArrow.GetHandColor(cue.handType);
                if (cue.miss)
                {
                    color = new Color(color.r * 0.45f, color.g * 0.45f, color.b * 0.45f, color.a);
                }

                int rowIndex = meleeTickRowUsage.TryGetValue(cue.tick, out int used) ? used : 0;
                meleeTickRowUsage[cue.tick] = rowIndex + 1;
                float y = rowIndex == 0 ? SongTimelineMeleeStripBottomRowY : SongTimelineMeleeStripTopRowY;

                CreateTimingDot(songTimelineGraphObject.transform, new Vector3(x, y, 0f), color, cue.behavior);
            }
        }

        public static void DestroySongTimelineGraph()
        {
            if (songTimelineGraphObject != null)
            {
                GameObject.Destroy(songTimelineGraphObject);
                songTimelineGraphObject = null;
            }
        }
    }
}