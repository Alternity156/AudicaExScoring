using System;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// The message of the day's optional link button on the Main page's release-notes panel
    /// (ApiContract Section 20.6). Shown only when the MOTD itself is showing (valid API key, no
    /// account link running) and has a button; AudicaExStatus decides that and calls Show/Hide. Shooting it opens the link in the
    /// player's browser.
    ///
    /// The wanted state (label + link, or hidden) is remembered here, so it doesn't matter whether the
    /// MOTD arrives before or after the button object is created by ReleaseNotesPanel.Setup().
    /// </summary>
    internal static class MotdButton
    {
        private const string BackButtonPath = "menu/ShellPage_Song/page/backParent/back";

        // Local position under ShellPanel_Left, below the panel text.
        private static readonly Vector3 ButtonPos = new Vector3(0f, -12f, 0f);
        private static readonly Vector3 ButtonRot = new Vector3(0f, 0f, 0f);
        private static readonly Vector3 ButtonScale = new Vector3(1.75f, 1.75f, 1.75f);

        private static GameObject button;

        // Wanted state. Both null = hidden.
        private static string wantedLabel;
        private static string wantedUrl;

        /// <summary>
        /// Creates the button under the given parent (ShellPanel_Left) if it doesn't exist yet, then
        /// applies the wanted state. Idempotent - safe to call every time the Main page is entered.
        /// </summary>
        public static void Setup(Transform parent)
        {
            if (parent == null) return;

            if (button == null)
            {
                GameObject refButton = GameObject.Find(BackButtonPath);
                if (refButton == null)
                {
                    DebugLog.Log("MotdButton: back button template not found, skipping");
                    return;
                }

                button = GameObject.Instantiate(refButton, parent);
                ButtonUtils.InitButton(button, "", new Action(OnShot), ButtonPos, ButtonRot);
                button.transform.localScale = ButtonScale;
            }

            Apply();
        }

        /// <summary>Shows the button with this label, opening this link when shot.</summary>
        public static void Show(string label, string url)
        {
            wantedLabel = label;
            wantedUrl = url;
            Apply();
        }

        public static void Hide()
        {
            wantedLabel = null;
            wantedUrl = null;
            Apply();
        }

        /// <summary>Only absolute https:// links are ever opened.</summary>
        public static bool IsAllowedUrl(string url)
        {
            return !string.IsNullOrEmpty(url)
                && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        private static void Apply()
        {
            if (button == null) return;

            bool show = !string.IsNullOrEmpty(wantedLabel) && IsAllowedUrl(wantedUrl);
            if (show)
            {
                // The label is plain text: noparse keeps any tags in it from being read.
                ButtonUtils.UpdateButtonLabel(button, "<noparse>" + wantedLabel + "</noparse>");
            }
            button.SetActive(show);
        }

        private static void OnShot()
        {
            string url = wantedUrl;
            if (!IsAllowedUrl(url))
            {
                DebugLog.Log("[ExScoring] MotdButton: link refused (not https)");
                return;
            }

            DebugLog.Log("[ExScoring] MotdButton: opening " + url);
            Application.OpenURL(url);
        }
    }
}