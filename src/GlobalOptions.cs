using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MelonLoader;

namespace ExScoringMod
{
    internal class OptionsCategory
    {
        public string Id;
        public string Title;
        public Action Build;
        public OptionsCategory(string id, string title, Action build) { Id = id; Title = title; Build = build; }
    }

    /// <summary>
    /// Global options: a drill-in level showing Back + category rows in the song list. Shooting a
    /// category hides the launch panel (animated) and draws that category's controls on the clone.
    /// </summary>
    internal static class GlobalOptions
    {
        private static GameObject launchPage;

        public static readonly Color OptionsRowColor = new Color(0.24f, 0.22f, 0.30f, 1f);
        private static readonly Color CategoryColor = new Color(0.26f, 0.26f, 0.30f, 1f);

        // Time to let HideLaunchPanel's animation play before showing the clone. Tune if needed.
        private static float hideAnimDelay = 0.25f;

        private static bool panelShown = false;

        // ── TEST CATEGORY state (logs only; not bound to Config yet) ──────────
        private static bool testToggle = false;
        private static float testSlider = 5f;

        private static readonly List<OptionsCategory> Categories = new List<OptionsCategory>
        {
            new OptionsCategory("opt_test", "Test", () =>
            {
                OptionsMenuClone.AddHeader(0, "Test Category");
                OptionsMenuClone.AddToggle(0, "Test Toggle",
                    () => testToggle,
                    v => { testToggle = v; MelonLogger.Log($"[Options] Test Toggle -> {v}"); });
                OptionsMenuClone.AddSlider(1, "Test Slider",
                    () => testSlider,
                    v => { testSlider = v; MelonLogger.Log($"[Options] Test Slider -> {v}"); },
                    0f, 10f, 1f, 5f, "N0");
            }),
        };

        // ── Song-list view (Back + categories) ────────────────────────────────
        public static List<ViewRow> BuildView()
        {
            var rows = new List<ViewRow>();
            rows.Add(ViewRow.ActionRow("Back", PlaylistNav.BackColor, FolderRowManager.NavBack));
            foreach (var cat in Categories)
            {
                var c = cat; // capture
                rows.Add(ViewRow.ActionRow(c.Title, CategoryColor, () => OnCategoryShot(c), null, c.Id));
            }
            return rows;
        }

        private static void OnCategoryShot(OptionsCategory cat)
        {
            MelonLogger.Log($"[Options] category shot: {cat.Title}");
            VirtualSongList.SetSelectedAction(cat.Id);
            ShowCategory(cat);
        }

        private static void ShowCategory(OptionsCategory cat)
        {
            if (!OptionsMenuClone.EnsureClone()) return;

            if (!panelShown)
            {
                panelShown = true;
                launchPage = GameObject.Find("menu/ShellPage_Launch");
                MelonLogger.Log("[Options] hiding launch panel (animated), then showing clone");
                ExScoring.HideLaunchPanel();
                MelonCoroutines.Start(ShowAfterHide(cat));
            }
            else
            {
                OptionsMenuClone.Draw(cat.Title, cat.Build); // already up: redraw, no animation
            }
        }

        private static float LaunchHideAnimLength()
        {
            if (launchPage != null)
            {
                var sp = launchPage.GetComponent<ShellPage>();
                if (sp != null && sp.hideAnim != null) return sp.hideAnim.length;
            }
            return hideAnimDelay; // fallback
        }

        private static IEnumerator ShowAfterHide(OptionsCategory cat)
        {
            float len = LaunchHideAnimLength();
            MelonLogger.Log($"[Options] waiting hideAnim length {len:N2}s before disabling launch page");
            yield return new WaitForSeconds(len);

            if (launchPage != null) launchPage.SetActive(false); // the missing SetActive(false)
            OptionsMenuClone.Show();
            yield return null; // let ShowPage settle a frame
            OptionsMenuClone.Draw(cat.Title, cat.Build);
            MelonLogger.Log("[Options] launch page disabled, clone shown");
        }

        public static void HidePanel()
        {
            VirtualSongList.SetSelectedAction(null);
            if (!panelShown) return;
            panelShown = false;
            MelonLogger.Log("[Options] hiding clone, restoring launch panel");

            OptionsMenuClone.Wipe();
            OptionsMenuClone.Hide();

            if (launchPage != null) launchPage.SetActive(true);

            bool prevSuppress = ExScoring.suppressShellPageAnimations;
            ExScoring.suppressShellPageAnimations = false;
            ExScoring.ShowLaunchPanel();
            ExScoring.suppressShellPageAnimations = prevSuppress;

            // Restore the same state we left: blank/no-preview if nothing was selected,
            // otherwise the populated song info.
            if (string.IsNullOrEmpty(ExScoring.selectedSong))
                ExScoring.SetLaunchPanelContentVisible(false);
            else
                ExScoring.UpdateLaunchPanelInfo();

            launchPage = null;
        }
    }
}