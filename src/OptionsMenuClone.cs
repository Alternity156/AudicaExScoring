using System;
using UnityEngine;
using MelonLoader;

namespace ExScoringMod
{
    /// <summary>
    /// Clones Audica's settings center panel (its own glass/frame + OptionsMenu) and parks it
    /// over the launch area. We drive it purely with ShowPage/AddButton/AddSlider/AddHeader —
    /// never SetPageActive/MenuState — so the menu state is never changed.
    /// </summary>
    internal static class OptionsMenuClone
    {
        private const string SettingsCenterPath = "ShellPage_Settings/page/ShellPanel_Center";
        private const string LaunchCenterPath = "ShellPage_Launch/page/ShellPanel_Center";

        internal static bool allowShowPage = false;
        private static GameObject cloneRoot;
        internal static ShellPage SongShellPage;
        internal static OptionsMenu Menu;

        /// <summary>Build the clone once. Returns false (and logs why) if a source isn't found yet.</summary>
        public static bool EnsureClone()
        {
            if (cloneRoot != null && Menu != null) return true;

            GameObject menuRoot = GameObject.Find("menu");
            if (menuRoot == null) { MelonLogger.Log("[Options] 'menu' root not found"); return false; }

            Transform src = menuRoot.transform.Find(SettingsCenterPath);
            if (src == null) { MelonLogger.Log("[Options] settings source not found: " + SettingsCenterPath); return false; }
            MelonLogger.Log("[Options] settings source found");

            cloneRoot = GameObject.Instantiate(src.gameObject);
            cloneRoot.name = "ExScoringOptionsPanel";
            cloneRoot.hideFlags |= HideFlags.DontUnloadUnusedAsset;

            Menu = cloneRoot.GetComponentInChildren<OptionsMenu>(true);
            if (Menu == null)
            {
                MelonLogger.Log("[Options] OptionsMenu component not found on clone");
                GameObject.Destroy(cloneRoot); cloneRoot = null; return false;
            }

            // Parent under the (active, interactable) song page so the buttons are shootable,
            // but position it at the launch center's world transform so it sits where the
            // launch panel was.
            Transform songPage = menuRoot.transform.Find("ShellPage_Song/page");
            Transform launchCenter = menuRoot.transform.Find(LaunchCenterPath);
            if (songPage != null && launchCenter != null)
            {
                cloneRoot.transform.SetParent(songPage, false);
                cloneRoot.transform.localPosition = new Vector3(10.5f, 9.25f, 24f);
                cloneRoot.transform.rotation = launchCenter.rotation;
                Vector3 ls = launchCenter.lossyScale, ps = songPage.lossyScale;
                cloneRoot.transform.localScale = new Vector3(
                    ps.x != 0 ? ls.x / ps.x : ls.x,
                    ps.y != 0 ? ls.y / ps.y : ls.y,
                    ps.z != 0 ? ls.z / ps.z : ls.z);
                MelonLogger.Log($"[Options] clone under song page, placed at world {launchCenter.position}");
            }
            else
            {
                MelonLogger.Log($"[Options] placement refs missing (songPage={(songPage != null)}, launchCenter={(launchCenter != null)})");
            }

            cloneRoot.SetActive(false);
            MelonLogger.Log("[Options] clone created");
            return true;
        }

        public static void Show() { if (cloneRoot != null) cloneRoot.SetActive(true); }
        public static void Hide() { if (cloneRoot != null) cloneRoot.SetActive(false); }

        /// <summary>Reset to a blank custom page, set the title, then let the category emit rows.</summary>
        // in OptionsMenuClone.Draw, at the top and bottom
        public static void Draw(string title, Action build)
        {
            if (Menu == null) return;
            allowShowPage = true;
            Menu.ShowPage(OptionsMenu.Page.Customization);
            allowShowPage = false;
            Wipe();
            if (Menu.screenTitle != null) Menu.screenTitle.text = title ?? "";
            build?.Invoke();
            Menu.scrollable.SnapTo(0, true);
        }

        /// <summary>
        /// Shows a page's real, native content as built by the game's own OptionsMenu logic —
        /// no Wipe(), no custom Build(). Used for pages we want to present unmodified (e.g. Customization).
        /// </summary>
        public static void DrawNativePage(string title, OptionsMenu.Page page)
        {
            if (Menu == null) return;
            allowShowPage = true;
            Menu.ShowPage(page);
            allowShowPage = false;
            if (title != null && Menu.screenTitle != null) Menu.screenTitle.text = title;
            Menu.scrollable.SnapTo(0, true);
        }

        public static void Wipe()
        {
            if (Menu == null) return;
            Transform t = Menu.transform;
            for (int i = t.childCount - 1; i >= 0; i--)
            {
                Transform child = t.GetChild(i);
                if (child.gameObject.name.Contains("(Clone)"))
                    GameObject.DestroyImmediate(child.gameObject); // Destroy is end-of-frame; DestroyImmediate clears them now
            }
            Menu.mRows.Clear();
            Menu.scrollable.ClearRows();
            Menu.scrollable.mRows.Clear();
            Menu.scrollable.mIndex = 0;
            Menu.scrollable.destroyChildren = true;
        }

        // ── Create-only (returns the control's GameObject, does NOT add a row) ──

        public static GameObject CreateHeader(int col, string text)
        {
            return Menu.AddHeader(col, text);
        }

        public static GameObject CreateToggle(int col, string label, Func<bool> get, Action<bool> set, string hover = null)
        {
            var omb = Menu.AddButton(col, label,
                new Action(() => { set(!get()); }),
                new Func<bool>(() => get()),
                hover ?? label);

            var gb = omb.button;
            if (gb != null)
            {
                if (SongShellPage != null) gb.mShellPage = SongShellPage;
                gb.SetInteractable(true);
            }

            var help = omb.help;
            if (help != null)
            {
                if (help.text != null) help.text.text = hover ?? label;
                help.forceDisable = false;
            }

            return omb.gameObject;
        }

