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

        // ── maudica search state ──────────────────────────────────────────────
        // Answered from the local maudica catalog (MaudicaCatalog): title, artist, mapper and
        // filename, newest upload first, paged locally. Only when there's no catalog yet (first
        // launch still downloading it, or maudica unreachable with no cache) does it fall back to
        // maudica's own search, and it switches to the catalog as soon as that's ready.
        public static List<Song> webResults = new List<Song>();
        public static bool webLoading = false;
        public static bool webError = false;
        private const int WebPageSize = 30;
        private static int webPage = 1;
        private static int webTotalPages = 1;
        private static int webGeneration = 0; // bumped per new search; stale callbacks bail
        private static bool webFromCatalog = false;
        private static List<Song> catalogMatches = new List<Song>();
        private static bool catalogSubscribed = false;

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
            webGeneration++;
            StartMaudicaSearch(q);

            SongFolderManager.SetSearchFolder($"Search Results ({q})");
            SongFolderManager.openFolder = SongFolderManager.searchFolderName;
            FolderRowManager.RefreshList();
        }

        /// <summary>
        /// Maudica matches for the query: from the catalog when it's ready, otherwise from
        /// maudica's own search (and redone from the catalog once that's ready).
        /// </summary>
        private static void StartMaudicaSearch(string q)
        {
            if (!catalogSubscribed)
            {
                catalogSubscribed = true;
                MaudicaCatalog.Changed += OnCatalogChanged;
            }

            if (MaudicaCatalog.IsReady)
            {
                webFromCatalog = true;
                webLoading = false;
                webError = false;
                catalogMatches = MaudicaCatalog.SearchSongs(q);
                ShowCatalogPages();
            }
            else
            {
                webFromCatalog = false;
                MaudicaCatalog.Initialize();
                StartWebFetch(q, 1, webGeneration);
            }
        }

        /// <summary>webResults = the first webPage pages of the catalog matches.</summary>
        private static void ShowCatalogPages()
        {
            webTotalPages = catalogMatches.Count == 0 ? 1 : (catalogMatches.Count + WebPageSize - 1) / WebPageSize;
            if (webPage > webTotalPages) webPage = webTotalPages;
            int take = System.Math.Min(catalogMatches.Count, webPage * WebPageSize);
            webResults.Clear();
            for (int i = 0; i < take; i++) webResults.Add(catalogMatches[i]);
        }

        /// <summary>The catalog changed (first load, new uploads): redo an active search from it.</summary>
        private static void OnCatalogChanged()
        {
            if (string.IsNullOrEmpty(query) || !MaudicaCatalog.IsReady) return;
            webGeneration++; // drop any in-flight web fetch
            int keepPage = webFromCatalog ? webPage : 1;
            webFromCatalog = true;
            webLoading = false;
            webError = false;
            catalogMatches = MaudicaCatalog.SearchSongs(query);
            webPage = keepPage;
            ShowCatalogPages();
            // MaudicaCatalog refreshes the list itself when its sync ends.
        }

        /// <summary>Kicks off a maudica web search for one page; appends results on callback.
        /// Only used while there's no catalog yet.</summary>
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
            if (webFromCatalog) ShowCatalogPages(); // instant: the catalog is local
            else StartWebFetch(query, webPage, webGeneration);
        }

        /// <summary>
        /// Clears the active search and removes the "Search Results" folder.
        /// </summary>
        public static void ClearSearch()
        {
            webResults.Clear();
            catalogMatches = new List<Song>();
            webFromCatalog = false;
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