using System.Collections.Generic;
using System.Text.RegularExpressions;
using MelonLoader;
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

        // ── maudica web search state ──────────────────────────────────────────
        public static List<Song> webResults = new List<Song>();
        public static bool webLoading = false;
        public static bool webError = false;
        private static int webPage = 1;
        private static int webTotalPages = 1;
        private static int webGeneration = 0; // bumped per new search; stale callbacks bail

        public static bool HasMoreWebPages => webPage < webTotalPages;

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

            // Fresh maudica search (page 1). Invalidate any in-flight fetch.
            webResults.Clear();
            webPage = 1;
            webTotalPages = 1;
            webError = false;
            StartWebFetch(q, 1, ++webGeneration);

            SongFolderManager.SetSearchFolder($"Search Results ({q})");
            SongFolderManager.openFolder = SongFolderManager.searchFolderName;
            FolderRowManager.RefreshList();
        }

        /// <summary>Kicks off a maudica web search for one page; appends results on callback.</summary>
        private static void StartWebFetch(string q, int page, int gen)
        {
            webLoading = true;
            MelonCoroutines.Start(SongDownloader.DoSongWebSearch(q, (search, result) =>
            {
                if (gen != webGeneration) return; // superseded by a newer search

                webLoading = false;
                if (result != null && result.songs != null)
                {
                    webTotalPages = result.total_pages < 1 ? 1 : result.total_pages;
                    foreach (var s in result.songs)
                        if (s != null) webResults.Add(s);
                }
                else
                {
                    webError = true;
                }

                FolderRowManager.RefreshList();
            }, DifficultyFilter.All, false, page, false));
        }

        /// <summary>Fetch and append the next maudica page (used by the "Load more" row).</summary>
        public static void LoadMoreWeb()
        {
            if (webLoading || !HasMoreWebPages || string.IsNullOrEmpty(query)) return;
            webPage++;
            StartWebFetch(query, webPage, webGeneration);
        }

        /// <summary>
        /// Clears the active search and removes the "Search Results" folder.
        /// </summary>
        public static void ClearSearch()
        {
            webResults.Clear();
            webLoading = false;
            webError = false;
            webPage = 1;
            webTotalPages = 1;
            webGeneration++;   // invalidate any in-flight fetch
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