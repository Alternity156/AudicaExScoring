using System;
using System.Collections;
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

        // True while EnsureIndicatorWhenReady is polling, so repeated song-page entries don't stack coroutines.
        private static bool indicatorRetryRunning = false;

        // How long (in frames) to keep polling for the indicator source after the toggle is created.
        private const int IndicatorRetryMaxFrames = 600;

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
                // Stay hidden while the song list is showing the Options menu.
                toggle.SetActive(!FolderRowManager.InGlobalOptions);
                ApplyCurrentSetting("song-page re-entry");
                return;
            }

            // The previous toggle (if any) was destroyed with its scene — drop the stale indicator reference too.
            indicator = null;

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

            // The clone carries over whatever highlight state the PracticeToggle had at that moment. Hide it
            // right away so the button never shows a state that isn't ours while our indicator is pending.
            Transform inherited = toggle.transform.Find("SelectedIndicator");
            if (inherited != null) inherited.gameObject.SetActive(false);

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
            // Stay hidden while the song list is showing the Options menu.
            toggle.SetActive(!FolderRowManager.InGlobalOptions);

            // Read the current setting and apply the highlight accordingly.
            ApplyCurrentSetting("creation");
        }

        /// <summary>Show/hide the toggle (hidden while the song list is in the Options menu).</summary>
        public static void SetVisible(bool visible)
        {
            if (toggle == null) return;
            toggle.SetActive(visible);
            if (visible) ApplyCurrentSetting("shown");
        }

        private static void OnShot()
        {
            bool now = !SongRequestIntegration.RequestsEnabled;
            SongRequestIntegration.SetRequestsEnabled(now);
            ApplyCurrentSetting("shot");
        }

        /// <summary>
        /// Makes the highlight match the current RequestsEnabled setting. If the indicator can't be built
        /// yet (its source isn't loaded this early), starts a short poll that builds it as soon as the
        /// source shows up and then applies the setting — instead of waiting for the next song-page entry.
        /// </summary>
        private static void ApplyCurrentSetting(string reason)
        {
            if (toggle == null) return;

            EnsureIndicator();

            if (indicator == null)
            {
                MelonLogger.Log($"[QueueToggle] ({reason}) indicator source not ready; polling for it.");
                if (!indicatorRetryRunning)
                {
                    indicatorRetryRunning = true;
                    MelonCoroutines.Start(EnsureIndicatorWhenReady());
                }
                return;
            }

            SyncIndicator();
            MelonLogger.Log($"[QueueToggle] ({reason}) highlight set to {(SongRequestIntegration.RequestsEnabled ? "ON" : "OFF")}.");
        }

        /// <summary>Polls until the indicator can be created (or the toggle is gone), then applies the setting.</summary>
        private static IEnumerator EnsureIndicatorWhenReady()
        {
            int frames = 0;
            while (toggle != null && indicator == null && frames < IndicatorRetryMaxFrames)
            {
                frames++;
                yield return null;
                if (toggle == null) break; // destroyed by a scene change while we were waiting
                EnsureIndicator();
            }

            indicatorRetryRunning = false;

            if (toggle == null)
            {
                MelonLogger.Log("[QueueToggle] toggle destroyed while waiting for the indicator; will rebuild on next song-page entry.");
                yield break;
            }

            if (indicator == null)
            {
                MelonLogger.Log($"[QueueToggle] indicator source still missing after {frames} frames; will retry next song-page entry.");
                yield break;
            }

            SyncIndicator();
            MelonLogger.Log($"[QueueToggle] indicator ready after {frames} frame(s); highlight set to {(SongRequestIntegration.RequestsEnabled ? "ON" : "OFF")}.");
        }

        /// <summary>
        /// Finds the SelectedIndicator source the difficulty/favorite buttons use. Uses explicit Unity null
        /// checks (not ??, which only checks the managed wrapper) so a source destroyed by a menu rebuild is
        /// never handed back, and reaches it through the launch panel's transform so it is found even while
        /// inactive.
        /// </summary>
        private static GameObject FindIndicatorSource()
        {
            GameObject cached = ExScoring.difficultyIndicatorSource;
            if (cached != null) return cached;

            var launchCenter = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center");
            if (launchCenter == null) return null;

            Transform t = launchCenter.transform.Find("play/SelectedIndicator");
            return t != null ? t.gameObject : null;
        }

        /// <summary>Create the SelectedIndicator from the same source the difficulty/favorite buttons use.</summary>
        private static void EnsureIndicator()
        {
            if (indicator != null || toggle == null) return;

            GameObject source = FindIndicatorSource();
            if (source == null) return; // not available yet — EnsureIndicatorWhenReady keeps trying

            // Drop the cloned toggle's own indicator (if any), then add ours.
            Transform existing = toggle.transform.Find("SelectedIndicator");
            if (existing != null) GameObject.Destroy(existing.gameObject);

            indicator = GameObject.Instantiate(source, toggle.transform);
            indicator.name = "SelectedIndicator";
            indicator.transform.localPosition = new Vector3(0f, 0f, -0.005f);
            indicator.transform.localRotation = Quaternion.identity;
            indicator.transform.localScale = indicatorScale;

            // Same white tint the favorite/marathon indicators use, so it can't inherit a stray colour
            // from whatever state the source was in when cloned.
            MeshRenderer renderer = indicator.GetComponent<MeshRenderer>();
            if (renderer != null)
                renderer.material.color = new Color(1f, 1f, 1f, 1f);

            // Start hidden; SyncIndicator decides whether it should be lit.
            indicator.SetActive(false);
        }

        /// <summary>Lit when the request queue is enabled.</summary>
        private static void SyncIndicator()
        {
            if (indicator != null)
                indicator.SetActive(SongRequestIntegration.RequestsEnabled);
        }
    }
}