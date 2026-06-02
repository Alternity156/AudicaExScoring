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

            // Playlists are handled by the legacy filter path for now (to be reworked).
            // When a playlist filter is active, yield the list back to the game.
            if (FilterPanel.IsFiltering("playlists"))
            {
                if (VirtualSongList.IsActive) VirtualSongList.Teardown();
                return;
            }

            WireHandler();
            Apply();
        }

        /// <summary>Shot a folder header: toggle it open/closed and rebuild the view.</summary>
        public static void ToggleFolder(string folderName)
        {
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

            VirtualSongList.ScrollToAndSelect(songID);
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
                if (FilterPanel.IsFavorite(sd.songID)) favCount++;
            }
            int searchCount = SongSearch.searchResult != null ? SongSearch.searchResult.Count : 0;

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
                    if (FilterPanel.IsFavorite(id)) result.Add(id);
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