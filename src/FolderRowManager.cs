using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using MelonLoader;
using TMPro;
using UnhollowerBaseLib;
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
        internal enum NavLevel { Root, PlaylistList, PlaylistContents, AddPicker, GlobalOptions, FolderEdit, FolderEditPicker }
        private static NavLevel level = NavLevel.Root;
        private static string currentPlaylist = null;
        private static readonly Stack<float> scrollStack = new Stack<float>();
        // Parallel to scrollStack: whether Wrap Song List (the toggle itself, not any particular
        // view's buffer size — sub-views like Options are short and always have buffer 0 regardless
        // of the toggle, so checking VirtualSongList.WrapBufferSize at pop time would compare the
        // wrong thing) was on when each position was pushed. A saved canonical scroll is only
        // meaningful relative to the wrap state it was captured under (wrap adds extra overscroll
        // room that doesn't exist without it) — if the toggle changed while drilled into a sub-view
        // (e.g. from within Options), replaying the raw number would clamp to a misleading position
        // (typically the very last screen) instead of where the user actually was.
        private static readonly Stack<bool> scrollWrapStack = new Stack<bool>();

        // ── Sort mode (drives which folder set BuildRootView produces) ─────────
        // Artist / Mapper / DateAdded / ExRank are the mod's own sorts: they aren't in SongSelect.Sort,
        // so they're set from the extra Sort menu buttons (SetCustomSort), not the ChangeSort postfix.
        internal enum SortMode { Default, AToZ, ZToA, MostStars, LeastStars, MostPlayed, LeastPlayed, MostRecent, Artist, Mapper, DateAdded, ExRank }
        private static SortMode currentSort = SortMode.Default;

        // ── Search folder maudica integration ─────────────────────────────────
        private static UnityEngine.Color SR_LoadMoreColor => ColorSettings.RowLoadMore.Value; // customisable (Options > Colors)
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

            RequestSortLabelRefresh(); // the game may have just rewritten the sort button's text

            // A custom sort is only left by picking a native sort in the Sort menu. A ChangeSort
            // made while the menu has been closed for a while (the game refreshing its own sort)
            // keeps it; the label pass puts the custom sort's name back on the sort button.
            if (IsCustomSort && !SortMenuRecentlyOpen())
            {
                DebugLog.Log($"[FolderRowManager] ChangeSort({gameSort}) with the Sort menu closed: keeping {currentSort}");
                return;
            }

            if (mode == currentSort) return;
            ChangeSortMode(mode);
        }

        /// <summary>
        /// Switches to one of the mod's own sorts (Artist, Mapper, Date Added, EX Rank) — called
        /// from their Sort menu buttons and the Starting Sort option. Native sorts go through the
        /// game (SongSelect.ChangeSort) so its own sort state stays in step. EX Rank only exists
        /// under EX scoring; asked for in Audica mode, it's ignored.
        /// </summary>
        public static void SetCustomSort(SortMode mode)
        {
            if (!IsCustomSortMode(mode)) return;
            if (mode == SortMode.ExRank && !Config.ExType) return;

            RequestSortLabelRefresh();
            if (mode == currentSort) return;
            ChangeSortMode(mode);
        }

        private static void ChangeSortMode(SortMode mode)
        {
            currentSort = mode;
            pendingGradeReveal = null;

            ResetNav();                          // generated folders only exist at root
            SongFolderManager.openFolder = null; // collapse on mode switch

            DebugLog.Log($"[FolderRowManager] Sort -> {currentSort}");

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

            // Latest Maudica left open (e.g. coming back from a song): load it if it never loaded,
            // but keep an already-loaded list as-is so the played song stays selected in place.
            if (SongFolderManager.openFolder == SongFolderManager.FolderLatestMaudica)
                LatestMaudica.EnsureLoaded();

            Apply();

            // Tier 2: warm the placeholder pool toward the largest view we might show, in the
            // background, so the first open of the biggest folder is allocation-free.
            VirtualSongList.WarmPlaceholderPool(EstimateMaxViewSize());
        }

        /// <summary>
        /// Shot a folder header: toggle it open/closed and rebuild the view.
        /// <paramref name="shotHeaderIndex"/> is the exact extended (un-wrapped) canonical index of
        /// the specific physical copy that was shot, captured at bind time in VirtualSongList — when
        /// Wrap Song List makes the same header visible twice on screen at once, this is what lets
        /// us anchor on the copy actually shot instead of guessing from the scroll position.
        /// </summary>
        public static void ToggleFolder(string folderName, int shotHeaderIndex)
        {
            // Folder headers only exist at Level 0, and in Edit Custom Folders (FolderEditMode).
            bool editing = level == NavLevel.FolderEdit;
            if (level != NavLevel.Root && !editing) return;
            if (editing) FolderEditMode.ClearTransient(); // a pending confirm / name entry belongs to the old view
            MarathonSetup.CancelIfActive();
            srClearArmed = false;   // any folder toggle cancels a pending Clear List confirm
            histClearArmed = false; // ...and a pending Clear History confirm

            // Preserve the clicked header's on-screen offset across the rebuild. SetView
            // otherwise preserves the raw numeric scroll index, which is only correct if
            // nothing else in the list changed size — but toggling this folder can also
            // collapse/expand a completely different, much larger folder (e.g. a huge
            // Unsorted), shifting every header after it by however many rows that other
            // folder's songs took up. Anchoring on this header's offset from the current
            // scroll (rather than the scroll index itself) keeps it exactly where it
            // already was on screen, regardless of what else changed size.
            float rawScroll = VirtualSongList.GetScroll();
            int oldHeaderIndex = shotHeaderIndex; // exact — the specific copy that was actually shot
            float headerOffset = oldHeaderIndex - rawScroll;

            bool opening = SongFolderManager.openFolder != folderName;
            SongFolderManager.openFolder = opening ? folderName : null;

            // Latest Maudica fetches on open (first open, or fresh after a song was played).
            if (!editing && opening && folderName == SongFolderManager.FolderLatestMaudica)
                LatestMaudica.OnFolderOpened();

            DebugLog.Log($"[FolderRowManager] Toggle '{folderName}' -> open={SongFolderManager.openFolder ?? "none"}");

            Apply();

            // Here, unlike the old index, there's no single "exact" answer for where this
            // header's copy lands post-rebuild (the rebuild may not preserve wrap cycles
            // 1:1), so this half stays a nearest-cycle prediction aimed at keeping the header
            // at roughly the same on-screen offset it had before the toggle.
            int newHeaderIndex = VirtualSongList.HeaderIndexNear(folderName, oldHeaderIndex - headerOffset);
            if (newHeaderIndex >= 0)
                VirtualSongList.SetScroll(newHeaderIndex - headerOffset);

            if (opening)
            {
                // Keep scroll position; only scroll if the opened folder's first song is off-screen.
                VirtualSongList.RevealFolderIfNeeded(folderName);

                // If the current selection isn't in this folder (or nothing is selected),
                // select the folder's first song.
                var songs = editing ? FolderEditMode.SongsIn(folderName) : GetFolderSongs(folderName);
                if (songs.Count > 0)
                {
                    string sel = CommunityMapManager.SelectedViewKey(ExScoring.selectedSong);
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

        /// <summary>
        /// Rebuild the view without moving it: no reveal of the open folder, and the rows that were
        /// on screen stay at the same screen position even if rows were added or removed above them.
        /// Used for background updates the player didn't trigger (lobby suggestions changing, a
        /// suggested song's lookup or download finishing), so browsing isn't yanked around.
        /// </summary>
        public static void RefreshListInPlace()
        {
            if (!VirtualSongList.IsActive) return; // list not currently shown
            WireHandler();
            var anchor = VirtualSongList.CaptureScrollAnchor();
            Apply();
            VirtualSongList.RestoreScrollAnchor(anchor);
        }

        public static void RefreshFavorites() => RefreshList();

        // ── Playlist navigation (drill-in / back) ─────────────────────────────

        public static void EnterPlaylistList()
        {
            MarathonSetup.CancelIfActive();
            PlaylistNav.ClearTransient();
            scrollStack.Push(VirtualSongList.GetScroll());
            scrollWrapStack.Push(Config.WrapSongList);
            level = NavLevel.PlaylistList;
            VirtualSongList.SetView(BuildView(), 0f, level == NavLevel.Root);
        }

        public static void EnterGlobalOptions()
        {
            MarathonSetup.CancelIfActive();
            PlaylistNav.ClearTransient();
            scrollStack.Push(VirtualSongList.GetScroll());
            scrollWrapStack.Push(Config.WrapSongList);
            level = NavLevel.GlobalOptions;
            SetSongListControlsVisible(false);
            VirtualSongList.SelectedActionId = null;
            VirtualSongList.SetView(BuildView(), 0f, level == NavLevel.Root);
        }

        // ── Edit Custom Folders (FolderEditMode builds the rows) ───────────────

        private static string folderEditReturnFolder;

        public static bool InFolderEdit => level == NavLevel.FolderEdit || level == NavLevel.FolderEditPicker;
        public static bool InFolderEditPicker => level == NavLevel.FolderEditPicker;

        /// <summary>
        /// From the Options list: shows the folder editor. Keeps the scroll saved when Options was
        /// entered, so ExitFolderEdit lands back where the song list was. Its own open folder is
        /// separate from the song list's (restored on exit).
        /// </summary>
        public static void EnterFolderEdit()
        {
            MarathonSetup.CancelIfActive();
            PlaylistNav.ClearTransient();
            if (level == NavLevel.GlobalOptions) GlobalOptions.HidePanel(); // no-op if FolderEditMode took its panel over
            if (level == NavLevel.Root)
            {
                scrollStack.Push(VirtualSongList.GetScroll());
                scrollWrapStack.Push(Config.WrapSongList);
            }

            folderEditReturnFolder = SongFolderManager.openFolder;
            SongFolderManager.openFolder = null;
            level = NavLevel.FolderEdit;
            SetSongListControlsVisible(false);
            VirtualSongList.SelectedActionId = null;
            VirtualSongList.SetView(BuildView(), 0f, false);
        }

        /// <summary>Switches between the folder list and the Move-to picker, at the given scroll (null = keep).</summary>
        public static void ShowFolderEditView(bool picker, float? scroll)
        {
            if (!InFolderEdit) return;
            level = picker ? NavLevel.FolderEditPicker : NavLevel.FolderEdit;
            VirtualSongList.SetView(BuildView(), scroll, false);
        }

        /// <summary>Done: back to the song list (folders already rebuilt with the changes), where it was before Options.</summary>
        public static void ExitFolderEdit()
        {
            if (!InFolderEdit) return;

            level = NavLevel.Root;
            SetSongListControlsVisible(true);
            currentPlaylist = null;
            pendingAddStem = null;

            string back = folderEditReturnFolder;
            folderEditReturnFolder = null;
            SongFolderManager.openFolder =
                back != null && SongFolderManager.availableFolders != null && SongFolderManager.availableFolders.Contains(back)
                    ? back : null;

            float restore = 0f;
            if (scrollStack.Count > 0)
            {
                // The bottom of the stack is the song list's own position (pushed entering Options).
                float[] scrolls = scrollStack.ToArray();       // top first
                bool[] wraps = scrollWrapStack.ToArray();
                float rootScroll = scrolls[scrolls.Length - 1];
                bool rootWrap = wraps.Length > 0 ? wraps[wraps.Length - 1] : Config.WrapSongList;
                restore = rootWrap == Config.WrapSongList ? rootScroll : 0f;
            }
            scrollStack.Clear();
            scrollWrapStack.Clear();

            VirtualSongList.SelectedActionId = null;
            VirtualSongList.SetView(BuildView(), restore, true);
            RandomSong.UpdateButtonState();
        }

        public static void EnterPlaylistContents(string playlistName)
        {
            MarathonSetup.CancelIfActive();
            PlaylistNav.ClearTransient();
            scrollStack.Push(VirtualSongList.GetScroll());
            scrollWrapStack.Push(Config.WrapSongList);
            currentPlaylist = playlistName;
            level = NavLevel.PlaylistContents;
            VirtualSongList.SetView(BuildView(), 0f, level == NavLevel.Root);

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
            else if (level == NavLevel.GlobalOptions) { GlobalOptions.HidePanel(); level = NavLevel.Root; SetSongListControlsVisible(true); }
            else return;

            float restore;
            if (scrollStack.Count > 0)
            {
                float poppedScroll = scrollStack.Pop();
                bool poppedWrapEnabled = scrollWrapStack.Count > 0 ? scrollWrapStack.Pop() : Config.WrapSongList;
                // Wrap's state changed since this position was saved (e.g. toggled from within
                // Options) — the saved number no longer means what it used to, so start fresh at
                // the top instead of clamping into a misleading spot.
                restore = (poppedWrapEnabled == Config.WrapSongList) ? poppedScroll : 0f;
            }
            else
            {
                restore = 0f;
            }
            VirtualSongList.SetView(BuildView(), restore, level == NavLevel.Root);
        }

        /// <summary>Force back to Level 0 (e.g. wire to a leave-song-page hook if desired).</summary>
        public static void ResetNav()
        {
            if (InFolderEdit)
            {
                FolderEditMode.ClearTransient();
                SongFolderManager.openFolder = folderEditReturnFolder;
                folderEditReturnFolder = null;
            }
            level = NavLevel.Root;
            SetSongListControlsVisible(true);
            currentPlaylist = null;
            pendingAddStem = null;
            scrollStack.Clear();
            scrollWrapStack.Clear();
        }

        /// <summary>
        /// Search field, Take Requests toggle, Random Song button and the game's sort button only
        /// belong to the song list proper, so they are hidden while the list is showing the
        /// Options menu.
        /// </summary>
        private static void SetSongListControlsVisible(bool visible)
        {
            SongSearchField.SetVisible(visible);
            SongRequestQueueToggle.SetVisible(visible);
            RandomSongButton.SetVisible(visible);
            SetSortControlsVisible(visible);
        }

        private const string SongSelectPath = "menu/ShellPage_Song/page/ShellPanel_Center/SongSelect";

        // Cached so the sort button can be re-shown while it (or the song page) is inactive:
        // GameObject.Find only sees active objects, transform.Find also reaches inactive children.
        private static Transform songSelectTransform;
        private static GameObject optionsSortButton;
        private static GameObject optionsSortMenu;

        /// <summary>
        /// Hides the game's sort button (and closes the sort menu if it is open) on entering
        /// Options; on leaving, re-shows the button only. The game does not re-activate the button
        /// itself, and the sort menu stays closed until the button is shot again.
        ///
        /// The menu is closed through SongSelect.ToggleSortOptions so the game's own
        /// showingSortOptions flag stays in step with it. Deactivating the SortMenu object alone
        /// left the flag set, and the sort button then needed two shots to reopen the menu.
        /// </summary>
        private static void SetSortControlsVisible(bool visible)
        {
            if (optionsSortButton == null || optionsSortMenu == null) // Unity-null: refetch after a scene change
            {
                if (songSelectTransform == null)
                {
                    GameObject songSelect = GameObject.Find(SongSelectPath);
                    if (songSelect != null) songSelectTransform = songSelect.transform;
                }

                if (songSelectTransform != null)
                {
                    Transform button = songSelectTransform.Find("SortButton");
                    if (button != null) optionsSortButton = button.gameObject;

                    Transform menu = songSelectTransform.Find("SortMenu");
                    if (menu != null) optionsSortMenu = menu.gameObject;
                }
            }

            if (optionsSortButton != null && optionsSortButton.activeSelf != visible)
                optionsSortButton.SetActive(visible);

            if (!visible)
                CloseSortMenu();
        }

        private static void CloseSortMenu()
        {
            SongSelect songSelect = songSelectTransform != null ? songSelectTransform.GetComponent<SongSelect>() : null;

            if (songSelect != null && songSelect.showingSortOptions)
                songSelect.ToggleSortOptions();

            // Fallback: if the menu is somehow still up, force it closed and keep the flag in step.
            if (optionsSortMenu != null && optionsSortMenu.activeSelf)
            {
                optionsSortMenu.SetActive(false);
                if (songSelect != null) songSelect.showingSortOptions = false;
            }
        }

        public static bool InPlaylistNav => level != NavLevel.Root;
        public static bool InGlobalOptions => level == NavLevel.GlobalOptions;

        // ── Transient Add-to-Playlist picker ──────────────────────────────────
        private static string pendingAddStem = null;
        private static NavLevel addReturnLevel = NavLevel.Root;
        private static string addReturnFolder = null;
        private static float addReturnScroll = 0f;
        private static bool addReturnWrapEnabled = false; // Config.WrapSongList at the moment addReturnScroll was captured
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
            addReturnWrapEnabled = Config.WrapSongList;
            addReturnSong = ExScoring.selectedSong;
            pendingAddStem = songStem;

            level = NavLevel.AddPicker;
            VirtualSongList.SetView(BuildView(), 0f, level == NavLevel.Root);
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
            float restoreScroll = (addReturnWrapEnabled == Config.WrapSongList) ? addReturnScroll : 0f;
            VirtualSongList.SetView(BuildView(), restoreScroll, level == NavLevel.Root);
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
            VirtualSongList.SetView(BuildView(), null, level == NavLevel.Root);
            RandomSong.UpdateButtonState();
        }

        /// <summary>
        /// Open the folder containing songID (if it isn't already open) and select that song.
        /// Used by auto-select to restore the previously selected song on re-entry.
        /// </summary>
        public static void RevealAndSelect(string songID)
        {
            if (string.IsNullOrEmpty(songID)) return;

            // Grade folders (EX scoring) aren't known until the background grade load finishes —
            // remember the song and let that load reveal it once the real view is up.
            if (level == NavLevel.Root && IsStarSort && UseGradeView && !EnsureGradeCache())
            {
                pendingGradeReveal = songID;
                return;
            }

            // If the song is already in the current view (e.g. shown in the open Song Requests, Favorites,
            // or search folder), keep it there rather than jumping to its home folder. A downloaded request
            // lives in the requests folder AND in Unsorted, and GetFolder would send us to the Unsorted copy.
            if (VirtualSongList.IndexOf(songID) < 0)
            {
                // The launched song isn't in the current view anymore — most likely it was a Song
                // Request that got dequeued the moment it was played (e.g. an already-owned request
                // the SongRequest mod auto-removes on play). Jumping to the song's actual home folder
                // is disorienting for a request, so if we were in the Song Requests folder, stay there
                // instead: select whatever's now first in the queue, or if it's empty, just scroll to
                // the (now empty) Song Requests header rather than bouncing to a different folder.
                if (SongFolderManager.openFolder == SongFolderManager.FolderSongRequests)
                {
                    var remaining = GetSongRequestSongIDs();
                    if (remaining.Count > 0)
                    {
                        VirtualSongList.SelectInView(remaining[0]);
                    }
                    else
                    {
                        int hdr = VirtualSongList.HeaderIndex(SongFolderManager.FolderSongRequests);
                        if (hdr >= 0) VirtualSongList.SetScroll(hdr);
                    }
                    return;
                }

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

        /// <summary>
        /// Online match launch: put songID in the list and select it through the game's own
        /// OnSelect, which has to run for the song before LaunchPanel.Play can load it.
        /// Unlike RevealAndSelect this never selects a different song (its Song Requests
        /// branch) and never defers to the background grade load: the caller retries instead.
        /// Leaves the Options menu, playlist navigation and the add-to-playlist picker first.
        /// Returns false when the song couldn't be put in view (the caller doesn't launch).
        ///
        /// With <paramref name="mapPath"/> (v2.60, a community map match) the row selected is that
        /// map's row ("cm:" + path, the parent song with the map's chart), from the Suggested Songs
        /// folder or the Community Maps folder.
        /// </summary>
        public static bool RevealForMatch(string songID, string mapPath = null)
        {
            if (string.IsNullOrEmpty(songID)) return false;

            if (level == NavLevel.AddPicker) RestoreFromAddPicker();
            for (int i = 0; i < 3 && level != NavLevel.Root; i++) NavBack();
            if (level != NavLevel.Root) return false;

            if (!VirtualSongList.IsActive) return false;

            // The row to select: the song, or the map's own row.
            string key = mapPath != null ? CommunityMapManager.ViewKeyPrefix + mapPath : songID;

            // Rebuild the view first: a song downloaded since it was built (the lobby's
            // auto-download) may still be a "downloadable" row, or missing from it entirely.
            Apply();

            // The match song is the lobby's song, so the Suggested Songs folder has it, and it
            // knows about songs downloaded this session (the regular folders don't until the
            // song list is reloaded).
            if (VirtualSongList.IndexOf(key) < 0 && MpSession.InLobby &&
                SongFolderManager.openFolder != LobbySongList.FolderName)
            {
                SongFolderManager.openFolder = LobbySongList.FolderName;
                Apply();
            }

            if (VirtualSongList.IndexOf(key) < 0 && mapPath != null)
            {
                // A map's home is the Community Maps folder (in every sort mode). A map downloaded
                // this session isn't listed until the maps are rescanned: rebuild once and look again.
                if (CommunityMapManager.FindByPath(mapPath) == null)
                    SongFolderManager.Rebuild(ExScoring.mainSongDirectory);
                if (CommunityMapManager.FindByPath(mapPath) == null) return false;
                if (SongFolderManager.openFolder != SongFolderManager.FolderCommunityMaps)
                {
                    SongFolderManager.openFolder = SongFolderManager.FolderCommunityMaps;
                    Apply();
                }
            }
            else if (VirtualSongList.IndexOf(key) < 0)
            {
                // Its home folder otherwise. A song downloaded this session isn't in the folder map
                // yet: rebuild it once and look again.
                string folder = CurrentFolderFor(songID);
                if (folder == null && currentSort == SortMode.Default)
                {
                    SongFolderManager.Rebuild(ExScoring.mainSongDirectory);
                    folder = CurrentFolderFor(songID);
                }
                if (folder == null) return false; // grades still loading, or not a listed song
                if (SongFolderManager.openFolder != folder)
                {
                    SongFolderManager.openFolder = folder;
                    Apply();
                }
            }

            if (VirtualSongList.IndexOf(key) < 0) return false;

            VirtualSongList.ScrollToAndSelect(key);
            return true;
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
                case NavLevel.GlobalOptions: return GlobalOptions.BuildView();
                case NavLevel.FolderEdit: return FolderEditMode.BuildEditView();
                case NavLevel.FolderEditPicker: return FolderEditMode.BuildPickerView();
                default: return BuildRootView();
            }
        }

        /// <summary>Root view dispatcher: Default folders, or generated letter folders.</summary>
        private static List<ViewRow> BuildRootView()
        {
            List<ViewRow> rows = BuildSortedRootView();

            // Community maps aren't songs of their own (no SongData), so the generated sort
            // folders never contain them: keep their folder at the end of every sorted view.
            // (The Default view lists it among availableFolders.)
            if (currentSort != SortMode.Default)
                AppendCommunityMapsFolder(rows);

            // Online play: the lobby's "Suggested Songs" folder, pinned at the top while in a lobby.
            try
            {
                LobbySongList.InsertInto(rows);
            }
            catch (Exception e)
            {
                DebugLog.Log("[FolderRowManager] Suggested Songs folder failed: " + e);
            }
            return rows;
        }

        private static List<ViewRow> BuildSortedRootView()
        {
            switch (currentSort)
            {
                case SortMode.AToZ: return BuildAlphaView(reversed: false);
                case SortMode.ZToA: return BuildAlphaView(reversed: true);
                case SortMode.MostStars: return UseGradeView ? BuildGradeView(reversed: false) : BuildStarsView(reversed: false);
                case SortMode.LeastStars: return UseGradeView ? BuildGradeView(reversed: true) : BuildStarsView(reversed: true);
                case SortMode.MostPlayed: return BuildPlaysView(reversed: false);
                case SortMode.LeastPlayed: return BuildPlaysView(reversed: true);
                case SortMode.MostRecent: return BuildRecentView();
                case SortMode.Artist: return BuildNameView(mapper: false);
                case SortMode.Mapper: return BuildNameView(mapper: true);
                case SortMode.DateAdded: return BuildDateAddedView();
                case SortMode.ExRank: return Config.ExType ? BuildRankView() : BuildDefaultRootView();
                default: return BuildDefaultRootView();
            }
        }

        /// <summary>
        /// Per-folder header color, customisable on Options > Colors (ColorSettings). Each built-in
        /// folder has its own setting; user-created subfolders share "Other Folders".
        /// </summary>
        private static Color FolderColorFor(string folder)
        {
            switch (folder)
            {
                case SongFolderManager.FolderFavorites: return ColorSettings.FolderFavorites.Value;
                case SongFolderManager.FolderAudica: return ColorSettings.FolderAudica.Value;
                case SongFolderManager.FolderAudicaDLC: return ColorSettings.FolderAudicaDLC.Value;
                case SongFolderManager.FolderCustom: return ColorSettings.FolderUnsorted.Value;      // "Unsorted"
                case SongFolderManager.FolderSongRequests: return ColorSettings.FolderSongRequests.Value;
                case SongFolderManager.FolderSongRequestHistory: return ColorSettings.FolderSongRequestHistory.Value;
                case SongFolderManager.FolderLatestMaudica: return ColorSettings.FolderLatestMaudica.Value;
                default:
                    // Search-results folder (dynamic name).
                    if (folder == SongFolderManager.searchFolderName)
                        return ColorSettings.FolderSearch.Value;
                    // Pack subfolder with a folder.json "color" → that color instead of "Other Folders".
                    FolderStyle style = FolderCustomization.Get(folder);
                    if (style != null && style.color.HasValue)
                        return style.color.Value;
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

                string home = SongFolderManager.GetFolder(sd.songID);
                if (home != null) counts[home] = counts.TryGetValue(home, out int c) ? c + 1 : 1;

                if (SongFolderManager.extraSongFolders.TryGetValue(sd.songID, out List<string> extras))
                {
                    for (int j = 0; j < extras.Count; j++)
                    {
                        string extra = extras[j];
                        counts[extra] = counts.TryGetValue(extra, out int ec) ? ec + 1 : 1;
                    }
                }

                if (FavoritesStore.IsFavorite(sd.songID)) favCount++;
            }
            int searchCount = SongSearch.searchResult != null ? SongSearch.searchResult.Count : 0;

            // Entry into the playlist system (drills into Level 1) — pinned to the top.
            int plCount = PlaylistManager.playlists != null ? PlaylistManager.playlists.Count : 0;
            rows.Add(ViewRow.ActionRow("Options", GlobalOptions.OptionsRowColor, EnterGlobalOptions));
            rows.Add(ViewRow.ActionRow("Playlists", ColorSettings.RowPlaylists.Value,
                EnterPlaylistList, $"{plCount} playlist{(plCount != 1 ? "s" : "")}"));

            // Song Request History only appears once something has been played / removed / cleared.
            bool hasHistory = SongRequestHistoryStore.Count > 0;
            if (!hasHistory && SongFolderManager.openFolder == SongFolderManager.FolderSongRequestHistory)
            {
                SongFolderManager.openFolder = null; // history was just cleared while open
                histClearArmed = false;
            }

            string open = SongFolderManager.openFolder;
            foreach (string folder in SongFolderManager.availableFolders)
            {
                if (folder == SongFolderManager.FolderSongRequestHistory)
                {
                    if (!hasHistory) continue;
                    GetHistoryCounts(out int total, out int missing);
                    string sub = missing > 0
                        ? $"{total} song{(total != 1 ? "s" : "")} ({missing} missing)"
                        : null;
                    rows.Add(ViewRow.Header(folder, total, sub, FolderColorFor(folder)));
                }
                else if (folder == SongFolderManager.FolderSongRequests)
                {
                    GetSongRequestCounts(out int total, out int missing);
                    string sub = missing > 0
                        ? $"{total} song{(total != 1 ? "s" : "")} ({missing} missing)"
                        : null; // null → header falls back to the default "{count} songs"
                    rows.Add(ViewRow.Header(folder, total, sub, FolderColorFor(folder)));
                }
                else if (folder == SongFolderManager.FolderLatestMaudica)
                {
                    rows.Add(ViewRow.Header(folder, LatestMaudica.results.Count, LatestMaudicaHeaderSubLabel(),
                             FolderColorFor(folder)));
                }
                else if (folder == SongFolderManager.FolderCommunityMaps)
                {
                    rows.Add(ViewRow.Header(folder, CommunityMapManager.Count, CommunityMapsHeaderSubLabel(),
                             FolderColorFor(folder)));
                }
                else
                {
                    int count =
                        folder == SongFolderManager.FolderFavorites ? favCount :
                        folder == SongFolderManager.searchFolderName ? (searchCount + GetSearchDownloadables().Count) :
                        (counts.TryGetValue(folder, out int c) ? c : 0);
                    ViewRow header = ViewRow.Header(folder, count, color: FolderColorFor(folder));
                    // folder.json look (name / image / text color) — pack subfolders only, never the virtual folders.
                    if (folder != SongFolderManager.FolderFavorites && folder != SongFolderManager.searchFolderName)
                        header.style = FolderCustomization.Get(folder);
                    rows.Add(header);
                }

                if (folder == open)
                {
                    if (folder == SongFolderManager.FolderSongRequests)
                        AppendSongRequestRows(rows);
                    else if (folder == SongFolderManager.FolderSongRequestHistory)
                        AppendHistoryRows(rows);
                    else if (folder == SongFolderManager.searchFolderName)
                        AppendSearchRows(rows);
                    else if (folder == SongFolderManager.FolderLatestMaudica)
                        AppendLatestMaudicaRows(rows);
                    else if (folder == SongFolderManager.FolderCommunityMaps)
                        AppendCommunityMapRows(rows);
                    else
                        foreach (string id in GetFolderSongs(folder))
                            rows.Add(ViewRow.SongRow(id));
                }
            }

            // DIAGNOSTIC: confirm the Playlists row is present at index 0.
            DebugLog.Log($"[FolderRowManager] BuildRootView: {rows.Count} rows; " +
                $"row0 = {(rows.Count > 0 ? rows[0].kind + " '" + (rows[0].label ?? rows[0].folderName) + "'" : "none")}");

            return rows;
        }

        // ── Community Maps folder ──────────────────────────────────────────────

        /// <summary>Header second line: map count, and how many songs they cover.</summary>
        private static string CommunityMapsHeaderSubLabel()
        {
            int maps = CommunityMapManager.Count;
            var songs = new HashSet<string>();
            foreach (var e in CommunityMapManager.Entries) songs.Add(e.parentSongID);
            return $"{maps} map{(maps != 1 ? "s" : "")} for {songs.Count} song{(songs.Count != 1 ? "s" : "")}";
        }

        /// <summary>One row per community map: the parent song's item titled "Title [map name]".</summary>
        private static void AppendCommunityMapRows(List<ViewRow> rows)
        {
            foreach (var e in CommunityMapManager.Entries)
                rows.Add(ViewRow.CommunityMapRow(e.parentSongID, e.path, CommunityMapManager.RowTitle(e)));
        }

        /// <summary>The Community Maps folder header (and its rows when open), for the sorted views.</summary>
        private static void AppendCommunityMapsFolder(List<ViewRow> rows)
        {
            if (CommunityMapManager.Count == 0) return;

            string folder = SongFolderManager.FolderCommunityMaps;
            rows.Add(ViewRow.Header(folder, CommunityMapManager.Count, CommunityMapsHeaderSubLabel(), FolderColorFor(folder)));
            if (SongFolderManager.openFolder == folder)
                AppendCommunityMapRows(rows);
        }

        /// <summary>
        /// Estimate the largest row count any single view could reach: root header/action rows plus
        /// the biggest single folder's songs (only one folder is open at a time). Sizes the Tier 2
        /// pre-grow. Over-estimates slightly on purpose; if a view ever exceeds it, Tier 1's lazy
        /// growth still covers the difference.
        /// </summary>
        // Cached EstimateMaxViewSize result. The full-library pass costs ~80 ms (Il2Cpp reads), and
        // the pool it sizes never shrinks, so it only needs recomputing when the library or folder
        // set changes size. A slightly stale estimate is harmless: Tier 1's lazy growth covers any
        // view that outgrows it.
        private static int cachedMaxViewSize = -1;
        private static int cachedMaxViewSongCount = -1;
        private static int cachedMaxViewFolderCount = -1;
        private static int cachedMaxViewMapCount = -1;

        private static int EstimateMaxViewSize()
        {
            if (SongList.I == null || SongList.I.songs == null) return 0;

            int songCount = SongList.I.songs.Count;
            int folderCount = SongFolderManager.availableFolders?.Count ?? 0;
            int mapCount = CommunityMapManager.Count;
            if (cachedMaxViewSize >= 0 &&
                songCount == cachedMaxViewSongCount &&
                folderCount == cachedMaxViewFolderCount &&
                mapCount == cachedMaxViewMapCount)
                return cachedMaxViewSize;

            cachedMaxViewSize = ComputeMaxViewSize();
            cachedMaxViewSongCount = songCount;
            cachedMaxViewFolderCount = folderCount;
            cachedMaxViewMapCount = mapCount;
            return cachedMaxViewSize;
        }

        private static int ComputeMaxViewSize()
        {
            var counts = new Dictionary<string, int>();
            int favCount = 0;
            for (int i = 0; i < SongList.I.songs.Count; i++)
            {
                var sd = SongList.I.songs[i];
                if (sd == null || sd.hidden) continue;

                string home = SongFolderManager.GetFolder(sd.songID);
                if (home != null) counts[home] = counts.TryGetValue(home, out int c) ? c + 1 : 1;

                if (SongFolderManager.extraSongFolders.TryGetValue(sd.songID, out List<string> extras))
                {
                    for (int j = 0; j < extras.Count; j++)
                    {
                        string extra = extras[j];
                        counts[extra] = counts.TryGetValue(extra, out int ec) ? ec + 1 : 1;
                    }
                }

                if (FavoritesStore.IsFavorite(sd.songID)) favCount++;
            }

            int maxFolder = Math.Max(favCount, CommunityMapManager.Count);
            foreach (var kv in counts) if (kv.Value > maxFolder) maxFolder = kv.Value;

            int headerRows = (SongFolderManager.availableFolders?.Count ?? 0) + 12; // folders + action-row slack
            return headerRows + maxFolder + 16; // extra slack for search/request rows
        }

        private static List<string> GetFolderSongs(string folder)
        {
            // Row keys ("cm:<path>"), not songIDs — shown in every sort mode.
            if (folder == SongFolderManager.FolderCommunityMaps)
                return CommunityMapManager.ViewKeys();

            switch (currentSort)
            {
                case SortMode.AToZ:
                case SortMode.ZToA: return GetAlphaFolderSongs(folder);
                case SortMode.MostStars:
                case SortMode.LeastStars: return UseGradeView ? GetGradeFolderSongs(folder) : GetStarFolderSongs(folder);
                case SortMode.MostPlayed:
                case SortMode.LeastPlayed: return GetPlayFolderSongs(folder);
                case SortMode.MostRecent: return GetRecentFolderSongs(folder);
                case SortMode.Artist: return GetGroupSongs(BuildNameGroups(mapper: false), folder);
                case SortMode.Mapper: return GetGroupSongs(BuildNameGroups(mapper: true), folder);
                case SortMode.DateAdded: return GetGroupSongs(BuildDateAddedGroups(), folder);
                case SortMode.ExRank: return GetGroupSongs(BuildRankGroups(), folder);
            }

            var result = new List<string>();

            if (folder == SongFolderManager.FolderSongRequests)
                return GetSongRequestSongIDs();

            if (folder == SongFolderManager.FolderSongRequestHistory)
                return GetHistorySongIDs();

            if (folder == SongFolderManager.FolderLatestMaudica)
                return GetLatestMaudicaSongIDs();

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
                else if (SongFolderManager.IsInFolder(id, folder))
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

            DebugLog.Log($"[FolderRowManager] BuildAlphaView ({(reversed ? "Z-A" : "A-Z")}): " +
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
            // A community map row key lives in the Community Maps folder in every sort mode.
            if (CommunityMapManager.IsViewKey(songID))
                return CommunityMapManager.FindByViewKey(songID) != null ? SongFolderManager.FolderCommunityMaps : null;

            if (currentSort == SortMode.Default)
                return SongFolderManager.GetFolder(songID);

            if (IsStarSort && UseGradeView)
            {
                if (!EnsureGradeCache()) return null; // grades still loading — no folder known yet
                return GradeBucketLabel(gradeCache.TryGetValue(GradeKey(songID, CurrentDifficultyName()), out int g) ? g : GradeUnplayed);
            }

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

            if (currentSort == SortMode.Artist || currentSort == SortMode.Mapper ||
                currentSort == SortMode.DateAdded || currentSort == SortMode.ExRank)
            {
                var sd = FindVisibleSong(songID);
                if (sd == null) return null;
                switch (currentSort)
                {
                    case SortMode.Artist: return NameGroupLabel(sd.artist, false);
                    case SortMode.Mapper: return NameGroupLabel(sd.author, true);
                    case SortMode.DateAdded: return DateAddedLabelFor(sd);
                    default:
                        if (!ExScoring.OnlineBestScoresSettled) return null; // ranks still loading
                        return RankLabel(RankFor(songID));
                }
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

        // ── Custom sorts: Artist / Mapper / Date Added / EX Rank ───────────────
        //
        // The mod's own sorts, on top of the game's eight. They aren't in SongSelect.Sort, so the
        // game knows nothing about them: four extra buttons are cloned into the Sort menu
        // (EnsureCustomSortButtons) and set currentSort directly (SetCustomSort). EX Rank only
        // exists under EX scoring — in Audica mode its button is removed, the menu frame shrinks
        // back to three extra rows, and an active EX Rank sort falls back to Default.
        //
        // Each view is built like the other generated sorts: a text header per non-empty group,
        // the open group's songs inlined, songs within a group A-Z by title.

        private static readonly string[] CustomSortObjNames = { "Sort_Artist", "Sort_Mapper", "Sort_DateAdded", "Sort_ExRank" };
        private static readonly string[] CustomSortLabels = { "Artist", "Mapper", "Date Added", "EX Rank" };
        private static readonly SortMode[] CustomSortModes = { SortMode.Artist, SortMode.Mapper, SortMode.DateAdded, SortMode.ExRank };
        private const int ExRankButtonIndex = 3;

        // Native button object per native sort mode (SortMenu/<name>/Label holds its wording).
        private static readonly Dictionary<SortMode, string> NativeSortObjNames = new Dictionary<SortMode, string>
        {
            { SortMode.Default, "Sort_Default" },
            { SortMode.AToZ, "Sort_A-Z" },
            { SortMode.ZToA, "Sort_Z-A" },
            { SortMode.MostStars, "Sort_MostStars" },
            { SortMode.LeastStars, "Sort_LeastStars" },
            { SortMode.MostPlayed, "Sort_MostPlayed" },
            { SortMode.LeastPlayed, "Sort_LeastPlayed" },
            { SortMode.MostRecent, "Sort_MostRecent" },
        };

        private static SongSelect customSortSongSelect;   // the SongSelect our buttons belong to
        private static GameObject customSortMenu;         // its SortMenu
        private static readonly GameObject[] customSortButtons = new GameObject[4];

        // The menu frame, and its native layout (read once, before anything is moved).
        private static Transform frameGlass, frameBottom, frameLeft, frameRight;
        private static Vector3 glassPos0, glassScale0, bottomPos0, leftPos0, leftScale0, rightPos0, rightScale0;
        private static float frameHeight0;      // highlight_top.y - highlight_bottom.y (native ~17.7)
        private static float buttonStep = 2f;   // vertical distance between buttons (native 2.0)
        private static int customFrameExtra = -1; // extra rows the frame is currently sized for; -1 = not sized yet
        private static bool customSortLastEx;

        // A native sort button can only be shot while the menu is open. ChangeSort calls made while
        // it has been closed for a while (the game refreshing its own sort) don't leave a custom sort.
        private static int sortMenuLastOpenFrame = -100;

        private static bool startingSortApplied; // once per launch

        private static bool IsCustomSortMode(SortMode m) =>
            m == SortMode.Artist || m == SortMode.Mapper || m == SortMode.DateAdded || m == SortMode.ExRank;

        private static bool IsCustomSort => IsCustomSortMode(currentSort);

        /// <summary>Display name of a custom sort (button and sort-button label).</summary>
        private static string CustomSortName(SortMode m)
        {
            int i = Array.IndexOf(CustomSortModes, m);
            return i >= 0 ? CustomSortLabels[i] : m.ToString();
        }

        private static bool IsCustomSortName(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < CustomSortLabels.Length; i++)
                if (string.Equals(text, CustomSortLabels[i], StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// Called from the SongSelect.OnEnable postfix: adds the custom sort buttons (once per Sort
        /// menu), sizes the frame for the current scoring mode, and applies the Starting Sort the
        /// first time the song page opens after launch.
        /// </summary>
        public static void OnSongSelectEnabled(SongSelect songSelect)
        {
            if (songSelect == null) return;
            customSortSongSelect = songSelect;
            if (songSelectTransform == null) songSelectTransform = songSelect.transform;

            try
            {
                EnsureCustomSortButtons(songSelect);
                UpdateCustomSortButtons();
            }
            catch (Exception ex)
            {
                DebugLog.Log($"[FolderRowManager] Custom sort buttons failed: {ex}");
            }

            RequestSortLabelRefresh();
            ApplyStartingSortOnce(songSelect);
        }

        /// <summary>Clones Sort_MostRecent four times under it and stretches the frame to fit.</summary>
        private static void EnsureCustomSortButtons(SongSelect songSelect)
        {
            GameObject menu = songSelect.sortMenu;
            if (menu == null)
            {
                DebugLog.Log("[FolderRowManager] Custom sort buttons: SongSelect.sortMenu is null.");
                return;
            }

            Transform menuT = menu.transform;

            if (customSortMenu == menu && customSortButtons[0] != null) return; // already set up

            // Our buttons already on this menu (shouldn't happen: statics live for the whole session) — reuse them.
            if (menuT.Find(CustomSortObjNames[0]) != null)
            {
                for (int i = 0; i < CustomSortObjNames.Length; i++)
                {
                    Transform existing = menuT.Find(CustomSortObjNames[i]);
                    customSortButtons[i] = existing != null ? existing.gameObject : null;
                }
                customSortMenu = menu;
                customFrameExtra = -1;
                return;
            }

            Transform template = menuT.Find("Sort_MostRecent");
            if (template == null)
            {
                DebugLog.Log("[FolderRowManager] Custom sort buttons: Sort_MostRecent not found.");
                return;
            }

            // Spacing between buttons, from the native layout (Sort_Default 1.4, Sort_A-Z -0.6).
            Transform first = menuT.Find("Sort_Default");
            Transform second = menuT.Find("Sort_A-Z");
            if (first != null && second != null)
            {
                float step = first.localPosition.y - second.localPosition.y;
                if (step > 0.01f) buttonStep = step;
            }

            // Frame pieces and their native layout.
            frameGlass = menuT.Find("Glass");
            Transform frameTop = menuT.Find("highlights/highlight_top");
            frameBottom = menuT.Find("highlights/highlight_bottom");
            frameLeft = menuT.Find("highlights/highlight_left");
            frameRight = menuT.Find("highlights/highlight_right");

            if (frameGlass != null) { glassPos0 = frameGlass.localPosition; glassScale0 = frameGlass.localScale; }
            if (frameBottom != null) bottomPos0 = frameBottom.localPosition;
            if (frameLeft != null) { leftPos0 = frameLeft.localPosition; leftScale0 = frameLeft.localScale; }
            if (frameRight != null) { rightPos0 = frameRight.localPosition; rightScale0 = frameRight.localScale; }

            frameHeight0 = (frameTop != null && frameBottom != null)
                ? frameTop.localPosition.y - frameBottom.localPosition.y
                : (frameGlass != null ? glassScale0.y : 0f);

            for (int i = 0; i < CustomSortObjNames.Length; i++)
                customSortButtons[i] = CreateCustomSortButton(template, menuT, i);

            customSortMenu = menu;
            customFrameExtra = -1;

            AddCustomButtonsToLayout(menu, template);

            DebugLog.Log($"[FolderRowManager] Custom sort buttons added (step {buttonStep:0.###}, frame height {frameHeight0:0.###}).");
        }

        private static GameObject CreateCustomSortButton(Transform template, Transform menuT, int index)
        {
            GameObject clone = GameObject.Instantiate(template.gameObject, menuT);
            clone.name = CustomSortObjNames[index];

            Transform t = clone.transform;
            Vector3 pos = template.localPosition;
            pos.y -= buttonStep * (index + 1);
            t.localPosition = pos;
            t.localRotation = template.localRotation;
            t.localScale = template.localScale;

            // Label: drop the Localizer (it would put "Most Recent" back), then our wording.
            Transform label = t.Find("Label");
            if (label != null)
            {
                var localizers = label.GetComponents<Localizer>();
                if (localizers != null)
                    foreach (Localizer loc in localizers)
                        if (loc != null) GameObject.DestroyImmediate(loc);

                TMP_Text text = label.GetComponent<TMP_Text>();
                if (text != null) text.text = CustomSortLabels[index];
            }

            // Hover / selected visuals copied in whatever state the template was in.
            SetChildActive(t, "Highlight", false);
            SetChildActive(t, "SelectedIndicator", false);
            SetChildActive(t, "FakeBloom", false);

            // The cloned GunButton still calls SongSelect.SetSort_MostRecent: replace the event.
            GunButton gb = clone.GetComponentInChildren<GunButton>(true);
            if (gb != null)
            {
                SortMode mode = CustomSortModes[index];
                gb.onHitEvent = new UnityEngine.Events.UnityEvent();
                gb.onHitEvent.AddListener(new Action(() => OnCustomSortButtonShot(mode)));
            }
            else
            {
                DebugLog.Log($"[FolderRowManager] Custom sort button {clone.name}: no GunButton found.");
            }

            return clone;
        }

        private static void SetChildActive(Transform parent, string name, bool active)
        {
            Transform child = parent.Find(name);
            if (child != null && child.gameObject.activeSelf != active) child.gameObject.SetActive(active);
        }

        /// <summary>
        /// Appends our buttons to the menu's ShellButtonLayout.buttons. Arrange() (the only method,
        /// no callers in game code) places buttons[i] at buttons[0] - up * i * spacingY, which is
        /// where ours already are — this only keeps the layout consistent if it ever runs. Skipped
        /// unless the native list ends with Sort_MostRecent (ours continue from it).
        /// </summary>
        private static void AddCustomButtonsToLayout(GameObject menu, Transform template)
        {
            try
            {
                ShellButtonLayout layout = menu.GetComponent<ShellButtonLayout>();
                if (layout == null) return;

                var old = layout.buttons;
                if (old == null || old.Length == 0) return;
                int n = old.Length;
                if (old[n - 1] == null || old[n - 1].name != template.name) return;

                var arr = new Il2CppReferenceArray<Transform>(n + customSortButtons.Length);
                for (int i = 0; i < n; i++) arr[i] = old[i];
                for (int i = 0; i < customSortButtons.Length; i++)
                    arr[n + i] = customSortButtons[i] != null ? customSortButtons[i].transform : null;
                layout.buttons = arr;
            }
            catch (Exception ex)
            {
                DebugLog.Log($"[FolderRowManager] ShellButtonLayout update skipped: {ex.Message}");
            }
        }

        /// <summary>Shows EX Rank only under EX scoring and sizes the frame for the visible rows.</summary>
        private static void UpdateCustomSortButtons()
        {
            bool ex = Config.ExType;
            customSortLastEx = ex;

            if (customSortButtons[0] == null) return; // not set up (or the menu was destroyed)

            GameObject rankButton = customSortButtons[ExRankButtonIndex];
            if (rankButton != null && rankButton.activeSelf != ex) rankButton.SetActive(ex);

            int extra = ex ? customSortButtons.Length : customSortButtons.Length - 1;
            if (extra == customFrameExtra) return;

            SizeSortMenuFrame(extra);
            customFrameExtra = extra;
        }

        /// <summary>Stretches the glass and the frame lines down by <paramref name="extra"/> button rows.</summary>
        private static void SizeSortMenuFrame(int extra)
        {
            float grow = buttonStep * extra;

            if (frameGlass != null)
            {
                frameGlass.localPosition = new Vector3(glassPos0.x, glassPos0.y - grow * 0.5f, glassPos0.z);
                frameGlass.localScale = new Vector3(glassScale0.x, glassScale0.y + grow, glassScale0.z);
            }

            if (frameBottom != null)
                frameBottom.localPosition = new Vector3(bottomPos0.x, bottomPos0.y - grow, bottomPos0.z);

            // The side lines are rotated 90 degrees: their length is localScale.x.
            float ratio = frameHeight0 > 0.01f ? (frameHeight0 + grow) / frameHeight0 : 1f;
            if (frameLeft != null)
            {
                frameLeft.localPosition = new Vector3(leftPos0.x, leftPos0.y - grow * 0.5f, leftPos0.z);
                frameLeft.localScale = new Vector3(leftScale0.x * ratio, leftScale0.y, leftScale0.z);
            }
            if (frameRight != null)
            {
                frameRight.localPosition = new Vector3(rightPos0.x, rightPos0.y - grow * 0.5f, rightPos0.z);
                frameRight.localScale = new Vector3(rightScale0.x * ratio, rightScale0.y, rightScale0.z);
            }
        }

        private static void OnCustomSortButtonShot(SortMode mode)
        {
            DebugLog.Log($"[FolderRowManager] Custom sort button shot: {mode}");
            CloseSortMenu(); // native sort buttons close the menu too
            SetCustomSort(mode);
        }

        /// <summary>True when a native sort pick can have come from the Sort menu (open now or a moment ago).</summary>
        private static bool SortMenuRecentlyOpen()
        {
            GameObject menu = CurrentSortMenu();
            if (menu == null) return true; // can't tell: behave as before (every ChangeSort counts)
            if (menu.activeInHierarchy) return true;
            return Time.frameCount - sortMenuLastOpenFrame <= 3;
        }

        /// <summary>The song page's SortMenu (ours, or the SongSelect's when our buttons couldn't be added).</summary>
        private static GameObject CurrentSortMenu()
        {
            if (customSortMenu != null) return customSortMenu;
            if (customSortSongSelect == null) return null;
            try { return customSortSongSelect.sortMenu; } catch { return null; }
        }

        /// <summary>
        /// Per frame, from TickSortLabels: tracks the menu, reacts to the scoring mode changing
        /// (EX Rank button, frame size, EX Rank falling back to Default), keeps the sort button's
        /// label on the custom sort's name, and re-applies the EX Rank view when ranks arrive.
        /// </summary>
        private static void TickCustomSorts()
        {
            GameObject menu = CurrentSortMenu();
            if (menu != null && menu.activeInHierarchy)
                sortMenuLastOpenFrame = Time.frameCount;

            bool ex = Config.ExType;

            // Audica scoring: there is no EX Rank sort (also covers a switch made off the song page).
            if (!ex && currentSort == SortMode.ExRank)
                FallBackToDefaultSort();

            if (customSortSongSelect == null || !VirtualSongList.IsActive) return;

            if (ex != customSortLastEx)
            {
                try { UpdateCustomSortButtons(); }
                catch (Exception e) { DebugLog.Log($"[FolderRowManager] Custom sort buttons update failed: {e.Message}"); }
            }

            UpdateSortButtonLabel();
            TickRankView();
        }

        /// <summary>
        /// EX Rank while Audica scoring is on: back to Default without leaving the view the player is
        /// in (they may be switching the scoring mode from the song list's Options).
        /// </summary>
        private static void FallBackToDefaultSort()
        {
            DebugLog.Log("[FolderRowManager] EX Rank sort without EX scoring -> Default");
            currentSort = SortMode.Default;
            pendingGradeReveal = null;
            SongFolderManager.openFolder = null;
            try { SongSelect.sListSort = SongSelect.Sort.Default; } catch { }
            RequestSortLabelRefresh();

            if (VirtualSongList.IsActive && level == NavLevel.Root)
                Apply();
        }

        /// <summary>
        /// The sort button reads the custom sort's name while one is active. Back on a native sort,
        /// if the game didn't rewrite it (it may skip that when its own sort didn't change), the
        /// native button's wording is put back.
        /// </summary>
        private static void UpdateSortButtonLabel()
        {
            TMP_Text label = null;
            try { label = customSortSongSelect.sortLabel; } catch { }
            if (label == null) return;

            string current = label.text ?? "";
            if (IsCustomSort)
            {
                string wanted = CustomSortName(currentSort);
                if (current != wanted) label.text = wanted;
                return;
            }

            if (!IsCustomSortName(current)) return;

            string native = NativeSortLabelText(currentSort);
            if (!string.IsNullOrEmpty(native) && native != current)
            {
                label.text = native;
                RequestSortLabelRefresh(); // stars/grade wording
            }
        }

        private static string NativeSortLabelText(SortMode mode)
        {
            GameObject menu = CurrentSortMenu();
            if (menu == null || !NativeSortObjNames.TryGetValue(mode, out string obj)) return null;
            Transform label = menu.transform.Find(obj + "/Label");
            if (label == null) return null;
            TMP_Text text = label.GetComponent<TMP_Text>();
            return text != null ? text.text : null;
        }

        // ── Starting Sort (Options > Menu) ─────────────────────────────────────

        // Keys are SortMode names (saved in Config.StartingSort); labels are what the option shows.
        private static readonly SortMode[] StartingSortModes =
        {
            SortMode.Default, SortMode.AToZ, SortMode.ZToA, SortMode.MostStars, SortMode.LeastStars,
            SortMode.MostPlayed, SortMode.LeastPlayed, SortMode.MostRecent,
            SortMode.Artist, SortMode.Mapper, SortMode.DateAdded, SortMode.ExRank
        };

        /// <summary>
        /// Options for the Starting Sort cycle. The stars sorts use the grade wording under EX
        /// scoring, like the Sort menu, and EX Rank is only offered under EX scoring.
        /// </summary>
        public static void GetStartingSortOptions(out string[] labels, out string[] keys)
        {
            bool ex = Config.ExType;
            var l = new List<string>();
            var k = new List<string>();
            foreach (SortMode m in StartingSortModes)
            {
                if (m == SortMode.ExRank && !ex) continue;
                string label;
                switch (m)
                {
                    case SortMode.Default: label = "Default"; break;
                    case SortMode.AToZ: label = "A-Z"; break;
                    case SortMode.ZToA: label = "Z-A"; break;
                    case SortMode.MostStars: label = ex ? HighGradeText : "Most Stars"; break;
                    case SortMode.LeastStars: label = ex ? LowGradeText : "Least Stars"; break;
                    case SortMode.MostPlayed: label = "Most Played"; break;
                    case SortMode.LeastPlayed: label = "Least Played"; break;
                    case SortMode.MostRecent: label = "Most Recent"; break;
                    default: label = CustomSortName(m); break;
                }
                l.Add(label);
                k.Add(m.ToString());
            }
            labels = l.ToArray();
            keys = k.ToArray();
        }

        private static bool TryGetGameSort(SortMode mode, out SongSelect.Sort sort)
        {
            switch (mode)
            {
                case SortMode.Default: sort = SongSelect.Sort.Default; return true;
                case SortMode.AToZ: sort = SongSelect.Sort.AToZ; return true;
                case SortMode.ZToA: sort = SongSelect.Sort.ZToA; return true;
                case SortMode.MostStars: sort = SongSelect.Sort.MostStars; return true;
                case SortMode.LeastStars: sort = SongSelect.Sort.LeastStars; return true;
                case SortMode.MostPlayed: sort = SongSelect.Sort.MostPlayed; return true;
                case SortMode.LeastPlayed: sort = SongSelect.Sort.LeastPlayed; return true;
                case SortMode.MostRecent: sort = SongSelect.Sort.MostRecent; return true;
                default: sort = SongSelect.Sort.Default; return false;
            }
        }

        /// <summary>
        /// First song page entry after launch: switch to the saved Starting Sort. Native sorts go
        /// through SongSelect.ChangeSort (the same path as the Sort menu), custom ones through
        /// SetCustomSort. EX Rank in Audica mode starts on Default; the saved choice is kept.
        /// </summary>
        private static void ApplyStartingSortOnce(SongSelect songSelect)
        {
            if (startingSortApplied) return;
            startingSortApplied = true;

            string key = Config.StartingSort;
            if (string.IsNullOrEmpty(key)) return;

            SortMode mode;
            try { mode = (SortMode)Enum.Parse(typeof(SortMode), key, true); }
            catch
            {
                DebugLog.Log($"[FolderRowManager] Starting Sort '{key}' not recognised, using Default.");
                return;
            }
            if (mode == SortMode.Default) return;

            DebugLog.Log($"[FolderRowManager] Starting Sort: {mode}");

            if (IsCustomSortMode(mode))
            {
                if (mode == SortMode.ExRank && !Config.ExType)
                {
                    DebugLog.Log("[FolderRowManager] Starting Sort is EX Rank but EX scoring is off: starting on Default.");
                    return;
                }
                SetCustomSort(mode);
                return;
            }

            if (!TryGetGameSort(mode, out SongSelect.Sort gameSort)) return;
            try
            {
                songSelect.ChangeSort(gameSort);
            }
            catch (Exception ex)
            {
                DebugLog.Log($"[FolderRowManager] Starting Sort ChangeSort failed ({ex.Message}), applying it to the folder view only.");
                SetSort(gameSort);
            }
        }

        // ── Shared helpers for the custom views ────────────────────────────────

        /// <summary>The visible (not hidden) SongData for an id, or null.</summary>
        private static SongList.SongData FindVisibleSong(string songID)
        {
            if (string.IsNullOrEmpty(songID) || SongList.I == null || SongList.I.songs == null) return null;
            for (int i = 0; i < SongList.I.songs.Count; i++)
            {
                var sd = SongList.I.songs[i];
                if (sd != null && !sd.hidden && sd.songID == songID) return sd;
            }
            return null;
        }

        /// <summary>(title, id) pairs to ids, sorted by title (case-insensitive).</summary>
        private static List<string> IdsByTitle(List<KeyValuePair<string, string>> entries)
        {
            entries.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));
            var ids = new List<string>(entries.Count);
            foreach (var e in entries) ids.Add(e.Value);
            return ids;
        }

        private static List<string> GetGroupSongs(List<KeyValuePair<string, List<string>>> groups, string folder)
        {
            foreach (var kv in groups)
                if (kv.Key == folder) return kv.Value;
            return new List<string>();
        }

        /// <summary>A header per group (color from <paramref name="colorFor"/>, or the default grey), open group's songs inlined.</summary>
        private static List<ViewRow> BuildGroupView(List<KeyValuePair<string, List<string>>> groups, string what,
                                                     Func<string, Color?> colorFor = null)
        {
            var rows = new List<ViewRow>();
            string open = SongFolderManager.openFolder;

            foreach (var kv in groups)
            {
                rows.Add(ViewRow.Header(kv.Key, kv.Value.Count, null, colorFor != null ? colorFor(kv.Key) : null));
                if (kv.Key == open)
                    foreach (string id in kv.Value)
                        rows.Add(ViewRow.SongRow(id));
            }

            DebugLog.Log($"[FolderRowManager] Build{what}View: {groups.Count} folder(s), open={open ?? "none"}");
            return rows;
        }

        // ── Artist / Mapper view ───────────────────────────────────────────────
        //
        // One folder per exact name, case-insensitive and trimmed ("A & B" is its own folder).
        // A name written with different casing across songs shows the spelling most songs use.
        // Folders A-Z, "Unknown Artist" / "Unknown Mapper" (no name) last.
        private const string UnknownArtistLabel = "Unknown Artist";
        private const string UnknownMapperLabel = "Unknown Mapper";

        // Last grouping's name key -> folder label, per field. Lets CurrentFolderFor find a song's
        // folder without regrouping; rebuilt with every grouping, dropped with the sort caches.
        private static Dictionary<string, string> artistLabelCache;
        private static Dictionary<string, string> mapperLabelCache;

        private static string NameKey(string name) => (name ?? "").Trim().ToLowerInvariant();

        private static List<KeyValuePair<string, List<string>>> BuildNameGroups(bool mapper)
        {
            var byKey = new Dictionary<string, List<KeyValuePair<string, string>>>(); // name key -> (title, id)
            var spellings = new Dictionary<string, Dictionary<string, int>>();          // name key -> spelling -> songs

            for (int i = 0; i < SongList.I.songs.Count; i++)
            {
                var sd = SongList.I.songs[i];
                if (sd == null || sd.hidden) continue;

                string name = ((mapper ? sd.author : sd.artist) ?? "").Trim();
                string key = name.ToLowerInvariant();

                if (!byKey.TryGetValue(key, out var list))
                {
                    list = new List<KeyValuePair<string, string>>();
                    byKey[key] = list;
                    spellings[key] = new Dictionary<string, int>();
                }
                list.Add(new KeyValuePair<string, string>(sd.title ?? "", sd.songID));

                var sp = spellings[key];
                sp[name] = sp.TryGetValue(name, out int c) ? c + 1 : 1;
            }

            string unknown = mapper ? UnknownMapperLabel : UnknownArtistLabel;
            var labels = new Dictionary<string, string>(); // name key -> label
            foreach (var kv in spellings)
            {
                if (kv.Key.Length == 0) { labels[kv.Key] = unknown; continue; }

                string best = null;
                int bestCount = -1;
                foreach (var s in kv.Value)
                {
                    if (s.Value > bestCount || (s.Value == bestCount && string.CompareOrdinal(s.Key, best) < 0))
                    {
                        best = s.Key;
                        bestCount = s.Value;
                    }
                }
                labels[kv.Key] = best;
            }

            var keys = new List<string>(byKey.Keys);
            keys.Sort((a, b) =>
            {
                if (a.Length == 0 || b.Length == 0) return (a.Length == 0 ? 1 : 0) - (b.Length == 0 ? 1 : 0); // unknown last
                int c = string.Compare(labels[a], labels[b], StringComparison.OrdinalIgnoreCase);
                return c != 0 ? c : string.CompareOrdinal(labels[a], labels[b]);
            });

            var result = new List<KeyValuePair<string, List<string>>>();
            foreach (string k in keys)
                result.Add(new KeyValuePair<string, List<string>>(labels[k], IdsByTitle(byKey[k])));

            if (mapper) mapperLabelCache = labels; else artistLabelCache = labels;
            return result;
        }

        private static List<ViewRow> BuildNameView(bool mapper)
            => BuildGroupView(BuildNameGroups(mapper), mapper ? "Mapper" : "Artist");

        /// <summary>The folder label a name goes in (same as the grouping's).</summary>
        private static string NameGroupLabel(string name, bool mapper)
        {
            var cache = mapper ? mapperLabelCache : artistLabelCache;
            if (cache == null)
            {
                BuildNameGroups(mapper);
                cache = mapper ? mapperLabelCache : artistLabelCache;
            }
            return cache != null && cache.TryGetValue(NameKey(name), out string label) ? label : null;
        }

        // ── Date Added view ────────────────────────────────────────────────────
        //
        // Buckets by when the song's .audica file arrived: the later of its creation and last-write
        // times (a copied file gets a new creation time but keeps its write time; one updated in
        // place gets a new write time). Same graduated labels as Most Recent (Today, Yesterday,
        // N days ago, months, years), newest first. Songs whose file can't be found go in "Unknown
        // Date". The official songs keep their usual "Audica" / "Audica DLC" folders, at the end.
        private const string UnknownDateLabel = "Unknown Date";

        // songID|zipPath -> file date (local ticks), 0 = file not found. Kept for the session: file
        // dates don't change under a loaded song, and a new file means a new zipPath key.
        private static readonly Dictionary<string, long> addedTicksCache = new Dictionary<string, long>();

        private static bool IsOfficialFolder(string folder) =>
            folder == SongFolderManager.FolderAudica || folder == SongFolderManager.FolderAudicaDLC;

        private static long AddedTicks(SongList.SongData sd)
        {
            string key = sd.songID + "|" + (sd.zipPath ?? "");
            if (addedTicksCache.TryGetValue(key, out long ticks)) return ticks;

            ticks = 0;
            string file = ResolveSongFilePath(sd);
            if (file != null)
            {
                try
                {
                    DateTime created = File.GetCreationTime(file);
                    DateTime written = File.GetLastWriteTime(file);
                    ticks = (created > written ? created : written).Ticks;
                }
                catch (Exception ex)
                {
                    DebugLog.Log($"[FolderRowManager] Date Added: couldn't read {file}: {ex.Message}");
                }
            }

            addedTicksCache[key] = ticks;
            return ticks;
        }

        /// <summary>The .audica file a song was loaded from, or null (same lookup as the Delete button).</summary>
        private static string ResolveSongFilePath(SongList.SongData song)
        {
            string zipPath = song.zipPath;
            if (string.IsNullOrEmpty(zipPath)) return null;

            var candidates = new List<string>();
            try
            {
                if (Path.IsPathRooted(zipPath))
                    candidates.Add(zipPath);
                else
                {
                    if (!string.IsNullOrEmpty(song.searchRoot)) candidates.Add(Path.Combine(song.searchRoot, zipPath));
                    candidates.Add(Path.Combine(Application.dataPath, zipPath));
                }

                string fileName = Path.GetFileName(zipPath);
                if (!string.IsNullOrEmpty(ExScoring.mainSongDirectory)) candidates.Add(Path.Combine(ExScoring.mainSongDirectory, fileName));
                if (!string.IsNullOrEmpty(ExScoring.downloadsDirectory)) candidates.Add(Path.Combine(ExScoring.downloadsDirectory, fileName));
            }
            catch { return null; } // invalid characters in the path

            foreach (string candidate in candidates)
            {
                try
                {
                    string full = Path.GetFullPath(candidate);
                    if (File.Exists(full)) return full;
                }
                catch { }
            }
            return null;
        }

        /// <summary>Whole days since the file date (clamped to 0), or -1 when unknown.</summary>
        private static int AddedDaysAgo(SongList.SongData sd)
        {
            long ticks = AddedTicks(sd);
            if (ticks <= 0) return -1;
            int d = (DateTime.Now.Date - new DateTime(ticks).Date).Days;
            return d < 0 ? 0 : d;
        }

        private static string DateAddedLabelFor(SongList.SongData sd)
        {
            string home = SongFolderManager.GetFolder(sd.songID);
            if (IsOfficialFolder(home)) return home;

            int days = AddedDaysAgo(sd);
            return days < 0 ? UnknownDateLabel : RecentLabel(days);
        }

        private static List<KeyValuePair<string, List<string>>> BuildDateAddedGroups()
        {
            var byLabel = new Dictionary<string, List<KeyValuePair<string, string>>>(); // label -> (title, id)
            var rank = new Dictionary<string, int>();                                    // label -> sort key (ascending)

            const int UnknownRank = int.MaxValue - 2;
            const int AudicaRank = int.MaxValue - 1;
            const int AudicaDLCRank = int.MaxValue;

            for (int i = 0; i < SongList.I.songs.Count; i++)
            {
                var sd = SongList.I.songs[i];
                if (sd == null || sd.hidden) continue;

                string label;
                int key;
                string home = SongFolderManager.GetFolder(sd.songID);
                if (home == SongFolderManager.FolderAudica) { label = home; key = AudicaRank; }
                else if (home == SongFolderManager.FolderAudicaDLC) { label = home; key = AudicaDLCRank; }
                else
                {
                    int days = AddedDaysAgo(sd);
                    if (days < 0) { label = UnknownDateLabel; key = UnknownRank; }
                    else { label = RecentLabel(days); key = days; }
                }

                if (!byLabel.TryGetValue(label, out var list))
                {
                    list = new List<KeyValuePair<string, string>>();
                    byLabel[label] = list;
                    rank[label] = key;
                }
                else if (key < rank[label]) rank[label] = key; // newest member represents the bucket
                list.Add(new KeyValuePair<string, string>(sd.title ?? "", sd.songID));
            }

            var labels = new List<string>(byLabel.Keys);
            labels.Sort((a, b) => rank[a].CompareTo(rank[b])); // fewest days ago first

            var result = new List<KeyValuePair<string, List<string>>>();
            foreach (string lbl in labels)
                result.Add(new KeyValuePair<string, List<string>>(lbl, IdsByTitle(byLabel[lbl])));
            return result;
        }

        private static List<ViewRow> BuildDateAddedView()
            => BuildGroupView(BuildDateAddedGroups(), "DateAdded",
                folder => IsOfficialFolder(folder) ? FolderColorFor(folder) : (Color?)null);

        // ── EX Rank view (EX scoring only) ─────────────────────────────────────
        //
        // One folder per exact global rank on the selected difficulty ("1st", "2nd", ... however many
        // that makes), then "Unranked" (no EX run on that difficulty, a hidden player, or no API key).
        // Ranks come from the once-per-launch /me/bests fetch (ExScoring.TryGetGlobalRank): until it
        // has answered, the view is a single "Loading ranks..." row. The view is rebuilt when the
        // ranks change (fetch done, a run uploaded) or the difficulty changes.
        private const string UnrankedLabel = "Unranked";
        private const string RankLoadingLabel = "Loading ranks...";

        private static int rankViewVersion = -1;
        private static string rankViewDifficulty;
        private static bool rankViewWaiting;

        private static int RankFor(string songID)
            => ExScoring.TryGetGlobalRank(songID, CurrentDifficultyName(), out int rank) ? rank : 0;

        private static string RankLabel(int rank) => rank > 0 ? RankOrdinal(rank) : UnrankedLabel;

        private static string RankOrdinal(int n)
        {
            int mod100 = n % 100;
            if (mod100 >= 11 && mod100 <= 13) return n + "th";
            switch (n % 10)
            {
                case 1: return n + "st";
                case 2: return n + "nd";
                case 3: return n + "rd";
                default: return n + "th";
            }
        }

        private static List<KeyValuePair<string, List<string>>> BuildRankGroups()
        {
            var byRank = new Dictionary<int, List<KeyValuePair<string, string>>>(); // rank (0 = unranked) -> (title, id)

            for (int i = 0; i < SongList.I.songs.Count; i++)
            {
                var sd = SongList.I.songs[i];
                if (sd == null || sd.hidden) continue;

                int rank = RankFor(sd.songID);
                if (!byRank.TryGetValue(rank, out var list))
                {
                    list = new List<KeyValuePair<string, string>>();
                    byRank[rank] = list;
                }
                list.Add(new KeyValuePair<string, string>(sd.title ?? "", sd.songID));
            }

            var ranks = new List<int>(byRank.Keys);
            ranks.Sort((a, b) =>
            {
                if (a == 0 || b == 0) return (a == 0 ? 1 : 0) - (b == 0 ? 1 : 0); // Unranked last
                return a.CompareTo(b);
            });

            var result = new List<KeyValuePair<string, List<string>>>();
            foreach (int r in ranks)
                result.Add(new KeyValuePair<string, List<string>>(RankLabel(r), IdsByTitle(byRank[r])));
            return result;
        }

        private static List<ViewRow> BuildRankView()
        {
            ExScoring.EnsureOnlineBestScores(); // nothing may have asked yet if no song row was bound

            rankViewVersion = ExScoring.OnlineRanksVersion;
            rankViewDifficulty = CurrentDifficultyName();

            if (!ExScoring.OnlineBestScoresSettled)
            {
                rankViewWaiting = true;
                return new List<ViewRow> { ViewRow.LabelRow(RankLoadingLabel, ViewRow.FolderColor) };
            }

            rankViewWaiting = false;
            return BuildGroupView(BuildRankGroups(), "Rank");
        }

        /// <summary>Re-applies the EX Rank view when the ranks or the difficulty changed since it was built.</summary>
        private static void TickRankView()
        {
            if (currentSort != SortMode.ExRank || level != NavLevel.Root) return;

            bool stale;
            if (rankViewWaiting)
                stale = ExScoring.OnlineBestScoresSettled;
            else
                stale = rankViewVersion != ExScoring.OnlineRanksVersion || rankViewDifficulty != CurrentDifficultyName();

            if (stale) Apply();
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
            artistLabelCache = null;
            mapperLabelCache = null;
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
                    DebugLog.Log($"[FolderRowManager] star calc failed for {id}: {ex.Message}");
                }
                starCache[id] = enc;
            }

            DebugLog.Log($"[FolderRowManager] Star cache built: {starCache.Count} songs");
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

            DebugLog.Log($"[FolderRowManager] BuildStarsView ({(reversed ? "Least" : "Most")}Stars): " +
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

        // ── Grade view (EX scoring's version of MostStars / LeastStars) ────────
        //
        // Under EX scoring the two "stars" sort buttons become "High grade" / "Low grade": songs are
        // bucketed by the grade of their best saved EX run ON THE CURRENTLY SELECTED DIFFICULTY (the
        // same run the song's own row shows), one folder per grade that actually has a song in it,
        // then "Unplayed". High grade: 4-Star first ... F, then Unplayed. Low grade flips the list.
        //
        // Unlike the native star cache this needs run files read from disk, so it's filled by the
        // background run loader (RunDataRecalculator.cs) and the view shows a single "Loading
        // grades..." row until every song's grade is known. Entries are keyed by song+difficulty and
        // kept for the whole session; a saved run drops just its own entry (InvalidateGrade).
        private const int GradeUnplayed = -1;
        private const string GradeLoadingLabel = "Loading grades...";
        private const float GradeLoadTimeoutSeconds = 30f;

        private static readonly Dictionary<string, int> gradeCache = new Dictionary<string, int>(); // songId|difficulty -> (int)Grade, or GradeUnplayed
        private static bool gradeLoadRunning;
        private static int gradeLoadPending;
        private static string pendingGradeReveal; // song to reveal once the grades finish loading

        private static bool IsStarSort => currentSort == SortMode.MostStars || currentSort == SortMode.LeastStars;

        /// <summary>True when the stars sorts should bucket by EX grade instead of Audica stars.</summary>
        private static bool UseGradeView => Config.ExType;

        private static string GradeKey(string songId, string difficulty) => songId + "|" + difficulty;

        private static string CurrentDifficultyName() => KataConfig.I.GetDifficulty().ToString();

        /// <summary>Forget one song+difficulty's cached grade (a new run was just saved for it).</summary>
        public static void InvalidateGrade(string songId, string difficulty)
        {
            gradeCache.Remove(GradeKey(songId, difficulty));
        }

        /// <summary>Forget every cached grade (the API key changed, so the online half of the
        /// merge belongs to a different account now).</summary>
        public static void ClearGradeCache()
        {
            gradeCache.Clear();
        }

        /// <summary>
        /// Local best grade merged with the player's uploaded (leaderboard) best, whichever is
        /// better. `localBest` is null when there's no local run. Grades run best (Quad = 0) to
        /// worst, so the lower int wins.
        /// </summary>
        private static int MergedGrade(string songId, string difficulty, ExScoring.RunSummary localBest)
        {
            int grade = localBest == null
                ? GradeUnplayed
                : (int)ExScoring.GetGrade(localBest.judgementPercent, localBest.failed);

            if (ExScoring.TryGetOnlineBest(songId, difficulty, out ExScoring.OnlineBestScore online))
            {
                int onlineGrade = (int)ExScoring.GetGrade(online.judgementPercent, online.failed);
                if (grade == GradeUnplayed || onlineGrade < grade) grade = onlineGrade;
            }

            return grade;
        }

        /// <summary>
        /// Called when the online best scores arrive (SongListHighScoreUI.cs). Normally the grade
        /// load already waited for them, so this changes nothing — it only matters when they land
        /// after a grade load gave up waiting (slow/failed request, retried later). Upgrades cached
        /// grades in place and rebuilds the grade view if it's what's on screen.
        /// </summary>
        public static void ApplyOnlineBestScores()
        {
            if (gradeCache.Count == 0) return;

            bool changed = false;
            var keys = new List<string>(gradeCache.Keys);

            foreach (string key in keys)
            {
                int sep = key.LastIndexOf('|');
                if (sep <= 0) continue;

                string songId = key.Substring(0, sep);
                string difficulty = key.Substring(sep + 1);

                if (!ExScoring.TryGetOnlineBest(songId, difficulty, out ExScoring.OnlineBestScore online)) continue;

                int onlineGrade = (int)ExScoring.GetGrade(online.judgementPercent, online.failed);
                int current = gradeCache[key];
                if (current == GradeUnplayed || onlineGrade < current)
                {
                    gradeCache[key] = onlineGrade;
                    changed = true;
                }
            }

            if (!changed) return;

            DebugLog.Log("[FolderRowManager] Grade cache upgraded from online best scores.");

            if (gradeLoadRunning) return; // the running load re-applies the view itself when it finishes
            if (!VirtualSongList.IsActive || level != NavLevel.Root || !IsStarSort || !UseGradeView) return;

            Apply();
        }

        /// <summary>
        /// True when every visible song has a cached grade for the selected difficulty. Otherwise
        /// kicks off (or keeps waiting on) the background load and returns false; the load re-applies
        /// the view by itself when it finishes.
        /// </summary>
        private static bool EnsureGradeCache()
        {
            if (gradeLoadRunning) return false;

            string difficulty = CurrentDifficultyName();
            List<string> missing = null;

            for (int i = 0; i < SongList.I.songs.Count; i++)
            {
                var sd = SongList.I.songs[i];
                if (sd == null || sd.hidden) continue;
                if (gradeCache.ContainsKey(GradeKey(sd.songID, difficulty))) continue;

                if (missing == null) missing = new List<string>();
                missing.Add(sd.songID);
            }

            if (missing == null) return true;

            gradeLoadRunning = true;
            MelonCoroutines.Start(LoadGradesCoroutine(missing, difficulty));
            return false;
        }

        private static IEnumerator LoadGradesCoroutine(List<string> songIds, string difficulty)
        {
            // The run file index is built on the loader thread; once it exists, songs with no run
            // files for this difficulty are settled here without queueing anything.
            yield return ExScoring.WaitForRunFileIndex();

            // Leaderboard-only scores (e.g. after a clean install) count too — wait for the
            // once-per-launch online fetch (bounded) so the buckets are built once with both sources
            // instead of reshuffling when it lands. The view is already showing "Loading grades...".
            yield return ExScoring.WaitForOnlineBestScores();

            gradeLoadPending = 0;
            int queued = 0;
            for (int i = 0; i < songIds.Count; i++)
            {
                string id = songIds[i];
                if (!ExScoring.HasRunFiles(id, difficulty))
                {
                    gradeCache[GradeKey(id, difficulty)] = MergedGrade(id, difficulty, null); // online best, or Unplayed
                    continue;
                }

                gradeLoadPending++;
                queued++;
                MelonCoroutines.Start(LoadOneGradeCoroutine(id, difficulty));
            }

            float deadline = Time.realtimeSinceStartup + GradeLoadTimeoutSeconds;
            while (gradeLoadPending > 0 && Time.realtimeSinceStartup < deadline)
                yield return null;

            if (gradeLoadPending > 0)
            {
                // Something never reported back. Settle the stragglers as Unplayed so the view can
                // still be built instead of sitting on the loading row (or reloading forever).
                DebugLog.Log($"[FolderRowManager] Grade load timed out with {gradeLoadPending} song(s) pending.");
                for (int i = 0; i < songIds.Count; i++)
                {
                    string key = GradeKey(songIds[i], difficulty);
                    if (!gradeCache.ContainsKey(key)) gradeCache[key] = GradeUnplayed;
                }
                gradeLoadPending = 0;
            }

            gradeLoadRunning = false;
            DebugLog.Log($"[FolderRowManager] Grade cache loaded for {difficulty}: {songIds.Count} song(s) checked, {queued} with runs.");

            string reveal = pendingGradeReveal;
            pendingGradeReveal = null;

            if (!VirtualSongList.IsActive || level != NavLevel.Root || !IsStarSort || !UseGradeView)
                yield break; // the player moved on; the cache is warm for next time

            Apply();
            if (!string.IsNullOrEmpty(reveal))
                RevealAndSelect(reveal);
        }

        private static IEnumerator LoadOneGradeCoroutine(string songId, string difficulty)
        {
            yield return ExScoring.LoadBestRunSummaryForSong(songId, difficulty, best =>
            {
                gradeCache[GradeKey(songId, difficulty)] = MergedGrade(songId, difficulty, best);
            });

            gradeLoadPending--;
        }

        /// <summary>Canonical bucket label for a cached grade. Must match between grouping and lookups.</summary>
        private static string GradeBucketLabel(int grade) =>
            grade == GradeUnplayed ? "Unplayed" : ExScoring.GetGradeText((ExScoring.Grade)grade);

        /// <summary>
        /// Group songs into ordered grade buckets for the selected difficulty. High grade order:
        /// best grade first, Unplayed last. Reversed flips the whole list. Only grades that have at
        /// least one song get a bucket. Songs within a bucket are alphabetical by title.
        /// Assumes EnsureGradeCache() returned true (anything still unknown counts as Unplayed).
        /// </summary>
        private static List<KeyValuePair<string, List<string>>> BuildGradeGroups(bool reversed)
        {
            string difficulty = CurrentDifficultyName();
            var byGrade = new Dictionary<int, List<KeyValuePair<string, string>>>(); // grade -> (title, id)

            for (int i = 0; i < SongList.I.songs.Count; i++)
            {
                var sd = SongList.I.songs[i];
                if (sd == null || sd.hidden) continue;
                string id = sd.songID, title = sd.title ?? "";

                int grade = gradeCache.TryGetValue(GradeKey(id, difficulty), out int g) ? g : GradeUnplayed;

                if (!byGrade.TryGetValue(grade, out var list))
                {
                    list = new List<KeyValuePair<string, string>>();
                    byGrade[grade] = list;
                }
                list.Add(new KeyValuePair<string, string>(title, id));
            }

            // The Grade enum runs best (Quad = 0) to worst (F), so ascending is best-first.
            var grades = new List<int>(byGrade.Keys);
            grades.Sort((a, b) =>
            {
                if (a == b) return 0;
                if (a == GradeUnplayed) return 1;
                if (b == GradeUnplayed) return -1;
                return a.CompareTo(b);
            });
            if (reversed) grades.Reverse();

            var result = new List<KeyValuePair<string, List<string>>>();
            foreach (int grade in grades)
            {
                var entries = byGrade[grade];
                entries.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));

                var ids = new List<string>(entries.Count);
                foreach (var kv in entries) ids.Add(kv.Value);
                result.Add(new KeyValuePair<string, List<string>>(GradeBucketLabel(grade), ids));
            }
            return result;
        }

        /// <summary>Grade-bucket view: a text header per non-empty grade, open bucket's songs inlined.
        /// A single inert "Loading grades..." row while the grades are still being read.</summary>
        private static List<ViewRow> BuildGradeView(bool reversed)
        {
            var rows = new List<ViewRow>();

            if (!EnsureGradeCache())
            {
                rows.Add(ViewRow.ActionRow(GradeLoadingLabel, ViewRow.FolderColor, null));
                return rows;
            }

            var groups = BuildGradeGroups(reversed);
            string open = SongFolderManager.openFolder;

            foreach (var kv in groups)
            {
                rows.Add(ViewRow.Header(kv.Key, kv.Value.Count));
                if (kv.Key == open)
                    foreach (string id in kv.Value)
                        rows.Add(ViewRow.SongRow(id));
            }

            DebugLog.Log($"[FolderRowManager] BuildGradeView ({(reversed ? "Low" : "High")} grade, {CurrentDifficultyName()}): " +
                $"{groups.Count} bucket(s), open={open ?? "none"}");
            return rows;
        }

        /// <summary>Songs in one grade bucket, in display (alphabetical) order.</summary>
        private static List<string> GetGradeFolderSongs(string folder)
        {
            if (!EnsureGradeCache()) return new List<string>();

            foreach (var kv in BuildGradeGroups(false))
                if (kv.Key == folder) return kv.Value;
            return new List<string>();
        }

        // ── Sort menu labels ("More stars"/"Less stars" <-> "High grade"/"Low grade") ──
        //
        // The two stars sort buttons are relabelled while EX scoring is on and put back for Audica
        // scoring. The labels are SortMenu/Sort_MostStars/Label and Sort_LeastStars/Label, re-applied
        // for a few frames whenever the menu opens, the sort changes or the scoring type flips, so
        // the game's own text refresh can't leave the native wording behind.
        private const string SortMenuPath = "menu/ShellPage_Song/page/ShellPanel_Center/SongSelect/SortMenu";
        private const string SortButtonPath = "menu/ShellPage_Song/page/ShellPanel_Center/SongSelect/SortButton";

        private const string MostStarsLabelPath = "Sort_MostStars/Label";
        private const string LeastStarsLabelPath = "Sort_LeastStars/Label";
        private const string HighGradeText = "High grade";
        private const string LowGradeText = "Low grade";

        private static TMP_Text mostStarsLabel;
        private static TMP_Text leastStarsLabel;
        private static string mostStarsOriginal;  // the game's own (localized) wording, captured before we overwrite it
        private static string leastStarsOriginal;

        private static GameObject sortMenuObj;
        private static GameObject sortButtonObj;
        private static List<TMP_Text> sortLabelTexts;
        private static bool sortMenuWasActive;
        private static bool sortLabelLastEx;
        private static int sortLabelPassFrames;
        private static int sortLabelNextFindFrame;

        /// <summary>Ask for the sort labels to be re-checked over the next few frames.</summary>
        public static void RequestSortLabelRefresh()
        {
            sortLabelPassFrames = 5;
        }

        /// <summary>Per-frame (Main.OnUpdate). Does nothing but two bool checks unless a refresh is due.</summary>
        public static void TickSortLabels()
        {
            TickCustomSorts();

            if (sortMenuObj == null)
            {
                // Only go looking while the song list is up, and not every frame.
                if (!VirtualSongList.IsActive) return;
                if (Time.frameCount < sortLabelNextFindFrame) return;
                sortLabelNextFindFrame = Time.frameCount + 60;

                sortMenuObj = GameObject.Find(SortMenuPath);
                sortButtonObj = GameObject.Find(SortButtonPath);
                sortLabelTexts = null;
                if (sortMenuObj == null) return;

                sortMenuWasActive = !sortMenuObj.activeInHierarchy; // force a first pass
            }

            bool menuActive = sortMenuObj.activeInHierarchy;
            if (menuActive != sortMenuWasActive || Config.ExType != sortLabelLastEx)
            {
                sortMenuWasActive = menuActive;
                sortLabelLastEx = Config.ExType;
                sortLabelTexts = null; // rescan: cheap, and picks up anything created since
                sortLabelPassFrames = 5;
            }

            if (sortLabelPassFrames <= 0) return;
            sortLabelPassFrames--;

            ApplySortLabels();
        }

        private static void ApplySortLabels()
        {
            try
            {
                if (sortLabelTexts == null)
                {
                    mostStarsLabel = FindSortLabel(MostStarsLabelPath);
                    leastStarsLabel = FindSortLabel(LeastStarsLabelPath);

                    // The sort button itself may echo the current sort's name.
                    sortLabelTexts = new List<TMP_Text>();
                    if (sortButtonObj != null)
                    {
                        var found = sortButtonObj.GetComponentsInChildren<TMP_Text>(true);
                        if (found != null)
                            for (int i = 0; i < found.Length; i++)
                                if (found[i] != null) sortLabelTexts.Add(found[i]);
                    }
                }

                bool ex = Config.ExType;
                ApplySortLabel(mostStarsLabel, ref mostStarsOriginal, HighGradeText, ex);
                ApplySortLabel(leastStarsLabel, ref leastStarsOriginal, LowGradeText, ex);

                for (int i = 0; i < sortLabelTexts.Count; i++)
                {
                    TMP_Text text = sortLabelTexts[i];
                    if (text == null) continue;

                    string current = text.text;
                    string wanted = SwapSortWording(current, mostStarsOriginal, HighGradeText, ex);
                    wanted = SwapSortWording(wanted, leastStarsOriginal, LowGradeText, ex);
                    if (wanted != current) text.text = wanted;
                }
            }
            catch (Exception ex)
            {
                DebugLog.Log($"[FolderRowManager] Sort label update failed: {ex.Message}");
                sortLabelTexts = null;
            }
        }

        /// <summary>The text component of one sort button's Label child (path relative to SortMenu).</summary>
        private static TMP_Text FindSortLabel(string relativePath)
        {
            Transform label = sortMenuObj.transform.Find(relativePath);
            if (label == null)
            {
                DebugLog.Log($"[FolderRowManager] Sort label not found: {relativePath}");
                return null;
            }

            TMP_Text text = label.GetComponent<TMP_Text>();
            if (text == null) text = label.gameObject.GetComponentInChildren<TMP_Text>(true);
            if (text == null) DebugLog.Log($"[FolderRowManager] No text component on sort label: {relativePath}");
            return text;
        }

        /// <summary>Shows the grade wording under EX scoring and the game's own wording otherwise.
        /// Whatever the game last wrote there is remembered so it can be put back.</summary>
        private static void ApplySortLabel(TMP_Text label, ref string original, string gradeText, bool ex)
        {
            if (label == null) return;

            string current = label.text ?? "";
            bool showingGrade = string.Equals(current, gradeText, StringComparison.OrdinalIgnoreCase);

            if (!showingGrade && current.Length > 0)
                original = current; // native text (first time, or the game just rewrote it)

            if (ex)
            {
                string wanted = MatchCase(gradeText, original);
                if (current != wanted) label.text = wanted;
            }
            else if (showingGrade && !string.IsNullOrEmpty(original))
            {
                label.text = original;
            }
        }

        /// <summary>Upper-cases the grade wording when the game's own label is all upper case.</summary>
        private static string MatchCase(string gradeText, string original)
        {
            if (string.IsNullOrEmpty(original)) return gradeText;
            return original == original.ToUpperInvariant() && original != original.ToLowerInvariant()
                ? gradeText.ToUpperInvariant()
                : gradeText;
        }

        /// <summary>Swaps the game's wording for the grade wording (or back) where it appears inside a longer label.</summary>
        private static string SwapSortWording(string text, string original, string gradeText, bool toGrade)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(original)) return text;

            string from = toGrade ? original : gradeText;
            string to = toGrade ? MatchCase(gradeText, original) : original;

            int at = text.IndexOf(from, StringComparison.OrdinalIgnoreCase);
            if (at < 0) return text;
            return text.Substring(0, at) + to + text.Substring(at + from.Length);
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
                    DebugLog.Log($"[FolderRowManager] play count failed for {id}: {ex.Message}");
                }
                playCache[id] = count;
            }

            DebugLog.Log($"[FolderRowManager] Play cache built: {playCache.Count} songs");
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

            DebugLog.Log($"[FolderRowManager] BuildPlaysView ({(reversed ? "Least" : "Most")}Played): " +
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
                    DebugLog.Log($"[FolderRowManager] recency failed for {id}: {ex.Message}");
                }
                recentCache[id] = daysAgo;
            }

            DebugLog.Log($"[FolderRowManager] Recent cache built: {recentCache.Count} songs");
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

            DebugLog.Log($"[FolderRowManager] BuildRecentView (MostRecent): {groups.Count} bucket(s), open={open ?? "none"}");
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
        private static UnityEngine.Color SR_DownloadableColor => ColorSettings.RowDownloadable.Value; // customisable (Options > Colors)
        private static UnityEngine.Color SR_DownloadAllColor => ColorSettings.RowDownloadAll.Value;

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
        internal static SongList.SongData FindLocalRequestSong(string title, string artist, string mapper, string songId)
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
            catch (System.Exception e) { DebugLog.Log($"[FolderRowManager] FindLocalRequestSong failed: {e.Message}"); }
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
            SongRequestStore.Remove(entry.SongID, toHistory: false); // moved to the available queue, not removed
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

        // ── Song Request History folder ───────────────────────────────────────────
        // Requests that left the list (played / removed / cleared), newest first. Downloaded songs show as
        // normal song rows; never-downloaded ones as downloadable rows (downloading does NOT re-request).

        // Two-shot Clear History: armed shows "Confirm Clear?" until shot again or a folder is toggled.
        private static bool histClearArmed;

        private static void OnClearHistory()
        {
            if (!histClearArmed)
            {
                histClearArmed = true;
                RefreshList();
                return;
            }

            histClearArmed = false;
            SongRequestHistoryStore.Clear();
            RefreshList(); // BuildDefaultRootView collapses + hides the now-empty folder
        }

        /// <summary>Header counts: total entries; missing = not downloaded locally.</summary>
        private static void GetHistoryCounts(out int total, out int missing)
        {
            var all = SongRequestHistoryStore.GetAll();
            total = all.Count;
            missing = 0;
            foreach (var h in all)
                if (FindLocalRequestSong(h.Title, h.Artist, h.Mapper, h.SongID) == null)
                    missing++;
        }

        /// <summary>Playable (downloaded) history song IDs in display order, for auto-select / random.</summary>
        private static List<string> GetHistorySongIDs()
        {
            var ids = new List<string>();
            var seen = new HashSet<string>();
            foreach (var h in SongRequestHistoryStore.GetAll())
            {
                var local = FindLocalRequestSong(h.Title, h.Artist, h.Mapper, h.SongID);
                if (local != null && !local.hidden && seen.Add(local.songID))
                    ids.Add(local.songID);
            }
            return ids;
        }

        private static void AppendHistoryRows(List<ViewRow> rows)
        {
            var all = SongRequestHistoryStore.GetAll();

            rows.Add(ViewRow.ActionRow(
                histClearArmed ? "Confirm Clear?" : "Clear History",
                histClearArmed ? PlaylistNav.ConfirmDeleteColor : PlaylistNav.DeleteColor,
                OnClearHistory,
                histClearArmed ? "Shoot again to confirm" : $"{all.Count} song{(all.Count != 1 ? "s" : "")}",
                "srh_clear"));

            var seen = new HashSet<string>();
            foreach (var h in all)
            {
                var local = FindLocalRequestSong(h.Title, h.Artist, h.Mapper, h.SongID);
                if (local != null)
                {
                    if (!local.hidden && seen.Add(local.songID))
                        rows.Add(ViewRow.SongRow(local.songID));
                    continue;
                }

                if (string.IsNullOrEmpty(h.SongID) || !seen.Add(h.SongID)) continue;
                string id = h.SongID;
                if (!string.IsNullOrEmpty(h.DownloadURL))
                    rows.Add(ViewRow.DownloadableSongRow(id, h.Title, h.Artist, h.Mapper, h.DownloadURL,
                             SR_DownloadableColor, () => OnDownloadHistory(id)));
                else
                    rows.Add(ViewRow.DisabledRow(h.Title ?? id, SR_DownloadableColor, "Not downloaded", id));
            }
        }

        private static SongRequestHistoryStore.HistoryEntry FindHistory(string songId)
        {
            foreach (var h in SongRequestHistoryStore.GetAll())
                if (h.SongID == songId) return h;
            return null;
        }

        private static void OnDownloadHistory(string songId)
        {
            if (srDownloadingAll) return;
            var entry = FindHistory(songId);
            if (entry == null || string.IsNullOrEmpty(entry.DownloadURL)) return;
            if (!srDownloading.Add(songId)) return; // already downloading

            srLastPct[songId] = -1;
            VirtualSongList.UpdateActionRowText(songId, entry.Title, "Downloading... 0%");
            MelonCoroutines.Start(SongDownloader.DownloadSong(
                entry.SongID, entry.DownloadURL,
                (id, ok) => OnHistoryDownloadComplete(entry, ok),
                (id, pct) =>
                {
                    int p = (int)(pct * 100f);
                    if (p < 0) p = 0;
                    if (p > 100) p = 100;
                    if (srLastPct.TryGetValue(entry.SongID, out int last) && last == p) return;
                    srLastPct[entry.SongID] = p;
                    VirtualSongList.UpdateActionRowText(entry.SongID, entry.Title, $"Downloading... {p}%");
                }));
        }

        private static void OnHistoryDownloadComplete(SongRequestHistoryStore.HistoryEntry entry, bool success)
        {
            srDownloading.Remove(entry.SongID);
            srLastPct.Remove(entry.SongID);

            if (!success)
            {
                VirtualSongList.UpdateActionRowText(entry.SongID, entry.Title, "Download failed - tap to retry");
                return;
            }

            // History only: the song is NOT put back in the request queue. Once the reload lands,
            // FindLocalRequestSong resolves it and the row turns into a normal song row.
            ExScoring.ReloadSongList(false);
            MelonCoroutines.Start(RefreshAfterReload());
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

        // ── Latest Maudica folder (maudica catalog, newest upload first) ──────────
        private static readonly HashSet<string> latestDownloading = new HashSet<string>();
        private static readonly Dictionary<string, int> latestLastPct = new Dictionary<string, int>();
        private static readonly HashSet<string> latestFailed = new HashSet<string>();

        /// <summary>Second line of the Latest Maudica header, reflecting load state.</summary>
        private static string LatestMaudicaHeaderSubLabel()
        {
            if (!LatestMaudica.Loaded)
            {
                if (LatestMaudica.Loading) return "Loading...";
                if (LatestMaudica.Error) return "Couldn't reach Maudica";
                return "Shoot to load";
            }
            if (LatestMaudica.Refreshing) return "Refreshing...";
            if (LatestMaudica.Error && LatestMaudica.ErrorWasRefresh) return "Couldn't refresh - showing earlier list";
            return $"Newest first - {LatestMaudica.TotalOnMaudica} on Maudica";
        }

        /// <summary>Installed songs in the loaded Latest Maudica list, in display order, deduped.
        /// Used for open-folder auto-select.</summary>
        private static List<string> GetLatestMaudicaSongIDs()
        {
            var ids = new List<string>();
            var seen = new HashSet<string>();
            foreach (var s in LatestMaudica.results)
            {
                string local = LatestMaudica.LocalSongIdFor(s);
                if (local != null && seen.Add(local))
                    ids.Add(local);
            }
            return ids;
        }

        /// <summary>
        /// Rows for the open Latest Maudica folder, in maudica order (newest first): installed
        /// songs as normal playable rows, the rest as downloadable rows, then "Load more songs"
        /// at the bottom while more pages remain.
        /// </summary>
        private static void AppendLatestMaudicaRows(List<ViewRow> rows)
        {
            if (!LatestMaudica.Loaded)
            {
                if (LatestMaudica.Loading)
                    rows.Add(ViewRow.ActionRow("Loading...", SR_LoadMoreColor, null, null, "latest_status"));
                else if (LatestMaudica.Error)
                    rows.Add(ViewRow.ActionRow("Couldn't reach Maudica", SR_LoadMoreColor, OnRetryLatest,
                             "Shoot to retry", "latest_status"));
                return;
            }

            var seenLocal = new HashSet<string>();
            foreach (var s in LatestMaudica.results)
            {
                string local = LatestMaudica.LocalSongIdFor(s);
                if (local != null)
                {
                    // Two maudica entries can resolve to the same local song; list it once.
                    if (seenLocal.Add(local))
                        rows.Add(ViewRow.SongRow(local));
                    continue;
                }

                var row = ViewRow.DownloadableSongRow(s.song_id, s.title, s.artist, s.author, s.download_url,
                          SR_DownloadableColor, () => OnDownloadLatestSong(s));

                // Keep download state on screen across redraws (another row loading, a page landing).
                if (latestDownloading.Contains(s.song_id))
                {
                    int pct = latestLastPct.TryGetValue(s.song_id, out int p) && p >= 0 ? p : 0;
                    row.subLabel = $"Downloading... {pct}%";
                }
                else if (latestFailed.Contains(s.song_id))
                {
                    row.subLabel = "Download failed - shoot to retry";
                }

                rows.Add(row);
            }

            if (LatestMaudica.Loading && !LatestMaudica.Refreshing)
                rows.Add(ViewRow.ActionRow("Loading...", SR_LoadMoreColor, null, null, "latest_loadmore"));
            else if (LatestMaudica.Error && LatestMaudica.ErrorWasRefresh)
                rows.Add(ViewRow.ActionRow("Refresh", SR_LoadMoreColor, OnRetryLatest,
                         "Couldn't refresh - shoot to retry", "latest_loadmore"));
            else if (LatestMaudica.Error)
                rows.Add(ViewRow.ActionRow("Load more songs", SR_LoadMoreColor, OnRetryLatest,
                         "Couldn't load - shoot to retry", "latest_loadmore"));
            else if (LatestMaudica.HasMorePages)
                rows.Add(ViewRow.ActionRow("Load more songs", SR_LoadMoreColor, OnLoadMoreLatest,
                         $"{LatestMaudica.results.Count} of {LatestMaudica.TotalOnMaudica} loaded", "latest_loadmore"));
        }

        private static void OnLoadMoreLatest()
        {
            if (LatestMaudica.Loading) return;
            LatestMaudica.LoadMore();
            RefreshList(); // show "Loading..." now; the fetch callback refreshes again
        }

        private static void OnRetryLatest()
        {
            if (LatestMaudica.Loading) return;
            LatestMaudica.Retry();
            RefreshList();
        }

        private static void OnDownloadLatestSong(Song s)
        {
            if (s == null) return;
            string id = s.song_id;
            if (string.IsNullOrEmpty(id)) return;
            if (!latestDownloading.Add(id)) return; // already downloading

            latestFailed.Remove(id);
            latestLastPct[id] = -1;
            VirtualSongList.UpdateActionRowText(id, s.title, "Downloading... 0%");
            MelonCoroutines.Start(SongDownloader.DownloadSong(
                id, s.download_url,
                (sid, ok) => OnLatestDownloadComplete(s, ok),
                (sid, pct) => OnLatestDownloadProgress(s, pct)));
        }

        private static void OnLatestDownloadProgress(Song s, float pct)
        {
            string id = s.song_id;
            int p = (int)(pct * 100f);
            if (p < 0) p = 0;
            if (p > 100) p = 100;
            if (latestLastPct.TryGetValue(id, out int last) && last == p) return;
            latestLastPct[id] = p;
            VirtualSongList.UpdateActionRowText(id, s.title, $"Downloading... {p}%");
        }

        private static void OnLatestDownloadComplete(Song s, bool success)
        {
            string id = s.song_id;
            latestDownloading.Remove(id);
            latestLastPct.Remove(id);

            if (!success)
            {
                latestFailed.Add(id);
                VirtualSongList.UpdateActionRowText(id, s.title, "Download failed - shoot to retry");
                return;
            }

            ExScoring.ReloadSongList(false);
            MelonCoroutines.Start(RefreshLatestAfterReload());
        }

        private static IEnumerator RefreshLatestAfterReload()
        {
            // Let ReloadSongList's async post-process settle, then re-match locals so the new
            // song turns into a normal (playable) row in place.
            yield return null;
            yield return null;
            yield return null;
            LatestMaudica.InvalidateLocalMatches();
            RefreshList();
        }
    }
}