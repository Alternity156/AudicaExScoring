using System.Collections.Generic;
using UnityEngine;

namespace ExScoringMod
{
    public partial class ExScoring
    {
        private static readonly List<GameObject> hiddenLaunchContent = new List<GameObject>();
        private static bool launchContentHidden = false;
        private static bool restorePreviewOnShow = false;
        private static Transform launchCenter;

        /// <summary>
        /// Show/hide the song-specific launch panel content (title/artist/mapper/tempo + target
        /// labels, album art, intensity/heatmap graphs, play &amp; difficulty buttons, toggles).
        /// The Glass background and the PanelFrame (frame panels + highlights) always stay visible.
        ///
        /// While blank we also uncheck the song-preview toggle, but only if it was on (so a box the
        /// user deliberately unchecked is left alone), and re-check it when content returns.
        ///
        /// The game re-activates its own play/difficulty buttons after we hide them, so the blank
        /// state is re-asserted every frame via EnforceLaunchPanelBlank(). We restore only the
        /// objects we deactivated, so Audica's permanently-hidden elements are never re-activated.
        /// </summary>
        public static void SetLaunchPanelContentVisible(bool visible)
        {
            if (visible)
            {
                for (int i = 0; i < hiddenLaunchContent.Count; i++)
                    if (hiddenLaunchContent[i] != null) hiddenLaunchContent[i].SetActive(true);
                hiddenLaunchContent.Clear();
                launchContentHidden = false;

                // Re-check song preview only if WE turned it off.
                if (restorePreviewOnShow)
                {
                    // NOTE: if SongPreviewOnLaunchScreen is a plain bool (no wrapper), drop ".mVal".
                    if (PlayerPreferences.I != null)
                        PlayerPreferences.I.SongPreviewOnLaunchScreen.mVal = true;
                    restorePreviewOnShow = false;
                }
                return;
            }

            bool firstHide = !launchContentHidden;
            HideContentNow();
            if (!launchContentHidden) return; // center not found yet

            if (firstHide)
            {
                if (PlayerPreferences.I != null && PlayerPreferences.I.SongPreviewOnLaunchScreen.mVal)
                {
                    PlayerPreferences.I.SongPreviewOnLaunchScreen.mVal = false;
                    restorePreviewOnShow = true;
                }
                else
                {
                    restorePreviewOnShow = false;
                }
            }
        }

        /// <summary>
        /// Re-assert the blank state each frame. Call from OnLateUpdate (after the game's own
        /// Update) so the game's re-activated play/difficulty buttons are hidden again with no
        /// visible flicker. Cheap: no GameObject.Find, just a child scan of the cached center.
        /// </summary>
        public static void EnforceLaunchPanelBlank()
        {
            if (!launchContentHidden) return;
            HideContentNow();
        }

        private static void HideContentNow()
        {
            Transform t = GetLaunchCenter();
            if (t == null) return;

            for (int i = 0; i < t.childCount; i++)
            {
                GameObject child = t.GetChild(i).gameObject;
                string n = child.name;
                if (n == "Glass" || n == "PanelFrame") continue; // keep background, frame, highlights
                if (!child.activeSelf) continue;

                child.SetActive(false);
                if (!hiddenLaunchContent.Contains(child)) hiddenLaunchContent.Add(child);
            }

            launchContentHidden = true;
        }

        private static Transform GetLaunchCenter()
        {
            if (launchCenter == null)
            {
                GameObject go = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center");
                if (go != null) launchCenter = go.transform;
            }
            return launchCenter;
        }
    }
}