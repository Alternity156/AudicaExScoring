using System;
using System.Collections;
using System.IO;
using MelonLoader;
using TMPro;
using UnityEngine;

namespace ExScoringMod
{
    internal static class AddPlaylistButton
    {
        private static GameObject playlistButton;

        private static Vector3 playlistButtonMenuPosition = new Vector3(2.5f, 1.5f, 0f);
        private static Vector3 playlistButtonMenuScale = new Vector3(0.75f, 0.75f, 0.75f);
        private static Vector3 playlistButtonMenuRotation = new Vector3(0f, 0f, 0f);
        private static Vector3 playlistButtonLabelScale = new Vector3(1.5f, 1.5f, 1.5f);

        private static Vector3 playlistButtonInGameUIPosition = new Vector3(5f, 17f, 0f);
        private static Vector3 playlistButtonInGameUIRotation = new Vector3(0f, 0f, 0f);

        public static string songToAdd;

        public static void CreatePlaylistButton(ButtonUtils.ButtonLocation location = ButtonUtils.ButtonLocation.Menu)
        {
            if (PlaylistManager.state == PlaylistManager.PlaylistState.Endless) return;

            if (location == ButtonUtils.ButtonLocation.Menu && playlistButton != null)
            {
                UpdateLabel();
                playlistButton.SetActive(true);
                return;
            }

            string name = "InGameUI/ShellPage_EndGameContinue/page/ShellPanel_Center/exit";
            Vector3 localPosition = playlistButtonInGameUIPosition;
            Vector3 rotation = playlistButtonInGameUIRotation;
            Action listener = new Action(() => { OnIngamePlaylistButtonShot(); });
            if (location == ButtonUtils.ButtonLocation.Failed)
            {
                name = "InGameUI/ShellPage_Failed/page/ShellPanel_Center/exit";
            }
            else if (location == ButtonUtils.ButtonLocation.Pause)
            {
                name = "InGameUI/ShellPage_Pause/page/ShellPanel_Center/exit";
            }
            else if (location == ButtonUtils.ButtonLocation.PracticeModeOver)
            {
                name = "InGameUI/ShellPage_PracticeModeOver/page/ShellPanel_Center/exit";
            }
            else if (location == ButtonUtils.ButtonLocation.Menu)
            {
                name = "menu/ShellPage_Launch/page/ShellPanel_Center/NoFailPracticeToggle/PracticeToggle";
                listener = new Action(() => { OnPlaylistButtonShot(); });
                localPosition = playlistButtonMenuPosition;
                rotation = playlistButtonMenuRotation;
            }

            var refButton = GameObject.Find(name);
            if (refButton == null) return;

            GameObject button = GameObject.Instantiate(refButton, refButton.transform.parent.transform);
            if (location == ButtonUtils.ButtonLocation.Menu)
            {
                playlistButton = button;
                button.transform.localScale = playlistButtonMenuScale;
            }
            ButtonUtils.InitButton(button, "Add to Playlist", listener, localPosition, rotation);
            if (location == ButtonUtils.ButtonLocation.Menu)
            {
                TextMeshPro label = button.GetComponentInChildren<TextMeshPro>();
                if (label != null)
                    label.transform.localScale = playlistButtonLabelScale;
            }
        }

        private static void OnPlaylistButtonShot()
        {
            SelectPlaylist();
        }

        private static void OnIngamePlaylistButtonShot()
        {
            MelonCoroutines.Start(GoToSelectPlaylist());
        }

        private static IEnumerator GoToSelectPlaylist()
        {
            InGameUI.I.ReturnToSongList();
            yield return new WaitForSecondsRealtime(.5f);
            SelectPlaylist();
        }

        private static void SelectPlaylist()
        {
            songToAdd = Path.GetFileName(SongDataHolder.I.songData.zipPath);
            songToAdd = songToAdd.Substring(0, songToAdd.Length - 7);
            PlaylistManager.state = PlaylistManager.PlaylistState.Adding;
            MenuState.I.GoToSettingsPage();
        }

        private static void UpdateLabel()
        {
            ButtonUtils.UpdateButtonLabel(playlistButton, "Add to Playlist");
        }
    }
}