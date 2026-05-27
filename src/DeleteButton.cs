using System;
using TMPro;
using UnityEngine;

namespace ExScoringMod
{
    public partial class ExScoring
    {
        private static GameObject deleteButtonObj;
        private static GunButton deleteGunButton;
        private static TextMeshPro deleteButtonText;

        private static Vector3 delButtonMenuPosition = new Vector3(1.1f, 15.5f, 0f);
        private static Vector3 delButtonMenuScale = new Vector3(0.5f, 0.75f, 0.75f);
        private static Vector3 delButtonMenuRotation = new Vector3(0f, 0f, 0f);
        private static Vector3 delButtonLabelScale = new Vector3(2.25f, 1.5f, 1.5f);

        private static Vector3 delButtonInGameUIPosition = new Vector3(-5f, 13.5f, 0f);
        private static Vector3 delButtonInGameUIRotation = new Vector3(0f, 0f, 0f);

        internal static void CreateDeleteButton(ButtonUtils.ButtonLocation location = ButtonUtils.ButtonLocation.Menu)
        {
            if (location == ButtonUtils.ButtonLocation.Menu && deleteButtonObj != null)
            {
                deleteButtonObj.SetActive(true);
                UpdateDeleteButtonEnabled(deleteGunButton, deleteButtonText);
                return;
            }

            string name = "InGameUI/ShellPage_EndGameContinue/page/ShellPanel_Center/exit";
            Vector3 localPosition = delButtonInGameUIPosition;
            Vector3 rotation = delButtonInGameUIRotation;
            Action listener = new Action(() => { OnInGameUIDeleteButtonShot(); });

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
                listener = new Action(() => { OnMenuDeleteButtonShot(); });
                localPosition = delButtonMenuPosition;
                rotation = delButtonMenuRotation;
            }

            var refButton = GameObject.Find(name);
            if (refButton == null) return;

            GameObject button = GameObject.Instantiate(refButton, refButton.transform.parent.transform);
            GunButton gunButton = button.GetComponentInChildren<GunButton>();
            TextMeshPro tmp = button.GetComponentInChildren<TextMeshPro>();
            ButtonUtils.InitButton(button, "Delete", listener, localPosition, rotation);
            if (location == ButtonUtils.ButtonLocation.Menu)
            {
                button.transform.localScale = delButtonMenuScale;
                TextMeshPro label = button.GetComponentInChildren<TextMeshPro>();
                if (label != null)
                    label.transform.localScale = delButtonLabelScale;
            }

            UpdateDeleteButtonEnabled(gunButton, tmp);

            if (location == ButtonUtils.ButtonLocation.Menu)
            {
                deleteButtonObj = button;
                deleteGunButton = gunButton;
                deleteButtonText = tmp;
            }
        }

        private static void OnMenuDeleteButtonShot()
        {
            var song = SongDataHolder.I.songData;
            KataConfig.I.CreateDebugText("Deleted " + song.title, new Vector3(0f, -1f, 5f), 5f, null, false, 0.2f);
            RemoveSong(song.songID);

            // Go back to song list and auto-select next song
            var songSelectObj = GameObject.Find("menu/ShellPage_Song/page/ShellPanel_Center/SongSelect");
            if (songSelectObj != null)
            {
                var ss = songSelectObj.GetComponent<SongSelect>();
                if (ss != null) ss.ShowSongList();
            }
            AutoSelectSong();
        }

        private static void OnInGameUIDeleteButtonShot()
        {
            var song = SongDataHolder.I.songData;
            KataConfig.I.CreateDebugText("Deleted " + song.title, new Vector3(0f, -1f, 5f), 5f, null, false, 0.2f);
            RemoveSong(song.songID);
            InGameUI.I.ReturnToSongList();
        }

        private static void UpdateDeleteButtonEnabled(GunButton button, TextMeshPro text)
        {
            if (SongDataHolder.I == null || SongDataHolder.I.songData == null)
            {
                button.SetInteractable(false);
                text.alpha = 0.25f;
                return;
            }

            if (SongSearch.IsCustomSong(SongDataHolder.I.songData.songID))
            {
                button.SetInteractable(true);
                text.alpha = 1.0f;
            }
            else
            {
                button.SetInteractable(false);
                text.alpha = 0.25f;
            }
        }

        internal static void UpdateDeleteButtonEnabled()
        {
            if (deleteGunButton != null && deleteButtonText != null)
                UpdateDeleteButtonEnabled(deleteGunButton, deleteButtonText);
        }
    }
}