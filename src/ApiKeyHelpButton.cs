using System;
using MelonLoader;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// Two helper buttons shown on the Main page's release-notes panel whenever the configured
    /// AudicaEX.org API key is missing or invalid: one opens audicaex.org in the default browser,
    /// the other pastes a key from the clipboard (overwriting any existing one, including a bad
    /// one). Visibility is driven entirely from AudicaExStatus.Refresh, since that's the only place
    /// that knows whether the key is missing, invalid, valid, or the server is just unreachable.
    /// </summary>
    internal static class ApiKeyHelpButtons
    {
        private const string OpenSiteUrl = "https://audicaex.org";
        private const string BackButtonPath = "menu/ShellPage_Song/page/backParent/back";

        private static GameObject openSiteButton;
        private static GameObject pasteKeyButton;

        private static readonly Vector3 OpenSiteButtonPos = new Vector3(-5f, 6f, 0f);
        private static readonly Vector3 PasteKeyButtonPos = new Vector3(5f, 6f, 0f);
        private static readonly Vector3 ButtonRot = new Vector3(0f, 0f, 0f);
        private static readonly Vector3 ButtonScale = new Vector3(1.75f, 1.75f, 1.75f);

        /// <summary>
        /// Creates both buttons under the given parent (ShellPanel_Left) if they don't already
        /// exist. Idempotent - safe to call every time the Main page is entered. Does not affect
        /// visibility; that's handled separately by SetVisible so it can be driven by
        /// AudicaExStatus's async result.
        /// </summary>
        public static void Setup(Transform parent)
        {
            if (parent == null) return;

            if (openSiteButton == null)
            {
                GameObject refButton = GameObject.Find(BackButtonPath);
                if (refButton == null)
                {
                    MelonLogger.Log("ApiKeyHelpButtons: back button template not found, skipping OpenSite button");
                }
                else
                {
                    openSiteButton = GameObject.Instantiate(refButton, parent);
                    ButtonUtils.InitButton(openSiteButton, "Open AudicaEX.org",
                        new Action(OnOpenSiteButtonShot), OpenSiteButtonPos, ButtonRot);
                    openSiteButton.transform.localScale = ButtonScale;
                }
            }

            if (pasteKeyButton == null)
            {
                GameObject refButton = GameObject.Find(BackButtonPath);
                if (refButton == null)
                {
                    MelonLogger.Log("ApiKeyHelpButtons: back button template not found, skipping PasteKey button");
                }
                else
                {
                    pasteKeyButton = GameObject.Instantiate(refButton, parent);
                    ButtonUtils.InitButton(pasteKeyButton, "Paste API Key",
                        new Action(OnPasteKeyButtonShot), PasteKeyButtonPos, ButtonRot);
                    pasteKeyButton.transform.localScale = ButtonScale;
                }
            }
        }

        /// <summary>Shows or hides both buttons together. No-op for any button not yet created.</summary>
        public static void SetVisible(bool visible)
        {
            if (openSiteButton != null) openSiteButton.SetActive(visible);
            if (pasteKeyButton != null) pasteKeyButton.SetActive(visible);
        }

        private static void OnOpenSiteButtonShot()
        {
            MelonLogger.Log("[ExScoring] ApiKeyHelpButtons: opening " + OpenSiteUrl);
            Application.OpenURL(OpenSiteUrl);
        }

        private static void OnPasteKeyButtonShot()
        {
            // Overwrites any existing key, valid or not - that's the point of this button.
            OptionsMenuFunctions.PasteApiKeyFromClipboard();
            AudicaExStatus.RefreshLast();
        }
    }
}