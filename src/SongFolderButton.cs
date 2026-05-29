using System;
using System.Collections;
using MelonLoader;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// Creates and manages the "Folder" button on the song select page.
    /// Visible only while the folder filter is active, so the player can
    /// see which folder is selected and tap to change it.
    /// </summary>
    internal static class SongFolderButton
    {
        private static GameObject folderButton;

        // Positioned just above the SelectPlaylistButton (which sits at y=15.1)
        private static readonly Vector3 buttonPos = new Vector3(0f, 17.7f, 0f);
        private static readonly Vector3 buttonRot = new Vector3(0f, 0f, 0f);

        private static bool isActive = false;

        public static void CreateFolderButton()
        {
            if (folderButton != null)
                return;
            MelonCoroutines.Start(CreateButton());
        }

        private static IEnumerator CreateButton()
        {
            while (EnvironmentLoader.I.IsSwitching())
            {
                yield return new WaitForSecondsRealtime(0.5f);
            }

            var backButton = GameObject.Find("menu/ShellPage_Song/page/backParent/back");
            if (backButton == null) yield break;

            folderButton = GameObject.Instantiate(backButton, backButton.transform.parent);
            ButtonUtils.InitButton(folderButton, GetButtonLabel(), new Action(OnFolderButtonShot),
                                   buttonPos, buttonRot);
            folderButton.SetActive(isActive);
        }

        private static void OnFolderButtonShot()
        {
            SongFolderPanel.isOpen = true;
            MenuState.I.GoToSettingsPage();
        }

        /// <summary>
        /// Updates the button label to reflect the currently selected folder.
        /// </summary>
        public static void RefreshLabel()
        {
            if (folderButton == null) return;
            ButtonUtils.UpdateButtonLabel(folderButton, GetButtonLabel());
        }

        public static void ShowFolderButton()
        {
            isActive = true;
            folderButton?.SetActive(true);
            RefreshLabel();
        }

        public static void HideFolderButton()
        {
            isActive = false;
            folderButton?.SetActive(false);
        }

        private static string GetButtonLabel()
        {
            return SongFolderManager.selectedFolder != null
                ? $"Folder: {SongFolderManager.selectedFolder}"
                : "Select Folder";
        }
    }
}