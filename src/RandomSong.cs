using System.Collections;
using Il2CppSystem.Collections.Generic;
using MelonLoader;
using UnityEngine;

namespace ExScoringMod
{
    internal static class RandomSong
    {
        private static int historySize = 10;

        private static List<string> currentSongs = new List<string>();
        private static List<string> currentSongsFull = new List<string>();
        private static List<string> recentlySelected = new List<string>();

        public static void UpdateAvailableSongs(List<string> songs, bool extras)
        {
            if (!extras)
            {
                // First call (main songs) — start fresh
                currentSongsFull = new List<string>();
            }

            // Add all songs from this call
            for (int i = 0; i < songs.Count; i++)
            {
                if (!currentSongsFull.Contains(songs[i]))
                    currentSongsFull.Add(songs[i]);
            }

            if (extras)
            {
                // Second call (extras) — now we have the full list
                FillSongList();

                if (currentSongsFull.Count == 0)
                    RandomSongButton.Disable();
                else
                    RandomSongButton.Enable();
            }
        }

        public static void SelectRandomSong()
        {
            if (currentSongsFull.Count == 0)
                return;

            if (currentSongs.Count == 0)
            {
                recentlySelected.Clear();
                FillSongList();
            }

            int songCount = currentSongs.Count;
            System.Random rand = new System.Random();
            int idx = rand.Next(0, songCount);
            string songID = currentSongs[idx];
            SongList.SongData data = SongList.I.GetSong(songID);
            if (data != null)
            {
                currentSongs.RemoveAt(idx);
                if (recentlySelected.Count > historySize)
                {
                    recentlySelected.RemoveAt(0);
                }
                recentlySelected.Add(songID);

                ExScoring.selectedSong = songID;
                MelonCoroutines.Start(ScrollToAndSelect(songID));
            }
        }

        private static IEnumerator ScrollToAndSelect(string songID)
        {
            var songSelectObj = GameObject.Find("menu/ShellPage_Song/page/ShellPanel_Center/SongSelect");
            if (songSelectObj == null) yield break;

            SongSelect songSelect = songSelectObj.GetComponent<SongSelect>();
            if (songSelect == null) yield break;

            var buttons = songSelect.mSongButtons;
            if (buttons == null || buttons.Count == 0) yield break;

            int targetIndex = -1;
            SongSelectItem targetItem = null;

            for (int i = 0; i < buttons.Count; i++)
            {
                var item = buttons[i];
                if (item != null && item.mSongData != null && item.mSongData.songID == songID)
                {
                    targetIndex = i;
                    targetItem = item;
                    break;
                }
            }

            if (targetItem == null) yield break;

            // Scroll the list to the target song
            ShellScrollable scrollable = songSelectObj.GetComponent<ShellScrollable>();
            if (scrollable != null)
            {
                scrollable.SnapTo(targetIndex);
            }

            yield return new WaitForSeconds(0.1f);

            ExScoring.isAutoSelecting = true;
            try
            {
                targetItem.OnSelect();
                ExScoring.UpdateLaunchPanelInfo();
            }
            finally
            {
                ExScoring.isAutoSelecting = false;
            }

            ExScoring.menuState = MenuState.State.SongPage;
        }

        private static void FillSongList()
        {
            currentSongs = new List<string>();

            for (int i = 0; i < currentSongsFull.Count; i++)
            {
                string songID = currentSongsFull[i];
                if (!recentlySelected.Contains(songID))
                {
                    currentSongs.Add(songID);
                }
            }
        }
    }
}