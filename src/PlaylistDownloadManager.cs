using System;
using System.Collections;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// Finds and downloads a playlist's missing songs from maudica.com.
    ///
    /// Playlist entries are filename stems ("4AEM-Continuum" for "4AEM-Continuum.audica"). Missing
    /// entries are looked up in the local maudica catalog (MaudicaCatalog.FindByStem: exact
    /// filename, then the same letters/digits, then without a copy suffix). When an entry isn't in
    /// the catalog, a catalog refresh is requested (throttled) so songs uploaded since the last
    /// check still turn up; stems that are still missing are logged once with their normalised form.
    ///
    /// Downloads mirror the search folder: shooting a row downloads that song, "Download all"
    /// downloads every found song of the playlist one at a time, then the song list is reloaded so
    /// the rows turn into normal song rows.
    /// </summary>
    internal static class PlaylistDownloadManager
    {
        public const string DownloadAllActionId = "playlist_downloadall";

        /// <summary>Download state of one missing playlist entry (keyed by stem).</summary>
        private class DownloadState
        {
            public bool downloading;
            public bool failed;
            public int percent = -1;
        }

        private static readonly Dictionary<string, DownloadState> downloads =
            new Dictionary<string, DownloadState>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> loggedMisses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>True while "Download all" is running (single downloads are blocked meanwhile).</summary>
        public static bool DownloadingAll { get; private set; }
        private static string downloadAllPlaylist = null;
        private static int downloadAllDone = 0;
        private static int downloadAllTotal = 0;

        // ── Lookups ───────────────────────────────────────────────────────────

        /// <summary>
        /// Called while building a playlist's rows. Starts the catalog if needed and, when some
        /// entries aren't in it, asks for a (throttled) refresh. Safe to call on every redraw.
        /// </summary>
        public static void EnsureLookups(IEnumerable<string> stems)
        {
            if (stems == null) return;
            if (!MaudicaCatalog.IsReady)
            {
                MaudicaCatalog.Initialize();
                return;
            }

            bool anyMissing = false;
            foreach (string stem in stems)
            {
                if (string.IsNullOrEmpty(stem) || MaudicaCatalog.FindByStem(stem) != null) continue;
                anyMissing = true;
                if (!MaudicaCatalog.IsSyncing && !MaudicaCatalog.CanRefreshNow && loggedMisses.Add(stem))
                    DebugLog.Log($"[PlaylistDownload] not on Maudica: '{stem}' (normalised '{MaudicaCatalog.Normalize(stem)}')");
            }

            if (anyMissing && MaudicaCatalog.CanRefreshNow)
                MaudicaCatalog.RequestRefresh();
        }

        private static Song Find(string stem)
        {
            CatalogMap map = MaudicaCatalog.FindByStem(stem);
            return map != null ? map.ToSong() : null;
        }

        private static DownloadState State(string stem)
        {
            if (!downloads.TryGetValue(stem, out DownloadState st))
            {
                st = new DownloadState();
                downloads[stem] = st;
            }
            return st;
        }

        // ── Rows ──────────────────────────────────────────────────────────────

        /// <summary>The row for a missing entry, reflecting catalog and download state.</summary>
        public static ViewRow BuildMissingRow(string stem)
        {
            Color statusColor = ColorSettings.RowLoadMore.Value;
            Song s = MaudicaCatalog.IsReady ? Find(stem) : null;

            if (s == null)
            {
                if (!MaudicaCatalog.IsReady)
                {
                    if (MaudicaCatalog.IsSyncing)
                        return ViewRow.DisabledRow(stem, statusColor, "Loading Maudica song list...");
                    return ViewRow.ActionRow(stem, statusColor, () => MaudicaCatalog.RequestRefresh(null, true),
                        "Couldn't reach Maudica - shoot to retry");
                }
                if (MaudicaCatalog.IsSyncing)
                    return ViewRow.DisabledRow(stem, statusColor, "Searching Maudica...");
                return ViewRow.DisabledRow(stem, statusColor, "Not on Maudica");
            }

            var row = ViewRow.DownloadableSongRow(s.song_id, s.title, s.artist, s.author, s.download_url,
                ColorSettings.RowDownloadable.Value, () => DownloadOne(stem));
            if (downloads.TryGetValue(stem, out DownloadState st))
            {
                if (st.downloading) row.subLabel = $"Downloading... {Math.Max(0, st.percent)}%";
                else if (st.failed) row.subLabel = "Download failed - shoot to retry";
            }
            return row;
        }

        /// <summary>
        /// The "Download all" row for a playlist, or null when there is nothing to download
        /// (nothing found and no download-all running for it).
        /// </summary>
        public static ViewRow? BuildDownloadAllRow(string playlistName, List<string> missingStems)
        {
            Color color = ColorSettings.RowDownloadAll.Value;

            if (DownloadingAll && downloadAllPlaylist == playlistName)
                return ViewRow.ActionRow("Download all", color, null, DownloadAllSubLabel(), DownloadAllActionId);

            int count = CountDownloadable(missingStems);
            if (count == 0) return null;

            return ViewRow.ActionRow("Download all", color, () => DownloadAll(playlistName),
                $"{count} song{(count != 1 ? "s" : "")}", DownloadAllActionId);
        }

        private static string DownloadAllSubLabel()
            => $"Downloading {Math.Max(1, downloadAllDone)}/{downloadAllTotal}...";

        private static int CountDownloadable(List<string> stems)
        {
            int n = 0;
            if (stems == null || !MaudicaCatalog.IsReady) return n;
            foreach (string stem in stems)
            {
                if (Find(stem) == null) continue;
                if (downloads.TryGetValue(stem, out DownloadState st) && st.downloading) continue;
                n++;
            }
            return n;
        }

        // ── Downloads ─────────────────────────────────────────────────────────

        /// <summary>Shot a downloadable row: download that one song.</summary>
        public static void DownloadOne(string stem)
        {
            if (DownloadingAll) return;
            Song s = Find(stem);
            if (s == null || State(stem).downloading) return;
            MelonCoroutines.Start(DownloadOneCo(stem, s));
        }

        private static IEnumerator DownloadOneCo(string stem, Song s)
        {
            bool ok = false;
            yield return DownloadEntry(stem, s, r => ok = r);
            if (ok)
                yield return ReloadAndRefresh();
        }

        /// <summary>Shot "Download all": download every found entry of the playlist, one at a time.</summary>
        public static void DownloadAll(string playlistName)
        {
            if (DownloadingAll) return;
            if (PlaylistManager.playlists == null || !PlaylistManager.playlists.ContainsKey(playlistName)) return;
            MelonCoroutines.Start(DownloadAllCo(playlistName));
        }

        private static IEnumerator DownloadAllCo(string playlistName)
        {
            var stems = new List<string>();
            var songs = new List<Song>();
            foreach (string stem in PlaylistNav.GetMissingStems(playlistName))
            {
                Song s = Find(stem);
                if (s == null || State(stem).downloading) continue;
                stems.Add(stem);
                songs.Add(s);
            }
            if (stems.Count == 0) yield break;

            DownloadingAll = true;
            downloadAllPlaylist = playlistName;
            downloadAllTotal = stems.Count;
            downloadAllDone = 0;
            int succeeded = 0;

            for (int i = 0; i < stems.Count; i++)
            {
                downloadAllDone++;
                VirtualSongList.UpdateActionRowText(DownloadAllActionId, "Download all", DownloadAllSubLabel());

                bool ok = false;
                yield return DownloadEntry(stems[i], songs[i], r => ok = r); // sequential, one at a time
                if (ok) succeeded++;
            }

            DebugLog.Log($"[PlaylistDownload] download all for '{playlistName}': {succeeded}/{downloadAllTotal} succeeded");
            DownloadingAll = false;
            downloadAllPlaylist = null;

            if (succeeded > 0)
                yield return ReloadAndRefresh();
            else
                RefreshOpenPlaylist();
        }

        /// <summary>Downloads one entry, showing progress on its row. Reports success through onDone.</summary>
        private static IEnumerator DownloadEntry(string stem, Song s, Action<bool> onDone)
        {
            DownloadState st = State(stem);
            st.downloading = true;
            st.failed = false;
            st.percent = 0;
            VirtualSongList.UpdateActionRowText(s.song_id, s.title, "Downloading... 0%");

            bool finished = false;
            bool success = false;
            try
            {
                MelonCoroutines.Start(SongDownloader.DownloadSong(s.song_id, s.download_url,
                    (id, ok) => { success = ok; finished = true; },
                    (id, progress) =>
                    {
                        int p = Mathf.Clamp((int)(progress * 100f), 0, 100);
                        if (p == st.percent) return;
                        st.percent = p;
                        VirtualSongList.UpdateActionRowText(s.song_id, s.title, $"Downloading... {p}%");
                    }));
            }
            catch (Exception ex)
            {
                DebugLog.Log($"[PlaylistDownload] download of {s.song_id} failed to start: {ex.Message}");
                finished = true;
            }

            while (!finished) yield return null;

            st.downloading = false;
            st.failed = !success;
            st.percent = -1;
            DebugLog.Log($"[PlaylistDownload] download of {s.song_id} {(success ? "done" : "failed")}");
            if (!success)
                VirtualSongList.UpdateActionRowText(s.song_id, s.title, "Download failed - shoot to retry");

            onDone?.Invoke(success);
        }

        private static IEnumerator ReloadAndRefresh()
        {
            ExScoring.ReloadSongList(false);
            // Let the reload's post-process settle so the new songs resolve as installed.
            yield return null;
            yield return null;
            yield return null;
            RefreshOpenPlaylist();
        }

        /// <summary>Redraws the list only while a playlist's contents are on screen.</summary>
        private static void RefreshOpenPlaylist()
        {
            if (FolderRowManager.CurrentPlaylistContext == null) return;
            if (PlaylistNav.Creating) return; // don't fight the in-row name entry
            FolderRowManager.RefreshList();
        }
    }
}