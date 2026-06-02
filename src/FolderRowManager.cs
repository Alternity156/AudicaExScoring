using System;
using System.Collections;
using System.Collections.Generic;
using MelonLoader;

namespace ExScoringMod
{
    /// <summary>
    /// PHASE 2 — Folder orchestration on top of VirtualSongList.
    ///
    /// FolderRowManager no longer builds or reparents GameObjects. It computes the ordered
    /// row list (all folder headers, with the open folder's songs inlined) and PUSHES it to
    /// VirtualSongList via SetView. VirtualSongList owns all the visuals and recycling.
    ///
    /// The old "let the game build every song, then hide/reparent" model is gone — the game
    /// is told to build nothing (InjectAllSongs clears the id list), and the virtual list
    /// renders only what's on screen.
    /// </summary>
    internal static class FolderRowManager
    {
        private static bool handlerWired = false;

        // ── Navigation level (playlists) ──────────────────────────────────────
        internal enum NavLevel { Root, PlaylistList, PlaylistContents, AddPicker }
        private static NavLevel level = NavLevel.Root;
        private static string currentPlaylist = null;
        private static readonly Stack<float> scrollStack = new Stack<float>();

        // ── Entry points (called from Hooks / search / favorites) ─────────────

        /// <summary>
        /// Called from the SongSelect.ShowSongList postfix. Builds the view and hands it to
        /// VirtualSongList. (The game's own build produced nothing — see InjectAllSongs.)
        /// </summary>
        public static void Rebuild(SongSelect songSelect)
        {
            if (songSelect == null) return;
            if (SongFolderManager.availableFolders == null || SongFolderManager.availableFolders.Count == 0)
                return;

            WireHandler();
            Apply();
        }

        /// <summary>Shot a folder header: toggle it open/closed and rebuild the view.</summary>
        public static void ToggleFolder(string folderName)
        {
            if (level != NavLevel.Root) return; // folder headers only exist at Level 0
            MarathonSetup.CancelIfActive();
            srClearArmed = false; // any folder toggle cancels a pending Clear List confirm
            bool opening = SongFolderManager.openFolder != folderName;
            SongFolderManager.openFolder = opening ? folderName : null;

            MelonLogger.Log($"[FolderRowManager] Toggle '{folderName}' -> open={SongFolderManager.openFolder ?? "none"}");

            Apply();

            if (opening)
            {
                // Keep scroll position; only scroll if the opened folder's first song is off-screen.
                VirtualSongList.RevealFolderIfNeeded(folderName);

                // If the current selection isn't in this folder (or nothing is selected),
                // select the folder's first song.
                var songs = GetFolderSongs(folderName);
                if (songs.Count > 0)
                {
                    string sel = ExScoring.selectedSong;
                    if (string.IsNullOrEmpty(sel) || !songs.Contains(sel))
                        VirtualSongList.SelectInView(songs[0]);
                }
            }
        }

        /// <summary>Rebuild the view in place (used after search / favorite changes).</summary>
        public static void RefreshList()
        {
            if (!VirtualSongList.IsActive) return; // list not currently shown
            WireHandler();
            Apply();

            // Make sure a freshly-opened virtual folder (e.g. search results) is visible.
            if (SongFolderManager.openFolder != null)
                VirtualSongList.RevealFolderIfNeeded(SongFolderManager.openFolder);
        }

        public static void RefreshFavorites() => RefreshList();

        // ── Playlist navigation (drill-in / back) ─────────────────────────────

        public static void EnterPlaylistList()
        {
            MarathonSetup.CancelIfActive();
            PlaylistNav.ClearTransient();
            scrollStack.Push(VirtualSongList.GetScroll());
            level = NavLevel.PlaylistList;
            VirtualSongList.SetView(BuildView(), 0f);
        }

        public static void EnterPlaylistContents(string playlistName)
        {
            MarathonSetup.CancelIfActive();
            PlaylistNav.ClearTransient();
            scrollStack.Push(VirtualSongList.GetScroll());
            currentPlaylist = playlistName;
            level = NavLevel.PlaylistContents;
            VirtualSongList.SetView(BuildView(), 0f);

            // Select the first resolved song so the launch panel reflects the playlist.
            var ids = VirtualSongList.CurrentViewSongIDs;
            if (ids.Count > 0) VirtualSongList.SelectInView(ids[0]);
        }

