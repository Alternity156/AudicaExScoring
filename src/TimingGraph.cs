using System.Collections.Generic;
using MelonLoader;
using TMPro;
using UnityEngine;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        public static GameObject timingGraphObject;

        private const float TimingGraphBinSizeMs = 1f;   // 1ms per bucket

        // Graph dimensions in local units — same conventions as IntensityGraph.
        private static readonly Vector3 TimingGraphLocalPosition = new Vector3(-1.5f, -16.5f, -0.05f);
        private static readonly Vector3 TimingGraphLocalScale = new Vector3(1f, 1f, 1f);
        private static readonly float TimingGraphWidth = 14f;
        private static readonly float TimingGraphHalfHeight = 1.5f;

        // Ascending order matters — GetWorstTimingJudgementRangeMs relies on it to stop early.
        private static readonly Judgement[] TimingJudgementTiersAscending =
        {
            Judgement.Impeccable, Judgement.Fantastic, Judgement.Excellent, Judgement.Great, Judgement.Good
        };

        private static float GetJudgementTimingWindowMs(Judgement judgement)
        {
            switch (judgement)
            {
                case Judgement.Impeccable: return judgementImpeccableTimingWindowMs;
                case Judgement.Fantastic: return judgementFantasticTimingWindowMs;
                case Judgement.Excellent: return judgementExcellentTimingWindowMs;
                case Judgement.Great: return judgementGreatTimingWindowMs;
                default: return judgementGoodTimingWindowMs; // Good (and Miss, which shouldn't reach here)
            }
        }

        /// <summary>
        /// The timing scale to use for a run — the window of the worst timing judgement actually
        /// achieved (both hands combined), so a run with no bad misses zooms in tighter instead of
        /// always showing the full +-100ms Good window. Shared by TimingGraph and SongTimelineGraph
        /// so both scale identically for the same run.
        /// </summary>
        private static float GetWorstTimingJudgementRangeMs(List<ExCue> exCues)
        {
            float worstAbs = 0f;
            foreach (var cue in exCues)
            {
                if (cue.miss) continue;
                if (!aimBehaviors.Contains(cue.behavior)) continue;

                float abs = Mathf.Abs(cue.timingMs);
                if (abs > worstAbs) worstAbs = abs;
            }

            return GetJudgementTimingWindowMs(GetTimingJudgement(worstAbs));
        }

        // Exact 7-tap smoothing kernel from Simply Love's Pane5/Calculations.lua (radius 3ms,
        // weights sum to 1.0) — turns the raw jagged 1ms histogram into the smooth "mountain range"
        // look their timing graph has, without changing the underlying 1ms bucket resolution.
        private static readonly float[] TimingSmoothingKernel = { 0.045f, 0.090f, 0.180f, 0.370f, 0.180f, 0.090f, 0.045f };

        private static float[] SmoothTimingHistogram(float[] counts)
        {
            int n = counts.Length;
            float[] smoothed = new float[n];
            for (int i = 0; i < n; i++)
            {
                float y = 0f;
                for (int j = -3; j <= 3; j++)
                {
                    int index = Mathf.Clamp(i + j, 0, n - 1);
                    y += counts[index] * TimingSmoothingKernel[j + 3];
                }
                smoothed[i] = y;
            }
            return smoothed;
        }
        /// <summary>Buckets non-miss aim-behavior cues' timingMs into a histogram for one hand, over
        /// the given range. Values beyond the range are clamped into the edge bins rather than dropped
        /// (shouldn't normally happen since rangeMs is sized to the worst offset actually present).</summary>
        private static float[] BuildTimingHistogram(List<ExCue> exCues, Target.TargetHandType hand, float rangeMs, int binCount)
        {
            float[] counts = new float[binCount];

            foreach (var cue in exCues)
            {
                if (cue.miss) continue;
                if (cue.handType != hand) continue;
                if (!aimBehaviors.Contains(cue.behavior)) continue; // only cues that actually have timingMs

                float clamped = Mathf.Clamp(cue.timingMs, -rangeMs, rangeMs);
                int bin = Mathf.RoundToInt((clamped + rangeMs) / TimingGraphBinSizeMs);
                bin = Mathf.Clamp(bin, 0, binCount - 1);
                counts[bin]++;
            }

            return counts;
        }

        /// <summary>Worst (largest-magnitude, sign preserved) and average signed timingMs for one
        /// hand's non-miss aim-behavior cues.</summary>
        private static void ComputeHandTimingStats(List<ExCue> exCues, Target.TargetHandType hand, out float worst, out float average)
        {
            worst = 0f;
            float worstAbs = -1f;
            float sum = 0f;
            int count = 0;

            foreach (var cue in exCues)
            {
                if (cue.miss) continue;
                if (cue.handType != hand) continue;
                if (!aimBehaviors.Contains(cue.behavior)) continue;

                float ms = cue.timingMs;
                sum += ms;
                count++;

                float abs = Mathf.Abs(ms);
                if (abs > worstAbs)
                {
                    worstAbs = abs;
                    worst = ms;
                }
            }

            average = count > 0 ? sum / count : 0f;
        }

        private static string FormatMs(float ms)
        {
            return (ms >= 0f ? "+" : "") + ms.ToString("0.0") + "ms";
        }

        /// <summary>Builds one mirrored histogram strip's mesh — solid color throughout (no
        /// baseline-to-peak fade), just able to point the peak up or down.</summary>
        private static Mesh BuildHistogramMesh(float[] counts, float maxCount, Color color, bool flipDown)
        {
            int pointCount = counts.Length;
            Vector3[] vertices = new Vector3[pointCount * 2];
            Color[] colors = new Color[pointCount * 2];
            int[] triangles = new int[(pointCount - 1) * 6];

            Color solidColor = new Color(color.r, color.g, color.b, 0.85f);

            for (int i = 0; i < pointCount; i++)
            {
                float t = i / (float)(pointCount - 1);
                float x = t * TimingGraphWidth - TimingGraphWidth / 2f;

                float normalized = maxCount > 0f ? counts[i] / maxCount : 0f;
                float y = normalized * TimingGraphHalfHeight;
                if (flipDown) y = -y;

                vertices[i * 2] = new Vector3(x, 0f, 0f);
                colors[i * 2] = solidColor;

                vertices[i * 2 + 1] = new Vector3(x, y, 0f);
                colors[i * 2 + 1] = solidColor;
            }

            for (int i = 0; i < pointCount - 1; i++)
            {
                int bl = i * 2, tl = i * 2 + 1, br = (i + 1) * 2, tr = (i + 1) * 2 + 1;
                int ti = i * 6;
                triangles[ti] = bl; triangles[ti + 1] = tl; triangles[ti + 2] = tr;
                triangles[ti + 3] = bl; triangles[ti + 4] = tr; triangles[ti + 5] = br;
            }

            Mesh mesh = new Mesh();
            mesh.vertices = vertices;
            mesh.colors = colors;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            return mesh;
        }

        private static GameObject CreateHistogramStrip(Transform parent, string name, float[] counts, float maxCount, Color color, bool flipDown)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.layer = parent.gameObject.layer;

            MeshFilter meshFilter = go.AddComponent<MeshFilter>();
            meshFilter.mesh = BuildHistogramMesh(counts, maxCount, color, flipDown);

            MeshRenderer meshRenderer = go.AddComponent<MeshRenderer>();
            meshRenderer.material = new Material(Shader.Find("Sprites/Default"));
            meshRenderer.material.renderQueue = 3100;

            return go;
        }

        private static TextMeshPro CreateTimingLabel(Transform parent, string name, Vector3 localPosition, Color color, TextAlignmentOptions alignment = TextAlignmentOptions.Center)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.layer = parent.gameObject.layer;
            go.transform.localPosition = localPosition;

            TextMeshPro tmp = go.AddComponent<TextMeshPro>();
            tmp.fontSize = 3.8f;
            tmp.alignment = alignment;
            tmp.color = color;
            return tmp;
        }

        /// <summary>
        /// Builds the timing graph (right hand spikes up, left hand spikes down, centered on 0ms)
        /// plus worst/average offset labels per hand, parented under `parent`. Destroys any previous
        /// graph first. Position/scale are placeholder defaults — tune live in UnityExplorer.
        /// </summary>
        public static void CreateTimingGraph(Transform parent, List<ExCue> exCues)
        {
            DestroyTimingGraph();
            if (parent == null || exCues == null) return;

            float rangeMs = GetWorstTimingJudgementRangeMs(exCues);
            int binCount = Mathf.RoundToInt((rangeMs * 2f) / TimingGraphBinSizeMs) + 1;

            timingGraphObject = new GameObject("TimingGraph (Clone)");
            timingGraphObject.transform.SetParent(parent, false);
            timingGraphObject.layer = parent.gameObject.layer;
            timingGraphObject.transform.localPosition = TimingGraphLocalPosition;
            timingGraphObject.transform.localScale = TimingGraphLocalScale;

            float[] rightCounts = BuildTimingHistogram(exCues, Target.TargetHandType.Right, rangeMs, binCount);
            float[] leftCounts = BuildTimingHistogram(exCues, Target.TargetHandType.Left, rangeMs, binCount);

            rightCounts = SmoothTimingHistogram(rightCounts);
            leftCounts = SmoothTimingHistogram(leftCounts);

            // Normalize each hand against its OWN smoothed peak (not the raw one, and not a shared
            // peak between hands) — with our much sparser per-run note counts, smoothing pulls a
            // peak's value down enough below the raw max that it visibly never reaches full height
            // otherwise. Normalizing independently per hand means each side's tallest point always
            // touches the top regardless of how many total notes that hand had relative to the
            // other — so the two hands' spread/shape stay comparable rather than one hand looking
            // artificially flattened just because it had fewer notes overall.
            float rightMaxCount = 0f;
            float leftMaxCount = 0f;
            for (int i = 0; i < binCount; i++)
            {
                if (rightCounts[i] > rightMaxCount) rightMaxCount = rightCounts[i];
                if (leftCounts[i] > leftMaxCount) leftMaxCount = leftCounts[i];
            }

            Color rightColor = ChainArrow.GetHandColor(Target.TargetHandType.Right);
            Color leftColor = ChainArrow.GetHandColor(Target.TargetHandType.Left);

            CreateHistogramStrip(timingGraphObject.transform, "RightHandSpikes", rightCounts, rightMaxCount, rightColor, flipDown: false);
            CreateHistogramStrip(timingGraphObject.transform, "LeftHandSpikes", leftCounts, leftMaxCount, leftColor, flipDown: true);

            // 0ms center line, with a label above it so it's unambiguous which line is the center.
            CreateTimingCenterLine(timingGraphObject.transform);

            // Vertical bands filling the region between each judgement's timing window boundary
            // (mirrored +/-), colored the same as that judgement — only up to the dynamic range,
            // since tiers larger for a run with a tighter scale would be meaningless (their true
            // position would be off-graph). Same treatment as SongTimelineGraph's bands.
            CreateTimingJudgementBands(timingGraphObject.transform, rangeMs);

            ComputeHandTimingStats(exCues, Target.TargetHandType.Right, out float rightWorst, out float rightAvg);
            ComputeHandTimingStats(exCues, Target.TargetHandType.Left, out float leftWorst, out float leftAvg);

            CreateTimingLabel(timingGraphObject.transform, "RightHandStats",
                new Vector3(TimingGraphWidth / 2f + 2f, TimingGraphHalfHeight * 0.5f, 0f), rightColor)
                .text = $"Worst: {FormatMs(rightWorst)}\nAvg: {FormatMs(rightAvg)}";

            CreateTimingLabel(timingGraphObject.transform, "LeftHandStats",
                new Vector3(TimingGraphWidth / 2f + 2f, -TimingGraphHalfHeight * 0.5f, 0f), leftColor)
                .text = $"Worst: {FormatMs(leftWorst)}\nAvg: {FormatMs(leftAvg)}";
        }

        private static void CreateTimingCenterLine(Transform parent)
        {
            GameObject go = new GameObject("CenterLine_0ms");
            go.transform.SetParent(parent, false);
            go.layer = parent.gameObject.layer;

            LineRenderer lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.positionCount = 2;
            lr.SetPosition(0, new Vector3(0f, -TimingGraphHalfHeight, 0f));
            lr.SetPosition(1, new Vector3(0f, TimingGraphHalfHeight, 0f));
            lr.startWidth = 0.04f;
            lr.endWidth = 0.04f;
            lr.material = new Material(Shader.Find("Sprites/Default"));
            lr.startColor = Color.white;
            lr.endColor = Color.white;
            lr.sortingOrder = 2; // draw above the judgement threshold lines too

            CreateTimingLabel(parent, "CenterLineLabel", new Vector3(0f, TimingGraphHalfHeight + 0.4f, 0f), Color.white)
                .text = "0ms";
        }

        /// <summary>Fills the region between each judgement's timing window boundary (mirrored +/-)
        /// with a translucent band colored the same as that judgement, up to the dynamic range.</summary>
        private static void CreateTimingJudgementBands(Transform parent, float rangeMs)
        {
            float prevMs = 0f;
            foreach (var tier in TimingJudgementTiersAscending)
            {
                float curMs = GetJudgementTimingWindowMs(tier);
                if (curMs > rangeMs + 0.01f) break;

                Color color = GetJudgementColor(tier);
                Color bandColor = new Color(color.r, color.g, color.b, 0.36f);

                CreateTimingBandQuad(parent, prevMs, curMs, rangeMs, bandColor);
                CreateTimingBandQuad(parent, -curMs, -prevMs, rangeMs, bandColor);

                prevMs = curMs;
            }
        }

        private static void CreateTimingBandQuad(Transform parent, float msFrom, float msTo, float rangeMs, Color color)
        {
            float xFrom = (msFrom / rangeMs) * (TimingGraphWidth / 2f);
            float xTo = (msTo / rangeMs) * (TimingGraphWidth / 2f);

            GameObject go = new GameObject($"JudgementBand_{msFrom:0}_{msTo:0}");
            go.transform.SetParent(parent, false);
            go.layer = parent.gameObject.layer;

            Mesh mesh = new Mesh();
            mesh.vertices = new Vector3[]
            {
                new Vector3(xFrom, -TimingGraphHalfHeight, 0f),
                new Vector3(xFrom, TimingGraphHalfHeight, 0f),
                new Vector3(xTo, TimingGraphHalfHeight, 0f),
                new Vector3(xTo, -TimingGraphHalfHeight, 0f),
            };
            mesh.colors = new Color[] { color, color, color, color };
            mesh.triangles = new int[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateNormals();

            MeshFilter mf = go.AddComponent<MeshFilter>();
            mf.mesh = mesh;

            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.material = new Material(Shader.Find("Sprites/Default"));
            mr.material.renderQueue = 2900; // behind the histogram bars, center line, and labels
        }

        public static void DestroyTimingGraph()
        {
            if (timingGraphObject != null)
            {
                GameObject.Destroy(timingGraphObject);
                timingGraphObject = null;
            }
        }
    }
}