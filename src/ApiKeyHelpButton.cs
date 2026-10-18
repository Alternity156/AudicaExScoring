using System;
using MelonLoader;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// Helper buttons on the Main page's release-notes panel, shown whenever the configured
    /// AudicaEX.org API key is missing or invalid. Visibility is driven from AudicaExStatus, since
    /// that's the only place that knows whether the key is missing, invalid, valid, or the server is
    /// just unreachable.
    ///
    /// Two layouts:
    ///   Normal — "Open AudicaEX.org" (-5, 6) and "Paste API Key" (5, 6) on top, "Link Account" (0, 3)
    ///            centered below.
    ///   Link   — while an account link (ApiContract.md Section 16, AccountLink.cs) is running, the three
    ///            normal buttons hide and a pair appears at the Link button's height, as far apart as the
    ///            top two: left (-5, 3) and right (5, 3).
    ///              Waiting for approval: left "Open AudicaEX.org", right "New Code".
    ///              Confirming:           left "Yes",               right "No".
    ///            Yes is on the website button's side on purpose: "New Code" is the button a player might
    ///            be shooting repeatedly, so an approval landing mid-shot can only hit "No".
    /// </summary>
    internal static class ApiKeyHelpButtons
    {
        private const string OpenSiteUrl = "https://audicaex.org";
        private const string BackButtonPath = "menu/ShellPage_Song/page/backParent/back";

        public const string OpenSiteLabel = "Open AudicaEX.org";
        public const string PasteLabel = "Paste API Key";
        public const string LinkLabel = "Link Account";
        public const string NewCodeLabel = "New Code";
        public const string YesLabel = "Yes";
        public const string NoLabel = "No";

        // Normal layout
        private static GameObject openSiteButton;
        private static GameObject pasteKeyButton;
        private static GameObject linkButton;

        // Link layout
        private static GameObject linkLeftButton;
        private static GameObject linkRightButton;

        private static readonly Vector3 OpenSiteButtonPos = new Vector3(-5f, 6f, 0f);
        private static readonly Vector3 PasteKeyButtonPos = new Vector3(5f, 6f, 0f);
        private static readonly Vector3 LinkButtonPos = new Vector3(0f, 3f, 0f);
        private static readonly Vector3 LinkLeftButtonPos = new Vector3(-5f, 3f, 0f);
        private static readonly Vector3 LinkRightButtonPos = new Vector3(5f, 3f, 0f);
        private static readonly Vector3 ButtonRot = new Vector3(0f, 0f, 0f);
        private static readonly Vector3 ButtonScale = new Vector3(1.75f, 1.75f, 1.75f);

        /// <summary>
        /// Creates the buttons under the given parent (ShellPanel_Left) if they don't already exist.
        /// Idempotent - safe to call every time the Main page is entered. Does not affect visibility of
        /// the normal layout; that's handled separately by SetVisible so it can be driven by
        /// AudicaExStatus's async result. The link-layout pair starts hidden.
        /// </summary>
        public static void Setup(Transform parent)
        {
            if (parent == null) return;

            if (openSiteButton == null)
                openSiteButton = CreateButton(parent, OpenSiteLabel, OnOpenSiteButtonShot, OpenSiteButtonPos);

            if (pasteKeyButton == null)
                pasteKeyButton = CreateButton(parent, PasteLabel, OnPasteKeyButtonShot, PasteKeyButtonPos);

            if (linkButton == null)
                linkButton = CreateButton(parent, LinkLabel, OnLinkButtonShot, LinkButtonPos);

            if (linkLeftButton == null)
            {
                linkLeftButton = CreateButton(parent, OpenSiteLabel, OnLinkLeftButtonShot, LinkLeftButtonPos);
                if (linkLeftButton != null && !AccountLink.IsActive) linkLeftButton.SetActive(false);
            }

            if (linkRightButton == null)
            {
                linkRightButton = CreateButton(parent, NewCodeLabel, OnLinkRightButtonShot, LinkRightButtonPos);
                if (linkRightButton != null && !AccountLink.IsActive) linkRightButton.SetActive(false);
            }
        }

        private static GameObject CreateButton(Transform parent, string label, Action onShot, Vector3 position)
        {
            GameObject refButton = GameObject.Find(BackButtonPath);
            if (refButton == null)
            {
                DebugLog.Log($"ApiKeyHelpButtons: back button template not found, skipping '{label}' button");
                return null;
            }

            GameObject button = GameObject.Instantiate(refButton, parent);
            ButtonUtils.InitButton(button, label, new Action(onShot), position, ButtonRot);
            button.transform.localScale = ButtonScale;
            return button;
        }

        /// <summary>
        /// Shows or hides the normal layout (Open site, Paste, Link). Always hides the link-layout pair:
        /// AudicaExStatus only calls this when no account link is running.
        /// </summary>
        public static void SetVisible(bool visible)
        {
            SetActive(openSiteButton, visible);
            SetActive(pasteKeyButton, visible);
            SetActive(linkButton, visible);
            SetActive(linkLeftButton, false);
            SetActive(linkRightButton, false);
        }

        /// <summary>
        /// Switches to the link layout: hides the three normal buttons and shows the pair at the Link
        /// button's height with the given labels. Called by AccountLink at each step of the flow.
        /// </summary>
        public static void ShowLinkLayout(string leftLabel, string rightLabel)
        {
            SetActive(openSiteButton, false);
            SetActive(pasteKeyButton, false);
            SetActive(linkButton, false);

            if (linkLeftButton != null) ButtonUtils.UpdateButtonLabel(linkLeftButton, leftLabel);
            if (linkRightButton != null) ButtonUtils.UpdateButtonLabel(linkRightButton, rightLabel);
            SetActive(linkLeftButton, true);
            SetActive(linkRightButton, true);
        }

        /// <summary>
        /// Back to the normal layout with all three normal buttons showing. Called by AccountLink when the
        /// flow ends — linking only runs while those buttons were showing (key missing or invalid). If the
        /// flow ended because a key was saved, AudicaExStatus's re-check hides them again once confirmed.
        /// </summary>
        public static void ShowNormalLayout()
        {
            SetVisible(true);
        }

        private static void SetActive(GameObject button, bool active)
        {
            if (button != null) button.SetActive(active);
        }

        private static void OpenSite()
        {
            DebugLog.Log("[ExScoring] ApiKeyHelpButtons: opening " + OpenSiteUrl);
            Application.OpenURL(OpenSiteUrl);
        }

        private static void OnOpenSiteButtonShot()
        {
            OpenSite();
        }

        private static void OnPasteKeyButtonShot()
        {
            // Overwrites any existing key, valid or not - that's the point of this button. Also
            // re-checks the key (OptionsMenuFunctions.SetApiKey).
            OptionsMenuFunctions.PasteApiKeyFromClipboard();
        }

        private static void OnLinkButtonShot()
        {
            AccountLink.Begin();
        }

        /// <summary>Link layout, left: "Open AudicaEX.org" while waiting, "Yes" while confirming.</summary>
        private static void OnLinkLeftButtonShot()
        {
            if (AccountLink.IsConfirming)
                AccountLink.Accept();
            else
                OpenSite();
        }

        /// <summary>Link layout, right: "New Code" while waiting, "No" while confirming.</summary>
        private static void OnLinkRightButtonShot()
        {
            if (AccountLink.IsConfirming)
                AccountLink.Decline();
            else
                AccountLink.Begin(); // cancels the current code and gets a fresh one
        }
    }
}