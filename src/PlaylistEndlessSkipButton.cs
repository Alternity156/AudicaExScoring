using System;
using UnityEngine;

namespace ExScoringMod
{
    internal static class PlaylistEndlessSkipButton
    {
        private static GameObject skipButton;

        private static Vector3 skipButtonInGameUIPosition = new Vector3(5f, 17f, 0f);
        private static Vector3 skipButtonInGameUIRotation = new Vector3(0f, 0f, 0f);

        public static void CreateSkipButton(ButtonUtils.ButtonLocation location = ButtonUtils.ButtonLocation.Menu)
        {
            if (PlaylistManager.state != PlaylistManager.PlaylistState.Endless) return;
            if (location != ButtonUtils.ButtonLocation.Pause && location != ButtonUtils.ButtonLocation.Failed) return;

            string name = "InGameUI/ShellPage_EndGameContinue/page/ShellPanel_Center/exit";
            Vector3 localPosition = skipButtonInGameUIPosition;
            Vector3 rotation = skipButtonInGameUIRotation;
            Action listener = new Action(() => { OnInGameSkipButtonShot(); });
            if (location == ButtonUtils.ButtonLocation.Failed)
            {
                name = "InGameUI/ShellPage_Failed/page/ShellPanel_Center/exit";
            }
            else if (location == ButtonUtils.ButtonLocation.Pause)
            {
                name = "InGameUI/ShellPage_Pause/page/ShellPanel_Center/exit";
            }

            var refButton = GameObject.Find(name);
            if (refButton == null) return;

            GameObject button = GameObject.Instantiate(refButton, refButton.transform.parent.transform);
            if (location == ButtonUtils.ButtonLocation.Menu)
            {
                skipButton = button;
            }
            ButtonUtils.InitButton(button, "Skip Song", listener, localPosition, rotation);
        }

        private static void OnInGameSkipButtonShot()
        {
            PlaylistEndlessManager.NextSong();
        }
    }
}