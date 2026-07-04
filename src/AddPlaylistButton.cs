using System;
using System.IO;
using TMPro;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// The launch-panel playlist button. Reads "Add to Playlist" for a normal song (opens the
    /// transient playlist picker) and "Remove from Playlist" for a song shown inside a playlist.
    /// In-game (Pause/Fail/EndGame/PracticeModeOver) variants were removed.
    /// </summary>
    internal static class AddPlaylistButton
    {
        private static GameObject playlistButton;

        private static Vector3 playlistButtonMenuPosition = new Vector3(2.5f, 2.4f, 0f);
        private static Vector3 playlistButtonMenuScale = new Vector3(0.75f, 0.75f, 0.75f);
        private static Vector3 playlistButtonMenuRotation = new Vector3(0f, 0f, 0f);
        private static Vector3 playlistButtonLabelScale = new Vector3(1.5f, 1.5f, 1.5f);

        public static void CreatePlaylistButton(ButtonUtils.ButtonLocation location = ButtonUtils.ButtonLocation.Menu)
        {
            if (location != ButtonUtils.ButtonLocation.Menu) return; // in-game add buttons removed
            if (PlaylistManager.state == PlaylistManager.PlaylistState.Endless) return;

            if (playlistButton != null)
            {
                // Context-aware label (Add vs Remove) so song-page re-setup doesn't clobber it.
                UpdateForSelection();
                if (!MarathonSetup.Active) playlistButton.SetActive(true);
                return;
            }

            var refButton = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center/NoFailPracticeToggle/PracticeToggle");
            if (refButton == null) return;

            playlistButton = GameObject.Instantiate(refButton, refButton.transform.parent.transform);
            playlistButton.transform.localScale = playlistButtonMenuScale;
            ButtonUtils.InitButton(playlistButton, "Add to Playlist",
                new Action(() => OnPlaylistButtonShot()), playlistButtonMenuPosition, playlistButtonMenuRotation);

            TextMeshPro label = playlistButton.GetComponentInChildren<TextMeshPro>();
            if (label != null) label.transform.localScale = playlistButtonLabelScale;

            UpdateForSelection();
        }

        private static void OnPlaylistButtonShot()
        {
            // In the Song Requests folder, this button removes the selected request.
            if (SongFolderManager.openFolder == SongFolderManager.FolderSongRequests
                && FolderRowManager.IsSelectedSongRequest(ExScoring.selectedSong))
            {
                FolderRowManager.RemoveSelectedRequest(ExScoring.selectedSong);
                UpdateForSelection(); // no longer a request → revert the label
                return;
            }

            string context = FolderRowManager.CurrentPlaylistContext;
            if (context != null)
            {
                RemoveSelectedFromPlaylist(context);
                return;
            }

            // Add: open the transient picker for the selected song.
            string stem = SelectedSongStem();
            if (stem != null) FolderRowManager.EnterAddPicker(stem);
        }

        /// <summary>Updates the launch-panel button label for the current selection's context.</summary>
        public static void UpdateForSelection()
        {
            if (playlistButton == null) return;

            // In the Song Requests folder, a request song turns this button into "Remove Request".
            if (SongFolderManager.openFolder == SongFolderManager.FolderSongRequests
                && FolderRowManager.IsSelectedSongRequest(ExScoring.selectedSong))
            {
                ButtonUtils.UpdateButtonLabel(playlistButton, "Remove Request");
                return;
            }

            string context = FolderRowManager.CurrentPlaylistContext;
            ButtonUtils.UpdateButtonLabel(playlistButton, context != null ? "Remove from Playlist" : "Add to Playlist");
        }

        public static void Hide()
        {
            if (playlistButton != null) playlistButton.SetActive(false);
        }

        public static void Show()
        {
            if (playlistButton != null) playlistButton.SetActive(true);
            UpdateForSelection();
        }

        private static void RemoveSelectedFromPlaylist(string playlistName)
        {
            string stem = SelectedSongStem();
            if (stem == null) return;

            PlaylistManager.RemoveSongFromPlaylistByName(playlistName, stem);
            FolderRowManager.RefreshAfterPlaylistEdit();
        }

        /// <summary>Filename stem (no ".audica") of the currently selected song, or null.</summary>
        private static string SelectedSongStem()
        {
            if (ExScoring.selectedSongData == null) return null;
            string stem = Path.GetFileName(ExScoring.selectedSongData.zipPath);
            if (stem.EndsWith(".audica")) stem = stem.Substring(0, stem.Length - ".audica".Length);
            return stem;
        }
    }
}