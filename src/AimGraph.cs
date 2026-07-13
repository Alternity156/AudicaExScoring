using System.Collections.Generic;
using MelonLoader;
using UnityEngine;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        public static GameObject aimGraphObject;

        // Display radius (local units) that the worst-achieved judgement's aim radius maps to —
        // same zoom concept as TimingGraph's GetWorstTimingJudgementRangeMs: a clean run (best miss
        // being only e.g. Fantastic) zooms in tighter instead of always reserving space for the full
        // Good-tier radius, while a run with bad misses zooms back out to fit them.
        private static readonly Vector3 AimGraphLocalPosition = new Vector3(-4.5f, -10.5f, -0.05f);
        private static readonly Vector3 AimGraphLocalScale = new Vector3(2.5f, 2.5f, 2.5f);
        private static readonly float AimGraphDisplayRadius = 1.5f;

        private static float GetJudgementAimRadius(Judgement judgement)
        {
            switch (judgement)
            {
                case Judgement.Impeccable: return judgementImpeccableAimRadius;
                case Judgement.Fantastic: return judgementFantasticAimRadius;
                case Judgement.Excellent: return judgementExcellentAimRadius;
                case Judgement.Great: return judgementGreatAimRadius;
                default: return judgementGoodAimRadius; // Good (and Miss, which shouldn't reach here)
            }
        }

        /// <summary>
        /// The aim radius to use as "full scale" for a run — the radius of the worst non-miss aim
        /// judgement actually achieved (both hands combined), so a run with tight aim zooms in
        /// instead of always showing the full Good-tier radius. Any miss present forces the full
        /// un-zoomed view instead, since a miss is conceptually worse than every judgement tier —
        /// it isn't measured directly (misses are plotted direction-only, not to scale), but its
        /// mere presence should stop the graph from zooming in as if the run were clean.
        /// </summary>
        private static bool HasAnyAimMiss(List<ExCue> exCues)
        {
            foreach (var cue in exCues)
            {
                if (aimBehaviors.Contains(cue.behavior) && cue.miss) return true;
            }
            return false;
        }

        private static float GetWorstAimJudgementRangeRadius(List<ExCue> exCues)
        {
            float worstDistance = 0f;
            bool hasAnyHit = false;
            bool hasAnyMiss = false;
            foreach (var cue in exCues)
            {
                if (!aimBehaviors.Contains(cue.behavior)) continue;

                if (cue.miss)
                {
                    hasAnyMiss = true;
                    continue;
                }

                hasAnyHit = true;
                float distance = (cue.intersectionPoint - cue.contactPos).magnitude;
                if (distance > worstDistance) worstDistance = distance;
            }

            // A miss is conceptually worse than any judgement tier (including Good) — its presence
            // should force the full un-zoomed view, same as if the worst achieved was Good itself.
            if (hasAnyMiss) return judgementGoodAimRadius;
            if (!hasAnyHit) return judgementGoodAimRadius; // nothing to measure — don't falsely zoom to Impeccable

            Judgement worstJudgement = GetAimJudgementFromDistance(worstDistance);
            if (worstJudgement == Judgement.Miss) worstJudgement = Judgement.Good; // safety clamp
            return GetJudgementAimRadius(worstJudgement);
        }

        private static Mesh BuildFilledCircleMesh(float radius, Color color, int segments = 16)
        {
            Vector3[] vertices = new Vector3[segments + 1];
            Color[] colors = new Color[segments + 1];
            int[] triangles = new int[segments * 3];

            vertices[0] = Vector3.zero;
            colors[0] = color;

            for (int i = 0; i < segments; i++)
            {
                float angle = (i / (float)segments) * (float)System.Math.PI * 2f;
                vertices[i + 1] = new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius, 0f);
                colors[i + 1] = color;
            }

            for (int i = 0; i < segments; i++)
            {
                int next = (i + 1) % segments;
                triangles[i * 3] = 0;
                triangles[i * 3 + 1] = i + 1;
                triangles[i * 3 + 2] = next + 1;
            }

            Mesh mesh = new Mesh();
            mesh.vertices = vertices;
            mesh.colors = colors;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            return mesh;
        }

        private static Mesh BuildAnnulusMesh(float innerRadius, float outerRadius, Color color, int segments = 48)
        {
            Vector3[] vertices = new Vector3[segments * 2];
            Color[] colors = new Color[segments * 2];
            int[] triangles = new int[segments * 6];

            for (int i = 0; i < segments; i++)
            {
                float angle = (i / (float)segments) * (float)System.Math.PI * 2f;
                float cos = Mathf.Cos(angle);
                float sin = Mathf.Sin(angle);
                vertices[i * 2] = new Vector3(cos * innerRadius, sin * innerRadius, 0f);
                vertices[i * 2 + 1] = new Vector3(cos * outerRadius, sin * outerRadius, 0f);
                colors[i * 2] = color;
                colors[i * 2 + 1] = color;
            }

            for (int i = 0; i < segments; i++)
            {
                int next = (i + 1) % segments;
                int a = i * 2;       // inner current
                int b = i * 2 + 1;   // outer current
                int c = next * 2;    // inner next
                int d = next * 2 + 1; // outer next

                triangles[i * 6] = a;
                triangles[i * 6 + 1] = b;
                triangles[i * 6 + 2] = d;
                triangles[i * 6 + 3] = a;
                triangles[i * 6 + 4] = d;
                triangles[i * 6 + 5] = c;
            }

            Mesh mesh = new Mesh();
            mesh.vertices = vertices;
            mesh.colors = colors;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            return mesh;
        }
        private static Mesh BuildDiamondMesh(float radius, Color color)
        {
            Vector3[] vertices = new Vector3[5];
            Color[] colors = new Color[5];

            vertices[0] = Vector3.zero; colors[0] = color;
            vertices[1] = new Vector3(0f, radius, 0f); colors[1] = color;   // top
            vertices[2] = new Vector3(radius, 0f, 0f); colors[2] = color;   // right
            vertices[3] = new Vector3(0f, -radius, 0f); colors[3] = color;  // bottom
            vertices[4] = new Vector3(-radius, 0f, 0f); colors[4] = color;  // left

            int[] triangles = { 0, 1, 2, 0, 2, 3, 0, 3, 4, 0, 4, 1 };

            Mesh mesh = new Mesh();
            mesh.vertices = vertices;
            mesh.colors = colors;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            return mesh;
        }

        private static Mesh BuildRectMesh(float halfWidth, float halfHeight, Color color)
        {
            Vector3[] vertices = new Vector3[4]
            {
                new Vector3(-halfWidth, -halfHeight, 0f),
                new Vector3(-halfWidth, halfHeight, 0f),
                new Vector3(halfWidth, halfHeight, 0f),
                new Vector3(halfWidth, -halfHeight, 0f),
            };
            Color[] colors = { color, color, color, color };
            int[] triangles = { 0, 1, 2, 0, 2, 3 };

            Mesh mesh = new Mesh();
            mesh.vertices = vertices;
            mesh.colors = colors;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            return mesh;
        }

        /// <summary>Dot shape indicates behavior: Standard = circle, Hold/ChainStart = diamond,
        /// Horizontal = thick horizontal line, Vertical = thick vertical line.</summary>
        private static Mesh BuildDotMesh(Target.TargetBehavior behavior, Color color, float sizeMultiplier = 1f)
        {
            const float dotRadius = 0.04f;
            switch (behavior)
            {
                case Target.TargetBehavior.Hold:
                case Target.TargetBehavior.ChainStart:
                    return BuildDiamondMesh(dotRadius * 1.3f * sizeMultiplier, color);
                case Target.TargetBehavior.Horizontal:
                    return BuildRectMesh(dotRadius * 1.6f * sizeMultiplier, dotRadius * 0.5f * sizeMultiplier, color);
                case Target.TargetBehavior.Vertical:
                    return BuildRectMesh(dotRadius * 0.5f * sizeMultiplier, dotRadius * 1.6f * sizeMultiplier, color);
                default: // Standard
                    return BuildFilledCircleMesh(dotRadius * sizeMultiplier, color);
            }
        }

        private static void CreateAimDot(Transform parent, Vector3 localPos, Color color, Target.TargetBehavior behavior)
        {
            // Black outline: same shape, slightly larger, drawn first/behind.
            GameObject outlineGo = new GameObject("AimDotOutline");
            outlineGo.transform.SetParent(parent, false);
            outlineGo.layer = parent.gameObject.layer;
            outlineGo.transform.localPosition = localPos;

            MeshFilter outlineMf = outlineGo.AddComponent<MeshFilter>();
            outlineMf.mesh = BuildDotMesh(behavior, Color.black, 1.35f);

            MeshRenderer outlineMr = outlineGo.AddComponent<MeshRenderer>();
            outlineMr.material = new Material(Shader.Find("Sprites/Default"));
            outlineMr.material.renderQueue = 3100; // above bands, below the fill

            // Colored fill on top.
            GameObject go = new GameObject("AimDot");
            go.transform.SetParent(parent, false);
            go.layer = parent.gameObject.layer;
            go.transform.localPosition = localPos;

            MeshFilter mf = go.AddComponent<MeshFilter>();
            mf.mesh = BuildDotMesh(behavior, color);

            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.material = new Material(Shader.Find("Sprites/Default"));
            mr.material.renderQueue = 3101; // draw above the radius rings
        }

        private static void CreateAimBackground(Transform parent, float radius)
        {
            GameObject go = new GameObject("Background");
            go.transform.SetParent(parent, false);
            go.layer = parent.gameObject.layer;

            MeshFilter mf = go.AddComponent<MeshFilter>();
            mf.mesh = BuildFilledCircleMesh(radius, new Color(0.5f, 0.5f, 0.5f, 0.6f), 48);

            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.material = new Material(Shader.Find("Sprites/Default"));
            mr.material.renderQueue = 2900; // behind the radius rings and dots
        }

        private static void CreateAimJudgementBand(Transform parent, string name, float innerRadius, float outerRadius, Color color, float alpha = 0.32f)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.layer = parent.gameObject.layer;

            Color bandColor = new Color(color.r, color.g, color.b, alpha);

            MeshFilter mf = go.AddComponent<MeshFilter>();
            mf.mesh = innerRadius <= 0f
                ? BuildFilledCircleMesh(outerRadius, bandColor, 48)
                : BuildAnnulusMesh(innerRadius, outerRadius, bandColor, 48);

            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.material = new Material(Shader.Find("Sprites/Default"));
            mr.material.renderQueue = 2950; // above the grey background, below the dots
        }

        private static void CreateAimCircleOutline(Transform parent, string name, float radius, Color color, int segments = 48)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.layer = parent.gameObject.layer;

            LineRenderer lr = go.AddComponent<LineRenderer>();
            lr.useWorldSpace = false;
            lr.loop = true;
            lr.positionCount = segments;
            for (int i = 0; i < segments; i++)
            {
                float angle = (i / (float)segments) * (float)System.Math.PI * 2f;
                lr.SetPosition(i, new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius, 0f));
            }
            lr.startWidth = 0.03f;
            lr.endWidth = 0.03f;
            lr.material = new Material(Shader.Find("Sprites/Default"));
            lr.startColor = color;
            lr.endColor = color;
            lr.sortingOrder = 1;
        }

        /// <summary>
        /// Builds the aim graph: the full target radius, each judgement's aim radius as concentric
        /// rings (colored via GetJudgementColor, matching the timing graph's threshold lines), and
        /// one dot per non-miss aim-behavior cue at its recorded aim deviation, colored by hand.
        /// Misses that still had a recorded aim attempt (ExCue.hasMissAimData) are also plotted, out
        /// in the grey area beyond the Good ring, showing direction only (not to-scale distance,
        /// since miss distances vary too wildly to be meaningful on this scale). Misses with no
        /// recorded attempt at all (target simply passed by unaimed) are skipped entirely.
        ///
        /// Dot direction is projected onto the target's own local right/up axes (ExCue.contactRotation,
        /// saved alongside contactPos) so it reflects the true up/down/left/right deviation regardless
        /// of how that target was oriented. Runs saved before contactRotation existed default to
        /// identity rotation here, which reproduces the previous raw-world-XY approximation rather
        /// than failing outright.
        /// </summary>
        public static void CreateAimGraph(Transform parent, List<ExCue> exCues)
        {
            DestroyAimGraph();
            if (parent == null || exCues == null) return;

            aimGraphObject = new GameObject("AimGraph (Clone)");
            aimGraphObject.transform.SetParent(parent, false);
            aimGraphObject.layer = parent.gameObject.layer;
            aimGraphObject.transform.localPosition = AimGraphLocalPosition;
            aimGraphObject.transform.localScale = AimGraphLocalScale;

            float worstRadius = GetWorstAimJudgementRangeRadius(exCues);
            float scale = AimGraphDisplayRadius / worstRadius;

            // Only reserve the extra margin beyond the outer ring when there's actually a miss that
            // needs that space to be plotted in — a clean run has nothing living out there, so the
            // background should sit flush with the outermost ring instead of leaving a visible gap.
            float backgroundMargin = HasAnyAimMiss(exCues) ? 1.1f : 1f;
            CreateAimBackground(aimGraphObject.transform, worstRadius * scale * backgroundMargin);

            // Filled translucent bands + outline rings per judgement zone, but only up through
            // whichever tier was actually the worst achieved this run — a tighter run zooms in and
            // simply doesn't draw the wider bands at all, rather than leaving them poking out past
            // the display background.
            (Judgement judgement, float inner, float outer)[] bandDefs =
            {
                (Judgement.Impeccable, 0f, judgementImpeccableAimRadius),
                (Judgement.Fantastic, judgementImpeccableAimRadius, judgementFantasticAimRadius),
                (Judgement.Excellent, judgementFantasticAimRadius, judgementExcellentAimRadius),
                (Judgement.Great, judgementExcellentAimRadius, judgementGreatAimRadius),
                (Judgement.Good, judgementGreatAimRadius, judgementGoodAimRadius),
            };

            foreach (var band in bandDefs)
            {
                if (band.outer > worstRadius + 0.0001f) continue; // wider than this run needs — skip
                Color color = GetJudgementColor(band.judgement);
                CreateAimJudgementBand(aimGraphObject.transform, band.judgement + "Band", band.inner * scale, band.outer * scale, color);
                CreateAimCircleOutline(aimGraphObject.transform, band.judgement + "Radius", band.outer * scale, color);
            }

            foreach (var cue in exCues)
            {
                if (!aimBehaviors.Contains(cue.behavior)) continue;
                if (cue.miss && !cue.hasMissAimData) continue; // nothing to plot — no attempt was recorded

                Vector3 diff = cue.intersectionPoint - cue.contactPos;

                // Project onto the target's own local right/up axes at the moment of the hit,
                // rather than raw world-space X/Y — gives the true up/down/left/right deviation
                // regardless of how that particular target was oriented. Falls back to world axes
                // (identity rotation) for older saved runs that predate contactRotation.
                // X is negated to match native's own convention, confirmed via matched tick+hand
                // logging against GameplayStats.ReportTargetHit (see GetTargetHitPos for the same fix).
                Vector3 right = cue.contactRotation * Vector3.right;
                Vector3 up = cue.contactRotation * Vector3.up;
                float localX = -Vector3.Dot(diff, right);
                float localY = Vector3.Dot(diff, up);

                Vector3 dotLocalPos;
                if (cue.miss)
                {
                    // Misses can be wildly off (or even suspiciously close) — raw distance isn't
                    // meaningful to plot to scale. Show direction only, at a fixed radius in the
                    // grey area outside the Good judgement ring.
                    Vector2 direction = new Vector2(localX, localY);
                    if (direction.sqrMagnitude < 0.0001f) direction = Vector2.up; // degenerate case fallback
                    direction.Normalize();
                    float missDisplayRadius = worstRadius * scale * 1.05f;
                    dotLocalPos = new Vector3(direction.x * missDisplayRadius, direction.y * missDisplayRadius, 0f);
                }
                else
                {
                    dotLocalPos = new Vector3(localX * scale, localY * scale, 0f);
                }

                Color dotColor = ChainArrow.GetHandColor(cue.handType);
                if (cue.miss)
                {
                    dotColor = new Color(dotColor.r * 0.45f, dotColor.g * 0.45f, dotColor.b * 0.45f, dotColor.a);
                }
                CreateAimDot(aimGraphObject.transform, dotLocalPos, dotColor, cue.behavior);
            }
        }

        public static void DestroyAimGraph()
        {
            if (aimGraphObject != null)
            {
                GameObject.Destroy(aimGraphObject);
                aimGraphObject = null;
            }
        }
    }
}