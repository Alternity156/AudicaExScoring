using System.Collections.Generic;
using System.Linq;
using MelonLoader;
using TMPro;
using UnityEngine;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        // Keyed by context ("history" / "leaderboard" / "results") — see timingGraphObjects in
        // TimingGraph.cs for why this isn't a single static field.
        private static readonly Dictionary<string, GameObject> chainGraphObjects = new Dictionary<string, GameObject>();

        // Graph dimensions in local units — same conventions as the other graphs. Right of the aim
        // graph; position and scale tuned in UnityExplorer (2026-11-24).
        private static readonly Vector3 ChainGraphLocalPosition = new Vector3(3f, -11.5f, -0.05f);
        private static readonly Vector3 ChainGraphLocalScale = new Vector3(0.9f, 0.9f, 0.9f);
        private static readonly float ChainGraphWidth = 6.5f;
        private static readonly float ChainGraphHalfHeight = 3f;

        // Left strip of the graph that holds the 1-node chain dots (they have no "along" to draw).
        // The curves use the rest of the width, from the strip's right edge (head) to the graph's
        // right edge (tail).
        private static readonly float ChainGraphSingleStripWidth = 0.9f;

        // Points per hand curve, from head to tail. Each chain is resampled onto these.
        private const int ChainGraphPointCount = 24;

        private static readonly float[] ChainSmoothingKernel = { 0.25f, 0.5f, 0.25f };

        /// <summary>One chain's traced node aims (ChainStart excluded), in tick order.</summary>
        private class ChainGraphChain
        {
            public Target.TargetHandType hand;
            public List<float> nodeAims = new List<float>();
            public float chainAverage;
        }

        /// <summary>
        /// Splits a run's cues into chains, per hand in tick order: a ChainStart opens a chain, each
        /// Chain / ChainSustain cue after it adds its aim, and the tail (isChainTail, or any other cue
        /// of that hand) closes it. Only fully hit chains are kept: a chain with a missed node was
        /// broken (scored as a miss), and a missed node saves no aim to plot. The chain's average is
        /// the mean of its node aims, the same as GetChainAverageFromLastNodeCue.
        /// </summary>
        private static List<ChainGraphChain> CollectChainGraphChains(List<ExCue> exCues)
        {
            var chains = new List<ChainGraphChain>();

            foreach (Target.TargetHandType hand in new[] { Target.TargetHandType.Right, Target.TargetHandType.Left })
            {
                ChainGraphChain current = null;
                bool currentBroken = false;

                void Close()
                {
                    if (current != null && !currentBroken && current.nodeAims.Count > 0)
                    {
                        current.chainAverage = current.nodeAims.Sum() / current.nodeAims.Count;
                        chains.Add(current);
                    }
                    current = null;
                    currentBroken = false;
                }

                // OrderBy is stable, so cues sharing a tick keep their list order.
                foreach (ExCue cue in exCues.Where(c => c.handType == hand).OrderBy(c => c.tick))
                {
                    if (cue.behavior == Target.TargetBehavior.ChainStart)
                    {
                        Close();
                        current = new ChainGraphChain { hand = hand };
                        if (cue.miss) currentBroken = true;
                        continue;
                    }

                    bool isChainNode = cue.behavior == Target.TargetBehavior.Chain || cue.behavior == ChainSustain.Behavior;
                    if (!isChainNode)
                    {
                        Close();
                        continue;
                    }

                    if (current == null) continue; // a node with no ChainStart before it

                    if (cue.miss) currentBroken = true;
                    else current.nodeAims.Add(cue.aim);

                    if (cue.isChainTail) Close();
                }

                Close();
            }

            return chains;
        }

        /// <summary>Resamples one chain's node aims onto ChainGraphPointCount points from head (0)
        /// to tail (1), linearly between nodes. Needs at least 2 nodes.</summary>
        private static float[] ResampleChain(List<float> nodeAims)
        {
            float[] points = new float[ChainGraphPointCount];
            int last = nodeAims.Count - 1;

            for (int g = 0; g < ChainGraphPointCount; g++)
            {
                float x = g / (float)(ChainGraphPointCount - 1) * last;
                int i = Mathf.FloorToInt(x);
                if (i >= last)
                {
                    points[g] = nodeAims[last];
                    continue;
                }
                float f = x - i;
                points[g] = nodeAims[i] * (1f - f) + nodeAims[i + 1] * f;
            }

            return points;
        }

        /// <summary>The average curve for one hand's multi-node chains, each chain counting once
        /// (not once per node, which would let one very long chain decide the whole curve), then
        /// lightly smoothed. Null when the hand has no chain with 2+ nodes.</summary>
        private static float[] BuildChainCurve(List<ChainGraphChain> handChains)
        {
            List<ChainGraphChain> multi = handChains.Where(c => c.nodeAims.Count >= 2).ToList();
            if (multi.Count == 0) return null;

            float[] sum = new float[ChainGraphPointCount];
            foreach (ChainGraphChain chain in multi)
            {
                float[] points = ResampleChain(chain.nodeAims);
                for (int g = 0; g < ChainGraphPointCount; g++) sum[g] += points[g];
            }

            float[] mean = new float[ChainGraphPointCount];
            for (int g = 0; g < ChainGraphPointCount; g++) mean[g] = sum[g] / multi.Count;

            float[] smoothed = new float[ChainGraphPointCount];
            for (int g = 0; g < ChainGraphPointCount; g++)
            {
                float y = 0f;
                for (int j = -1; j <= 1; j++)
                {
                    int index = Mathf.Clamp(g + j, 0, ChainGraphPointCount - 1);
                    y += mean[index] * ChainSmoothingKernel[j + 1];
                }
                smoothed[g] = y;
            }

            return smoothed;
        }

        private class ChainGraphHandData
        {
            public float[] curve;      // null: no multi-node chain
            public int singleCount;    // 1-node chains
            public float singleAverage;
            public int chainCount;     // every kept chain, single or not
            public float chainAverage; // mean of the kept chains' averages
        }

        private static ChainGraphHandData BuildChainGraphHandData(List<ChainGraphChain> chains, Target.TargetHandType hand)
        {
            List<ChainGraphChain> handChains = chains.Where(c => c.hand == hand).ToList();
            List<ChainGraphChain> singles = handChains.Where(c => c.nodeAims.Count == 1).ToList();

            return new ChainGraphHandData
            {
                curve = BuildChainCurve(handChains),
                singleCount = singles.Count,
                singleAverage = singles.Count > 0 ? singles.Average(c => c.nodeAims[0]) : 0f,
                chainCount = handChains.Count,
                chainAverage = handChains.Count > 0 ? handChains.Average(c => c.chainAverage) : 0f
            };
        }

        /// <summary>Aim 0 at the bottom edge, 1 at the top. Fixed scale, no zoom.</summary>
        private static float ChainValueToY(float value)
        {
            return -ChainGraphHalfHeight + Mathf.Clamp01(value) * ChainGraphHalfHeight * 2f;
        }

        private static float ChainPlotLeftX => -ChainGraphWidth / 2f + ChainGraphSingleStripWidth;
        private static float ChainPlotRightX => ChainGraphWidth / 2f;

        private static void CreateChainGraphQuad(Transform parent, string name, float xFrom, float xTo, float yFrom, float yTo, Color color, int renderQueue)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.layer = parent.gameObject.layer;
            go.transform.localPosition = new Vector3((xFrom + xTo) / 2f, (yFrom + yTo) / 2f, 0f);

            MeshFilter mf = go.AddComponent<MeshFilter>();
            mf.mesh = BuildRectMesh((xTo - xFrom) / 2f, (yTo - yFrom) / 2f, color);

            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.material = new Material(Shader.Find("Sprites/Default"));
            mr.material.renderQueue = renderQueue;
        }

        private static void CreateChainLine(Transform parent, string name, Vector3[] positions, Color color, float width, int sortingOrder)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.layer = parent.gameObject.layer;

            LineRenderer lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.positionCount = positions.Length;
            for (int i = 0; i < positions.Length; i++) lr.SetPosition(i, positions[i]);
            lr.startWidth = width;
            lr.endWidth = width;
            lr.material = new Material(Shader.Find("Sprites/Default"));
            lr.startColor = color;
            lr.endColor = color;
            lr.sortingOrder = sortingOrder;
        }

        private static void CreateChainCurve(Transform parent, string name, float[] curve, Color color)
        {
            Vector3[] positions = new Vector3[curve.Length];
            for (int g = 0; g < curve.Length; g++)
            {
                float t = g / (float)(curve.Length - 1);
                float x = Mathf.Lerp(ChainPlotLeftX, ChainPlotRightX, t);
                positions[g] = new Vector3(x, ChainValueToY(curve[g]), 0f);
            }
            CreateChainLine(parent, name, positions, color, 0.07f, 3);
        }

        /// <summary>A dot for one hand's 1-node chains, at their average aim, in the left strip.
        /// Bigger with more chains. Black outline drawn behind, like the aim graph's dots.</summary>
        private static void CreateChainSingleDot(Transform parent, string name, float x, float y, int count, Color color)
        {
            float radius = Mathf.Min(0.28f, 0.1f + 0.03f * Mathf.Sqrt(count));

            GameObject outlineGo = new GameObject(name + "Outline");
            outlineGo.transform.SetParent(parent, false);
            outlineGo.layer = parent.gameObject.layer;
            outlineGo.transform.localPosition = new Vector3(x, y, 0f);
            outlineGo.AddComponent<MeshFilter>().mesh = BuildFilledCircleMesh(radius * 1.25f, Color.black, 24);
            MeshRenderer outlineMr = outlineGo.AddComponent<MeshRenderer>();
            outlineMr.material = new Material(Shader.Find("Sprites/Default"));
            outlineMr.material.renderQueue = 3100;

            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.layer = parent.gameObject.layer;
            go.transform.localPosition = new Vector3(x, y, 0f);
            go.AddComponent<MeshFilter>().mesh = BuildFilledCircleMesh(radius, color, 24);
            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.material = new Material(Shader.Find("Sprites/Default"));
            mr.material.renderQueue = 3101;
        }

        private static string FormatChainHandStats(ChainGraphHandData data)
        {
            if (data.chainCount == 0) return "No chains";

            string text = $"Avg: {data.chainAverage:0.00}";
            if (data.singleCount > 0) text += $"\nSingle: {data.singleAverage:0.00} ({data.singleCount})";
            return text;
        }

        /// <summary>
        /// Builds the chain tracing graph: for each hand, the average aim of its traced nodes from
        /// the head (left) to the tail (right), each chain resampled to ChainGraphPointCount points
        /// and counted once. 1-node chains have no "along", so each hand gets a dot for them in the
        /// strip on the left instead. Grey background on a fixed 0–1 scale with a line at 0.5 (no
        /// judgement colours, no zoom). Labels on the right: each hand's average chain score and its 1-node
        /// average and count. Returns null (nothing drawn) when the run has no fully hit chain.
        /// </summary>
        public static GameObject CreateChainGraph(string context, Transform parent, List<ExCue> exCues)
        {
            DestroyChainGraph(context);
            if (parent == null || exCues == null) return null;

            List<ChainGraphChain> chains = CollectChainGraphChains(exCues);
            if (chains.Count == 0) return null;

            ChainGraphHandData right = BuildChainGraphHandData(chains, Target.TargetHandType.Right);
            ChainGraphHandData left = BuildChainGraphHandData(chains, Target.TargetHandType.Left);

            GameObject chainGraphObject = new GameObject("ChainGraph (Clone)");
            chainGraphObject.transform.SetParent(parent, false);
            chainGraphObject.layer = parent.gameObject.layer;
            chainGraphObject.transform.localPosition = ChainGraphLocalPosition;
            chainGraphObject.transform.localScale = ChainGraphLocalScale;
            Transform t = chainGraphObject.transform;

            CreateChainGraphQuad(t, "Background", -ChainGraphWidth / 2f, ChainPlotRightX,
                -ChainGraphHalfHeight, ChainGraphHalfHeight, new Color(0.5f, 0.5f, 0.5f, 0.6f), 2900);

            // Reference line at aim 0.5, across the curve area and the 1-node strip.
            CreateChainLine(t, "HalfLine", new[]
            {
                new Vector3(-ChainGraphWidth / 2f, ChainValueToY(0.5f), 0f),
                new Vector3(ChainPlotRightX, ChainValueToY(0.5f), 0f)
            }, new Color(1f, 1f, 1f, 0.5f), 0.03f, 2);

            // Divider between the 1-node strip and the head end of the curves.
            CreateChainLine(t, "SingleStripDivider", new[]
            {
                new Vector3(ChainPlotLeftX, -ChainGraphHalfHeight, 0f),
                new Vector3(ChainPlotLeftX, ChainGraphHalfHeight, 0f)
            }, new Color(1f, 1f, 1f, 0.6f), 0.03f, 2);

            Color rightColor = ChainArrow.GetStatsHandColor(Target.TargetHandType.Right);
            Color leftColor = ChainArrow.GetStatsHandColor(Target.TargetHandType.Left);

            if (right.curve != null) CreateChainCurve(t, "RightHandCurve", right.curve, rightColor);
            if (left.curve != null) CreateChainCurve(t, "LeftHandCurve", left.curve, leftColor);

            // Side by side in the strip, left hand on the left and right hand on the right, so two
            // dots at the same value don't hide each other.
            float stripLeft = -ChainGraphWidth / 2f;
            if (right.singleCount > 0)
                CreateChainSingleDot(t, "RightHandSingleDot", stripLeft + ChainGraphSingleStripWidth * 0.68f,
                    ChainValueToY(right.singleAverage), right.singleCount, rightColor);
            if (left.singleCount > 0)
                CreateChainSingleDot(t, "LeftHandSingleDot", stripLeft + ChainGraphSingleStripWidth * 0.32f,
                    ChainValueToY(left.singleAverage), left.singleCount, leftColor);

            float axisLabelY = -ChainGraphHalfHeight - 0.4f;
            // Above the strip (-2.8, 3.4): the head/tail labels sit below the graph.
            CreateTimingLabel(t, "SingleLabel", new Vector3(stripLeft + ChainGraphSingleStripWidth / 2f, ChainGraphHalfHeight + 0.4f, 0f), Color.white)
                .text = "1";
            CreateTimingLabel(t, "HeadLabel", new Vector3(ChainPlotLeftX, axisLabelY, 0f), Color.white, TextAlignmentOptions.Left)
                .text = "Head";
            CreateTimingLabel(t, "TailLabel", new Vector3(ChainPlotRightX, axisLabelY, 0f), Color.white, TextAlignmentOptions.Right)
                .text = "Tail";

            CreateTimingLabel(t, "RightHandStats",
                new Vector3(ChainGraphWidth / 2f + 1.65f, ChainGraphHalfHeight * 0.5f, 0f), rightColor)
                .text = FormatChainHandStats(right);
            CreateTimingLabel(t, "LeftHandStats",
                new Vector3(ChainGraphWidth / 2f + 1.65f, -ChainGraphHalfHeight * 0.5f, 0f), leftColor)
                .text = FormatChainHandStats(left);

            chainGraphObjects[context] = chainGraphObject;
            return chainGraphObject;
        }

        public static void DestroyChainGraph(string context)
        {
            if (chainGraphObjects.TryGetValue(context, out GameObject existing) && existing != null)
            {
                GameObject.Destroy(existing);
            }
            chainGraphObjects.Remove(context);
        }
    }
}