using System.Collections.Generic;
using MelonLoader;

namespace ExScoringMod
{
    /// <summary>
    /// Data behind the "Latest Maudica" virtual folder: the maudica.com catalog, newest upload
    /// first (maudica's default listing order), loaded one page at a time.
    ///
    /// Fetch rules:
    ///   - Nothing is fetched until the folder is first shot open.
    ///   - Opening the folder fetches page 1 fresh only if nothing is loaded yet, the last load
    ///     failed, a song has been played since the last fetch, or the page-size option changed.
    ///     Closing and reopening without playing a song keeps every loaded page.
    ///   - "Load more songs" appends the next page using the page size the list was started with,
    ///     so paging offsets stay consistent even if the option changes mid-list.
    ///
    /// Row building and downloads live in FolderRowManager, like the search folder.
    /// </summary>
    internal static class LatestMaudica
    {
        /// <summary>Loaded maudica entries in display order (newest first), deduped by song_id.</summary>
        public static readonly List<Song> results = new List<Song>();

        /// <summary>Total maps in the maudica catalog, from the last successful response.</summary>
        public static int TotalOnMaudica { get; private set; }

        /// <summary>True once at least page 1 has loaded successfully.</summary>
        public static bool Loaded { get; private set; }

        /// <summary>True while any page request is in flight.</summary>
        public static bool Loading { get; private set; }

        /// <summary>True when the in-flight request is a fresh page-1 load (vs. Load more).</summary>
        public static bool Refreshing { get; private set; }

        /// <summary>True when the most recent request failed.</summary>
        public static bool Error { get; private set; }

        /// <summary>True when the failed request was a fresh page-1 load (vs. Load more).</summary>
        public static bool ErrorWasRefresh { get; private set; }

        /// <summary>More catalog pages remain after what's loaded.</summary>
        public static bool HasMorePages => Loaded && page < totalPages;

        private static int page = 0;
        private static int totalPages = 1;
        private static int pageSizeInUse = 0;
        private static int generation = 0; // bumped per fresh load; stale callbacks bail
        private static bool songPlayedSinceFetch = false;
        private static readonly HashSet<string> seenIds = new HashSet<string>();

        // maudica song_id -> matched local songID ("" = not installed). Rebuilt whenever the
        // local song count changes (a download, delete or reload) or on explicit invalidation.
        private static readonly Dictionary<string, string> localMatchCache = new Dictionary<string, string>();
        private static int localMatchSongCount = -1;

        // ── Triggers ─────────────────────────────────────────────────────────

        /// <summary>Called when a song is launched. The next folder open fetches fresh.</summary>
        public static void MarkSongPlayed()
        {
            songPlayedSinceFetch = true;
        }

        /// <summary>Called when the folder header is shot open (before the view is rebuilt).</summary>
        public static void OnFolderOpened()
        {
            if (Loading && Refreshing) return; // a fresh load is already on its way

            bool needsFresh = !Loaded
                || Error
                || songPlayedSinceFetch
                || pageSizeInUse != Config.LatestMaudicaPageSize;

            if (needsFresh)
                FetchFresh();
        }

        /// <summary>
        /// Called when the song list is (re)built with this folder still open, e.g. coming back
        /// to song select. Loads only if nothing has been loaded and no attempt is pending or failed,
        /// so the list (and the selected song in it) stays put after playing.
        /// </summary>
        public static void EnsureLoaded()
        {
            if (!Loaded && !Loading && !Error)
                FetchFresh();
        }

        /// <summary>Retry after a failure: redo a failed fresh load, otherwise the next page.</summary>
        public static void Retry()
        {
            if (Loading) return;
            if (!Loaded || ErrorWasRefresh) FetchFresh();
            else LoadMore();
        }

        /// <summary>Fetch and append the next page (the "Load more songs" row).</summary>
        public static void LoadMore()
        {
            if (Loading || !HasMorePages) return;
            Fetch(page + 1, false, pageSizeInUse, generation);
        }

        // ── Fetching ─────────────────────────────────────────────────────────

        /// <summary>Start a page-1 load. Already-loaded rows stay visible until it lands.</summary>
        private static void FetchFresh()
        {
            songPlayedSinceFetch = false;
            Fetch(1, true, Config.LatestMaudicaPageSize, ++generation);
        }

        private static void Fetch(int pageToLoad, bool fresh, int pageSize, int gen)
        {
            Loading = true;
            Refreshing = fresh;
            Error = false;

            DebugLog.Log($"[LatestMaudica] Fetching page {pageToLoad} ({pageSize} per page, fresh={fresh})");

            MelonCoroutines.Start(SongDownloader.DoSongWebSearch("", (search, result) =>
            {
                if (gen != generation) return; // superseded by a newer fresh load

                Loading = false;
                Refreshing = false;

                if (result == null || result.songs == null)
                {
                    Error = true;
                    ErrorWasRefresh = fresh;
                    DebugLog.Log($"[LatestMaudica] Page {pageToLoad} failed");
                    FolderRowManager.RefreshList();
                    return;
                }

                if (fresh)
                {
                    results.Clear();
                    seenIds.Clear();
                    pageSizeInUse = pageSize;
                    Loaded = true;
                }

                page = pageToLoad;
                totalPages = result.total_pages < 1 ? 1 : result.total_pages;
                TotalOnMaudica = result.song_count;

                foreach (var s in result.songs)
                {
                    if (s == null || string.IsNullOrEmpty(s.song_id)) continue;
                    // New uploads between page requests shift the listing, so a later page
                    // can repeat entries from an earlier one.
                    if (seenIds.Add(s.song_id))
                        results.Add(s);
                }

                DebugLog.Log($"[LatestMaudica] Page {page}/{totalPages} loaded: {results.Count} of {TotalOnMaudica} shown");
                FolderRowManager.RefreshList();
            }, DifficultyFilter.All, false, pageToLoad, false, pageSize));
        }

        // ── Local matching ───────────────────────────────────────────────────

        /// <summary>
        /// The local songID this maudica entry is installed as, or null if it isn't installed.
        /// Cached so redraws don't rescan the whole library for every row.
        /// </summary>
        public static string LocalSongIdFor(Song s)
        {
            if (s == null || string.IsNullOrEmpty(s.song_id)) return null;

            int songCount = (SongList.I != null && SongList.I.songs != null) ? SongList.I.songs.Count : 0;
            if (songCount != localMatchSongCount)
            {
                localMatchCache.Clear();
                localMatchSongCount = songCount;
            }

            if (!localMatchCache.TryGetValue(s.song_id, out string local))
            {
                var match = FolderRowManager.FindLocalRequestSong(s.title, s.artist, s.author, s.song_id);
                local = match != null ? match.songID : "";
                localMatchCache[s.song_id] = local;
            }

            return local.Length > 0 ? local : null;
        }

        /// <summary>Forget cached local matches (after a download/reload).</summary>
        public static void InvalidateLocalMatches()
        {
            localMatchCache.Clear();
            localMatchSongCount = -1;
        }
    }
}
