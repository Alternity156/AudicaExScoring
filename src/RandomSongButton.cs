using System;
using UnityEngine;

namespace ExScoringMod
{
    internal static class RandomSongButton
    {
        private static bool enabled;

        private static GameObject randomSongButton;
        private static GunButton randomSongGB;

        private static Vector3 randomSongButtonPos = new Vector3(10.4f, 0f, 0f);
        private static Vector3 randomSongButtonRot = new Vector3(0f, 0f, 0f);

        public static void CreateRandomSongButton()
        {
            if (randomSongButton != null)
            {
                // Stay hidden while the song list is showing the Options menu.
                randomSongButton.SetActive(!FolderRowManager.InGlobalOptions);

                if (enabled)
                    randomSongGB.SetInteractable(true);
                else
                    randomSongGB.SetInteractable(false);

                return;
            }

            var backButton = GameObject.Find("menu/ShellPage_Song/page/backParent/back");
            if (backButton == null)
                return;

            randomSongButton = GameObject.Instantiate(backButton, backButton.transform.parent.transform);
            ButtonUtils.InitButton(randomSongButton, "Random Song", new Action(() => { OnRandomSongButtonShot(); }),
                                   randomSongButtonPos, randomSongButtonRot);

            randomSongGB = randomSongButton.GetComponentInChildren<GunButton>();
            randomSongButton.SetActive(!FolderRowManager.InGlobalOptions);

            if (enabled)
                randomSongGB.SetInteractable(true);
            else
                randomSongGB.SetInteractable(false);
        }

        /// <summary>Show/hide the button (hidden while the song list is in the Options menu).</summary>
        public static void SetVisible(bool visible)
        {
            if (randomSongButton != null)
                randomSongButton.SetActive(visible);
        }

        private static void OnRandomSongButtonShot()
        {
            RandomSong.SelectRandomSong();
        }

        public static void Enable()
        {
            enabled = true;
            if (randomSongGB != null)
                randomSongGB.SetInteractable(true);
        }

        public static void Disable()
        {
            enabled = false;
            if (randomSongGB != null)
                randomSongGB.SetInteractable(false);
        }
    }
}