        public static void NavBack()
        {
            MarathonSetup.CancelIfActive();
            PlaylistNav.ClearTransient();
            if (level == NavLevel.PlaylistContents) { level = NavLevel.PlaylistList; currentPlaylist = null; }
            else if (level == NavLevel.PlaylistList) { level = NavLevel.Root; }
            else return;

            float restore = scrollStack.Count > 0 ? scrollStack.Pop() : 0f;
            VirtualSongList.SetView(BuildView(), restore);
        }

        /// <summary>Force back to Level 0 (e.g. wire to a leave-song-page hook if desired).</summary>
        public static void ResetNav()
        {
            level = NavLevel.Root;
            currentPlaylist = null;
            pendingAddStem = null;
            scrollStack.Clear();
        }

        public static bool InPlaylistNav => level != NavLevel.Root;

        // ── Transient Add-to-Playlist picker ──────────────────────────────────
        private static string pendingAddStem = null;
        private static NavLevel addReturnLevel = NavLevel.Root;
        private static string addReturnFolder = null;
        private static float addReturnScroll = 0f;
        private static string addReturnSong = null;

        /// <summary>Snapshot the current view and drill into the playlist picker for songStem.</summary>
        public static void EnterAddPicker(string songStem)
        {
            if (string.IsNullOrEmpty(songStem) || level == NavLevel.AddPicker) return;

            MarathonSetup.CancelIfActive();
            PlaylistNav.ClearTransient();

            addReturnLevel = level;
            addReturnFolder = SongFolderManager.openFolder;
            addReturnScroll = VirtualSongList.GetScroll();
            addReturnSong = ExScoring.selectedSong;
            pendingAddStem = songStem;

            level = NavLevel.AddPicker;
            VirtualSongList.SetView(BuildView(), 0f);
        }

        /// <summary>Picked a playlist in the add picker: add the song, then restore the snapshot.</summary>
        public static void AddPickerPick(string playlistName)
        {
            if (!string.IsNullOrEmpty(pendingAddStem))
                PlaylistManager.AddSongToPlaylist(playlistName, pendingAddStem);
            RestoreFromAddPicker();
        }

        public static void AddPickerCancel() => RestoreFromAddPicker();

        /// <summary>After a playlist is created: in the add picker, add the pending song to it;
        /// otherwise just refresh so the new playlist shows in the list.</summary>
        public static void OnPlaylistCreated(string name)
        {
            if (level == NavLevel.AddPicker) AddPickerPick(name);
            else RefreshList();
        }

        private static void RestoreFromAddPicker()
        {
            PlaylistNav.ClearTransient();
            pendingAddStem = null;
            level = addReturnLevel;
            SongFolderManager.openFolder = addReturnFolder;
            VirtualSongList.SetView(BuildView(), addReturnScroll);
            if (!string.IsNullOrEmpty(addReturnSong))
                VirtualSongList.ScrollToAndSelect(addReturnSong, true); // keep position if still visible
        }

        /// <summary>The playlist whose contents are currently shown, or null if not in one.</summary>
        public static string CurrentPlaylistContext => level == NavLevel.PlaylistContents ? currentPlaylist : null;

        /// <summary>Rebuild the current playlist-contents view after an add/remove, selecting the first song.</summary>
        public static void RefreshAfterPlaylistEdit()
        {
            RefreshList();
            var ids = VirtualSongList.CurrentViewSongIDs;
            if (ids.Count > 0) VirtualSongList.SelectInView(ids[0]);
        }

