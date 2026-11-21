using System;
using TMPro;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// One-time question on the Main page's release-notes panel: "Download community maps for your
    /// songs automatically?" with Yes / No buttons. Community map auto download is opt in:
    /// Config.AutoDownloadCommunityMaps stays off until this is answered (Config.CommunityMapsOptInAsked),
    /// so the first boot never downloads anything.
    ///
    ///   Yes — turns Auto Download on, saves, and starts a background check right away.
    ///   No  — saves it as off.
    /// Either way the question never comes back. Changing Options > Data > Community Maps > Auto
    /// Download also counts as an answer (Config.UpdateAutoDownloadCommunityMaps sets the flag).
    ///
    /// It sits where the MOTD link button goes (MotdButton.cs), which stays hidden while the
    /// question is up. Created by ReleaseNotesPanel.Setup() on every Main page visit (idempotent).
    /// </summary>
    internal static class CommunityMapsOptInPrompt
    {
        private const string BackButtonPath = "menu/ShellPage_Song/page/backParent/back";

        private const string QuestionText = "Download community maps for\nyour songs automatically?";
        private const string YesLabel = "Yes";
        private const string NoLabel = "No";

        // Local positions under ShellPanel_Left: question just above the MOTD button's spot (y -12),
        // Yes/No below it as far apart as the API key help buttons.
        private static readonly Vector3 QuestionPos = new Vector3(0f, -11f, 0f);
        private static readonly Vector3 YesButtonPos = new Vector3(-5f, -13.5f, 0f);
        private static readonly Vector3 NoButtonPos = new Vector3(5f, -13.5f, 0f);
        private static readonly Vector3 ButtonRot = new Vector3(0f, 0f, 0f);
        private static readonly Vector3 ButtonScale = new Vector3(1.75f, 1.75f, 1.75f);

        private static GameObject question;
        private static GameObject yesButton;
        private static GameObject noButton;

        /// <summary>True until the player has answered (here or in Options).</summary>
        public static bool IsShowing => !Config.CommunityMapsOptInAsked;

        /// <summary>
        /// Creates the question and buttons under the given parent (ShellPanel_Left) if they don't
        /// exist yet, but only while the question is unanswered. Then applies visibility.
        /// </summary>
        public static void Setup(Transform parent)
        {
            if (parent == null) return;

            if (IsShowing)
            {
                GameObject refButton = GameObject.Find(BackButtonPath);
                if (refButton == null)
                {
                    DebugLog.Log("CommunityMapsOptInPrompt: back button template not found, skipping");
                    return;
                }

                if (question == null) question = CreateQuestion(parent, refButton);
                if (yesButton == null) yesButton = CreateButton(parent, refButton, YesLabel, OnYesShot, YesButtonPos);
                if (noButton == null) noButton = CreateButton(parent, refButton, NoLabel, OnNoShot, NoButtonPos);
            }

            Refresh();
        }

        /// <summary>Shows or hides everything from the saved answer, and re-applies the MOTD button.</summary>
        public static void Refresh()
        {
            bool show = IsShowing;
            SetActive(question, show);
            SetActive(yesButton, show);
            SetActive(noButton, show);
            MotdButton.Refresh();
        }

        private static GameObject CreateButton(Transform parent, GameObject refButton, string label, Action onShot, Vector3 position)
        {
            GameObject button = GameObject.Instantiate(refButton, parent);
            button.name = "ExCommunityMapsOptIn_" + label;
            ButtonUtils.InitButton(button, label, new Action(onShot), position, ButtonRot);
            button.transform.localScale = ButtonScale;
            return button;
        }

        /// <summary>
        /// The question line: a copy of the back button's label text (same font and look as the
        /// buttons around it), inside a holder scaled like the buttons so it renders at the same size.
        /// </summary>
        private static GameObject CreateQuestion(Transform parent, GameObject refButton)
        {
            TextMeshPro refText = refButton.GetComponentInChildren<TextMeshPro>(true);
            if (refText == null)
            {
                DebugLog.Log("CommunityMapsOptInPrompt: back button has no TextMeshPro, skipping the question text");
                return null;
            }

            GameObject holder = new GameObject("ExCommunityMapsOptIn_Question");
            holder.transform.SetParent(parent, false);
            holder.layer = parent.gameObject.layer;
            holder.transform.localPosition = QuestionPos;
            holder.transform.localRotation = Quaternion.Euler(ButtonRot);
            holder.transform.localScale = ButtonScale;

            // Keep the label's scale and rotation relative to its button, so it matches the button text.
            Transform refButtonT = refButton.transform;
            Transform refTextT = refText.transform;
            Vector3 buttonScale = refButtonT.lossyScale;
            Vector3 textScale = refTextT.lossyScale;
            Vector3 relativeScale = new Vector3(
                buttonScale.x != 0f ? textScale.x / buttonScale.x : 1f,
                buttonScale.y != 0f ? textScale.y / buttonScale.y : 1f,
                buttonScale.z != 0f ? textScale.z / buttonScale.z : 1f);
            Quaternion relativeRotation = Quaternion.Inverse(refButtonT.rotation) * refTextT.rotation;

            GameObject label = GameObject.Instantiate(refText.gameObject, holder.transform);
            label.name = "Text";
            label.transform.localPosition = Vector3.zero;
            label.transform.localRotation = relativeRotation;
            label.transform.localScale = relativeScale;

            Localizer localizer = label.GetComponent<Localizer>();
            if (localizer != null) GameObject.Destroy(localizer);

            TextMeshPro text = label.GetComponent<TextMeshPro>();
            text.text = QuestionText;
            text.richText = false;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Overflow;
            text.alignment = TextAlignmentOptions.Center;

            return holder;
        }

        private static void OnYesShot()
        {
            if (!IsShowing) return;
            DebugLog.Log("[ExScoring] CommunityMapsOptInPrompt: opted in to community map auto download");
            Config.UpdateAutoDownloadCommunityMaps(true);
            Refresh();
            PlaylistUtil.Popup("Community maps will download automatically (Options > Data to change)");
            CommunityMapSync.StartBackgroundSync("opted in");
        }

        private static void OnNoShot()
        {
            if (!IsShowing) return;
            DebugLog.Log("[ExScoring] CommunityMapsOptInPrompt: declined community map auto download");
            Config.UpdateAutoDownloadCommunityMaps(false);
            Refresh();
            PlaylistUtil.Popup("Community maps won't download automatically (Options > Data to change)");
        }

        private static void SetActive(GameObject obj, bool active)
        {
            if (obj != null && obj.activeSelf != active) obj.SetActive(active);
        }
    }
}