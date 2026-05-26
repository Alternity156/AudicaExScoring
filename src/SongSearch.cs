using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ExScoringMod
{
    internal static class SongSearch
    {
        public static List<string> searchResult = new List<string>();
        public static string query;
        public static MapType mapType = MapType.All;
        public static bool searchInProgress = false;

        public enum MapType
        {
            All,
            OfficialOnly,
            CustomsOnly
        }

        public static void CancelSearch()
        {
            searchInProgress = false;
            MenuState.I.GoToSongPage();
            SongSearchButton.UpdateSearchButton();
        }

        public static void Search()
        {
            searchResult.Clear();
            searchInProgress = false;

            if (query == null)
                return;

            for (int i = 0; i < SongList.I.songs.Count; i++)
            {
                SongList.SongData currentSong = SongList.I.songs[i];
                bool isCustom = IsCustomSong(currentSong.songID);

                if ((mapType == MapType.CustomsOnly && !isCustom) ||
                    (mapType == MapType.OfficialOnly && isCustom))
                    continue;

                if (currentSong.songID == "tutorial")
                    continue;

                string cleanQuery = CleanForSearch(query);

                if (CleanForSearch(currentSong.artist).Contains(cleanQuery) ||
                    CleanForSearch(currentSong.title).Contains(cleanQuery) ||
                    CleanForSearch(currentSong.songID).Contains(cleanQuery) ||
                    currentSong.author != null && CleanForSearch(currentSong.author).Contains(cleanQuery) ||
                    CleanForSearch(currentSong.artist).Replace(" ", "").Contains(cleanQuery) ||
                    CleanForSearch(currentSong.title).Replace(" ", "").Contains(cleanQuery))
                {
                    searchResult.Add(currentSong.songID);
                }
            }
        }

        public static void OnNewUserSearch()
        {
            Search();
            FilterPanel.ResetFilterState();
            MenuState.I.GoToSongPage();
            SongSearchButton.UpdateSearchButton();
        }

        private static string CleanForSearch(string s)
        {
            return s?.ToLowerInvariant().Replace("'", "");
        }

        /// <summary>
        /// Returns true if the songID appears to be a custom song (has a 32-char hex hash suffix).
        /// </summary>
        public static bool IsCustomSong(string songID)
        {
            string[] components = songID.Split('_');
            if (components.Length == 1)
                return false;

            string potentialHash = components[components.Length - 1];

            if (potentialHash.Length == 32 && Regex.IsMatch(potentialHash, @"^[0-9a-f]+$"))
                return true;

            return false;
        }
    }
}