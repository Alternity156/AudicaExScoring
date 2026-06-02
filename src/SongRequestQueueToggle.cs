using System;
using MelonLoader;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

namespace ExScoringMod
{
    /// <summary>
    /// Song-page on/off toggle for SongRequest's request queue (RequestsEnabled). Lives next to the
    /// search field and uses the same SelectedIndicator as the difficulty/favorite buttons (lit = taking
    /// requests) for visual consistency. Only created when SongRequest is installed.
    ///
    /// Created/synced from the SongPage state hook (alongside SongSearchField.CreateField), so it follows
    /// the search field's lifecycle and survives scene changes.
    /// </summary>
    internal static class SongRequestQueueToggle
    {
        private static GameObject toggle;
        private static GameObject indicator;

        // Tunable placement — to the right of the search field (which sits at x=-5.98, y=13). Adjust to taste,
        // like the other *UISetup position constants.
        private static Vector3 togglePos = new Vector3(14f, 11.5f, 0f);
        private static Vector3 toggleScale = new Vector3(0.75f, 0.75f, 0.75f);
        private static Vector3 indicatorScale = new Vector3(0.675f, 0.75f, 1f);

        public static void Create()
        {
            if (!SongRequestIntegration.IsPresent) return;

            if (toggle != null) // Unity-null: recreated after a scene change
            {
                EnsureIndicator(); // attach the indicator if the source wasn't available the first time
                SyncIndicator();
                return;
            }

            var parent = GameObject.Find("menu/ShellPage_Song/page/ShellPanel_Center");
            if (parent == null) { MelonLogger.Log("[QueueToggle] song-page center not found; will retry next entry."); return; }

            // The PracticeToggle may be inactive (launch panel blanked when no song is selected), so reach it
            // through its (always-active) parent with transform.Find — that traverses inactive children,
            // whereas GameObject.Find only sees active ones.
            var launchCenter = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center");
            Transform refT = launchCenter != null ? launchCenter.transform.Find("NoFailPracticeToggle/PracticeToggle") : null;
            if (refT == null) { MelonLogger.Log("[QueueToggle] PracticeToggle source not found; will retry next entry."); return; }

            toggle = GameObject.Instantiate(refT.gameObject, parent.transform);
            toggle.name = "ExScoring_QueueToggle";

            Localizer loc = toggle.GetComponentInChildren<Localizer>();
            if (loc != null) GameObject.Destroy(loc);

            TextMeshPro label = toggle.GetComponentInChildren<TextMeshPro>();
            if (label != null) label.text = "Take Requests";

            GunButton gb = toggle.GetComponentInChildren<GunButton>();
            if (gb != null)
            {
                gb.destroyOnShot = false;
                gb.disableOnShot = false;
                gb.doMeshExplosion = false;
                gb.doParticles = false;
                gb.onHitEvent = new UnityEvent();
                gb.onHitEvent.AddListener(new Action(OnShot));
            }

            toggle.transform.localPosition = togglePos;
            toggle.transform.localRotation = Quaternion.identity;
            toggle.transform.localScale = toggleScale;
            toggle.SetActive(true);

            EnsureIndicator();
            SyncIndicator();
        }

        private static void OnShot()
        {
            bool now = !SongRequestIntegration.RequestsEnabled;
            SongRequestIntegration.SetRequestsEnabled(now);
            SongRequestIntegration.EmitQueueStateChanged(now); // chat: "Request queue enabled/disabled."
            SyncIndicator();
        }

        /// <summary>Create the SelectedIndicator from the same source the difficulty/favorite buttons use.</summary>
        private static void EnsureIndicator()
        {
            if (indicator != null || toggle == null) return;

            GameObject source = ExScoring.difficultyIndicatorSource
                ?? GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center/play/SelectedIndicator");
            if (source == null) return; // not available yet — try again on a later song-page entry

            // Drop the cloned toggle's own indicator (if any), then add ours.
            Transform existing = toggle.transform.Find("SelectedIndicator");
            if (existing != null) GameObject.Destroy(existing.gameObject);

            indicator = GameObject.Instantiate(source, toggle.transform);
            indicator.name = "SelectedIndicator";
            indicator.transform.localPosition = new Vector3(0f, 0f, -0.005f);
            indicator.transform.localRotation = Quaternion.identity;
            indicator.transform.localScale = indicatorScale;
        }

        /// <summary>Lit when the request queue is enabled.</summary>
        private static void SyncIndicator()
        {
            if (indicator != null)
                indicator.SetActive(SongRequestIntegration.RequestsEnabled);
        }
    }
}