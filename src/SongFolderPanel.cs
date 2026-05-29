using System;
using TMPro;
using UnityEngine;
using MelonLoader;

namespace ExScoringMod
{
    /// <summary>
    /// Displays the list of available song folders in the settings page,
    /// letting the player pick one to filter the song list.
    /// Mirrors the pattern used by PlaylistSelectPanel.
    /// </summary>
    internal static class SongFolderPanel
    {
        private static OptionsMenu primaryMenu;

        /// <summary>Set to true before GoToSettingsPage() so the hook knows to show this panel.</summary>
        public static bool isOpen = false;

        public static void SetMenu(OptionsMenu optionsMenu)
        {
            primaryMenu = optionsMenu;
        }

        public static void GoToPanel()
        {
            primaryMenu.ShowPage(OptionsMenu.Page.Customization);
            CleanUpPage(primaryMenu);
            AddButtons(primaryMenu);
            primaryMenu.screenTitle.text = "Song Folders";
        }

        public static void Cancel()
        {
            isOpen = false;
            MenuState.I.GoToSongPage();
        }

        private static void AddButtons(OptionsMenu optionsMenu)
        {
            if (SongFolderManager.availableFolders.Count == 0)
            {
                var empty = optionsMenu.AddHeader(0, "No folders found");
                optionsMenu.scrollable.AddRow(empty);

                var backBtn = optionsMenu.AddButton(0, "Back", new Action(Cancel), null, "Return to song list");
                optionsMenu.scrollable.AddRow(backBtn.gameObject);
                return;
            }

            foreach (string folderName in SongFolderManager.availableFolders)
            {
                // Capture for closure
                string capturedName = folderName;

                var nameBlock = optionsMenu.AddTextBlock(0, folderName);
                var tmp = nameBlock.transform.GetChild(0).GetComponent<TextMeshPro>();
                tmp.fontSizeMax = 32;
                tmp.fontSizeMin = 8;
                optionsMenu.scrollable.AddRow(nameBlock.gameObject);

                var selectBtn = optionsMenu.AddButton(0, "Select", new Action(() =>
                {
                    SongFolderManager.SelectFolder(capturedName);
                    FilterPanel.ResetFilterState();
                    SongFolderButton.RefreshLabel();
                    isOpen = false;
                    MelonCoroutines.Start(SelectFolderAndAutoSelect());
                }), null, $"Show songs in {folderName}", optionsMenu.buttonPrefab);

                optionsMenu.scrollable.AddRow(selectBtn.gameObject);
            }

            // Clear folder selection
            var header = optionsMenu.AddHeader(0, "Options");
            optionsMenu.scrollable.AddRow(header);

            var clearBtn = optionsMenu.AddButton(0, "Clear Folder Filter", new Action(() =>
            {
                SongFolderManager.ClearFolder();
                FilterPanel.DisableCustomFilters();
                FilterPanel.ResetFilterState();
                SongFolderButton.HideFolderButton();
                isOpen = false;
                MenuState.I.GoToSongPage();
            }), null, "Remove the folder filter and show all songs");
            optionsMenu.scrollable.AddRow(clearBtn.gameObject);

            var backButton = optionsMenu.AddButton(0, "Back", new Action(Cancel), null, "Return to song list");
            optionsMenu.scrollable.AddRow(backButton.gameObject);
        }

        private static void CleanUpPage(OptionsMenu optionsMenu)
        {
            Transform optionsTransform = optionsMenu.transform;
            for (int i = 0; i < optionsTransform.childCount; i++)
            {
                Transform child = optionsTransform.GetChild(i);
                if (child.gameObject.name.Contains("(Clone)"))
                {
                    GameObject.Destroy(child.gameObject);
                }
            }
            optionsMenu.mRows.Clear();
            optionsMenu.scrollable.ClearRows();
            optionsMenu.scrollable.mRows.Clear();
        }

        private static System.Collections.IEnumerator SelectFolderAndAutoSelect()
        {
            MenuState.I.GoToSongPage();

            // Wait for the song page to be ready
            yield return new WaitForSeconds(0.3f);

            // Force the song list to rebuild with the filter applied
            var songSelectObj = GameObject.Find("menu/ShellPage_Song/page/ShellPanel_Center/SongSelect");
            if (songSelectObj == null) yield break;

            var songSelect = songSelectObj.GetComponent<SongSelect>();
            if (songSelect == null) yield break;

            songSelect.ShowSongList();

            // Wait for the list to rebuild
            yield return new WaitForSeconds(0.3f);

            // Select the first item
            var buttons = songSelect.mSongButtons;
            if (buttons == null || buttons.Count == 0) yield break;

            for (int i = 0; i < buttons.Count; i++)
            {
                var item = buttons[i];
                if (item != null && item.mSongData != null)
                {
                    ExScoring.selectedSong = null;
                    item.OnSelect();
                    break;
                }
            }
        }
    }
}