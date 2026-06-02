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

            string folder = SongFolderManager.GetFolder(songID);
            if (folder != null && SongFolderManager.openFolder != folder)
            {
                SongFolderManager.openFolder = folder;
                Apply();
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
                int count =
                    folder == SongFolderManager.FolderFavorites ? favCount :
                    folder == SongFolderManager.searchFolderName ? searchCount :
                    (counts.TryGetValue(folder, out int c) ? c : 0);

                rows.Add(ViewRow.Header(folder, count));

                if (folder == open)
                    foreach (string id in GetFolderSongs(folder))
                        rows.Add(ViewRow.SongRow(id));
            }

            // DIAGNOSTIC: confirm the Playlists row is present at index 0.
            MelonLogger.Log($"[FolderRowManager] BuildRootView: {rows.Count} rows; " +
                $"row0 = {(rows.Count > 0 ? rows[0].kind + " '" + (rows[0].label ?? rows[0].folderName) + "'" : "none")}");

            return rows;
        }

        private static List<string> GetFolderSongs(string folder)
        {
            var result = new List<string>();

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
    }
}