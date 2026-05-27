using System;
using MelonLoader;
using UnityEngine;

namespace ExScoringMod
{
    internal static class PlaylistEndlessPanel
    {
        private static OptionsMenu primaryMenu;
        private static OptionsMenuButton shuffleButton;
        private static OptionsMenuButton showScoreButton;
        private static OptionsMenuButton noFailButton;
        private static OptionsMenuButton resetHealthButton;

        static public void SetMenu(OptionsMenu optionsMenu)
        {
            primaryMenu = optionsMenu;
        }

        static public void GoToPanel()
        {
            primaryMenu.ShowPage(OptionsMenu.Page.Customization);
            CleanUpPage(primaryMenu);
            AddButtons(primaryMenu);
            primaryMenu.screenTitle.text = "Playlist Marathon";
        }

        public static void CancelEndless()
        {
            PlaylistManager.state = PlaylistManager.PlaylistState.None;
            MelonPrefs.SaveConfig();
            MenuState.I.GoToSongPage();
            SelectPlaylistButton.UpdatePlaylistButton();
        }

        private static void ToggleShuffle()
        {
            PlaylistConfig.Shuffle = !PlaylistConfig.Shuffle;
            MelonPrefs.SetBool(PlaylistConfig.Category, nameof(PlaylistConfig.Shuffle), PlaylistConfig.Shuffle);
            shuffleButton.label.text = PlaylistConfig.Shuffle ? "<color=\"green\">Shuffle ON" : "<color=\"red\">Shuffle OFF";
        }

        private static void ToggleShowScore()
        {
            PlaylistConfig.ShowScores = !PlaylistConfig.ShowScores;
            MelonPrefs.SetBool(PlaylistConfig.Category, nameof(PlaylistConfig.ShowScores), PlaylistConfig.ShowScores);
            showScoreButton.label.text = PlaylistConfig.ShowScores ? "<color=\"green\">Show Score ON" : "<color=\"red\">Show Score OFF";
        }

        private static void ToggleNoFail()
        {
            PlaylistConfig.NoFail = !PlaylistConfig.NoFail;
            MelonPrefs.SetBool(PlaylistConfig.Category, nameof(PlaylistConfig.NoFail), PlaylistConfig.NoFail);
            noFailButton.label.text = PlaylistConfig.NoFail ? "<color=\"green\">NoFail ON" : "<color=\"red\">NoFail OFF";
        }

        private static void ToggleResetHealth()
        {
            PlaylistConfig.ResetHealth = !PlaylistConfig.ResetHealth;
            MelonPrefs.SetBool(PlaylistConfig.Category, nameof(PlaylistConfig.ResetHealth), PlaylistConfig.ResetHealth);
            resetHealthButton.label.text = PlaylistConfig.ResetHealth ? "<color=\"green\">Reset Health ON" : "<color=\"red\">Reset Health OFF";
        }

        private static void AddButtons(OptionsMenu optionsMenu)
        {
            var header = optionsMenu.AddHeader(0, "Marathon Settings");
            optionsMenu.scrollable.AddRow(header);

            string shuffleText = PlaylistConfig.Shuffle ? "<color=\"green\">Shuffle ON" : "<color=\"red\">Shuffle OFF";
            shuffleButton = optionsMenu.AddButton(0, shuffleText, new Action(() =>
            {
                ToggleShuffle();
            }), null, "Shuffles the songs", optionsMenu.buttonPrefab);

            string showScoreText = PlaylistConfig.ShowScores ? "<color=\"green\">Show Score ON" : "<color=\"red\">Show Score OFF";
            showScoreButton = optionsMenu.AddButton(1, showScoreText, new Action(() =>
            {
                ToggleShowScore();
            }), null, "Shows 'Level Complete' at the end of each song", optionsMenu.buttonPrefab);

            string noFailText = PlaylistConfig.NoFail ? "<color=\"green\">NoFail ON" : "<color=\"red\">NoFail OFF";
            noFailButton = optionsMenu.AddButton(0, noFailText, new Action(() =>
            {
                ToggleNoFail();
            }), null, "Play the marathon with NoFail on or off", optionsMenu.buttonPrefab);

            string resetHealthText = PlaylistConfig.ResetHealth ? "<color=\"green\">Reset Health ON" : "<color=\"red\">Reset Health OFF";
            resetHealthButton = optionsMenu.AddButton(1, resetHealthText, new Action(() =>
            {
                ToggleResetHealth();
            }), null, "Reset Health at the end of each song", optionsMenu.buttonPrefab);

            Il2CppSystem.Collections.Generic.List<GameObject> toggles = new Il2CppSystem.Collections.Generic.List<GameObject>();
            toggles.Add(shuffleButton.gameObject);
            toggles.Add(showScoreButton.gameObject);
            optionsMenu.scrollable.AddRow(toggles);
            toggles.Clear();
            toggles.Add(noFailButton.gameObject);
            toggles.Add(resetHealthButton.gameObject);
            optionsMenu.scrollable.AddRow(toggles);

            var divider = optionsMenu.AddHeader(0, "");
            optionsMenu.scrollable.AddRow(divider);
            var start = optionsMenu.AddButton(1, "Start", new Action(() =>
            {
                MelonPrefs.SaveConfig();
                PlaylistEndlessManager.StartEndlessSession();
            }), null, "Starts the marathon", optionsMenu.buttonPrefab);
            optionsMenu.scrollable.AddRow(start.gameObject);
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
    }
}