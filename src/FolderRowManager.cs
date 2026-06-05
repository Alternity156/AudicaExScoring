using System;
using System.Collections;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;

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

        // ── Sort mode (drives which folder set BuildRootView produces) ─────────
        internal enum SortMode { Default, AToZ, ZToA, MostStars, LeastStars, MostPlayed, LeastPlayed, MostRecent }
        private static SortMode currentSort = SortMode.Default;

        // ── Search folder maudica integration ─────────────────────────────────
        private static readonly UnityEngine.Color SR_LoadMoreColor = new UnityEngine.Color(0.22f, 0.22f, 0.30f, 1f);
        private static readonly HashSet<string> searchDownloading = new HashSet<string>();
        private static readonly Dictionary<string, int> searchLastPct = new Dictionary<string, int>();
        private static bool searchDownloadingAll;

        /// <summary>
        /// Called from the ChangeSort postfix. Maps the game's Sort to our view mode.
        /// Unimplemented sorts (played/recent) fall back to Default for now.
        /// Switching modes collapses any open folder and leaves playlist nav, since
        /// generated folders (letters / star tiers) only exist at the root level.
        /// </summary>
        public static void SetSort(SongSelect.Sort gameSort)
        {
            SortMode mode;
            switch (gameSort)
            {
                case SongSelect.Sort.AToZ: mode = SortMode.AToZ; break;
                case SongSelect.Sort.ZToA: mode = SortMode.ZToA; break;
                case SongSelect.Sort.MostStars: mode = SortMode.MostStars; break;
                case SongSelect.Sort.LeastStars: mode = SortMode.LeastStars; break;
                case SongSelect.Sort.MostPlayed: mode = SortMode.MostPlayed; break;
                case SongSelect.Sort.LeastPlayed: mode = SortMode.LeastPlayed; break;
                case SongSelect.Sort.MostRecent: mode = SortMode.MostRecent; break;
                default: mode = SortMode.Default; break;
            }

            if (mode == currentSort) return;
            currentSort = mode;

            ResetNav();                          // generated folders only exist at root
            SongFolderManager.openFolder = null; // collapse on mode switch

            MelonLogger.Log($"[FolderRowManager] Sort -> {currentSort}");

            if (VirtualSongList.IsActive)
                Apply();
        }

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

            ClearStarCache(); // scores may have changed since last entry; recompute lazily on demand
            WireHandler();
            Apply();

            // Tier 2: warm the placeholder pool toward the largest view we might show, in the
            // background, so the first open of the biggest folder is allocation-free.
            VirtualSongList.WarmPlaceholderPool(EstimateMaxViewSize());
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
                string folder = CurrentFolderFor(songID);
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

        /// <summary>Root view dispatcher: Default folders, or generated letter folders.</summary>
        private static List<ViewRow> BuildRootView()
        {
            switch (currentSort)
            {
                case SortMode.AToZ: return BuildAlphaView(reversed: false);
                case SortMode.ZToA: return BuildAlphaView(reversed: true);
                case SortMode.MostStars: return BuildStarsView(reversed: false);
                case SortMode.LeastStars: return BuildStarsView(reversed: true);
                case SortMode.MostPlayed: return BuildPlaysView(reversed: false);
                case SortMode.LeastPlayed: return BuildPlaysView(reversed: true);
                case SortMode.MostRecent: return BuildRecentView();
                default: return BuildDefaultRootView();
            }
        }

        /// <summary>
        /// Per-folder header color. Hardcoded/virtual folders use the lighter playlist grey;
        /// custom subfolders fall back to the default folder grey. Add cases here to give a
        /// specific folder its own color later.
        /// </summary>
        private static Color FolderColorFor(string folder)
        {
            switch (folder)
            {
                case SongFolderManager.FolderFavorites:
                case SongFolderManager.FolderAudica:
                case SongFolderManager.FolderAudicaDLC:
                case SongFolderManager.FolderCustom:      // "Unsorted"
                case SongFolderManager.FolderSongRequests:
                    return PlaylistNav.PlaylistRowColor;
                default:
                    // Search-results folder (dynamic name) also gets the new color.
                    if (folder == SongFolderManager.searchFolderName)
                        return PlaylistNav.PlaylistRowColor;
                    return ViewRow.FolderColor;
            }
        }

        /// <summary>Level 0: every folder header (open folder's songs inlined) + a Playlists row.</summary>
        private static List<ViewRow> BuildDefaultRootView()
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
                    rows.Add(ViewRow.Header(folder, total, sub, FolderColorFor(folder)));
                }
                else
                {
                    int count =
                        folder == SongFolderManager.FolderFavorites ? favCount :
                        folder == SongFolderManager.searchFolderName ? (searchCount + GetSearchDownloadables().Count) :
                        (counts.TryGetValue(folder, out int c) ? c : 0);
                    rows.Add(ViewRow.Header(folder, count, color: FolderColorFor(folder)));
                }

                if (folder == open)
                {
                    if (folder == SongFolderManager.FolderSongRequests)
                        AppendSongRequestRows(rows);
                    else if (folder == SongFolderManager.searchFolderName)
                        AppendSearchRows(rows);
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

        /// <summary>
        /// Estimate the largest row count any single view could reach: root header/action rows plus
        /// the biggest single folder's songs (only one folder is open at a time). Sizes the Tier 2
        /// pre-grow. Over-estimates slightly on purpose; if a view ever exceeds it, Tier 1's lazy
        /// growth still covers the difference.
        /// </summary>
        private static int EstimateMaxViewSize()
        {
            if (SongList.I == null || SongList.I.songs == null) return 0;

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

            int maxFolder = favCount;
            foreach (var kv in counts) if (kv.Value > maxFolder) maxFolder = kv.Value;

            int headerRows = (SongFolderManager.availableFolders?.Count ?? 0) + 12; // folders + action-row slack
            return headerRows + maxFolder + 16; // extra slack for search/request rows
        }

        private static List<string> GetFolderSongs(string folder)
        {
            switch (currentSort)
            {
                case SortMode.AToZ:
                case SortMode.ZToA: return GetAlphaFolderSongs(folder);
                case SortMode.MostStars:
                case SortMode.LeastStars: return GetStarFolderSongs(folder);
                case SortMode.MostPlayed:
                case SortMode.LeastPlayed: return GetPlayFolderSongs(folder);
                case SortMode.MostRecent: return GetRecentFolderSongs(folder);
            }

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

        // ── Alphabetical view (A-Z / Z-A) ─────────────────────────────────────

        /// <summary>Bucket key for a title: uppercase first letter, or "#" for digits/symbols/empty.</summary>
        private static string AlphaBucket(string trimmedTitle)
        {
            if (string.IsNullOrEmpty(trimmedTitle)) return "#";
            char c = trimmedTitle[0];
            return char.IsLetter(c) ? char.ToUpperInvariant(c).ToString() : "#";
        }

        /// <summary>
        /// Group every visible song by its title's first character. Buckets are ordered
        /// "#", A..Z (reversed wholesale for Z-A). Songs within each bucket are always
        /// sorted by title ascending, case-insensitive — only the folder order flips.
        /// </summary>
        private static List<KeyValuePair<string, List<string>>> BuildAlphaGroups(bool reversed)
        {
            var tmp = new Dictionary<string, List<KeyValuePair<string, string>>>(); // key -> (title, id)

            for (int i = 0; i < SongList.I.songs.Count; i++)
            {
                var sd = SongList.I.songs[i];
                if (sd == null || sd.hidden) continue;

                string title = sd.title ?? "";
                string key = AlphaBucket(title.TrimStart());

                if (!tmp.TryGetValue(key, out var list))
                {
                    list = new List<KeyValuePair<string, string>>();
                    tmp[key] = list;
                }
                list.Add(new KeyValuePair<string, string>(title, sd.songID));
            }

            var keys = new List<string>(tmp.Keys);
            keys.Sort(StringComparer.Ordinal); // "#" (0x23) sorts before A..Z
            if (reversed) keys.Reverse();

            var groups = new List<KeyValuePair<string, List<string>>>();
            foreach (string k in keys)
            {
                var entries = tmp[k];
                entries.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));

                var ids = new List<string>(entries.Count);
                foreach (var e in entries) ids.Add(e.Value);
                groups.Add(new KeyValuePair<string, List<string>>(k, ids));
            }
            return groups;
        }

        /// <summary>Letter-folder view: a header per non-empty bucket (with count), open bucket's songs inlined.</summary>
        private static List<ViewRow> BuildAlphaView(bool reversed)
        {
            var rows = new List<ViewRow>();
            var groups = BuildAlphaGroups(reversed);
            string open = SongFolderManager.openFolder;

            foreach (var kv in groups)
            {
                rows.Add(ViewRow.Header(kv.Key, kv.Value.Count));
                if (kv.Key == open)
                    foreach (string id in kv.Value)
                        rows.Add(ViewRow.SongRow(id));
            }

            MelonLogger.Log($"[FolderRowManager] BuildAlphaView ({(reversed ? "Z-A" : "A-Z")}): " +
                $"{groups.Count} letter folder(s), open={open ?? "none"}");
            return rows;
        }

        /// <summary>Songs in one letter bucket, in display (ascending) order.</summary>
        private static List<string> GetAlphaFolderSongs(string letter)
        {
            foreach (var kv in BuildAlphaGroups(false))
                if (kv.Key == letter) return kv.Value;
            return new List<string>();
        }

        /// <summary>The folder a song lives in under the active sort (Default folder, letter, or star bucket).</summary>
        public static string CurrentFolderFor(string songID)
        {
            if (currentSort == SortMode.Default)
                return SongFolderManager.GetFolder(songID);

            if (currentSort == SortMode.MostStars || currentSort == SortMode.LeastStars)
            {
                EnsureStarCache();
                return StarBucketLabel(starCache.TryGetValue(songID, out int e) ? e : -1);
            }

            if (currentSort == SortMode.MostPlayed || currentSort == SortMode.LeastPlayed)
            {
                EnsurePlayCache();
                return PlayLabel(playCache.TryGetValue(songID, out int pc) ? pc : 0);
            }

            if (currentSort == SortMode.MostRecent)
            {
                EnsureRecentCache();
                return RecentLabel(recentCache.TryGetValue(songID, out int da) ? da : -1);
            }

            if (SongList.I != null && SongList.I.songs != null)
            {
                for (int i = 0; i < SongList.I.songs.Count; i++)
                {
                    var sd = SongList.I.songs[i];
                    if (sd != null && !sd.hidden && sd.songID == songID)
                        return AlphaBucket((sd.title ?? "").TrimStart());
                }
            }
            return null;
        }

        // ── Star view (MostStars / LeastStars) ─────────────────────────────────
        //
        // Buckets by each song's best result (HighScoreRecords.GetHighScore + StarThresholds):
        // a single "Gold Stars" bucket (tier 6, Expert), then "{N} Stars ({difficulty})" for 1..5
        // per difficulty, then "Unplayed". Headers reuse the game's star icons (see VirtualSongList).
        //
        // Encoding in starCache: -1 = unplayed; otherwise (int)difficulty * 100 + tier (tier 1..6).
        private static Dictionary<string, int> starCache;
        private static readonly Dictionary<int, string> diffNameCache = new Dictionary<int, string>();

        /// <summary>Drop the cached star tiers, play counts and recency (e.g. on song-select entry, after a play).</summary>
        public static void ClearStarCache()
        {
            starCache = null;
            diffNameCache.Clear();
            playCache = null;
            recentCache = null;
        }

        /// <summary>Compute every visible song's star tier once. Lazy: only runs when a star view is needed.</summary>
        private static void EnsureStarCache()
        {
            if (starCache != null) return;
            starCache = new Dictionary<string, int>();

            var st = StarThresholds.I;
            for (int i = 0; i < SongList.I.songs.Count; i++)
            {
                var sd = SongList.I.songs[i];
                if (sd == null || sd.hidden) continue;
                string id = sd.songID;

                int enc = -1;
                try
                {
                    var best = HighScoreRecords.GetHighScore(id);
                    if (best != null && best.score > 0)
                    {
                        int raw = 1;
                        if (st != null)
                            raw = UnityEngine.Mathf.FloorToInt(st.GetStarCount(id, best.difficulty, best.score));
                        if (raw >= 1) // a real result; raw < 1 (no stars) stays "unplayed"
                            enc = (int)best.difficulty * 100 + (raw > 6 ? 6 : raw);
                    }
                }
                catch (System.Exception ex)
                {
                    MelonLogger.Log($"[FolderRowManager] star calc failed for {id}: {ex.Message}");
                }
                starCache[id] = enc;
            }

            MelonLogger.Log($"[FolderRowManager] Star cache built: {starCache.Count} songs");
        }

        private static string StarLabel(int tier) => tier == 6 ? "Gold Stars" : (tier == 1 ? "1 Star" : tier + " Stars");

        /// <summary>Localized difficulty name (cached). Falls back to the enum name.</summary>
        private static string DiffName(int diff)
        {
            if (diffNameCache.TryGetValue(diff, out string cached)) return cached;
            string name = null;
            try { name = KataConfig.GetDifficultyName((KataConfig.Difficulty)diff); }
            catch { }
            if (string.IsNullOrEmpty(name)) name = ((KataConfig.Difficulty)diff).ToString();
            diffNameCache[diff] = name;
            return name;
        }

        /// <summary>Canonical bucket label for an encoded tier. Must match between grouping and lookups.</summary>
        private static string StarBucketLabel(int enc)
        {
            if (enc == -1) return "Unplayed";
            int tier = enc % 100, diff = enc / 100;
            if (tier == 6) return "Gold Stars";                 // gold == Expert: one merged bucket
            return StarLabel(tier) + " (" + DiffName(diff) + ")";
        }

        /// <summary>
        /// Group songs into ordered star buckets. MostStars order: Gold, then Expert 5..1,
        /// Hard 5..1, Normal 5..1, Easy 5..1, then Unplayed. Reversed flips the whole list.
        /// Songs within a bucket are alphabetical by title.
        /// </summary>
        private static List<KeyValuePair<string, List<string>>> BuildStarGroups(bool reversed)
        {
            EnsureStarCache();

            var byLabel = new Dictionary<string, List<KeyValuePair<string, string>>>(); // label -> (title, id)
            var rank = new Dictionary<string, long>();                                   // label -> sort key (higher = nearer top)

            for (int i = 0; i < SongList.I.songs.Count; i++)
            {
                var sd = SongList.I.songs[i];
                if (sd == null || sd.hidden) continue;
                string id = sd.songID, title = sd.title ?? "";

                int enc = starCache.TryGetValue(id, out int e) ? e : -1;
                string label = StarBucketLabel(enc);

                long sortKey;
                if (enc == -1) sortKey = long.MinValue;          // Unplayed last in MostStars
                else
                {
                    int tier = enc % 100, diff = enc / 100;
                    sortKey = tier == 6 ? long.MaxValue          // Gold above everything
                                        : ((long)diff * 10 + tier); // difficulty desc, then stars desc
                }

                if (!byLabel.TryGetValue(label, out var list))
                {
                    list = new List<KeyValuePair<string, string>>();
                    byLabel[label] = list;
                }
                list.Add(new KeyValuePair<string, string>(title, id));
                rank[label] = sortKey;
            }

            var labels = new List<string>(byLabel.Keys);
            labels.Sort((a, b) => rank[b].CompareTo(rank[a])); // descending
            if (reversed) labels.Reverse();

            var result = new List<KeyValuePair<string, List<string>>>();
            foreach (string lbl in labels)
            {
                var entries = byLabel[lbl];
                entries.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));

                var ids = new List<string>(entries.Count);
                foreach (var kv in entries) ids.Add(kv.Value);
                result.Add(new KeyValuePair<string, List<string>>(lbl, ids));
            }
            return result;
        }

        /// <summary>Star-bucket view: a header per non-empty bucket (star icons via StarHeader, text for Unplayed),
        /// with the open bucket's songs inlined.</summary>
        private static List<ViewRow> BuildStarsView(bool reversed)
        {
            var rows = new List<ViewRow>();
            var groups = BuildStarGroups(reversed);
            string open = SongFolderManager.openFolder;

            foreach (var kv in groups)
            {
                string label = kv.Key;
                int count = kv.Value.Count;

                if (label == "Unplayed" || kv.Value.Count == 0)
                {
                    rows.Add(ViewRow.StarHeader(label, count, 0, 0)); // pips only, no filled stars
                }
                else
                {
                    int enc = starCache.TryGetValue(kv.Value[0], out int e) ? e : -1;
                    int tier = enc % 100, diff = enc / 100;
                    rows.Add(ViewRow.StarHeader(label, count, diff, tier));
                }

                if (label == open)
                    foreach (string id in kv.Value)
                        rows.Add(ViewRow.SongRow(id));
            }

            MelonLogger.Log($"[FolderRowManager] BuildStarsView ({(reversed ? "Least" : "Most")}Stars): " +
                $"{groups.Count} bucket(s), open={open ?? "none"}");
            return rows;
        }

        /// <summary>Songs in one star bucket, in display (alphabetical) order.</summary>
        private static List<string> GetStarFolderSongs(string folder)
        {
            foreach (var kv in BuildStarGroups(false))
                if (kv.Key == folder) return kv.Value;
            return new List<string>();
        }

        // ── Play-count view (MostPlayed / LeastPlayed) ─────────────────────────
        //
        // Buckets by each song's exact play count (SongPlayHistory.GetRecentPlayHistory.playCount):
        // "Played N times" / "Played once" / "Never Played". Plain text headers (no icons).
        private static Dictionary<string, int> playCache;

        /// <summary>Compute every visible song's play count once. Lazy: only runs when a play view is needed.</summary>
        private static void EnsurePlayCache()
        {
            if (playCache != null) return;
            playCache = new Dictionary<string, int>();

            var hist = SongPlayHistory.I;
            for (int i = 0; i < SongList.I.songs.Count; i++)
            {
                var sd = SongList.I.songs[i];
                if (sd == null || sd.hidden) continue;
                string id = sd.songID;

                int count = 0;
                try
                {
                    var rec = hist != null ? hist.GetRecentPlayHistory(id) : null;
                    if (rec != null && rec.playCount > 0) count = rec.playCount;
                }
                catch (System.Exception ex)
                {
                    MelonLogger.Log($"[FolderRowManager] play count failed for {id}: {ex.Message}");
                }
                playCache[id] = count;
            }

            MelonLogger.Log($"[FolderRowManager] Play cache built: {playCache.Count} songs");
        }

        private static string PlayLabel(int count) =>
            count <= 0 ? "Never Played" : (count == 1 ? "Played once" : $"Played {count} times");

        /// <summary>
        /// Group songs into ordered play-count buckets. MostPlayed: highest count first, Never Played last.
        /// Reversed flips the whole list. Songs within a bucket are alphabetical by title.
        /// </summary>
        private static List<KeyValuePair<string, List<string>>> BuildPlayGroups(bool reversed)
        {
            EnsurePlayCache();

            var byLabel = new Dictionary<string, List<KeyValuePair<string, string>>>(); // label -> (title, id)
            var rank = new Dictionary<string, int>();                                    // label -> play count (sort key)

            for (int i = 0; i < SongList.I.songs.Count; i++)
            {
                var sd = SongList.I.songs[i];
                if (sd == null || sd.hidden) continue;
                string id = sd.songID, title = sd.title ?? "";

                int count = playCache.TryGetValue(id, out int c) ? c : 0;
                string label = PlayLabel(count);

                if (!byLabel.TryGetValue(label, out var list))
                {
                    list = new List<KeyValuePair<string, string>>();
                    byLabel[label] = list;
                }
                list.Add(new KeyValuePair<string, string>(title, id));
                rank[label] = count; // count 0 (Never Played) naturally sorts lowest -> last in MostPlayed
            }

            var labels = new List<string>(byLabel.Keys);
            labels.Sort((a, b) => rank[b].CompareTo(rank[a])); // descending by play count
            if (reversed) labels.Reverse();

            var result = new List<KeyValuePair<string, List<string>>>();
            foreach (string lbl in labels)
            {
                var entries = byLabel[lbl];
                entries.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));

                var ids = new List<string>(entries.Count);
                foreach (var kv in entries) ids.Add(kv.Value);
                result.Add(new KeyValuePair<string, List<string>>(lbl, ids));
            }
            return result;
        }

        /// <summary>Play-count view: a text header per non-empty bucket, open bucket's songs inlined.</summary>
        private static List<ViewRow> BuildPlaysView(bool reversed)
        {
            var rows = new List<ViewRow>();
            var groups = BuildPlayGroups(reversed);
            string open = SongFolderManager.openFolder;

            foreach (var kv in groups)
            {
                rows.Add(ViewRow.Header(kv.Key, kv.Value.Count));
                if (kv.Key == open)
                    foreach (string id in kv.Value)
                        rows.Add(ViewRow.SongRow(id));
            }

            MelonLogger.Log($"[FolderRowManager] BuildPlaysView ({(reversed ? "Least" : "Most")}Played): " +
                $"{groups.Count} bucket(s), open={open ?? "none"}");
            return rows;
        }

        /// <summary>Songs in one play-count bucket, in display (alphabetical) order.</summary>
        private static List<string> GetPlayFolderSongs(string folder)
        {
            foreach (var kv in BuildPlayGroups(false))
                if (kv.Key == folder) return kv.Value;
            return new List<string>();
        }

        // ── Recency view (MostRecent) ──────────────────────────────────────────
        //
        // Buckets by how long ago each song was last played (RecentSongHistory.lastPlayDate, stored as
        // .NET DateTime ticks). Graduated: Today, Yesterday, exact days up to a month, then 30-day months,
        // then 365-day years; Never Played pinned at the bottom. Plain text headers. Single sort (no reverse).
        private static Dictionary<string, int> recentCache; // songID -> whole days since last play; -1 = never

        /// <summary>Compute every visible song's "days since last play" once. Lazy.</summary>
        private static void EnsureRecentCache()
        {
            if (recentCache != null) return;
            recentCache = new Dictionary<string, int>();

            var hist = SongPlayHistory.I;
            DateTime today = DateTime.Now.Date;
            for (int i = 0; i < SongList.I.songs.Count; i++)
            {
                var sd = SongList.I.songs[i];
                if (sd == null || sd.hidden) continue;
                string id = sd.songID;

                int daysAgo = -1; // never played
                try
                {
                    var rec = hist != null ? hist.GetRecentPlayHistory(id) : null;
                    if (rec != null && rec.playCount > 0 && rec.lastPlayDate > 0)
                    {
                        DateTime dt = DateTime.FromBinary(rec.lastPlayDate);
                        int d = (today - dt.Date).Days;
                        daysAgo = d < 0 ? 0 : d; // clamp future timestamps (clock skew) to Today
                    }
                }
                catch (System.Exception ex)
                {
                    MelonLogger.Log($"[FolderRowManager] recency failed for {id}: {ex.Message}");
                }
                recentCache[id] = daysAgo;
            }

            MelonLogger.Log($"[FolderRowManager] Recent cache built: {recentCache.Count} songs");
        }

        /// <summary>Graduated recency label: days, then 30-day months, then 365-day years.</summary>
        private static string RecentLabel(int daysAgo)
        {
            if (daysAgo < 0) return "Never Played";
            if (daysAgo == 0) return "Today";
            if (daysAgo == 1) return "Yesterday";
            if (daysAgo < 30) return $"{daysAgo} days ago";
            if (daysAgo < 365) { int m = daysAgo / 30; return m == 1 ? "1 month ago" : $"{m} months ago"; }
            int y = daysAgo / 365;
            return y == 1 ? "1 year ago" : $"{y} years ago";
        }

        /// <summary>Group songs into ordered recency buckets, most recent first, Never Played last.
        /// Songs within a bucket are alphabetical by title.</summary>
        private static List<KeyValuePair<string, List<string>>> BuildRecentGroups()
        {
            EnsureRecentCache();

            var byLabel = new Dictionary<string, List<KeyValuePair<string, string>>>(); // label -> (title, id)
            var rank = new Dictionary<string, int>();                                    // label -> days (sort key, ascending)

            for (int i = 0; i < SongList.I.songs.Count; i++)
            {
                var sd = SongList.I.songs[i];
                if (sd == null || sd.hidden) continue;
                string id = sd.songID, title = sd.title ?? "";

                int daysAgo = recentCache.TryGetValue(id, out int d) ? d : -1;
                string label = RecentLabel(daysAgo);
                int key = daysAgo < 0 ? int.MaxValue : daysAgo; // Never Played sorts last

                if (!byLabel.TryGetValue(label, out var list))
                {
                    list = new List<KeyValuePair<string, string>>();
                    byLabel[label] = list;
                    rank[label] = key;
                }
                else if (key < rank[label]) rank[label] = key; // most-recent member represents the bucket
                list.Add(new KeyValuePair<string, string>(title, id));
            }

            var labels = new List<string>(byLabel.Keys);
            labels.Sort((a, b) => rank[a].CompareTo(rank[b])); // ascending days = most recent first

            var result = new List<KeyValuePair<string, List<string>>>();
            foreach (string lbl in labels)
            {
                var entries = byLabel[lbl];
                entries.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));

                var ids = new List<string>(entries.Count);
                foreach (var kv in entries) ids.Add(kv.Value);
                result.Add(new KeyValuePair<string, List<string>>(lbl, ids));
            }
            return result;
        }

        /// <summary>Recency view: a text header per non-empty bucket, open bucket's songs inlined.</summary>
        private static List<ViewRow> BuildRecentView()
        {
            var rows = new List<ViewRow>();
            var groups = BuildRecentGroups();
            string open = SongFolderManager.openFolder;

            foreach (var kv in groups)
            {
                rows.Add(ViewRow.Header(kv.Key, kv.Value.Count));
                if (kv.Key == open)
                    foreach (string id in kv.Value)
                        rows.Add(ViewRow.SongRow(id));
            }

            MelonLogger.Log($"[FolderRowManager] BuildRecentView (MostRecent): {groups.Count} bucket(s), open={open ?? "none"}");
            return rows;
        }

        /// <summary>Songs in one recency bucket, in display (alphabetical) order.</summary>
        private static List<string> GetRecentFolderSongs(string folder)
        {
            foreach (var kv in BuildRecentGroups())
                if (kv.Key == folder) return kv.Value;
            return new List<string>();
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

        /// <summary>maudica results that aren't already installed locally, deduped by id.</summary>
        private static List<Song> GetSearchDownloadables()
        {
            var result = new List<Song>();
            if (SongSearch.webResults == null) return result;

            var seen = new HashSet<string>();
            foreach (var s in SongSearch.webResults)
            {
                if (s == null) continue;
                string id = s.song_id ?? "";
                if (id.Length > 0 && !seen.Add(id)) continue;                          // dupe within web list
                if (FindLocalRequestSong(s.title, s.artist, s.author, s.song_id) != null) continue; // already installed
                result.Add(s);
            }
            return result;
        }

        /// <summary>
        /// Rows for the open search folder: a "Download all" action (top), installed matches as
        /// normal song rows, not-installed maudica matches as downloadable rows, and a "Load more"
        /// row at the bottom while more pages remain.
        /// </summary>
        private static void AppendSearchRows(List<ViewRow> rows)
        {
            var downloadable = GetSearchDownloadables();

            // "Download all" pinned at the top (mirrors Song Requests).
            if (downloadable.Count > 0 || searchDownloadingAll)
                rows.Add(ViewRow.ActionRow("Download all", SR_DownloadAllColor, OnDownloadAllSearch,
                         searchDownloadingAll ? "Downloading..." : $"{downloadable.Count} song{(downloadable.Count != 1 ? "s" : "")}",
                         "search_downloadall"));

            // Installed matches as normal, playable song rows.
            if (SongSearch.searchResult != null)
                foreach (string id in SongSearch.searchResult)
                    rows.Add(ViewRow.SongRow(id));

            // Not-installed maudica matches as downloadable rows.
            foreach (var s in downloadable)
                rows.Add(ViewRow.DownloadableSongRow(s.song_id, s.title, s.artist, s.author, s.download_url,
                         SR_DownloadableColor, () => OnDownloadSearchSong(s)));

            // "Load more" at the very bottom while more maudica pages remain (or a page is loading).
            if (SongSearch.HasMoreWebPages || SongSearch.webLoading)
                rows.Add(ViewRow.ActionRow(
                    SongSearch.webLoading ? "Loading..." : "Load more",
                    SR_LoadMoreColor, OnLoadMoreSearch, null, "search_loadmore"));
        }

        private static void OnLoadMoreSearch()
        {
            SongSearch.LoadMoreWeb();
            RefreshList(); // show "Loading..." now; the fetch callback refreshes again
        }

        private static void OnDownloadSearchSong(Song s)
        {
            if (searchDownloadingAll || s == null) return;
            string id = s.song_id;
            if (string.IsNullOrEmpty(id)) return;
            if (!searchDownloading.Add(id)) return; // already downloading

            searchLastPct[id] = -1;
            VirtualSongList.UpdateActionRowText(id, s.title, "Downloading... 0%");
            MelonCoroutines.Start(SongDownloader.DownloadSong(
                id, s.download_url,
                (sid, ok) => OnSearchDownloadComplete(s, ok),
                (sid, pct) => OnSearchDownloadProgress(s, pct)));
        }

        private static void OnSearchDownloadProgress(Song s, float pct)
        {
            string id = s.song_id;
            int p = (int)(pct * 100f);
            if (p < 0) p = 0;
            if (p > 100) p = 100;
            if (searchLastPct.TryGetValue(id, out int last) && last == p) return;
            searchLastPct[id] = p;
            VirtualSongList.UpdateActionRowText(id, s.title, $"Downloading... {p}%");
        }

        private static void OnSearchDownloadComplete(Song s, bool success)
        {
            string id = s.song_id;
            searchDownloading.Remove(id);
            searchLastPct.Remove(id);

            if (!success)
            {
                VirtualSongList.UpdateActionRowText(id, s.title, "Download failed - tap to retry");
                return;
            }

            ExScoring.ReloadSongList(false);
            MelonCoroutines.Start(RefreshSearchAfterReload());
        }

        private static IEnumerator RefreshSearchAfterReload()
        {
            // Let ReloadSongList's async post-process settle, then re-match locals so the new
            // song shows as a normal (playable) row and drops out of the downloadable list.
            yield return null;
            yield return null;
            yield return null;
            SongSearch.Search();
            RefreshList();
        }

        private static void OnDownloadAllSearch()
        {
            if (searchDownloadingAll) return;
            MelonCoroutines.Start(DownloadAllSearchCo());
        }

        private static IEnumerator DownloadAllSearchCo()
        {
            searchDownloadingAll = true;

            var entries = GetSearchDownloadables();
            int total = entries.Count, done = 0;
            foreach (var s in entries)
            {
                done++;
                VirtualSongList.UpdateActionRowText("search_downloadall", "Download all", $"Downloading {done}/{total}...");

                bool finished = false;
                MelonCoroutines.Start(SongDownloader.DownloadSong(
                    s.song_id, s.download_url, (id, ok) => { finished = true; }, null));
                while (!finished) yield return null; // sequential — one at a time
            }

            ExScoring.ReloadSongList(false);
            searchDownloadingAll = false;
            yield return null;
            yield return null;
            yield return null;
            SongSearch.Search();
            RefreshList();
        }
    }
}