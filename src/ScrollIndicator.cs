using MelonLoader;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// A thin, non-interactive scroll-position indicator on the right side of the song list.
    ///
    /// The full track (top ScrollUpArrow world position to bottom ScrollDownArrow world position)
    /// always represents the ENTIRE list, regardless of how many rows it has. A "pill" segment
    /// (a thin line with rounded ends) shows the currently-visible slice, sized proportionally —
    /// if the whole list fits on screen, the pill fills the whole track.
    ///
    /// In Wrap List mode, when the visible window straddles the list's start/end boundary (you're
    /// seeing the tail of the list at the top of the screen and the head of the list at the bottom,
    /// or vice versa), the pill splits into two segments — one pinned to each end of the track —
    /// each sized to whatever fraction of the wrapped-around content is actually showing.
    ///
    /// Built the same way as Heatmap.cs's procedural overlays: a plain Mesh + MeshRenderer using
    /// the Sprites/Default shader with per-vertex color, no reliance on any specific game prefab.
    /// </summary>
    internal static class ScrollIndicator
    {
        // ── Tunables — safe to poke live via UnityExplorer's C# console while testing.
        // Once you've landed on good values, tell me the numbers and I'll bake them in as defaults. ──
        public static float Thickness = 0.35f;
        public static Color BarColor = new Color(0.88f, 0.88f, 0.88f, 1f);
        public static float XOffset = -0.05f;
        public static float ZOffset = 0f;
        public static float TopYOffset = 0.25f;
        public static float BottomYOffset = 1.15f;

        private const float PI = (float)System.Math.PI;

        private const int CapSegments = 10;
        private const float MinSegmentHeight = 0.02f;
        private const float MeshRebuildEpsilon = 0.01f;

        // ── Cached scene refs. Re-resolved automatically if the menu hierarchy gets torn down
        // and rebuilt (Unity's overloaded null-check on a destroyed object reports true). ──
        private static Transform ssSongSelect;
        private static Transform ssArrowUp;
        private static Transform ssArrowDown;
        private static Transform launchCenter;

        // ── Built geometry ──
        private static GameObject container;
        private static GameObject thumbMain;   // the normal contiguous visible-range segment
        private static GameObject thumbWrap;   // the second segment, used only during a wrap split
        private static float thumbMainW = -1f, thumbMainH = -1f;
        private static float thumbWrapW = -1f, thumbWrapH = -1f;

        /// <summary>Call once per frame (from OnLateUpdate). Cheap no-op off the song page.</summary>
        public static void Tick()
        {
            if (!VirtualSongList.IsActive) { Hide(); return; }

            ShellScrollable scroller = VirtualSongList.Scroller;
            int n = VirtualSongList.CurrentView.Count;
            if (scroller == null || n <= 0 || scroller.displayCount <= 0f) { Hide(); return; }

            if (!CacheRefs()) { Hide(); return; }
            if (!EnsureBuilt()) { Hide(); return; }

            Vector3 top = ssArrowUp.position + new Vector3(0f, TopYOffset, 0f);
            Vector3 bottom = ssArrowDown.position + new Vector3(0f, BottomYOffset, 0f);
            float x = (ssSongSelect.position.x + launchCenter.position.x) * 0.5f + XOffset;
            float z = ssSongSelect.position.z + ZOffset;

            float displayCount = scroller.displayCount;

            if (displayCount >= n)
            {
                // Whole list fits on screen — one full-height segment, no split possible.
                Place(thumbMain, top, bottom, 0f, 1f, x, z, ref thumbMainW, ref thumbMainH);
                thumbMain.SetActive(true);
                thumbWrap.SetActive(false);
                return;
            }

            // Canonical top row of the visible window. While Wrap List is on, this can go
            // negative (peeking at the tail before row 0) or past (n - displayCount) (peeking at
            // the head after the last row) — see VirtualSongList.GetScroll/ResolveScrollIndex.
            float scrollTop = VirtualSongList.GetScroll();
            float scrollBottom = scrollTop + displayCount;

            float midStart = Mathf.Clamp(scrollTop, 0f, n);
            float midEnd = Mathf.Clamp(scrollBottom, 0f, n);
            float lowOverflow = Mathf.Max(0f, -scrollTop);          // window dips below index 0 (tail wrapped to top of screen)
            float highOverflow = Mathf.Max(0f, scrollBottom - n);   // window spills past index n-1 (head wrapped to bottom of screen)

            if (midEnd > midStart)
            {
                Place(thumbMain, top, bottom, midStart / n, midEnd / n, x, z, ref thumbMainW, ref thumbMainH);
                thumbMain.SetActive(true);
            }
            else
            {
                thumbMain.SetActive(false);
            }

            if (lowOverflow > 0f)
            {
                // Tail-of-list rows are showing at the top of the screen -> pin the second segment
                // to the BOTTOM of the track (that's where the tail lives on the full-list scale).
                Place(thumbWrap, top, bottom, (n - lowOverflow) / n, 1f, x, z, ref thumbWrapW, ref thumbWrapH);
                thumbWrap.SetActive(true);
            }
            else if (highOverflow > 0f)
            {
                // Head-of-list rows are showing at the bottom of the screen -> pin the second
                // segment to the TOP of the track.
                Place(thumbWrap, top, bottom, 0f, highOverflow / n, x, z, ref thumbWrapW, ref thumbWrapH);
                thumbWrap.SetActive(true);
            }
            else
            {
                thumbWrap.SetActive(false);
            }
        }

        private static void Hide()
        {
            if (thumbMain != null) thumbMain.SetActive(false);
            if (thumbWrap != null) thumbWrap.SetActive(false);
        }

        private static bool CacheRefs()
        {
            if (ssSongSelect != null && ssArrowUp != null && ssArrowDown != null && launchCenter != null)
                return true;

            GameObject songSelectGO = GameObject.Find("menu/ShellPage_Song/page/ShellPanel_Center/SongSelect");
            GameObject arrowUpGO = GameObject.Find("menu/ShellPage_Song/page/ShellPanel_Center/SongSelect/ScrollUpArrow");
            GameObject arrowDownGO = GameObject.Find("menu/ShellPage_Song/page/ShellPanel_Center/SongSelect/ScrollDownArrow");
            GameObject launchGO = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center");

            if (songSelectGO == null || arrowUpGO == null || arrowDownGO == null || launchGO == null)
                return false;

            ssSongSelect = songSelectGO.transform;
            ssArrowUp = arrowUpGO.transform;
            ssArrowDown = arrowDownGO.transform;
            launchCenter = launchGO.transform;
            return true;
        }

        private static bool EnsureBuilt()
        {
            if (container != null) return true;
            if (ssSongSelect == null) return false;

            // Parented to SongSelect itself (not a persistent root) so the indicator inherits the
            // real song list's activeInHierarchy state — hidden/destroyed/rebuilt right alongside
            // it, instead of only tracking our own VirtualSongList.IsActive flag.
            container = new GameObject("ExScoring_ScrollIndicator");
            container.transform.SetParent(ssSongSelect, false);

            thumbMain = CreateThumb("ScrollIndicatorThumbMain");
            thumbWrap = CreateThumb("ScrollIndicatorThumbWrap");
            thumbMainW = thumbMainH = thumbWrapW = thumbWrapH = -1f; // force first mesh build

            MelonLogger.Log("[ScrollIndicator] Built.");
            return true;
        }

        private static GameObject CreateThumb(string name)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(container.transform, false);

            MeshFilter mf = go.AddComponent<MeshFilter>();
            mf.mesh = new Mesh();

            MeshRenderer mr = go.AddComponent<MeshRenderer>();
            mr.material = new Material(Shader.Find("Sprites/Default"));
            mr.material.renderQueue = 3100; // draw on top — same trick Heatmap's overlays use

            go.SetActive(false);
            return go;
        }

        private static void Place(GameObject go, Vector3 trackTop, Vector3 trackBottom, float f0, float f1,
                                   float x, float z, ref float cachedWidth, ref float cachedHeight)
        {
            Vector3 p0 = Vector3.Lerp(trackTop, trackBottom, f0);
            Vector3 p1 = Vector3.Lerp(trackTop, trackBottom, f1);
            float height = Mathf.Max(Mathf.Abs(p1.y - p0.y), MinSegmentHeight);
            float centerY = (p0.y + p1.y) * 0.5f;

            go.transform.position = new Vector3(x, centerY, z);
            ApplyMesh(go, Thickness, height, ref cachedWidth, ref cachedHeight);
        }

        /// <summary>Rebuilds the segment's pill mesh only when its size actually changed enough to matter.</summary>
        private static void ApplyMesh(GameObject go, float width, float height, ref float cachedWidth, ref float cachedHeight)
        {
            height = Mathf.Max(height, width); // never let it get thinner than it is wide — keeps the round caps sane
            if (Mathf.Abs(width - cachedWidth) < MeshRebuildEpsilon && Mathf.Abs(height - cachedHeight) < MeshRebuildEpsilon)
                return;

            MeshFilter mf = go.GetComponent<MeshFilter>();
            if (mf == null) return;

            BuildPillMesh(mf.mesh, width, height, BarColor);
            cachedWidth = width;
            cachedHeight = height;
        }

        /// <summary>
        /// Fills `mesh` with a vertical "pill"/stadium shape: a straight rectangle of the given
        /// width, capped top and bottom by semicircles — a thin line with rounded ends. Built flat
        /// in local XY (Z=0), fan-triangulated from a center vertex, and double-sided (both
        /// triangle windings) so it renders regardless of which way the panel happens to face.
        /// </summary>
        private static void BuildPillMesh(Mesh mesh, float width, float height, Color color)
        {
            float r = Mathf.Max(0.001f, width * 0.5f);
            float straightHalf = Mathf.Max(0f, height * 0.5f - r);

            int rim = (CapSegments + 1) * 2;
            var verts = new Vector3[rim + 1];
            var colors = new Color[rim + 1];
            verts[0] = Vector3.zero; // fan center
            colors[0] = color;

            int vi = 1;
            // Top semicircle, centered at (0, +straightHalf), angle 0 (right) -> 180 (left)
            for (int i = 0; i <= CapSegments; i++)
            {
                float ang = Mathf.Lerp(0f, PI, i / (float)CapSegments);
                verts[vi] = new Vector3(Mathf.Cos(ang) * r, straightHalf + Mathf.Sin(ang) * r, 0f);
                colors[vi] = color;
                vi++;
            }
            // Bottom semicircle, centered at (0, -straightHalf), angle 180 -> 360, continuing the same winding
            for (int i = 0; i <= CapSegments; i++)
            {
                float ang = Mathf.Lerp(PI, 2f * PI, i / (float)CapSegments);
                verts[vi] = new Vector3(Mathf.Cos(ang) * r, -straightHalf + Mathf.Sin(ang) * r, 0f);
                colors[vi] = color;
                vi++;
            }

            var tris = new int[rim * 3 * 2];
            int ti = 0;
            for (int i = 1; i <= rim; i++)
            {
                int a = i;
                int b = (i % rim) + 1;
                tris[ti++] = 0; tris[ti++] = a; tris[ti++] = b; // front winding
            }
            for (int i = 1; i <= rim; i++)
            {
                int a = i;
                int b = (i % rim) + 1;
                tris[ti++] = 0; tris[ti++] = b; tris[ti++] = a; // back winding (reversed)
            }

            mesh.Clear();
            mesh.vertices = verts;
            mesh.colors = colors;
            mesh.triangles = tris;
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
        }
    }
}