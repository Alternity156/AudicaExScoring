using MelonLoader;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        public static GameObject gradeVisualObject;

        // Placeholder position/scale — same convention as the other graphs (TimingGraph, AimGraph,
        // SongTimelineGraph): tune live in UnityExplorer once visible in-game. Positioned just below
        // the existing score label at (35, -0.5).
        private static readonly Vector3 GradeVisualLocalPosition = new Vector3(7f, -0.5f, 0f);
        private static readonly Vector3 GradeVisualLocalScale = new Vector3(3f, 3f, 3f);

        // Half-width/height of the square footprint stars are laid out within. Sized so a single
        // star roughly matches the text-grade label's height under the same 3,3,3 container scale
        // — still a rough estimate, tune live in UnityExplorer alongside the position/scale above.
        private const float GradeVisualFootprintHalf = 0.35f;

        // Separate (much smaller) footprint for the compact per-row visual on Play History list
        // rows — these sit directly on a UGUI row's info Text, not the world-space stats panel, so
        // they need their own scale. Placeholder — tune live in UnityExplorer.
        private const float RowGradeVisualFootprintHalf = 0.1f;

        // Row visuals live inside a UGUI Canvas hierarchy, whose local-unit-to-world-unit ratio is
        // wildly different from the world-space "menu" panels the detail visual lives on — hence
        // the much larger scale here. Tune live in UnityExplorer.
        private static readonly Vector3 RowGradeVisualLocalPosition = new Vector3(-10f, -12f, 0f);
        private static readonly Vector3 RowGradeVisualLocalScale = new Vector3(125f, 125f, 125f);

        // One grade visual per visible Play History row slot (index-keyed, same slot convention as
        // historyHitboxRuns in PlayHistoryButton.cs), since unlike the single detail panel, several
        // different grades can be on screen at once here.
        private static readonly Dictionary<int, GameObject> historyRowGradeVisuals = new Dictionary<int, GameObject>();

        // Gentle back-and-forth Z rotation for the star grades while the panel is shown, oscillating
        // between -gradeStarRotationAmplitude and +gradeStarRotationAmplitude degrees, starting at 0.
        // Both public static (not const) so they're tunable live in UnityExplorer.
        public static float gradeStarRotationAmplitude = 10f;
        public static float gradeStarRotationSpeed = 2f; // radians/sec inside the sine — ~3.14s per full cycle at 2f

        /// <summary>Classic 5-point star polygon, fan-triangulated from a center vertex. Winding
        /// doesn't matter — Sprites/Default (used below) renders both faces.</summary>
        private static Mesh BuildStarMesh(float outerRadius, Color color)
        {
            const int rimCount = 10; // 5 outer points + 5 inner points
            float innerRadius = outerRadius * 0.5f;

            Vector3[] vertices = new Vector3[rimCount + 1];
            Color[] colors = new Color[rimCount + 1];
            int[] triangles = new int[rimCount * 3];

            vertices[0] = Vector3.zero;
            colors[0] = color;

            for (int i = 0; i < rimCount; i++)
            {
                float angleRad = (90f - i * (360f / rimCount)) * ((float)System.Math.PI / 180f);
                float radius = (i % 2 == 0) ? outerRadius : innerRadius;
                vertices[i + 1] = new Vector3(Mathf.Cos(angleRad) * radius, Mathf.Sin(angleRad) * radius, 0f);
                colors[i + 1] = color;
            }

            for (int i = 0; i < rimCount; i++)
            {
                int a = i + 1;
                int b = (i + 1) % rimCount + 1;
                int ti = i * 3;
                triangles[ti] = 0;
                triangles[ti + 1] = a;
                triangles[ti + 2] = b;
            }

            Mesh mesh = new Mesh();
            mesh.vertices = vertices;
            mesh.colors = colors;
            mesh.triangles = triangles;
            mesh.RecalculateNormals();
            return mesh;
        }

        private static GameObject CreateStarObject(Transform parent, string name, Vector3 localPosition, float outerRadius, Color color)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.layer = parent.gameObject.layer;
            go.transform.localPosition = localPosition;

            MeshFilter meshFilter = go.AddComponent<MeshFilter>();
            meshFilter.mesh = BuildStarMesh(outerRadius, color);

            MeshRenderer meshRenderer = go.AddComponent<MeshRenderer>();
            meshRenderer.material = new Material(Shader.Find("Sprites/Default"));
            meshRenderer.material.renderQueue = 3100;

            return go;
        }

        private static TextMeshPro CreateGradeTextLabel(Transform parent, Grade grade)
        {
            TextMeshPro tmp = CreateTimingLabel(parent, "GradeText (Clone)", Vector3.zero, GetGradeColor(grade), TextAlignmentOptions.Center);
            tmp.text = GetGradeText(grade);
            tmp.fontSize = 6f;
            return tmp;
        }

        /// <summary>
        /// Lays out 1-4 stars within a square footprint (ITG-style): 1 star fills the square;
        /// 2 stars sit top-left/bottom-right, pulled in toward center; 3 stars sit top-middle +
        /// bottom-left + bottom-right; 4 stars sit in a 2x2 grid across all corners. More stars
        /// shrink to keep the whole group inside the same footprint.
        /// </summary>
        private static void BuildStarLayout(Transform parent, int starCount, Color color, float half)
        {
            switch (starCount)
            {
                case 1:
                    // Fills the footprint — this one should read at roughly the same size as the
                    // text-grade label.
                    CreateStarObject(parent, "Star1 (Clone)", Vector3.zero, half * 0.9f, color);
                    break;

                case 2:
                    CreateStarObject(parent, "Star1 (Clone)", new Vector3(-half * 0.5f, half * 0.5f, 0f), half * 0.35f, color);
                    CreateStarObject(parent, "Star2 (Clone)", new Vector3(half * 0.5f, -half * 0.5f, 0f), half * 0.35f, color);
                    break;

                case 3:
                    CreateStarObject(parent, "Star1 (Clone)", new Vector3(0f, half * 0.6f, 0f), half * 0.3f, color);
                    CreateStarObject(parent, "Star2 (Clone)", new Vector3(-half * 0.55f, -half * 0.5f, 0f), half * 0.3f, color);
                    CreateStarObject(parent, "Star3 (Clone)", new Vector3(half * 0.55f, -half * 0.5f, 0f), half * 0.3f, color);
                    break;

                case 4:
                    CreateStarObject(parent, "Star1 (Clone)", new Vector3(-half * 0.55f, half * 0.55f, 0f), half * 0.25f, color);
                    CreateStarObject(parent, "Star2 (Clone)", new Vector3(half * 0.55f, half * 0.55f, 0f), half * 0.25f, color);
                    CreateStarObject(parent, "Star3 (Clone)", new Vector3(-half * 0.55f, -half * 0.55f, 0f), half * 0.25f, color);
                    CreateStarObject(parent, "Star4 (Clone)", new Vector3(half * 0.55f, -half * 0.55f, 0f), half * 0.25f, color);
                    break;
            }
        }

        public static void DestroyGradeVisual()
        {
            if (gradeVisualObject != null)
            {
                GameObject.Destroy(gradeVisualObject);
                gradeVisualObject = null;
            }
        }

        /// <summary>
        /// Builds the grade visual (star layout or text label) for the given judgement percentage
        /// and failure state, parented onto `parent` at the default placeholder position/scale.
        /// Caller (BuildGameplayStatsContent) applies scaleMultiplier/yOffset the same way it does
        /// for the timing/aim/song-timeline graphs.
        /// </summary>
        public static void CreateGradeVisual(Transform parent, float judgementPercent, bool failed)
        {
            DestroyGradeVisual();
            if (parent == null) return;

            Grade grade = GetGrade(judgementPercent, failed);

            gradeVisualObject = new GameObject("GradeVisual (Clone)");
            gradeVisualObject.transform.SetParent(parent, false);
            gradeVisualObject.layer = parent.gameObject.layer;
            gradeVisualObject.transform.localPosition = GradeVisualLocalPosition;
            gradeVisualObject.transform.localScale = GradeVisualLocalScale;

            if (IsStarGrade(grade))
            {
                BuildStarLayout(gradeVisualObject.transform, GetStarCount(grade), GetGradeColor(grade), GradeVisualFootprintHalf);
            }
            else
            {
                CreateGradeTextLabel(gradeVisualObject.transform, grade);
            }
        }

        /// <summary>
        /// Call once per frame (from OnUpdate). Oscillates each star's Z rotation between
        /// -gradeStarRotationAmplitude and +gradeStarRotationAmplitude, starting at 0 — same
        /// shared phase for every star, same convention as TrippyMenu.Tick() (static state,
        /// driven from the mod's own OnUpdate rather than a per-object MonoBehaviour, since
        /// registering custom Il2Cpp-injected components isn't worth it for a simple oscillation).
        /// Covers both the single detail-panel visual and every currently-tracked Play History row
        /// visual. No-ops on anything showing a text grade (only star children get rotated —
        /// GradeText isn't named "Star...", so it's skipped naturally by the name check below).
        /// </summary>
        public static void TickGradeVisualAnimation()
        {
            float angle = Mathf.Sin(Time.time * gradeStarRotationSpeed) * gradeStarRotationAmplitude;

            RotateStarChildren(gradeVisualObject, angle);

            foreach (var visual in historyRowGradeVisuals.Values)
            {
                RotateStarChildren(visual, angle);
            }
        }

        private static void RotateStarChildren(GameObject visual, float angle)
        {
            if (visual == null) return;

            for (int i = 0; i < visual.transform.childCount; i++)
            {
                Transform child = visual.transform.GetChild(i);
                if (child.name.StartsWith("Star"))
                {
                    child.localEulerAngles = new Vector3(0f, 0f, angle);
                }
            }
        }

        /// <summary>
        /// Builds (or rebuilds) the compact star visual for one Play History row slot, parented
        /// onto `parent` (the row's info Text transform) at local zero. Only used for star grades —
        /// callers should use ClearRowGradeVisual for text grades instead.
        /// </summary>
        public static void CreateOrUpdateRowGradeVisual(int slot, Transform parent, Grade grade)
        {
            ClearRowGradeVisual(slot);
            if (parent == null) return;

            GameObject visual = new GameObject("RowGradeVisual (Clone)");
            visual.transform.SetParent(parent, false);
            visual.layer = parent.gameObject.layer;
            visual.transform.localPosition = RowGradeVisualLocalPosition;
            visual.transform.localScale = RowGradeVisualLocalScale;

            BuildStarLayout(visual.transform, GetStarCount(grade), GetGradeColor(grade), RowGradeVisualFootprintHalf);

            historyRowGradeVisuals[slot] = visual;
        }

        /// <summary>Destroys and untracks a row's grade visual, if any. Call for text-grade rows
        /// (no visual needed) and for rows that become inactive as history scrolls/reloads.</summary>
        public static void ClearRowGradeVisual(int slot)
        {
            if (historyRowGradeVisuals.TryGetValue(slot, out GameObject visual))
            {
                if (visual != null) GameObject.Destroy(visual);
                historyRowGradeVisuals.Remove(slot);
            }
        }
    }
}