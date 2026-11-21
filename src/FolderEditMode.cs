using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using MelonLoader;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// Edit Custom Folders (Options list → "Edit Custom Folders"): organizes the custom songs into
    /// pack subfolders of the songs folder, on disk.
    ///
    /// The song list shows Done, New Folder, "Unsorted" (songs loose in the songs folder) and every
    /// pack folder, empty ones included. The built-in folders (Favorites, Audica, DLC, Song
    /// Requests, Latest Maudica, Community Maps, search) aren't shown, and official songs can't be
    /// moved. An open pack folder starts with Rename Folder and Delete Folder rows.
    ///
    /// The launch panel is replaced by a panel on the shared OptionsMenuClone (same hide-then-show
    /// flow as PerSongOptionsPage). Selecting a song shows Move to Folder, Delete Song (two
    /// shots), and Undo Move when it has a pending move. Move to Folder switches the list to a
    /// folder picker (destinations, plus New Folder to create one and move there); after a move
    /// the next song of the folder is selected, to sort a folder quickly.
    ///
    /// What happens on disk, and when:
    ///  - New Folder: created right away (an empty folder can't be in use).
    ///  - Rename / delete of a folder that's empty on disk and not used by any pending change:
    ///    right away too.
    ///  - Everything else (song moves, other renames and deletes) goes to FolderOpsJournal and is
    ///    applied when the game closes, or on the next launch before the songs are scanned. Until
    ///    then every view shows the folders as they'll be (SongFolderManager overlays the journal).
    ///  - Delete Song: the existing DeleteSong (DeleteButton.cs), right away.
    /// Deleting a folder sends its songs to Unsorted; its other files (folder.json, images) go to
    /// SongBackups/Deleted Folders. File names never change, so song IDs and scores are kept.
    /// </summary>
    internal static class FolderEditMode
    {
        private const string LogPrefix = "[FolderEdit]";
        private const int MaxNameLength = 64;

        private const string NewFolderActionId = "fe_newfolder";
        private const string RenameActionId = "fe_rename";

        private static Color TitleColor => PlaylistNav.PlaylistTitleColor;
        private static Color BackColor => PlaylistNav.BackColor;
        private static Color CreateColor => PlaylistNav.CreateColor;
        private static Color DeleteColor => PlaylistNav.DeleteColor;
        private static Color ConfirmDeleteColor => PlaylistNav.ConfirmDeleteColor;
        private static Color RenameColor => PlaylistNav.PlaylistRowColor;

        private static readonly string[] ReservedNames =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        // Folder names the song list itself uses: a pack folder with one of these names would be
        // hidden by (or mistaken for) the built-in folder.
        private static readonly string[] BuiltInNames =
        {
            SongFolderManager.FolderFavorites, SongFolderManager.FolderAudica, SongFolderManager.FolderAudicaDLC,
            SongFolderManager.FolderCustom, SongFolderManager.FolderSongRequests, SongFolderManager.FolderSongRequestHistory,
            SongFolderManager.FolderLatestMaudica, SongFolderManager.FolderCommunityMaps,
            "Suggested Songs", "Options", "Playlists",
        };

        /// <summary>True while the song list shows the folder editor (or its Move-to picker).</summary>
        public static bool Active => FolderRowManager.InFolderEdit;

        // ── Selection / transient state ───────────────────────────────────────

        private static string selectedSongID;
        private static string selectedKey;        // journal key of the folder the selected row is in

        private static string armedDeleteSong;
        private static string armedDeleteFolder;
        private static bool armedDiscard;

        private enum TypingTarget { None, NewFolder, NewFolderForMove, Rename }
        private static TypingTarget typingTarget = TypingTarget.None;
        private static string typed = "";
        private static string renameKey;

        public static bool Typing => typingTarget != TypingTarget.None;

        // Move-to picker
        private static string moveSongID;
        private static string moveFile;
        private static string moveTitle;
        private static string moveFromKey;
        private static List<string> moveDiskCopies;
        private static float pickerReturnScroll;

        // Folders created this game session (on-disk names). Discard deletes the ones still empty.
        private static readonly List<string> createdThisSession = new List<string>();

        // ── Panel (OptionsMenuClone in place of the launch page) ─────────────

        private enum PageState { Closed, Opening, Open }
        private static PageState state = PageState.Closed;
        private static int openSeq;
        private static GameObject launchPage;
        private static float hideAnimDelay = 0.25f; // fallback if the hide animation length can't be read

        // ══════════════════════════════════════════════════════════════════════
        //  Enter / leave
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>"Edit Custom Folders" row in the Options list.</summary>
        public static void Begin()
        {
            if (Active) return;
            if (SongList.I == null || string.IsNullOrEmpty(ExScoring.mainSongDirectory)) return;

            DebugLog.Log($"{LogPrefix} Entering Edit Custom Folders ({FolderOpsJournal.Count} pending change(s))");

            bool handedOff = GlobalOptions.HandOffPanel(out GameObject handedLaunchPage);

            ClearTransient();
            ClearMove();
            selectedSongID = null;
            selectedKey = null;

            RebuildFolders();
            FolderRowManager.EnterFolderEdit();
            OpenPanel(handedOff, handedLaunchPage);
        }

        /// <summary>Done row / panel button: back to the song list. Pending changes are kept.</summary>
        public static void Done()
        {
            ClearTransient();
            ClearMove();

            if (Active)
            {
                RebuildFolders();
                FolderRowManager.ExitFolderEdit();
            }
            ClosePanel();

            int pending = FolderOpsJournal.Count;
            if (pending > 0)
                PlaylistUtil.Popup($"{pending} folder change{(pending != 1 ? "s" : "")} will be applied when the game closes");
            DebugLog.Log($"{LogPrefix} Left Edit Custom Folders ({pending} pending change(s))");
        }

        /// <summary>The song page is being torn down: drop the editor without restoring the launch panel.</summary>
        public static void ForceTeardown()
        {
            if (state == PageState.Closed && !Active) return;

            ClearTransient();
            ClearMove();

            bool wasOpen = state == PageState.Open;
            state = PageState.Closed;
            openSeq++;
            launchPage = null;
            if (wasOpen)
            {
                OptionsMenuClone.Wipe();
                OptionsMenuClone.Hide();
            }

            if (Active)
            {
                FolderRowManager.ResetNav();
                RebuildFolders();
            }
            DebugLog.Log($"{LogPrefix} Torn down with the song page ({FolderOpsJournal.Count} pending change(s) kept)");
        }

        /// <summary>Cancels name entry and every pending two-shot confirm. On any view change.</summary>
        public static void ClearTransient()
        {
            CancelTyping();
            bool redraw = armedDeleteSong != null || armedDiscard;
            armedDeleteSong = null;
            armedDeleteFolder = null;
            armedDiscard = false;
            if (redraw) RedrawNextFrame();
        }

        private static void ClearMove()
        {
            moveSongID = null;
            moveFile = null;
            moveTitle = null;
            moveFromKey = null;
            moveDiskCopies = null;
        }

        private static void RebuildFolders()
        {
            try
            {
                SongFolderManager.Rebuild(ExScoring.mainSongDirectory);
                if (SongFolderManager.searchFolderName != null) SongSearch.Search();
            }
            catch (Exception e)
            {
                DebugLog.Log($"{LogPrefix} Rebuilding the folders failed: {e}");
            }
        }

        private static void RefreshList()
        {
            FolderRowManager.RefreshListInPlace();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Model: the folders and songs as they'll be once the changes are applied
        // ══════════════════════════════════════════════════════════════════════

        private sealed class Entry
        {
            public string SongID;
            public string FileName;
            public string DiskKey;   // folder this copy is in on disk right now
            public string Title;
            public string Artist;
            public string Mapper;
            public bool Moved;       // a pending move changes where it ends up
        }

        private sealed class Model
        {
            public readonly List<string> Folders = new List<string>(); // pack folder keys, sorted
            public readonly Dictionary<string, List<Entry>> Songs =
                new Dictionary<string, List<Entry>>(StringComparer.OrdinalIgnoreCase);

            private static readonly List<Entry> None = new List<Entry>();

            public List<Entry> In(string key)
                => Songs.TryGetValue(key ?? FolderOpsJournal.Root, out List<Entry> l) ? l : None;

            public bool HasFile(string key, string fileName)
                => In(key).Exists(e => string.Equals(e.FileName, fileName, StringComparison.OrdinalIgnoreCase));

            public Entry Find(string songID, string key)
                => In(key).Find(e => e.SongID == songID);

            /// <summary>The first folder (Unsorted, then packs in order) holding songID.</summary>
            public string FolderOf(string songID)
            {
                if (In(FolderOpsJournal.Root).Exists(e => e.SongID == songID)) return FolderOpsJournal.Root;
                foreach (string k in Folders)
                    if (In(k).Exists(e => e.SongID == songID)) return k;
                return null;
            }
        }

        private static Model BuildModel()
        {
            var m = new Model();
            string root = ExScoring.mainSongDirectory;

            var disk = new List<string>();
            try
            {
                if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
                    foreach (string d in Directory.GetDirectories(root, "*", SearchOption.TopDirectoryOnly))
                        disk.Add(Path.GetFileName(d));
            }
            catch (Exception e)
            {
                DebugLog.Log($"{LogPrefix} Couldn't list the song folders: {e.Message}");
            }

            m.Songs[FolderOpsJournal.Root] = new List<Entry>();
            foreach (string k in FolderOpsJournal.VirtualFolderKeys(disk))
            {
                if (m.Songs.ContainsKey(k)) continue;
                m.Folders.Add(k);
                m.Songs[k] = new List<Entry>();
            }

            if (SongList.I != null && SongList.I.songs != null)
            {
                for (int i = 0; i < SongList.I.songs.Count; i++)
                {
                    SongList.SongData sd = SongList.I.songs[i];
                    if (sd == null || sd.hidden || string.IsNullOrEmpty(sd.songID)) continue;
                    if (SongFolderManager.IsOfficial(sd.songID) || !SongSearch.IsCustomSong(sd.songID)) continue;

                    string file = Path.GetFileName(sd.zipPath);
                    if (string.IsNullOrEmpty(file)) continue;

                    List<string> copies = SongFolderManager.DiskFoldersOf(file);
                    if (copies.Count == 0) copies.Add(SongFolderManager.FolderCustom); // still in Downloads: counts as loose

                    foreach (string folder in copies)
                    {
                        string diskKey = FolderOpsJournal.ToKey(folder);
                        string loc = FolderOpsJournal.VirtualLocation(file, diskKey);

                        if (!m.Songs.TryGetValue(loc, out List<Entry> list))
                        {
                            list = new List<Entry>();
                            m.Songs[loc] = list;
                            m.Folders.Add(loc);
                        }
                        if (list.Exists(e => e.SongID == sd.songID)) continue;

                        list.Add(new Entry
                        {
                            SongID = sd.songID,
                            FileName = file,
                            DiskKey = diskKey,
                            Title = string.IsNullOrEmpty(sd.title) ? file : sd.title,
                            Artist = sd.artist,
                            Mapper = sd.author,
                            Moved = FolderOpsJournal.IsMoved(file, diskKey),
                        });
                    }
                }
            }

            m.Folders.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in m.Songs)
                kv.Value.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
            return m;
        }

        /// <summary>Disk folders (keys) holding a copy of fileName.</summary>
        private static List<string> DiskKeysOf(string fileName)
        {
            var keys = new List<string>();
            foreach (string folder in SongFolderManager.DiskFoldersOf(fileName))
                keys.Add(FolderOpsJournal.ToKey(folder));
            if (keys.Count == 0) keys.Add(FolderOpsJournal.Root);
            return keys;
        }

        /// <summary>Songs (IDs) of a folder as shown in the editor, in display order. Used by ToggleFolder.</summary>
        public static List<string> SongsIn(string folderName)
        {
            var ids = new List<string>();
            foreach (Entry e in BuildModel().In(FolderOpsJournal.ToKey(folderName)))
                ids.Add(e.SongID);
            return ids;
        }

        /// <summary>The selected song's entry, with selectedKey corrected if it moved (or null: nothing selected).</summary>
        private static Entry ResolveSelection(Model m)
        {
            if (string.IsNullOrEmpty(selectedSongID)) return null;
            Entry e = selectedKey != null ? m.Find(selectedSongID, selectedKey) : null;
            if (e != null) return e;

            string key = m.FolderOf(selectedSongID);
            if (key == null) return null;
            selectedKey = key;
            return m.Find(selectedSongID, key);
        }

        /// <summary>The song after songID in its folder (else the one before), or null.</summary>
        private static string NeighborInFolder(Model m, string songID, string key)
        {
            List<Entry> list = m.In(key);
            int idx = list.FindIndex(e => e.SongID == songID);
            if (idx < 0) return null;
            if (idx + 1 < list.Count) return list[idx + 1].SongID;
            if (idx - 1 >= 0) return list[idx - 1].SongID;
            return null;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Song list views
        // ══════════════════════════════════════════════════════════════════════

        private static string Plural(int n, string word) => $"{n} {word}{(n != 1 ? "s" : "")}";

        private static string PendingSubLabel()
        {
            int n = FolderOpsJournal.Count;
            return n == 0
                ? "No pending changes"
                : $"{Plural(n, "pending change")} · applied when the game closes";
        }

        private static Color FolderColor(string key)
        {
            if (string.IsNullOrEmpty(key)) return ColorSettings.FolderUnsorted.Value;
            FolderStyle style = FolderCustomization.Get(key);
            return style != null && style.color.HasValue ? style.color.Value : ViewRow.FolderColor;
        }

        private static string FolderSubLabel(string key, int count)
        {
            var parts = new List<string> { Plural(count, "song") };
            if (string.IsNullOrEmpty(key))
            {
                parts.Add("loose in the songs folder");
            }
            else
            {
                string disk = FolderOpsJournal.DiskNameFor(key);
                if (!string.Equals(disk, key, StringComparison.Ordinal)) parts.Add($"renamed from {disk}");
                if (createdThisSession.Exists(c => FolderOpsJournal.SameFolder(c, disk))) parts.Add("new");
                string shown = FolderCustomization.DisplayName(key);
                if (!string.Equals(shown, key, StringComparison.Ordinal)) parts.Add($"shown as \"{shown}\"");
            }
            return string.Join(" · ", parts.ToArray());
        }

        /// <summary>Level FolderEdit: Done, New Folder, Unsorted, then every pack folder.</summary>
        public static List<ViewRow> BuildEditView()
        {
            var rows = new List<ViewRow>();
            Model m = BuildModel();

            rows.Add(ViewRow.LabelRow("Edit Custom Folders", TitleColor, PendingSubLabel()));
            rows.Add(ViewRow.ActionRow("Done", BackColor, Done, "Back to the song list"));
            rows.Add(ViewRow.ActionRow(NewFolderLabel(TypingTarget.NewFolder), CreateColor, OnNewFolderShot,
                NewFolderSubLabel(TypingTarget.NewFolder), NewFolderActionId));

            string open = SongFolderManager.openFolder;
            AddFolderRows(rows, m, FolderOpsJournal.Root, open);
            foreach (string key in m.Folders)
                AddFolderRows(rows, m, key, open);

            return rows;
        }

        private static void AddFolderRows(List<ViewRow> rows, Model m, string key, string open)
        {
            string name = FolderOpsJournal.ToFolder(key);
            List<Entry> songs = m.In(key);
            rows.Add(ViewRow.Header(name, songs.Count, FolderSubLabel(key, songs.Count), FolderColor(key)));

            if (open == null || !string.Equals(open, name, StringComparison.Ordinal)) return;

            if (!string.IsNullOrEmpty(key))
            {
                string k = key;
                rows.Add(ViewRow.ActionRow(RenameLabel(k), RenameColor, () => OnRenameShot(k),
                    RenameSubLabel(k), RenameActionId));

                bool armed = armedDeleteFolder != null && FolderOpsJournal.SameFolder(armedDeleteFolder, k);
                rows.Add(ViewRow.ActionRow(
                    armed ? "Confirm Delete Folder?" : "Delete Folder",
                    armed ? ConfirmDeleteColor : DeleteColor,
                    () => OnDeleteFolderShot(k),
                    armed ? "Shoot again: its songs go to Unsorted" : null));
            }

            foreach (Entry e in songs)
                rows.Add(e.Moved ? ViewRow.SongRow(e.SongID, e.Title + "  (moved)") : ViewRow.SongRow(e.SongID));
        }

        /// <summary>Level FolderEditPicker: where to move the song.</summary>
        public static List<ViewRow> BuildPickerView()
        {
            var rows = new List<ViewRow>();
            Model m = BuildModel();

            rows.Add(ViewRow.LabelRow($"Move \"{moveTitle ?? "song"}\"", TitleColor,
                $"From {FolderOpsJournal.ToFolder(moveFromKey)}"));
            rows.Add(ViewRow.ActionRow("Cancel", BackColor, CancelPicker, "Back to the folders"));
            rows.Add(ViewRow.ActionRow(NewFolderLabel(TypingTarget.NewFolderForMove), CreateColor, OnPickerNewFolderShot,
                NewFolderSubLabel(TypingTarget.NewFolderForMove), NewFolderActionId));

            var destinations = new List<string> { FolderOpsJournal.Root };
            destinations.AddRange(m.Folders);

            foreach (string dest in destinations)
            {
                if (FolderOpsJournal.SameFolder(dest, moveFromKey)) continue;

                string name = FolderOpsJournal.ToFolder(dest);
                int count = m.In(dest).Count;
                if (moveFile != null && m.HasFile(dest, moveFile))
                {
                    rows.Add(ViewRow.DisabledRow(name, FolderColor(dest), "Already has a file with this name"));
                    continue;
                }

                string d = dest;
                rows.Add(ViewRow.ActionRow(name, FolderColor(dest), () => PickDestination(d), Plural(count, "song")));
            }

            return rows;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Song actions (panel buttons)
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>From the SongSelectItem.OnSelect postfix (Hooks.cs) while the editor is up.</summary>
        public static void OnSongSelected(SongList.SongData sd)
        {
            if (!Active || sd == null) return;

            // The native select runs the game's page transitions (SongPage -> LaunchPage -> SongPage),
            // which can switch the hidden launch page back on, frozen in its hidden pose.
            KeepLaunchPageHidden("song select");

            bool folderArmed = armedDeleteFolder != null;
            ClearTransient();
            if (folderArmed) RefreshList(); // put "Confirm Delete Folder?" back to "Delete Folder"
            selectedSongID = sd.songID;
            selectedKey = SongFolderManager.openFolder != null ? FolderOpsJournal.ToKey(SongFolderManager.openFolder) : null;
            RedrawNextFrame();
        }

        private static void OnMoveShot()
        {
            if (!Active || FolderRowManager.InFolderEditPicker) return;

            Model m = BuildModel();
            Entry e = ResolveSelection(m);
            if (e == null) return;

            ClearTransient();
            moveSongID = e.SongID;
            moveFile = e.FileName;
            moveTitle = e.Title;
            moveFromKey = selectedKey;
            moveDiskCopies = DiskKeysOf(e.FileName);
            pickerReturnScroll = VirtualSongList.GetScroll();

            FolderRowManager.ShowFolderEditView(true, 0f);
            RedrawNextFrame();
        }

        private static void CancelPicker()
        {
            ClearTransient();
            ClearMove();
            FolderRowManager.ShowFolderEditView(false, pickerReturnScroll);
            RedrawNextFrame();
        }

        private static void PickDestination(string destKey)
        {
            if (moveFile == null) return;

            Model before = BuildModel();
            string next = NeighborInFolder(before, moveSongID, moveFromKey);

            FolderOpsJournal.AddMove(moveFile, moveFromKey, destKey, moveDiskCopies);
            PlaylistUtil.Popup($"{moveTitle} moved to {FolderOpsJournal.ToFolder(destKey)}");

            ClearTransient();
            ClearMove();
            FolderRowManager.ShowFolderEditView(false, pickerReturnScroll);

            // Next song of the same folder, so a folder can be sorted one song after another.
            if (next != null)
            {
                VirtualSongList.SelectInView(next);
            }
            else
            {
                selectedSongID = null;
                selectedKey = null;
            }
            RedrawNextFrame();
        }

        private static void OnUndoMoveShot()
        {
            Model m = BuildModel();
            Entry e = ResolveSelection(m);
            if (e == null) return;

            ClearTransient();
            if (FolderOpsJournal.UndoMoves(e.FileName) > 0)
            {
                PlaylistUtil.Popup($"{e.Title} stays in {FolderOpsJournal.ToFolder(FolderOpsJournal.VirtualLocation(e.FileName, e.DiskKey))}");
                selectedKey = null; // found again where it now is
            }
            RefreshList();
            RedrawNextFrame();
        }

        private static void OnDeleteSongShot()
        {
            Model m = BuildModel();
            Entry e = ResolveSelection(m);
            if (e == null) return;

            if (armedDeleteSong != e.SongID)
            {
                CancelTyping();
                armedDeleteFolder = null;
                armedDiscard = false;
                armedDeleteSong = e.SongID;
                PlaylistUtil.Popup("Shoot again to confirm delete");
                RedrawNextFrame();
                return;
            }

            armedDeleteSong = null;
            string next = NeighborInFolder(m, e.SongID, selectedKey);
            // Next frame: this runs inside the panel button's own hit event, and the redraw wipes it.
            MelonCoroutines.Start(DeleteSongNextFrame(e.SongID, e.FileName, next));
        }

        private static IEnumerator DeleteSongNextFrame(string songID, string fileName, string next)
        {
            yield return null;
            if (!Active) yield break;

            SongList.SongData sd = SongList.I != null ? SongList.I.GetSong(songID) : null;
            if (sd == null || !SongSearch.IsCustomSong(songID)) yield break;

            DebugLog.Log($"{LogPrefix} Deleting song {songID} ({fileName})");
            if (!ExScoring.DeleteSong(sd, false))
            {
                RedrawNextFrame();
                yield break;
            }

            // Its pending moves have nothing left to move.
            FolderOpsJournal.UndoMoves(fileName);

            if (ExScoring.selectedSong == songID)
            {
                ExScoring.selectedSong = null;
                ExScoring.selectedSongData = null;
            }
            selectedSongID = null;
            selectedKey = null;

            RefreshList();
            if (next != null) VirtualSongList.SelectInView(next);
            RedrawNextFrame();
        }

        private static void OnDiscardShot()
        {
            if (FolderOpsJournal.Count == 0 && createdThisSession.Count == 0) return;

            if (!armedDiscard)
            {
                CancelTyping();
                armedDeleteSong = null;
                armedDeleteFolder = null;
                armedDiscard = true;
                PlaylistUtil.Popup("Shoot again to discard the pending changes");
                RedrawNextFrame();
                return;
            }

            armedDiscard = false;
            int n = FolderOpsJournal.Count;
            FolderOpsJournal.DiscardAll();

            // Folders made this session that are still empty go too.
            int removed = 0;
            foreach (string name in createdThisSession.ToArray())
            {
                string path = Path.Combine(ExScoring.mainSongDirectory, name);
                try
                {
                    if (!Directory.Exists(path)) { createdThisSession.Remove(name); continue; }
                    if (Directory.GetFileSystemEntries(path).Length > 0) continue;
                    Directory.Delete(path, false);
                    createdThisSession.Remove(name);
                    removed++;
                }
                catch (Exception ex)
                {
                    DebugLog.Log($"{LogPrefix} Couldn't remove new folder {name}: {ex.Message}");
                }
            }

            if (SongFolderManager.openFolder != null && !FolderExistsInEditor(SongFolderManager.openFolder))
                SongFolderManager.openFolder = null;
            selectedKey = null;

            PlaylistUtil.Popup($"Discarded {Plural(n, "change")}" + (removed > 0 ? $", removed {Plural(removed, "new folder")}" : ""));
            RebuildFolders();
            RefreshList();
            RedrawNextFrame();
        }

        private static bool FolderExistsInEditor(string folderName)
        {
            if (folderName == SongFolderManager.FolderCustom) return true;
            return BuildModel().Folders.Exists(k => FolderOpsJournal.SameFolder(k, folderName));
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Folder actions (song list rows)
        // ══════════════════════════════════════════════════════════════════════

        private static void OnNewFolderShot()
        {
            if (typingTarget == TypingTarget.NewFolder) { CancelTyping(); return; } // shot again: cancel
            StartTyping(TypingTarget.NewFolder, null, "");
        }

        private static void OnPickerNewFolderShot()
        {
            if (typingTarget == TypingTarget.NewFolderForMove) { CancelTyping(); return; }
            StartTyping(TypingTarget.NewFolderForMove, null, "");
        }

        private static void OnRenameShot(string key)
        {
            if (typingTarget == TypingTarget.Rename && FolderOpsJournal.SameFolder(renameKey, key)) { CancelTyping(); return; }
            StartTyping(TypingTarget.Rename, key, key);
        }

        private static void OnDeleteFolderShot(string key)
        {
            if (armedDeleteFolder == null || !FolderOpsJournal.SameFolder(armedDeleteFolder, key))
            {
                CancelTyping();
                armedDeleteSong = null;
                armedDiscard = false;
                armedDeleteFolder = key;
                PlaylistUtil.Popup("Shoot again to delete the folder (its songs go to Unsorted)");
                RefreshList();
                return;
            }

            armedDeleteFolder = null;
            DeleteFolder(key);
        }

        private static void DeleteFolder(string key)
        {
            Model m = BuildModel();
            List<Entry> songs = m.In(key);

            // Its songs go to Unsorted: a different file of the same name there would collide.
            foreach (Entry e in songs)
            {
                if (m.HasFile(FolderOpsJournal.Root, e.FileName))
                {
                    PlaylistUtil.Popup($"Can't delete {key}: {e.FileName} is also in Unsorted. Move or delete one first");
                    RefreshList();
                    return;
                }
            }

            string path = Path.Combine(ExScoring.mainSongDirectory, key);
            if (CanChangeNow(key, path))
            {
                try
                {
                    Directory.Delete(path, false);
                    createdThisSession.RemoveAll(c => FolderOpsJournal.SameFolder(c, key));
                    DebugLog.Log($"{LogPrefix} Deleted empty folder {key}");
                    PlaylistUtil.Popup($"Deleted folder {key}");
                    AfterFolderGone(key, null);
                    return;
                }
                catch (Exception ex)
                {
                    DebugLog.Log($"{LogPrefix} Couldn't delete {key} now ({ex.Message}): queued");
                }
            }

            FolderOpsJournal.AddDelete(key);
            PlaylistUtil.Popup(songs.Count > 0
                ? $"{key} will be deleted when the game closes, {Plural(songs.Count, "song")} go to Unsorted"
                : $"{key} will be deleted when the game closes");
            AfterFolderGone(key, FolderOpsJournal.Root);
        }

        /// <summary>After a folder was renamed (newKey) or deleted (newKey Root/null): fix the open folder and selection.</summary>
        private static void AfterFolderGone(string oldKey, string newKey)
        {
            if (SongFolderManager.openFolder != null && FolderOpsJournal.SameFolder(FolderOpsJournal.ToKey(SongFolderManager.openFolder), oldKey))
                SongFolderManager.openFolder = newKey != null && newKey != FolderOpsJournal.Root ? newKey : null;
            if (selectedKey != null && FolderOpsJournal.SameFolder(selectedKey, oldKey))
                selectedKey = newKey;

            RebuildFolders();
            RefreshList();
            RedrawNextFrame();
        }

        /// <summary>
        /// A folder can be renamed/deleted on disk right away when it's empty and no pending
        /// change refers to it (it's not the result of a pending rename, nothing is moving in or out).
        /// </summary>
        private static bool CanChangeNow(string key, string path)
        {
            if (!string.Equals(FolderOpsJournal.DiskNameFor(key), key, StringComparison.Ordinal)) return false;
            if (FolderOpsJournal.References(key)) return false;
            try
            {
                return Directory.Exists(path) && Directory.GetFileSystemEntries(path).Length == 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool CreateFolder(string name)
        {
            try
            {
                Directory.CreateDirectory(Path.Combine(ExScoring.mainSongDirectory, name));
                if (!createdThisSession.Exists(c => FolderOpsJournal.SameFolder(c, name))) createdThisSession.Add(name);
                DebugLog.Log($"{LogPrefix} Created folder {name}");
                PlaylistUtil.Popup($"Created folder {name}");
                return true;
            }
            catch (Exception e)
            {
                DebugLog.Log($"{LogPrefix} Couldn't create folder {name}: {e.Message}");
                PlaylistUtil.Popup($"Couldn't create {name}");
                return false;
            }
        }

        private static void RenameFolder(string key, string newName)
        {
            string root = ExScoring.mainSongDirectory;
            string path = Path.Combine(root, key);

            if (CanChangeNow(key, path))
            {
                try
                {
                    string target = Path.Combine(root, newName);
                    if (string.Equals(key, newName, StringComparison.OrdinalIgnoreCase))
                    {
                        string tmp = path + FolderOpsJournal.RenameTempSuffix;
                        Directory.Move(path, tmp);
                        Directory.Move(tmp, target);
                    }
                    else
                    {
                        Directory.Move(path, target);
                    }

                    int idx = createdThisSession.FindIndex(c => FolderOpsJournal.SameFolder(c, key));
                    if (idx >= 0) createdThisSession[idx] = newName;
                    DebugLog.Log($"{LogPrefix} Renamed empty folder {key} to {newName}");
                    PlaylistUtil.Popup($"Renamed {key} to {newName}");
                    AfterFolderGone(key, newName);
                    return;
                }
                catch (Exception e)
                {
                    DebugLog.Log($"{LogPrefix} Couldn't rename {key} now ({e.Message}): queued");
                    if (!Directory.Exists(path) && Directory.Exists(path + FolderOpsJournal.RenameTempSuffix))
                    {
                        // Case-only rename cut off half way: put it back before queueing.
                        try { Directory.Move(path + FolderOpsJournal.RenameTempSuffix, path); } catch { }
                    }
                }
            }

            FolderOpsJournal.AddRename(key, newName);
            PlaylistUtil.Popup($"{key} will be renamed to {newName} when the game closes");
            AfterFolderGone(key, newName);
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Name entry (keyboard, routed from Hooks.cs)
        // ══════════════════════════════════════════════════════════════════════

        private static string NewFolderLabel(TypingTarget target)
            => typingTarget == target ? "New Folder: " + TypedDisplay() : "New Folder";

        private static string NewFolderSubLabel(TypingTarget target)
            => typingTarget == target ? TypingHint : "Created right away";

        private static string RenameLabel(string key)
            => typingTarget == TypingTarget.Rename && FolderOpsJournal.SameFolder(renameKey, key)
                ? "Rename: " + TypedDisplay()
                : "Rename Folder";

        private static string RenameSubLabel(string key)
            => typingTarget == TypingTarget.Rename && FolderOpsJournal.SameFolder(renameKey, key)
                ? TypingHint
                : null;

        private static string TypedDisplay() => string.IsNullOrEmpty(typed) ? "_" : typed;

        private const string TypingHint = "Done to confirm \u00B7 Clear then Done to cancel";

        private static void StartTyping(TypingTarget target, string key, string initial)
        {
            bool folderArmed = armedDeleteFolder != null;
            ClearTransient();
            typingTarget = target;
            renameKey = key;
            typed = initial ?? "";
            SearchKeyboard.ShowGeneric();
            if (folderArmed) RefreshList(); // rebuilds every row, the name row included
            else UpdateTypingRow();
        }

        private static void UpdateTypingRow()
        {
            switch (typingTarget)
            {
                case TypingTarget.NewFolder:
                case TypingTarget.NewFolderForMove:
                    VirtualSongList.UpdateActionRowText(NewFolderActionId, NewFolderLabel(typingTarget), NewFolderSubLabel(typingTarget));
                    break;
                case TypingTarget.Rename:
                    VirtualSongList.UpdateActionRowText(RenameActionId, RenameLabel(renameKey), RenameSubLabel(renameKey));
                    break;
            }
        }

        public static void AppendTyped(string s)
        {
            if (!Typing || string.IsNullOrEmpty(s)) return;
            if (typed.Length + s.Length > MaxNameLength) return;
            typed += s;
            UpdateTypingRow();
        }

        public static void BackspaceTyped()
        {
            if (!Typing || typed.Length == 0) return;
            typed = typed.Substring(0, typed.Length - 1);
            UpdateTypingRow();
        }

        public static void ClearTyped()
        {
            if (!Typing) return;
            typed = "";
            UpdateTypingRow();
        }

        /// <summary>Stops name entry, hides the keyboard and puts the row's label back.</summary>
        public static void CancelTyping()
        {
            if (!Typing) return;
            TypingTarget target = typingTarget;
            string key = renameKey;
            typingTarget = TypingTarget.None;
            renameKey = null;
            typed = "";
            SearchKeyboard.Hide();

            if (target == TypingTarget.Rename)
                VirtualSongList.UpdateActionRowText(RenameActionId, RenameLabel(key), RenameSubLabel(key));
            else
                VirtualSongList.UpdateActionRowText(NewFolderActionId, NewFolderLabel(target), NewFolderSubLabel(target));
        }

        /// <summary>Keyboard Done: checks the name (the keyboard stays up on a bad one), then creates / renames.</summary>
        public static void FinishTyping()
        {
            if (!Typing) return;

            string name = (typed ?? "").Trim();
            TypingTarget target = typingTarget;
            string key = renameKey;

            // Done with no name (or an unchanged one) cancels, so the keyboard can always be closed.
            if (name.Length == 0 || (target == TypingTarget.Rename && string.Equals(name, key, StringComparison.Ordinal)))
            {
                CancelTyping();
                return;
            }

            string error = CheckFolderName(name, target == TypingTarget.Rename ? key : null);
            if (error != null)
            {
                PlaylistUtil.Popup(error);
                return;
            }

            CancelTyping();

            switch (target)
            {
                case TypingTarget.NewFolder:
                    if (CreateFolder(name)) RefreshList();
                    break;
                case TypingTarget.NewFolderForMove:
                    if (CreateFolder(name)) PickDestination(name);
                    else RefreshList();
                    break;
                case TypingTarget.Rename:
                    RenameFolder(key, name);
                    break;
            }
        }

        /// <summary>
        /// Null if name is a usable new folder name, else the reason it isn't. renamingKey: the
        /// folder being renamed (a change of letter case only is allowed).
        /// </summary>
        private static string CheckFolderName(string name, string renamingKey)
        {
            if (name.Length == 0) return "The folder name can't be empty";
            if (name.Length > MaxNameLength) return $"The folder name can't be longer than {MaxNameLength} characters";
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "A folder name can't contain \\ / : * ? \" < > |";
            if (name.EndsWith(".")) return "A folder name can't end with a dot";
            if (name.EndsWith(FolderOpsJournal.RenameTempSuffix, StringComparison.OrdinalIgnoreCase)) return "That name is reserved";

            string stem = name.Split('.')[0].Trim().ToUpperInvariant();
            foreach (string reserved in ReservedNames)
                if (stem == reserved) return $"Windows doesn't allow a folder named {name}";

            foreach (string builtIn in BuiltInNames)
                if (string.Equals(name, builtIn, StringComparison.OrdinalIgnoreCase))
                    return $"{builtIn} is already used by the song list";
            if (name.StartsWith("Search Results", StringComparison.OrdinalIgnoreCase))
                return "That name is used by the song list's search";

            bool caseOnly = renamingKey != null && string.Equals(name, renamingKey, StringComparison.OrdinalIgnoreCase);
            if (caseOnly) return null;

            if (BuildModel().Folders.Exists(k => FolderOpsJournal.SameFolder(k, name)))
                return $"There's already a folder named {name}";

            // Still on disk under that name (e.g. waiting for its own pending rename or delete).
            try
            {
                string path = Path.Combine(ExScoring.mainSongDirectory, name);
                if (Directory.Exists(path) || File.Exists(path))
                    return $"{name} still exists in the songs folder until the pending changes are applied";
            }
            catch
            {
                return "That name can't be used";
            }
            return null;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Panel
        // ══════════════════════════════════════════════════════════════════════

        private static GameObject FindLaunchPage()
        {
            // Through "menu" so it's found while inactive (GameObject.Find only sees active objects).
            Transform t = GameObject.Find("menu")?.transform.Find("ShellPage_Launch");
            return t != null ? t.gameObject : null;
        }

        /// <summary>
        /// Switches the launch page off again if something turned it on while the panel stands in
        /// for it. Called on song select and every frame (Update).
        /// </summary>
        private static void KeepLaunchPageHidden(string reason)
        {
            if (state != PageState.Open || !Active) return;

            if (launchPage == null) launchPage = FindLaunchPage();
            if (launchPage == null || !launchPage.activeSelf) return;

            launchPage.SetActive(false);
            ShellPage sp = launchPage.GetComponent<ShellPage>();
            if (sp != null)
            {
                sp.mActive = false;
                sp.mInteractable = false;
            }
            DebugLog.Log($"{LogPrefix} Launch page was switched on ({reason}): hidden again");
        }

        /// <summary>Per frame (Main.OnUpdate).</summary>
        public static void Update()
        {
            if (state == PageState.Open) KeepLaunchPageHidden("frame check");
        }

        private static void OpenPanel(bool handedOff, GameObject handedLaunchPage)
        {
            if (!OptionsMenuClone.EnsureClone())
            {
                DebugLog.Log($"{LogPrefix} Options clone not available: no panel");
                return;
            }

            int seq = ++openSeq;
            if (handedOff)
            {
                // Options' category panel was up: the launch page is already hidden, keep it so.
                launchPage = handedLaunchPage != null ? handedLaunchPage : FindLaunchPage();
                if (launchPage != null && launchPage.activeSelf) launchPage.SetActive(false);
                state = PageState.Open;
                Draw();
                return;
            }

            launchPage = FindLaunchPage();
            state = PageState.Opening;
            ExScoring.HideLaunchPanel();
            MelonCoroutines.Start(ShowAfterHide(seq));
        }

        private static float LaunchHideAnimLength()
        {
            if (launchPage != null)
            {
                var sp = launchPage.GetComponent<ShellPage>();
                if (sp != null && sp.hideAnim != null) return sp.hideAnim.length;
            }
            return hideAnimDelay;
        }

        private static IEnumerator ShowAfterHide(int seq)
        {
            yield return new WaitForSeconds(LaunchHideAnimLength());
            if (seq != openSeq || state != PageState.Opening) yield break;

            if (launchPage != null) launchPage.SetActive(false);
            yield return null;
            if (seq != openSeq || state != PageState.Opening) yield break;

            state = PageState.Open;
            Draw();
        }

        /// <summary>Back to the launch panel (Done).</summary>
        private static void ClosePanel()
        {
            if (state == PageState.Closed) return;

            bool wasOpen = state == PageState.Open;
            state = PageState.Closed;
            openSeq++;

            if (wasOpen)
            {
                OptionsMenuClone.Wipe();
                OptionsMenuClone.Hide();
            }

            if (launchPage != null) launchPage.SetActive(true);

            bool prevSuppress = ExScoring.suppressShellPageAnimations;
            ExScoring.suppressShellPageAnimations = false;
            ExScoring.ShowLaunchPanel(); // also re-activates the launch page if our ref is stale
            ExScoring.suppressShellPageAnimations = prevSuppress;

            if (string.IsNullOrEmpty(ExScoring.selectedSong) || ExScoring.selectedSongData == null)
                ExScoring.SetLaunchPanelContentVisible(false);
            else
                ExScoring.UpdateLaunchPanelInfo();

            launchPage = null;
        }

        private static void Draw()
        {
            if (state != PageState.Open) return;
            OptionsMenuClone.RestoreDefaultPlacement();
            OptionsMenuClone.Show();

            Model m = BuildModel();
            Entry e = FolderRowManager.InFolderEditPicker ? null : ResolveSelection(m);
            string title = FolderRowManager.InFolderEditPicker ? "Move Song" : (e != null ? e.Title : "Edit Custom Folders");
            OptionsMenuClone.Draw(title, () => Build(m, e));
        }

        /// <summary>Redraw next frame — never wipe the rows from inside one of their own hit events.</summary>
        private static void RedrawNextFrame()
        {
            if (state != PageState.Open) return;
            MelonCoroutines.Start(RedrawRoutine(openSeq));
        }

        private static IEnumerator RedrawRoutine(int seq)
        {
            yield return null;
            if (seq != openSeq || state != PageState.Open) yield break;
            Draw();
        }

        private static void DoneNextFrame()
        {
            MelonCoroutines.Start(DoneRoutine(openSeq));
        }

        private static IEnumerator DoneRoutine(int seq)
        {
            yield return null;
            if (seq != openSeq) yield break;
            Done();
        }

        private static void Build(Model m, Entry e)
        {
            if (FolderRowManager.InFolderEditPicker)
            {
                BuildPickerPanel();
                return;
            }
            if (e == null)
            {
                BuildOverviewPanel();
                return;
            }
            BuildSongPanel(e);
        }

        private static void BuildOverviewPanel()
        {
            int pending = FolderOpsJournal.Count;
            OptionsMenuClone.AddRow(OptionsMenuClone.CreateHeader(0,
                pending == 0 ? "No pending changes" : Plural(pending, "pending change")));
            OptionsMenuClone.AddRow(OptionsMenuClone.CreateHeader(0, "Applied when the game closes,"));
            OptionsMenuClone.AddRow(OptionsMenuClone.CreateHeader(0, "or on the next launch"));
            OptionsMenuClone.AddRow(OptionsMenuClone.CreateHeader(0, "New folders are created right away"));
            OptionsMenuClone.AddRow(OptionsMenuClone.CreateHeader(0, "Shoot a song to move or delete it"));

            var doneButton = OptionsMenuClone.CreateButton(0, "Done", () => DoneNextFrame(),
                "Back to the song list. Pending changes are kept");
            if (pending > 0 || createdThisSession.Count > 0)
            {
                var discardButton = OptionsMenuClone.CreateButton(1,
                    armedDiscard ? "Confirm Discard?" : "Discard Changes",
                    () => OnDiscardShot(),
                    "Forgets every pending change, and removes the new folders that are still empty. Shoot twice");
                OptionsMenuClone.AddRow(doneButton, discardButton);
            }
            else
            {
                OptionsMenuClone.AddRow(doneButton);
            }
        }

        private static void BuildSongPanel(Entry e)
        {
            string info = string.IsNullOrEmpty(e.Mapper) ? (e.Artist ?? "") : $"{e.Artist} - mapped by {e.Mapper}";
            if (!string.IsNullOrEmpty(info))
                OptionsMenuClone.AddRow(OptionsMenuClone.CreateHeader(0, info));

            OptionsMenuClone.AddRow(OptionsMenuClone.CreateHeader(0, "Folder: " + FolderOpsJournal.ToFolder(selectedKey)));
            if (e.Moved)
                OptionsMenuClone.AddRow(OptionsMenuClone.CreateHeader(0, "Moving here from " + FolderOpsJournal.ToFolder(e.DiskKey)));

            var moveButton = OptionsMenuClone.CreateButton(0, "Move to Folder", () => OnMoveShot(),
                "Pick the folder to move this song to. Moved on disk when the game closes");
            bool armed = armedDeleteSong == e.SongID;
            var deleteButton = OptionsMenuClone.CreateButton(1, armed ? "Confirm Delete?" : "Delete Song",
                () => OnDeleteSongShot(),
                "Deletes the song file. Shoot twice");
            OptionsMenuClone.AddRow(moveButton, deleteButton);

            var doneButton = OptionsMenuClone.CreateButton(0, "Done", () => DoneNextFrame(),
                "Back to the song list. Pending changes are kept");
            if (e.Moved)
            {
                var undoButton = OptionsMenuClone.CreateButton(1, "Undo Move", () => OnUndoMoveShot(),
                    "Keeps the song where it is on disk");
                OptionsMenuClone.AddRow(doneButton, undoButton);
            }
            else
            {
                OptionsMenuClone.AddRow(doneButton);
            }

            int pending = FolderOpsJournal.Count;
            if (pending > 0)
                OptionsMenuClone.AddRow(OptionsMenuClone.CreateHeader(0, Plural(pending, "pending change") + ", applied when the game closes"));
        }

        private static void BuildPickerPanel()
        {
            OptionsMenuClone.AddRow(OptionsMenuClone.CreateHeader(0, $"Moving \"{moveTitle ?? "song"}\""));
            OptionsMenuClone.AddRow(OptionsMenuClone.CreateHeader(0, "from " + FolderOpsJournal.ToFolder(moveFromKey)));
            OptionsMenuClone.AddRow(OptionsMenuClone.CreateHeader(0, "Shoot the destination folder in the list"));
            OptionsMenuClone.AddRow(OptionsMenuClone.CreateButton(0, "Cancel", () => CancelPickerNextFrame(),
                "Back to the folders"));
        }

        private static void CancelPickerNextFrame()
        {
            MelonCoroutines.Start(CancelPickerRoutine(openSeq));
        }

        private static IEnumerator CancelPickerRoutine(int seq)
        {
            yield return null;
            if (seq != openSeq || !FolderRowManager.InFolderEditPicker) yield break;
            CancelPicker();
        }
    }
}