        /// <summary>
        /// Called from the SongSelect.GetSongIDs postfix. VirtualSongList owns the display now,
        /// so the game must NOT build the full list — clear the ids it would have built.
        /// </summary>
        public static void InjectAllSongs(Il2CppSystem.Collections.Generic.List<string> result)
        {
            if (SongFolderManager.availableFolders == null || SongFolderManager.availableFolders.Count == 0)
                return; // no folder system yet — leave the game's default behavior

            result.Clear();
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>Push the current view to VirtualSongList and refresh the Random Song button.</summary>
        private static void Apply()
        {
            VirtualSongList.SetView(BuildView());
            RandomSong.UpdateButtonState();
        }

        /// <summary>
        /// Open the folder containing songID (if it isn't already open) and select that song.
        /// Used by auto-select to restore the previously selected song on re-entry.
        /// </summary>
        public static void RevealAndSelect(string songID)
        {
            if (string.IsNullOrEmpty(songID)) return;

            // If the song is already in the current view (e.g. shown in the open Song Requests, Favorites,
            // or search folder), keep it there rather than jumping to its home folder. A downloaded request
            // lives in the requests folder AND in Unsorted, and GetFolder would send us to the Unsorted copy.
            if (VirtualSongList.IndexOf(songID) < 0)
            {
                string folder = SongFolderManager.GetFolder(songID);
                if (folder != null && SongFolderManager.openFolder != folder)
                {
                    SongFolderManager.openFolder = folder;
                    Apply();
                }
            }

            // Restore: keep the user's prior scroll if the song is already visible there,
            // otherwise center it.
            VirtualSongList.ScrollToAndSelect(songID, true);
        }

        private static void WireHandler()
        {
            if (handlerWired) return;
            VirtualSongList.FolderToggleHandler = ToggleFolder;
            handlerWired = true;
        }

        /// <summary>
        /// Build the full row list: every folder header in order, with the open folder's
        /// songs inlined right after its header. This is the shared "what's in view" source.
        /// </summary>
        public static List<ViewRow> BuildView()
        {
            switch (level)
            {
                case NavLevel.PlaylistList: return PlaylistNav.BuildPlaylistListView();
                case NavLevel.PlaylistContents: return PlaylistNav.BuildPlaylistContentsView(currentPlaylist);
                case NavLevel.AddPicker: return PlaylistNav.BuildAddPickerView();
                default: return BuildRootView();
            }
        }

        /// <summary>Level 0: every folder header (open folder's songs inlined) + a Playlists row.</summary>
        private static List<ViewRow> BuildRootView()
        {
            var rows = new List<ViewRow>();
            if (SongFolderManager.availableFolders == null) return rows;

            // One pass over the song list for normal-folder counts + favorites count.
            var counts = new Dictionary<string, int>();
            int favCount = 0;
            for (int i = 0; i < SongList.I.songs.Count; i++)
            {
                var sd = SongList.I.songs[i];
                if (sd == null || sd.hidden) continue;
                string f = SongFolderManager.GetFolder(sd.songID);
                if (f != null) counts[f] = counts.TryGetValue(f, out int c) ? c + 1 : 1;
                if (FavoritesStore.IsFavorite(sd.songID)) favCount++;
            }
            int searchCount = SongSearch.searchResult != null ? SongSearch.searchResult.Count : 0;

            // Entry into the playlist system (drills into Level 1) — pinned to the top.
            int plCount = PlaylistManager.playlists != null ? PlaylistManager.playlists.Count : 0;
            rows.Add(ViewRow.ActionRow("Playlists", PlaylistNav.PlaylistRowColor,
                EnterPlaylistList, $"{plCount} playlist{(plCount != 1 ? "s" : "")}"));

            string open = SongFolderManager.openFolder;
            foreach (string folder in SongFolderManager.availableFolders)
            {
                if (folder == SongFolderManager.FolderSongRequests)
                {
                    GetSongRequestCounts(out int total, out int missing);
                    string sub = missing > 0
                        ? $"{total} song{(total != 1 ? "s" : "")} ({missing} missing)"
                        : null; // null → header falls back to the default "{count} songs"
                    rows.Add(ViewRow.Header(folder, total, sub));
                }
                else
                {
                    int count =
                        folder == SongFolderManager.FolderFavorites ? favCount :
                        folder == SongFolderManager.searchFolderName ? searchCount :
                        (counts.TryGetValue(folder, out int c) ? c : 0);
                    rows.Add(ViewRow.Header(folder, count));
                }

                if (folder == open)
                {
                    if (folder == SongFolderManager.FolderSongRequests)
                        AppendSongRequestRows(rows);
                    else
                        foreach (string id in GetFolderSongs(folder))
                            rows.Add(ViewRow.SongRow(id));
                }
            }

            // DIAGNOSTIC: confirm the Playlists row is present at index 0.
            MelonLogger.Log($"[FolderRowManager] BuildRootView: {rows.Count} rows; " +
                $"row0 = {(rows.Count > 0 ? rows[0].kind + " '" + (rows[0].label ?? rows[0].folderName) + "'" : "none")}");

            return rows;
        }

        private static List<string> GetFolderSongs(string folder)
        {
            var result = new List<string>();

            if (folder == SongFolderManager.FolderSongRequests)
                return GetSongRequestSongIDs();

            if (folder == SongFolderManager.searchFolderName && SongSearch.searchResult != null)
            {
                for (int i = 0; i < SongSearch.searchResult.Count; i++)
                    result.Add(SongSearch.searchResult[i]);
                return result;
            }

            for (int i = 0; i < SongList.I.songs.Count; i++)
            {
                var sd = SongList.I.songs[i];
                if (sd == null || sd.hidden) continue;
                string id = sd.songID;

                if (folder == SongFolderManager.FolderFavorites)
                {
                    if (FavoritesStore.IsFavorite(id)) result.Add(id);
                }
                else if (SongFolderManager.GetFolder(id) == folder)
                {
                    result.Add(id);
                }
            }
            return result;
        }

        // ── Song Requests folder (optional SongRequest integration) ───────────────
        private static readonly UnityEngine.Color SR_DownloadableColor = new UnityEngine.Color(0.15f, 0.28f, 0.40f, 1f);
        private static readonly UnityEngine.Color SR_DownloadAllColor = new UnityEngine.Color(0.18f, 0.34f, 0.22f, 1f);

        // Two-shot Clear List: armed shows "Confirm Clear?" until shot again or the folder is toggled.
        private static bool srClearArmed;

        /// <summary>First shot arms the confirm; second shot (row now "Confirm Clear?") clears the whole list.</summary>
        private static void OnClearRequests()
        {
            if (!srClearArmed)
            {
                srClearArmed = true;
                RefreshList(); // re-render the row as "Confirm Clear?"
                return;
            }

            srClearArmed = false;

            // Clear available (downloaded) requests silently, then our missing/downloadable store.
            foreach (var a in SongRequestIntegration.GetAvailableRequests()) // a fresh copy — safe to remove during
                if (!string.IsNullOrEmpty(a.SongID))
                    SongRequestIntegration.RemoveRequest(a.SongID);
            SongRequestStore.Clear();
            SongRequestIntegration.EmitQueueCleared();

            RefreshList();
        }

        /// <summary>Counts for the Song Requests header: total = available (downloaded) + stored;
        /// missing = stored entries not yet downloaded locally.</summary>
        private static void GetSongRequestCounts(out int total, out int missing)
        {
            int available = SongRequestIntegration.GetAvailableRequests().Count;
            var stored = SongRequestStore.GetAll();
            missing = 0;
            foreach (var m in stored)
                if (FindLocalRequestSong(m.Title, m.Artist, m.Mapper, m.SongID) == null)
                    missing++;
            total = available + stored.Count;
        }

        /// <summary>Playable song IDs in the Song Requests folder, in display order (available, then
        /// already-downloaded store entries). Used for open-folder auto-select. Excludes not-yet-downloaded
        /// rows, which aren't selectable songs.</summary>
        private static List<string> GetSongRequestSongIDs()
        {
            var ids = new List<string>();
            var seen = new HashSet<string>();
            foreach (var a in SongRequestIntegration.GetAvailableRequests())
                if (!string.IsNullOrEmpty(a.SongID) && seen.Add(a.SongID))
                    ids.Add(a.SongID);
            foreach (var m in SongRequestStore.GetAll())
            {
                var local = FindLocalRequestSong(m.Title, m.Artist, m.Mapper, m.SongID);
                if (local != null && seen.Add(local.songID))
                    ids.Add(local.songID);
            }
            return ids;
        }

        /// <summary>True if a (local) song ID is a current request — an available request, or a
        /// downloaded-but-unreconciled store entry that resolves to it. Used to relabel the launch button.</summary>
        internal static bool IsSelectedSongRequest(string localSongID)
        {
            if (string.IsNullOrEmpty(localSongID)) return false;
            foreach (var a in SongRequestIntegration.GetAvailableRequests())
                if (a.SongID == localSongID) return true;
            foreach (var m in SongRequestStore.GetAll())
            {
                var local = FindLocalRequestSong(m.Title, m.Artist, m.Mapper, m.SongID);
                if (local != null && local.songID == localSongID) return true;
            }
            return false;
        }

        /// <summary>Remove a (local) song from the request list: drop it from SongRequest's available queue
        /// and from our store (matched by resolved local song), then refresh the view in place.</summary>
        internal static void RemoveSelectedRequest(string localSongID)
        {
            if (string.IsNullOrEmpty(localSongID)) return;
            SongRequestIntegration.RemoveRequest(localSongID); // available queue (no-op if not present)
            foreach (var m in SongRequestStore.GetAll())
            {
                var local = FindLocalRequestSong(m.Title, m.Artist, m.Mapper, m.SongID);
                if (local != null && local.songID == localSongID) { SongRequestStore.Remove(m.SongID); break; }
            }
            RefreshList();
        }

        /// <summary>
        /// Rows for the open Song Requests folder: a "Download all missing" action row (only when there is
        /// something to download), downloaded requests as normal song rows, and not-yet-downloaded store
        /// entries as downloadable rows.
        /// </summary>
        private static void AppendSongRequestRows(List<ViewRow> rows)
        {
            var available = SongRequestIntegration.GetAvailableRequests();
            var stored = SongRequestStore.GetAll();
            var seen = new HashSet<string>();
            var downloadable = new List<SongRequestStore.RequestedSong>();
            var localStoreIds = new List<string>();

            // Store entries that have since been downloaded show as normal song rows; the rest are downloadable.
            foreach (var m in stored)
            {
                var local = FindLocalRequestSong(m.Title, m.Artist, m.Mapper, m.SongID);
                if (local != null) localStoreIds.Add(local.songID);
                else downloadable.Add(m);
            }

            // "Clear List" (two-shot confirm) pinned at the very top whenever the list has anything.
            int totalRequests = available.Count + stored.Count;
            if (totalRequests > 0)
                rows.Add(ViewRow.ActionRow(
                    srClearArmed ? "Confirm Clear?" : "Clear List",
                    srClearArmed ? PlaylistNav.ConfirmDeleteColor : PlaylistNav.DeleteColor,
                    OnClearRequests,
                    srClearArmed ? "Shoot again to confirm" : $"{totalRequests} request{(totalRequests != 1 ? "s" : "")}",
                    "sr_clearlist"));

            // "Download all missing" pinned first — only when something is actually downloadable.
            if (downloadable.Count > 0)
                rows.Add(ViewRow.ActionRow("Download all missing", SR_DownloadAllColor, OnDownloadAllMissing,
                         $"{downloadable.Count} song{(downloadable.Count != 1 ? "s" : "")}", "sr_downloadall"));

            // Playable (downloaded) requests as normal song rows.
            foreach (var a in available)
                if (!string.IsNullOrEmpty(a.SongID) && seen.Add(a.SongID))
                    rows.Add(ViewRow.SongRow(a.SongID));
            foreach (string id in localStoreIds)
                if (seen.Add(id))
                    rows.Add(ViewRow.SongRow(id));

            // Not-yet-downloaded requests as downloadable rows.
            foreach (var m in downloadable)
            {
                if (!seen.Add(m.SongID)) continue;
                string id = m.SongID;
                rows.Add(ViewRow.DownloadableSongRow(id, m.Title, m.Artist, m.Mapper, m.DownloadURL,
                         SR_DownloadableColor, () => OnDownloadRequest(id)));
            }
        }

        /// <summary>Find a local song matching a stored request's metadata (mirrors the interceptor's match).</summary>
        private static SongList.SongData FindLocalRequestSong(string title, string artist, string mapper, string songId)
        {
            try
            {
                if (SongList.I == null || SongList.I.songs == null) return null;
                string id = (songId ?? "").ToLowerInvariant();
                string bTitle = (title ?? "").ToLowerInvariant().Trim();
                string bArtist = (artist ?? "").ToLowerInvariant().Replace(" ", "");
                string bMapper = (mapper ?? "").ToLowerInvariant().Replace(" ", "");

                for (int i = 0; i < SongList.I.songs.Count; i++)
                {
                    var s = SongList.I.songs[i];
                    if (s == null) continue;
                    if (id.Length > 0 && (s.songID?.ToLowerInvariant() ?? "") == id) return s;

                    string lTitle = (s.title ?? "").ToLowerInvariant().Trim();
                    string lArtist = (s.artist ?? "").ToLowerInvariant().Replace(" ", "");
                    string lAuthor = (s.author ?? "").ToLowerInvariant().Replace(" ", "");
                    bool titleExact = lTitle.Length > 0 && lTitle == bTitle;
                    bool artistOk = bArtist == "" || lArtist.Contains(bArtist);
                    bool mapperOk = bMapper == "" || lAuthor.Contains(bMapper);
                    if (titleExact && artistOk && mapperOk) return s;
                }
            }
            catch (System.Exception e) { MelonLogger.Log($"[FolderRowManager] FindLocalRequestSong failed: {e.Message}"); }
            return null;
        }

        // ── Song Requests downloads (3b) ──────────────────────────────────────────
        private static readonly HashSet<string> srDownloading = new HashSet<string>();
        private static readonly Dictionary<string, int> srLastPct = new Dictionary<string, int>();
        private static bool srDownloadingAll;

        private static SongRequestStore.RequestedSong FindStored(string songId)
        {
            foreach (var m in SongRequestStore.GetAll())
                if (m.SongID == songId) return m;
            return null;
        }

        private static void OnDownloadRequest(string songId)
        {
            if (srDownloadingAll) return;
            var entry = FindStored(songId);
            if (entry == null) return;
            if (!srDownloading.Add(songId)) return; // already downloading

            srLastPct[songId] = -1;
            VirtualSongList.UpdateActionRowText(songId, entry.Title, "Downloading... 0%");
            MelonCoroutines.Start(SongDownloader.DownloadSong(
                entry.SongID, entry.DownloadURL,
                (id, ok) => OnRequestDownloadComplete(entry, ok),
                (id, pct) => OnRequestDownloadProgress(entry, pct)));
        }

        private static void OnRequestDownloadProgress(SongRequestStore.RequestedSong entry, float pct)
        {
            int p = (int)(pct * 100f);
            if (p < 0) p = 0;
            if (p > 100) p = 100;
            if (srLastPct.TryGetValue(entry.SongID, out int last) && last == p) return; // throttle to whole %
            srLastPct[entry.SongID] = p;
            VirtualSongList.UpdateActionRowText(entry.SongID, entry.Title, $"Downloading... {p}%");
        }

        private static void OnRequestDownloadComplete(SongRequestStore.RequestedSong entry, bool success)
        {
            srDownloading.Remove(entry.SongID);
            srLastPct.Remove(entry.SongID);

            if (!success)
            {
                VirtualSongList.UpdateActionRowText(entry.SongID, entry.Title, "Download failed - tap to retry");
                return;
            }

            ReconcileDownloaded(entry);
            ExScoring.ReloadSongList(false);
            MelonCoroutines.Start(RefreshAfterReload());
        }

        /// <summary>Fold a freshly downloaded request into SongRequest's available queue and drop it from the store.</summary>
        private static void ReconcileDownloaded(SongRequestStore.RequestedSong entry)
        {
            var local = FindLocalRequestSong(entry.Title, entry.Artist, entry.Mapper, entry.SongID);
            if (local != null)
            {
                // Prime SongRequest's dedupe cache so AddRequest doesn't re-announce a song we already
                // announced at request time, then add it as a native available request.
                SongRequestIntegration.PrimeDownloadNameCache(local.songID);
                DateTime at = entry.RequestedAtUtcTicks > 0
                    ? new DateTime(entry.RequestedAtUtcTicks, DateTimeKind.Utc).ToLocalTime()
                    : DateTime.Now;
                SongRequestIntegration.AddAvailableRequest(local, entry.RequestedBy, at);
            }
            SongRequestStore.Remove(entry.SongID);
        }

        private static IEnumerator RefreshAfterReload()
        {
            // Let ReloadSongList's async post-process (folder/tracker rebuild) settle, then refresh in place.
            yield return null;
            yield return null;
            yield return null;
            RefreshList();
        }

        private static void OnDownloadAllMissing()
        {
            if (srDownloadingAll) return;
            MelonCoroutines.Start(DownloadAllMissingCo());
        }

        private static IEnumerator DownloadAllMissingCo()
        {
            srDownloadingAll = true;

            var entries = new List<SongRequestStore.RequestedSong>();
            foreach (var m in SongRequestStore.GetAll())
                if (FindLocalRequestSong(m.Title, m.Artist, m.Mapper, m.SongID) == null)
                    entries.Add(m);

            int total = entries.Count, done = 0;
            foreach (var entry in entries)
            {
                done++;
                VirtualSongList.UpdateActionRowText("sr_downloadall", "Download all missing", $"Downloading {done}/{total}...");

                bool finished = false, ok = false;
                MelonCoroutines.Start(SongDownloader.DownloadSong(
                    entry.SongID, entry.DownloadURL, (id, s) => { finished = true; ok = s; }, null));
                while (!finished) yield return null; // sequential — one at a time

                if (ok) ReconcileDownloaded(entry);
            }

            ExScoring.ReloadSongList(false);
            srDownloadingAll = false;
            yield return null;
            yield return null;
            yield return null;
            RefreshList();
        }
    }
}