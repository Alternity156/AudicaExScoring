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
        private const float GradeVisualTextFontSize = 6f;

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

        // Same idea as RowGradeVisual, but for the Song Info panel's top-score rows (one per
        // difficulty) instead of Play History rows. Sits on the native StarDisplayUI's transform
        // (its pips/stars are hidden — see TopScoreUI.cs), which is a different UGUI hierarchy than
        // the history rows', so it gets its own placeholder position/scale/footprint to tune
        // independently in UnityExplorer.
        private const float TopScoreGradeVisualFootprintHalf = 0.1f;
        // Font size is NOT derived from the footprint above — text and star rendering don't scale
        // the same way across contexts with different canvas setups. Starting from the song list's
        // working value (SongRowGradeVisualTextFontSize) rather than the main panel's, since this
        // context's container scale (250) is much closer in magnitude to the song list's (300) than
        // to the main panel's (3) — tune live in UnityExplorer if still off.
        private const float TopScoreGradeVisualTextFontSize = 1.71f;
        private static readonly Vector3 TopScoreGradeVisualLocalPosition = new Vector3(200f, 0f, 0f);
        private static readonly Vector3 TopScoreGradeVisualLocalScale = new Vector3(250f, 250f, 250f);

        // One grade visual per difficulty's top-score row. Keyed by difficulty rather than a list
        // slot since SetTopScore is only ever called once per difficulty per refresh.
        private static readonly Dictionary<KataConfig.Difficulty, GameObject> topScoreGradeVisuals = new Dictionary<KataConfig.Difficulty, GameObject>();

        // Same idea again, but for song-list row StarDisplay components (VirtualSongList's pooled
        // SongSelectItem rows). Keyed by the row GameObject's instance ID rather than difficulty or
        // list slot, since a pooled row can be rebound to any song and only ever shows one
        // difficulty's result at a time. Its own placeholder position/scale/footprint since it's a
        // much smaller footprint (song list row) than either the history rows or the top-score
        // panel — tune live in UnityExplorer.
        private const float SongRowGradeVisualFootprintHalf = 0.1f;
        // Matches what the previous footprint-derived value (0.1 * 6/0.35 ≈ 1.71) already rendered
        // correctly at, kept as its own explicit constant now rather than computed — tune live in
        // UnityExplorer if needed.
        private const float SongRowGradeVisualTextFontSize = 1.71f;
        private static readonly Vector3 SongRowGradeVisualLocalPosition = new Vector3(30f, -15f, 0f);
        private static readonly Vector3 SongRowGradeVisualLocalScale = new Vector3(300f, 300f, 300f);
        private static readonly Dictionary<int, GameObject> songRowGradeVisuals = new Dictionary<int, GameObject>();

        // Single visual shown on the level-end results screen (SongEndSequence's
        // ScorePercentStars/StarDisplay), replacing native's stars/star_pips/star_meters — see
        // HideEndSequenceNativeStars in Hooks.cs. Parented directly onto StarDisplay's transform
        // (native's own stars sit at that transform's local origin), so starting at zero offset.
        // Scale/footprint/font borrowed from TopScoreGradeVisual/SongRowGradeVisual since this is
        // very likely the same family of world-space star prefab — tune live in UnityExplorer once
        // visible.
        public static GameObject endSequenceGradeVisualObject;
        private const float EndSequenceGradeVisualFootprintHalf = 0.1f;
        private const float EndSequenceGradeVisualTextFontSize = 1.71f;
        private static readonly Vector3 EndSequenceGradeVisualLocalPosition = new Vector3(0f, 0f, 0f);
        private static readonly Vector3 EndSequenceGradeVisualLocalScale = new Vector3(250f, 250f, 250f);

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

        private static TextMeshPro CreateGradeTextLabel(Transform parent, Grade grade, float fontSize)
        {
            TextMeshPro tmp = CreateTimingLabel(parent, "GradeText (Clone)", Vector3.zero, GetGradeColor(grade), TextAlignmentOptions.Center);
            tmp.text = GetGradeText(grade);
            tmp.fontSize = fontSize;
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
                    CreateStarObject(parent, "Star1 (Clone)", new Vector3(-half * 0.35f, half * 0.35f, 0f), half * 0.525f, color);
                    CreateStarObject(parent, "Star2 (Clone)", new Vector3(half * 0.35f, -half * 0.35f, 0f), half * 0.525f, color);
                    break;

                case 3:
                    CreateStarObject(parent, "Star1 (Clone)", new Vector3(0f, half * 0.45f, 0f), half * 0.45f, color);
                    CreateStarObject(parent, "Star2 (Clone)", new Vector3(-half * 0.4f, -half * 0.35f, 0f), half * 0.45f, color);
                    CreateStarObject(parent, "Star3 (Clone)", new Vector3(half * 0.4f, -half * 0.35f, 0f), half * 0.45f, color);
                    break;

                case 4:
                    CreateStarObject(parent, "Star1 (Clone)", new Vector3(-half * 0.5f, half * 0.5f, 0f), half * 0.45f, color);
                    CreateStarObject(parent, "Star2 (Clone)", new Vector3(half * 0.5f, half * 0.5f, 0f), half * 0.45f, color);
                    CreateStarObject(parent, "Star3 (Clone)", new Vector3(-half * 0.5f, -half * 0.5f, 0f), half * 0.45f, color);
                    CreateStarObject(parent, "Star4 (Clone)", new Vector3(half * 0.5f, -half * 0.5f, 0f), half * 0.45f, color);
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
                CreateGradeTextLabel(gradeVisualObject.transform, grade, GradeVisualTextFontSize);
            }
        }

        /// <summary>
        /// Call once per frame (from OnUpdate). Oscillates each star's Z rotation between
        /// -gradeStarRotationAmplitude and +gradeStarRotationAmplitude, starting at 0 — same
        /// shared phase for every star, same convention as TrippyMenu.Tick() (static state,
        /// driven from the mod's own OnUpdate rather than a per-object MonoBehaviour, since
        /// registering custom Il2Cpp-injected components isn't worth it for a simple oscillation).
        /// Covers the single detail-panel visual, every currently-tracked Play History row visual,
        /// and every currently-tracked Song Info top-score row visual. No-ops on anything showing a
        /// text grade (only star children get rotated — GradeText isn't named "Star...", so it's
        /// skipped naturally by the name check below).
        /// </summary>
        public static void TickGradeVisualAnimation()
        {
            float angle = Mathf.Sin(Time.time * gradeStarRotationSpeed) * gradeStarRotationAmplitude;

            RotateStarChildren(gradeVisualObject, angle);

            foreach (var visual in historyRowGradeVisuals.Values)
            {
                RotateStarChildren(visual, angle);
            }

            foreach (var visual in topScoreGradeVisuals.Values)
            {
                RotateStarChildren(visual, angle);
            }

            foreach (var visual in songRowGradeVisuals.Values)
            {
                RotateStarChildren(visual, angle);
            }

            RotateStarChildren(endSequenceGradeVisualObject, angle);
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

        /// <summary>Destroys and untracks every Play History row grade visual at once — call when
        /// leaving EX scoring entirely (native history no longer uses these) rather than per-slot.</summary>
        public static void ClearAllRowGradeVisuals()
        {
            foreach (var visual in historyRowGradeVisuals.Values)
            {
                if (visual != null) GameObject.Destroy(visual);
            }
            historyRowGradeVisuals.Clear();
        }

        /// <summary>
        /// Mirrors UpdateHistoryHitboxVisibility (PlayHistoryButton.cs) for the Play History
        /// star-grade visuals. A row's letter-grade TMP text gets clipped for free by the native
        /// RectMask2D since it's a UI Graphic, but these star visuals are plain MeshRenderer meshes
        /// (see CreateStarObject) which a RectMask2D has no effect on — so without this they'd stay
        /// visible while scrolled out of the history list's viewport. Reuses IsHistoryRowVisible
        /// (PlayHistoryButton.cs), same partial class, against each visual's own transform, since
        /// it's parented at local zero on the row's info Text the same way the hitbox quad tracks
        /// its row.
        /// </summary>
        public static void UpdateHistoryRowGradeVisualVisibility()
        {
            foreach (var visual in historyRowGradeVisuals.Values)
            {
                if (visual == null) continue;

                bool visible = IsHistoryRowVisible(visual.transform);

                var renderers = visual.GetComponentsInChildren<MeshRenderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                    renderers[i].enabled = visible;
            }
        }

        /// <summary>
        /// Builds (or rebuilds) the grade visual for one Song Info top-score row (one per
        /// difficulty), parented onto `parent` (the row's StarDisplayUI transform, with its native
        /// pips/stars hidden — see TopScoreUI.cs) at the placeholder position/scale. Unlike
        /// CreateOrUpdateRowGradeVisual, this handles BOTH star and text grades itself (top-score
        /// items have no spare text field to repurpose the way Play History rows do via `info`).
        /// </summary>
        public static void CreateOrUpdateTopScoreGradeVisual(KataConfig.Difficulty slot, Transform parent, Grade grade)
        {
            ClearTopScoreGradeVisual(slot);
            if (parent == null) return;

            GameObject visual = new GameObject("TopScoreGradeVisual (Clone)");
            visual.transform.SetParent(parent, false);
            visual.layer = parent.gameObject.layer;
            visual.transform.localPosition = TopScoreGradeVisualLocalPosition;
            visual.transform.localScale = TopScoreGradeVisualLocalScale;

            if (IsStarGrade(grade))
            {
                BuildStarLayout(visual.transform, GetStarCount(grade), GetGradeColor(grade), TopScoreGradeVisualFootprintHalf);
            }
            else
            {
                CreateGradeTextLabel(visual.transform, grade, TopScoreGradeVisualTextFontSize);
            }

            topScoreGradeVisuals[slot] = visual;
        }

        /// <summary>Destroys and untracks a top-score row's grade visual, if any. Call whenever that
        /// difficulty's row has no saved EX run (row gets hidden entirely) or is being replaced.</summary>
        public static void ClearTopScoreGradeVisual(KataConfig.Difficulty slot)
        {
            if (topScoreGradeVisuals.TryGetValue(slot, out GameObject visual))
            {
                if (visual != null) GameObject.Destroy(visual);
                topScoreGradeVisuals.Remove(slot);
            }
        }

        /// <summary>
        /// Builds (or rebuilds) the grade visual for one song-list row, parented onto `parent` (the
        /// row's StarDisplay transform, with its native per-difficulty star arrays hidden — see
        /// SongListHighScoreUI.cs) at the placeholder position/scale. Handles both star and text
        /// grades itself, same as CreateOrUpdateTopScoreGradeVisual.
        /// </summary>
        public static void CreateOrUpdateSongRowGradeVisual(int slot, Transform parent, Grade grade)
        {
            ClearSongRowGradeVisual(slot);
            if (parent == null) return;

            GameObject visual = new GameObject("SongRowGradeVisual (Clone)");
            visual.transform.SetParent(parent, false);
            visual.layer = parent.gameObject.layer;
            visual.transform.localPosition = SongRowGradeVisualLocalPosition;
            visual.transform.localScale = SongRowGradeVisualLocalScale;

            if (IsStarGrade(grade))
            {
                BuildStarLayout(visual.transform, GetStarCount(grade), GetGradeColor(grade), SongRowGradeVisualFootprintHalf);
            }
            else
            {
                CreateGradeTextLabel(visual.transform, grade, SongRowGradeVisualTextFontSize);
            }

            songRowGradeVisuals[slot] = visual;
        }

        /// <summary>Destroys and untracks a song-list row's grade visual, if any. Call whenever that
        /// row has no cached EX result (row's high-score UI gets hidden entirely), is being
        /// rebound/replaced, or EX scoring is switched off (native stars getting restored).</summary>
        public static void ClearSongRowGradeVisual(int slot)
        {
            if (songRowGradeVisuals.TryGetValue(slot, out GameObject visual))
            {
                if (visual != null) GameObject.Destroy(visual);
                songRowGradeVisuals.Remove(slot);
            }
        }

        /// <summary>
        /// Builds (or rebuilds) the grade visual shown on the level-end results screen, parented
        /// onto `parent` (ScorePercentStars/StarDisplay, with native stars/star_pips/star_meters
        /// hidden — see HideEndSequenceNativeStars in Hooks.cs). Single instance like
        /// CreateGradeVisual, since only one result is ever shown at a time here.
        /// </summary>
        public static void CreateOrUpdateEndSequenceGradeVisual(Transform parent, Grade grade)
        {
            ClearEndSequenceGradeVisual();
            if (parent == null) return;

            endSequenceGradeVisualObject = new GameObject("EndSequenceGradeVisual (Clone)");
            endSequenceGradeVisualObject.transform.SetParent(parent, false);
            endSequenceGradeVisualObject.layer = parent.gameObject.layer;
            endSequenceGradeVisualObject.transform.localPosition = EndSequenceGradeVisualLocalPosition;
            endSequenceGradeVisualObject.transform.localScale = EndSequenceGradeVisualLocalScale;

            if (IsStarGrade(grade))
            {
                BuildStarLayout(endSequenceGradeVisualObject.transform, GetStarCount(grade), GetGradeColor(grade), EndSequenceGradeVisualFootprintHalf);
            }
            else
            {
                CreateGradeTextLabel(endSequenceGradeVisualObject.transform, grade, EndSequenceGradeVisualTextFontSize);
            }
        }

        /// <summary>Destroys and untracks the end-sequence grade visual. Call when a fresh
        /// SongEndSequence starts (see SongEndSequenceStartPatch Postfix in Hooks.cs) so each run
        /// gets its own grade rebuilt rather than showing a stale one leftover from the previous
        /// song.</summary>
        public static void ClearEndSequenceGradeVisual()
        {
            if (endSequenceGradeVisualObject != null)
            {
                GameObject.Destroy(endSequenceGradeVisualObject);
                endSequenceGradeVisualObject = null;
            }
        }
    }
}