        public static GameObject CreateSlider(int col, string label, Func<float> get, Action<float> set,
                                              float min, float max, float step, float def,
                                              string format = "N0", string hover = null)
        {
            var sl = Menu.AddSlider(col, label, format,
                new Action<float>((amount) =>
                {
                    float v = get() + amount * step;
                    if (v > max) v = max; else if (v < min) v = min;
                    set(v);
                }),
                new Func<float>(() => get()),
                new Action(() => set(def)),
                hover ?? label,
                new Func<float, string>((v) => v.ToString(format)));
            return sl.gameObject;
        }

        public static GameObject CreateSlider(int col, string label, Func<float> get, Action<float> set,
                                              float min, float max, float step, float def,
                                              Func<float, string> displayFormatter, string hover = null)
        {
            var sl = Menu.AddSlider(col, label, "N0",
                new Action<float>((amount) =>
                {
                    float v = get() + amount * step;
                    if (v > max) v = max; else if (v < min) v = min;
                    set(v);
                }),
                new Func<float>(() => get()),
                new Action(() => set(def)),
                hover ?? label,
                new Func<float, string>((v) => displayFormatter(v)));
            return sl.gameObject;
        }

        public static GameObject CreateCycle(int col, string label, string[] options,
                                             Func<int> get, Action<int> set, int def = 0, string hover = null)
        {
            int n = options != null ? options.Length : 0;
            var sl = Menu.AddSlider(col, label, "0",
                new Action<float>((amount) =>
                {
                    if (n == 0) return;
                    int dir = amount > 0f ? 1 : (amount < 0f ? -1 : 0);
                    int idx = ((get() + dir) % n + n) % n;
                    set(idx);
                }),
                new Func<float>(() => get()),
                new Action(() => set(def)),
                hover ?? label,
                new Func<float, string>((v) =>
                {
                    if (n == 0) return "";
                    int idx = ((Mathf.RoundToInt(v) % n) + n) % n;
                    return options[idx];
                }));
            return sl.gameObject;
        }

        public static GameObject CreateWideSlider(string label, Func<float> get, Action<float> set,
                                          float min, float max, float step, float def,
                                          string format = "N0", string hover = null)
        {
            var sl = Menu.AddSlider(0, label, format,
                new Action<float>((amount) =>
                {
                    float v = get() + amount * step;
                    if (v > max) v = max; else if (v < min) v = min;
                    set(v);
                }),
                new Func<float>(() => get()),
                new Action(() => set(def)),
                hover ?? label,
                new Func<float, string>((v) => v.ToString(format)));

            var go = sl.gameObject;
            MelonCoroutines.Start(ApplyWideSliderLayout(go));
            return go;
        }

        private static System.Collections.IEnumerator ApplyWideSliderLayout(GameObject go)
        {
            yield return null;

            if (go == null) yield break;

            Transform panelT = go.transform.Find("panel");
            Transform labelT = go.transform.Find("label");
            Transform leftT = go.transform.Find("left");
            Transform rightT = go.transform.Find("right");
            Transform resetT = go.transform.Find("reset");

            if (panelT != null)
            {
                panelT.localPosition = new Vector3(4.2f, panelT.localPosition.y, panelT.localPosition.z);
                panelT.localScale = new Vector3(1.95f, panelT.localScale.y, panelT.localScale.z);
            }

            if (leftT != null)
                leftT.localPosition = new Vector3(0.4f, leftT.localPosition.y, leftT.localPosition.z);

            if (rightT != null)
                rightT.localPosition = new Vector3(8.0f, rightT.localPosition.y, rightT.localPosition.z);

            if (resetT != null)
                resetT.localPosition = new Vector3(4.2f, resetT.localPosition.y, resetT.localPosition.z);

            if (labelT != null)
            {
                labelT.localPosition = new Vector3(4.2f, labelT.localPosition.y, labelT.localPosition.z);
                var rt = labelT.GetComponent<RectTransform>();
                if (rt != null)
                    rt.sizeDelta = new Vector2(8.0f, rt.sizeDelta.y);
            }
        }

        // ── Row adders ──

        public static void AddRow(GameObject single)
        {
            Menu.scrollable.AddRow(single);
        }

        public static void AddRow(GameObject left, GameObject right)
        {
            var list = new Il2CppSystem.Collections.Generic.List<GameObject>();
            list.Add(left);
            if (right != null) list.Add(right);
            Menu.scrollable.AddRow(list);
        }

        // ── Single-row convenience (unchanged signatures) ──

        public static void AddHeader(int col, string text)
            => AddRow(CreateHeader(col, text));

        public static void AddToggle(int col, string label, Func<bool> get, Action<bool> set, string hover = null)
            => AddRow(CreateToggle(col, label, get, set, hover));

        public static void AddSlider(int col, string label, Func<float> get, Action<float> set,
                                     float min, float max, float step, float def, string format = "N0", string hover = null)
            => AddRow(CreateSlider(col, label, get, set, min, max, step, def, format, hover));

        public static void AddCycle(int col, string label, string[] options,
                                    Func<int> get, Action<int> set, int def = 0, string hover = null)
            => AddRow(CreateCycle(col, label, options, get, set, def, hover));

        public static void AddWideSlider(string label, Func<float> get, Action<float> set,
                                 float min, float max, float step, float def,
                                 string format = "N0", string hover = null)
            => AddRow(CreateWideSlider(label, get, set, min, max, step, def, format, hover));
    }
}