using System.Collections.Generic;
using System.Text.RegularExpressions;
using TMPro;

namespace ExScoringMod
{
    public static class SongSearch
    {
        public static List<string> searchResult = new List<string>();
        public static string query;
        public static bool searchInProgress = false;
        public static TextMeshPro liveText = null;
        public static string placeholder = "Search...";

        public static void Search()
        {
            searchResult.Clear();
            searchInProgress = false;

            if (query == null)
                return;

            string cleanQuery = CleanForSearch(query);

            for (int i = 0; i < SongList.I.songs.Count; i++)
            {
                SongList.SongData currentSong = SongList.I.songs[i];

                if (currentSong.songID == "tutorial")
                    continue;

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

        /// <summary>
        /// Runs a search for the given query and surfaces results as the virtual
        /// "Search Results (query)" folder, opened at the top of the folder list.
        /// An empty/whitespace query clears the search and removes the folder.
        /// </summary>
        public static void RunSearch(string q)
        {
            if (string.IsNullOrEmpty(q?.Trim()))
            {
                ClearSearch();
                return;
            }

            query = q;
            Search();

            SongFolderManager.SetSearchFolder($"Search Results ({q})");
            SongFolderManager.openFolder = SongFolderManager.searchFolderName;
            FolderRowManager.RefreshList();
        }

        /// <summary>
        /// Clears the active search and removes the "Search Results" folder.
        /// </summary>
        public static void ClearSearch()
        {
            searchResult.Clear();
            query = "";

            if (SongFolderManager.openFolder == SongFolderManager.searchFolderName)
                SongFolderManager.openFolder = null;

            SongFolderManager.SetSearchFolder(null);
            FolderRowManager.RefreshList();
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

        public static void UpdateLiveText()
        {
            if (liveText == null) return;
            liveText.text = string.IsNullOrEmpty(query) ? placeholder : query;
        }
    }
}