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
        public static void Draw(string title, Action build)
        {
            if (Menu == null) return;
            Menu.ShowPage(OptionsMenu.Page.Customization);
            Wipe();
            if (Menu.screenTitle != null) Menu.screenTitle.text = title ?? "";
            build?.Invoke();
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
                    GameObject.Destroy(child.gameObject);
            }
            Menu.mRows.Clear();
            Menu.scrollable.ClearRows();
            Menu.scrollable.mRows.Clear();
            Menu.scrollable.mIndex = 0;
            Menu.scrollable.destroyChildren = true;
        }

        // ── Control wrappers (single column) ──────────────────────────────────

        public static void AddHeader(int col, string text)
        {
            var h = Menu.AddHeader(col, text);
            Menu.scrollable.AddRow(h);
        }

        public static void AddToggle(int col, string label, Func<bool> get, Action<bool> set, string hover = null)
        {
            var omb = Menu.AddButton(col, label,
                new Action(() => { set(!get()); }),
                new Func<bool>(() => get()),
                hover ?? label);

            var gb = omb.button;                 // GunButton field on OptionsMenuButton
            if (gb != null)
            {
                if (SongShellPage != null) gb.mShellPage = SongShellPage;
                gb.SetInteractable(true);
            }

            // AddButton's displayName doesn't fill the hover-help for checkbox buttons,
            // so populate it directly: give it text and clear the force-disable.
            var help = omb.help;
            if (help != null)
            {
                if (help.text != null) help.text.text = hover ?? label;
                help.forceDisable = false;
            }

            Menu.scrollable.AddRow(omb.gameObject);
        }

        public static void AddSlider(int col, string label, Func<float> get, Action<float> set,
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
            Menu.scrollable.AddRow(sl.gameObject);
        }
    }
}