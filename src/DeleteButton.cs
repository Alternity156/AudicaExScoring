using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using MelonLoader;
using TMPro;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// The Delete button (song launch panel, and the pause / failed / end-game / practice-over
    /// pages in a song) and the song deletion itself.
    ///
    /// Double-shot protected, like Delete Playlist: the first shot arms the button ("Confirm?",
    /// confirm-delete red, "Shoot again to confirm delete" popup), the second shot on the same
    /// button for the same song deletes. Selecting another song, or the page being shown again,
    /// disarms it.
    ///
    /// A confirmed delete (DeleteSong):
    ///  - removes the song from SongList.I.songs right away, so it's gone from every folder,
    ///    search results and the random-song pool (playlists keep the entry and show it missing);
    ///  - removes its entry from the song cache (song_cache.json) and saves it;
    ///  - deletes the .audica file, plus any byte-identical copy with the same file name in the
    ///    songs folder, its pack subfolders or Downloads (a copy that differs is a different
    ///    version and is left alone). From the menu that happens a frame later; from a song it
    ///    waits until the song list is back, so the file isn't pulled out from under the song.
    ///    A file that can't be deleted is retried on quit, then on the next launch;
    ///  - blocks the lobby's auto-download of that song for the rest of the session
    ///    (deletedSongs), so a suggestion doesn't bring it straight back. Shooting its row in
    ///    the Suggested Songs folder still downloads it by hand.
    /// Every step is logged with a [DeleteSong] prefix.
    /// </summary>
    public partial class ExScoring
    {
        private const string DeleteLabel = "Delete";
        private const string ConfirmDeleteLabel = "Confirm?";
        private const string LogPrefix = "[DeleteSong]";

        private class DeleteButtonInstance
        {
            public ButtonUtils.ButtonLocation location;
            public GameObject obj;
            public GunButton gunButton;
            public TextMeshPro text;
            public Color normalColor;
        }

        // One button per location, reused every time that page is shown (a new copy used to be
        // stacked on the pause/failed/end pages each time they opened).
        private static readonly Dictionary<ButtonUtils.ButtonLocation, DeleteButtonInstance> deleteButtons =
            new Dictionary<ButtonUtils.ButtonLocation, DeleteButtonInstance>();

        // The button waiting for its confirm shot, and the song it was armed for.
        private static DeleteButtonInstance armedDeleteButton;
        private static string armedDeleteSongId;

        private static Vector3 delButtonMenuPosition = new Vector3(1.1f, 15.5f, 0f);
        private static Vector3 delButtonMenuScale = new Vector3(0.5f, 0.75f, 0.75f);
        private static Vector3 delButtonMenuRotation = new Vector3(0f, 0f, 0f);
        private static Vector3 delButtonLabelScale = new Vector3(2.25f, 1.5f, 1.5f);

        private static Vector3 delButtonInGameUIPosition = new Vector3(-5f, 13.5f, 0f);
        private static Vector3 delButtonInGameUIRotation = new Vector3(0f, 0f, 0f);

        // ── Button ──

        internal static void CreateDeleteButton(ButtonUtils.ButtonLocation location = ButtonUtils.ButtonLocation.Menu)
        {
            if (deleteButtons.TryGetValue(location, out DeleteButtonInstance existing) && IsAlive(existing))
            {
                existing.obj.SetActive(true);
                if (armedDeleteButton == existing) DisarmDelete();
                UpdateDeleteButtonEnabled(existing);
                return;
            }

            string name = "InGameUI/ShellPage_EndGameContinue/page/ShellPanel_Center/exit";
            Vector3 localPosition = delButtonInGameUIPosition;
            Vector3 rotation = delButtonInGameUIRotation;

            if (location == ButtonUtils.ButtonLocation.Failed)
            {
                name = "InGameUI/ShellPage_Failed/page/ShellPanel_Center/exit";
            }
            else if (location == ButtonUtils.ButtonLocation.Pause)
            {
                name = "InGameUI/ShellPage_Pause/page/ShellPanel_Center/exit";
            }
            else if (location == ButtonUtils.ButtonLocation.PracticeModeOver)
            {
                name = "InGameUI/ShellPage_PracticeModeOver/page/ShellPanel_Center/exit";
            }
            else if (location == ButtonUtils.ButtonLocation.Menu)
            {
                name = "menu/ShellPage_Launch/page/ShellPanel_Center/NoFailPracticeToggle/PracticeToggle";
                localPosition = delButtonMenuPosition;
                rotation = delButtonMenuRotation;
            }

            var refButton = GameObject.Find(name);
            if (refButton == null) return;

            DeleteButtonInstance instance = new DeleteButtonInstance { location = location };
            Action listener = new Action(() => { OnDeleteButtonShot(instance); });

            GameObject button = GameObject.Instantiate(refButton, refButton.transform.parent.transform);
            ButtonUtils.InitButton(button, DeleteLabel, listener, localPosition, rotation);
            if (location == ButtonUtils.ButtonLocation.Menu)
            {
                button.transform.localScale = delButtonMenuScale;
                TextMeshPro label = button.GetComponentInChildren<TextMeshPro>();
                if (label != null)
                    label.transform.localScale = delButtonLabelScale;
            }

            instance.obj = button;
            instance.gunButton = button.GetComponentInChildren<GunButton>();
            instance.text = button.GetComponentInChildren<TextMeshPro>();
            instance.normalColor = instance.text != null ? instance.text.color : Color.white;
            deleteButtons[location] = instance;

            UpdateDeleteButtonEnabled(instance);
        }

        private static bool IsAlive(DeleteButtonInstance instance)
        {
            return instance != null && instance.obj != null && instance.gunButton != null && instance.text != null;
        }

        /// <summary>The song a button would delete: the selected song in the menu, the playing one in a song.</summary>
        private static SongList.SongData GetDeleteTarget(ButtonUtils.ButtonLocation location)
        {
            if (location == ButtonUtils.ButtonLocation.Menu && selectedSongData != null)
                return selectedSongData;
            return SongDataHolder.I != null ? SongDataHolder.I.songData : null;
        }

        private static void OnDeleteButtonShot(DeleteButtonInstance instance)
        {
            SongList.SongData song = GetDeleteTarget(instance.location);
            if (song == null || !SongSearch.IsCustomSong(song.songID)) return;

            // First shot: arm.
            if (armedDeleteButton != instance || armedDeleteSongId != song.songID)
            {
                DisarmDelete();
                armedDeleteButton = instance;
                armedDeleteSongId = song.songID;
                if (IsAlive(instance))
                {
                    ButtonUtils.UpdateButtonLabel(instance.obj, ConfirmDeleteLabel);
                    instance.text.color = PlaylistNav.ConfirmDeleteColor;
                }
                PlaylistUtil.Popup("Shoot again to confirm delete");
                return;
            }

            // Second shot: delete.
            DisarmDelete();
            bool inSong = instance.location != ButtonUtils.ButtonLocation.Menu;
            string songId = song.songID;
            string neighbor = FindNeighborSong(songId);

            if (!DeleteSong(song, inSong)) return;

            // Whatever was next to it in the list becomes the selection (nothing, when unknown).
            selectedSong = neighbor;
            selectedSongData = neighbor != null ? SongList.I.GetSong(neighbor) : null;

            if (inSong)
            {
                InGameUI.I.ReturnToSongList();
                return;
            }

            var songSelectObj = GameObject.Find("menu/ShellPage_Song/page/ShellPanel_Center/SongSelect");
            if (songSelectObj != null)
            {
                var ss = songSelectObj.GetComponent<SongSelect>();
                if (ss != null) ss.ShowSongList();
            }
            AutoSelectSong();
        }

        private static void DisarmDelete()
        {
            DeleteButtonInstance armed = armedDeleteButton;
            armedDeleteButton = null;
            armedDeleteSongId = null;
            if (!IsAlive(armed)) return;
            ButtonUtils.UpdateButtonLabel(armed.obj, DeleteLabel);
            armed.text.color = armed.normalColor;
        }

        private static void UpdateDeleteButtonEnabled(DeleteButtonInstance instance)
        {
            if (!IsAlive(instance)) return;

            SongList.SongData song = GetDeleteTarget(instance.location);

            // Armed for another song than the one shown now: back to "Delete".
            if (armedDeleteButton == instance && (song == null || song.songID != armedDeleteSongId))
                DisarmDelete();

            bool enabled = song != null && SongSearch.IsCustomSong(song.songID);
            instance.gunButton.SetInteractable(enabled);
            instance.text.alpha = enabled ? 1.0f : 0.25f;
        }

        /// <summary>From the launch panel refresh (SongListUI): follows the selected song.</summary>
        internal static void UpdateDeleteButtonEnabled()
        {
            if (deleteButtons.TryGetValue(ButtonUtils.ButtonLocation.Menu, out DeleteButtonInstance menu))
                UpdateDeleteButtonEnabled(menu);
        }

        /// <summary>
        /// The song next to songId in the current song list view (the one after it, else the one
        /// before), or null when the view doesn't have it.
        /// </summary>
        private static string FindNeighborSong(string songId)
        {
            int index = VirtualSongList.IndexOf(songId);
            if (index < 0 || SongList.I == null) return null;

            string after = null, before = null;
            int afterIndex = int.MaxValue, beforeIndex = int.MinValue;
            for (int i = 0; i < SongList.I.songs.Count; i++)
            {
                SongList.SongData sd = SongList.I.songs[i];
                if (sd == null || sd.songID == songId) continue;
                int idx = VirtualSongList.IndexOf(sd.songID);
                if (idx < 0) continue;
                if (idx > index && idx < afterIndex) { afterIndex = idx; after = sd.songID; }
                else if (idx < index && idx > beforeIndex) { beforeIndex = idx; before = sd.songID; }
            }
            return after ?? before;
        }

        // ── Deletion ──

        /// <summary>True when the song was deleted this session: the lobby won't auto-download it.</summary>
        public static bool IsDeletedThisSession(string songId)
        {
            return !string.IsNullOrEmpty(songId) && deletedSongs.Contains(songId);
        }

        /// <summary>A song deleted this session was downloaded again by hand: lift the block.</summary>
        public static void OnSongRedownloaded(string songId)
        {
            if (!string.IsNullOrEmpty(songId) && deletedSongs.Remove(songId))
                DebugLog.Log($"{LogPrefix} {songId} was downloaded again: auto-download no longer blocked");
        }

        /// <summary>
        /// Deletes a custom song from the song list, the song cache and the disk (see the class
        /// summary). inSong: called from a song's pause/end pages, so the file is deleted once
        /// the song list is back instead of right away.
        /// </summary>
        public static bool DeleteSong(SongList.SongData song, bool inSong)
        {
            if (song == null || SongList.I == null) return false;

            string songId = song.songID;
            string title = song.title;
            string zipPath = song.zipPath;
            string fileName = string.IsNullOrEmpty(zipPath) ? null : Path.GetFileName(zipPath);

            DebugLog.Log($"{LogPrefix} Deleting {songId} \"{title}\" (zipPath={zipPath}, searchRoot={song.searchRoot}, from {(inSong ? "a song" : "the menu")})");

            List<string> files;
            try
            {
                string primary = ResolveSongFile(song);
                DebugLog.Log($"{LogPrefix} File: {primary ?? "not found on disk"}");
                files = FindFilesToDelete(primary, fileName);
            }
            catch (Exception ex)
            {
                DebugLog.Log($"{LogPrefix} Couldn't locate the file: {ex.Message}");
                files = new List<string>();
            }

            // 1. Song list.
            int removed = 0;
            for (int i = SongList.I.songs.Count - 1; i >= 0; i--)
            {
                SongList.SongData sd = SongList.I.songs[i];
                if (sd != null && sd.songID == songId)
                {
                    SongList.I.songs.RemoveAt(i);
                    removed++;
                }
            }
            DebugLog.Log($"{LogPrefix} Removed {removed} entr{(removed == 1 ? "y" : "ies")} from the song list");
            if (SongList.I.GetSong(songId) != null)
                DebugLog.Log($"{LogPrefix} [WARNING] SongList.GetSong still finds {songId} after removing it from SongList.songs");

            // 2. Auto-download block for this session.
            if (!deletedSongs.Contains(songId)) deletedSongs.Add(songId);

            // 3. Song cache.
            try
            {
                int cacheRemoved = SongCache.RemoveSong(songId, zipPath);
                SongCache.SaveIfDirty();
                DebugLog.Log($"{LogPrefix} Removed {cacheRemoved} song cache entr{(cacheRemoved == 1 ? "y" : "ies")}");
            }
            catch (Exception ex)
            {
                DebugLog.Log($"{LogPrefix} Couldn't update the song cache: {ex.Message}");
            }

            // 4. Song trackers (download state, filename lookups).
            try
            {
                SongDownloadTracker.songIDs.Remove(songId);
                if (fileName != null)
                {
                    bool fileNameStillUsed = false;
                    for (int i = 0; i < SongList.I.songs.Count; i++)
                    {
                        SongList.SongData sd = SongList.I.songs[i];
                        if (sd != null && Path.GetFileName(sd.zipPath) == fileName) { fileNameStillUsed = true; break; }
                    }
                    if (!fileNameStillUsed)
                    {
                        SongDownloadTracker.songFilenames.Remove(fileName);
                        SongDownloadTracker.songDictionary.Remove(fileName);
                    }
                    SongDownloader.downloadedFileNames.Remove(fileName);
                    SongDownloader.failedDownloads.Remove(fileName);
                }
            }
            catch (Exception ex)
            {
                DebugLog.Log($"{LogPrefix} Couldn't update the song trackers: {ex.Message}");
            }

            // 5. Files.
            if (files.Count > 0)
                MelonCoroutines.Start(DeleteFilesWhenSafe(files, inSong));

            // 6. Folders and search results (the views rebuild when the song list is shown).
            try
            {
                SongFolderManager.Rebuild(mainSongDirectory);
                if (SongFolderManager.searchFolderName != null) SongSearch.Search();
            }
            catch (Exception ex)
            {
                DebugLog.Log($"{LogPrefix} Couldn't rebuild the folders: {ex.Message}");
            }

            // 7. Online lobby.
            try
            {
                LobbySongList.OnSongDeleted(songId);
                MpGameplay.OnSongDeleted(songId);
            }
            catch (Exception ex)
            {
                DebugLog.Log($"{LogPrefix} Couldn't update the lobby state: {ex.Message}");
            }

            PlaylistUtil.Popup("Deleted " + title);
            return true;
        }

        /// <summary>The .audica file a song was loaded from, or null when it isn't on disk.</summary>
        private static string ResolveSongFile(SongList.SongData song)
        {
            string zipPath = song.zipPath;
            if (string.IsNullOrEmpty(zipPath)) return null;

            var candidates = new List<string>();
            if (Path.IsPathRooted(zipPath))
                candidates.Add(zipPath);
            else
            {
                if (!string.IsNullOrEmpty(song.searchRoot)) candidates.Add(Path.Combine(song.searchRoot, zipPath));
                candidates.Add(Path.Combine(Application.dataPath, zipPath));
            }

            string fileName = Path.GetFileName(zipPath);
            candidates.Add(Path.Combine(mainSongDirectory, fileName));
            candidates.Add(Path.Combine(downloadsDirectory, fileName));

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

        /// <summary>
        /// The song's file plus every byte-identical copy with the same name in the songs folder,
        /// its pack subfolders and Downloads (any of them would load the song again next launch).
        /// </summary>
        private static List<string> FindFilesToDelete(string primary, string fileName)
        {
            var result = new List<string>();
            if (primary == null) return result;
            result.Add(primary);
            if (string.IsNullOrEmpty(fileName)) return result;

            var dirs = new List<string> { mainSongDirectory, downloadsDirectory };
            try
            {
                if (Directory.Exists(mainSongDirectory))
                    dirs.AddRange(Directory.GetDirectories(mainSongDirectory, "*", SearchOption.TopDirectoryOnly));
            }
            catch (Exception ex)
            {
                DebugLog.Log($"{LogPrefix} Couldn't list the song subfolders: {ex.Message}");
            }

            foreach (string dir in dirs)
            {
                string candidate;
                try
                {
                    candidate = Path.GetFullPath(Path.Combine(dir, fileName));
                }
                catch { continue; }

                if (!File.Exists(candidate)) continue;
                if (result.Exists(p => string.Equals(p, candidate, StringComparison.OrdinalIgnoreCase))) continue;

                if (FilesIdentical(primary, candidate))
                {
                    result.Add(candidate);
                    DebugLog.Log($"{LogPrefix} Identical copy, deleting too: {candidate}");
                }
                else
                {
                    DebugLog.Log($"{LogPrefix} Same name but different contents (another version), keeping: {candidate}");
                }
            }
            return result;
        }

        private static bool FilesIdentical(string a, string b)
        {
            try
            {
                FileInfo fa = new FileInfo(a), fb = new FileInfo(b);
                if (fa.Length != fb.Length) return false;

                const int ChunkSize = 64 * 1024;
                byte[] bufA = new byte[ChunkSize], bufB = new byte[ChunkSize];
                using (FileStream sa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (FileStream sb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    while (true)
                    {
                        int readA = sa.Read(bufA, 0, ChunkSize);
                        int readB = sb.Read(bufB, 0, ChunkSize);
                        if (readA != readB) return false;
                        if (readA == 0) return true;
                        for (int i = 0; i < readA; i++)
                            if (bufA[i] != bufB[i]) return false;
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLog.Log($"{LogPrefix} Couldn't compare {a} and {b}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Deletes the files a frame later from the menu, or once the song list is back (plus a
        /// second) from a song. A file that can't be deleted now goes on the delete-on-quit list.
        /// </summary>
        private static IEnumerator DeleteFilesWhenSafe(List<string> files, bool inSong)
        {
            yield return null;

            if (inSong)
            {
                float deadline = Time.unscaledTime + 120f;
                while (menuState != MenuState.State.SongPage && Time.unscaledTime < deadline)
                    yield return null;
                float settle = Time.unscaledTime + 1f;
                while (Time.unscaledTime < settle)
                    yield return null;
            }

            foreach (string path in files)
            {
                try
                {
                    if (File.Exists(path)) File.Delete(path);
                    DebugLog.Log($"{LogPrefix} Deleted file {path}");
                }
                catch (Exception ex)
                {
                    if (!deletedSongPaths.Contains(path)) deletedSongPaths.Add(path);
                    DebugLog.Log($"{LogPrefix} Couldn't delete {path} now ({ex.Message}): will retry on quit");
                }
            }
        }
    }
}