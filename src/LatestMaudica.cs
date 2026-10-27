using System.Collections.Generic;

namespace ExScoringMod
{
    /// <summary>
    /// Data behind the "Latest Maudica" virtual folder: the maudica.com catalog, newest upload
    /// first, served from the local catalog cache (MaudicaCatalog) one page at a time.
    ///
    /// Paging is local, so "Load more songs" is instant. The catalog itself is kept current by its
    /// launch check; opening the folder after playing a song (or after a failed sync) also asks it
    /// for a refresh (throttled, so this costs at most one small request a minute).
    ///
    /// The public surface (Loaded / Loading / Refreshing / Error / results...) is unchanged, so the
    /// row building and downloads in FolderRowManager work as before.
    /// </summary>
    internal static class LatestMaudica
    {
        /// <summary>Entries shown so far, newest first (pages loaded × page size).</summary>
        public static readonly List<Song> results = new List<Song>();

        /// <summary>Total maps in the maudica catalog.</summary>
        public static int TotalOnMaudica => MaudicaCatalog.Count;

        /// <summary>True once the catalog has data.</summary>
        public static bool Loaded => MaudicaCatalog.IsReady;

        /// <summary>True while the catalog is being fetched with nothing to show yet (a background
        /// refresh of an existing catalog doesn't block "Load more").</summary>
        public static bool Loading => MaudicaCatalog.IsSyncing && !MaudicaCatalog.IsReady;

        /// <summary>True when a sync runs while a list is already shown.</summary>
        public static bool Refreshing => MaudicaCatalog.IsSyncing && MaudicaCatalog.IsReady;

        /// <summary>True when the catalog couldn't be fetched and there is no cached copy. With a
        /// cached copy, a failed refresh just keeps showing it.</summary>
        public static bool Error => !MaudicaCatalog.IsSyncing && MaudicaCatalog.LastSyncFailed && !MaudicaCatalog.IsReady;

        /// <summary>Always false: a failed refresh with a cached catalog isn't shown as an error.</summary>
        public static bool ErrorWasRefresh => false;

        /// <summary>More catalog entries remain after what's shown.</summary>
        public static bool HasMorePages => Loaded && nextIndex < MaudicaCatalog.Count;

        private static int pagesShown = 1;
        private static int pageSizeInUse = 0;
        private static int builtForVersion = -1;
        private static int nextIndex = 0; // first catalog entry not yet shown
        private static bool songPlayedSinceFetch = false;
        private static bool subscribed = false;

        // maudica song_id -> matched local songID ("" = not installed). Rebuilt whenever the
        // local song count changes (a download, delete or reload) or on explicit invalidation.
        private static readonly Dictionary<string, string> localMatchCache = new Dictionary<string, string>();
        private static int localMatchSongCount = -1;

        // ── Triggers ─────────────────────────────────────────────────────────

        /// <summary>Called when a song is launched. The next folder open asks for a catalog refresh.</summary>
        public static void MarkSongPlayed()
        {
            songPlayedSinceFetch = true;
        }

        /// <summary>Called when the folder header is shot open (before the view is rebuilt).</summary>
        public static void OnFolderOpened()
        {
            Subscribe();

            if (pageSizeInUse != Config.LatestMaudicaPageSize)
                pagesShown = 1;

            if (!Loaded || Error || songPlayedSinceFetch)
            {
                songPlayedSinceFetch = false;
                MaudicaCatalog.RequestRefresh();
            }
            Rebuild();
        }

        /// <summary>
        /// Called when the song list is (re)built with this folder still open, e.g. coming back
        /// to song select. Keeps the shown list (and the selected song in it) as it is.
        /// </summary>
        public static void EnsureLoaded()
        {
            Subscribe();
            MaudicaCatalog.Initialize();
            if (builtForVersion != MaudicaCatalog.Version) Rebuild();
        }

        /// <summary>Retry after a failure: forces a catalog refresh.</summary>
        public static void Retry()
        {
            MaudicaCatalog.RequestRefresh(null, true);
        }

        /// <summary>Show the next page (the "Load more songs" row). Instant: the catalog is local.</summary>
        public static void LoadMore()
        {
            if (!HasMorePages) return;
            pagesShown++;
            Rebuild();
        }

        // ── Building ─────────────────────────────────────────────────────────

        private static void Subscribe()
        {
            if (subscribed) return;
            subscribed = true;
            MaudicaCatalog.Changed += Rebuild;
        }

        private static void Rebuild()
        {
            pageSizeInUse = Config.LatestMaudicaPageSize;
            int want = pagesShown * pageSizeInUse;

            results.Clear();
            var seen = new HashSet<string>();
            var all = MaudicaCatalog.NewestFirst;
            int i = 0;
            for (; i < all.Count && results.Count < want; i++)
            {
                Song s = all[i].ToSong();
                if (string.IsNullOrEmpty(s.song_id)) continue;
                // Two maps can share a filename; list it once (rows key on song_id).
                if (seen.Add(s.song_id)) results.Add(s);
            }
            nextIndex = i;

            builtForVersion = MaudicaCatalog.Version;
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