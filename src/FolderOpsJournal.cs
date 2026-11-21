using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using UnityEngine;

namespace ExScoringMod
{
    public enum FolderOpKind { MoveSong, RenameFolder, DeleteFolder }

    /// <summary>
    /// One change recorded by Edit Custom Folders (FolderEditMode). Folder names are pack
    /// subfolders of the songs folder; "" is the songs folder itself (shown as "Unsorted").
    /// </summary>
    [Serializable]
    public class FolderOp
    {
        [JsonConverter(typeof(StringEnumConverter))]
        public FolderOpKind Kind;
        public string FileName; // MoveSong: the .audica file name
        public string From;     // MoveSong: folder it's in at this point; Rename/Delete: the folder
        public string To;       // MoveSong: destination folder; Rename: the new name
        public string Recorded; // local time it was recorded, for the log
        public int Attempts;    // failed apply attempts (file in use...): given up on after MaxAttempts
    }

    [Serializable]
    public class FolderOpsFile
    {
        public int Version = 1;
        public List<FolderOp> Ops = new List<FolderOp>();
    }

    /// <summary>
    /// The pending folder changes of Edit Custom Folders, kept in
    /// UserData/ExScoring_PendingFolderChanges.json (saved after every change, so a crash loses
    /// nothing). Nothing is moved on disk while the game runs: the game keeps reading every song
    /// from where it was loaded, and the folder views show the changes through VirtualLocation /
    /// Overlay instead. Creating a new, empty folder is the one thing done right away
    /// (FolderEditMode), since it can't be in use.
    ///
    /// ApplyPending runs on quit (best effort: it only runs if the game closes normally, and a
    /// file the game still has open makes it stop) and on launch (Main.OnApplicationStart, before
    /// the game scans the songs folder, so nothing is open yet). Ops are applied in order and each
    /// is removed from the file once done, so a crash part way through just continues next time.
    /// Every op checks the disk first, so running one twice is harmless.
    ///
    /// Song IDs come from the file name (see SongID-Computation), so moving a file between folders
    /// keeps its scores. File names are never changed.
    /// </summary>
    internal static class FolderOpsJournal
    {
        public const string Root = "";
        private const string LogPrefix = "[FolderEdit]";
        private const int MaxAttempts = 3;

        public const string WhenLaunch = "launch";
        public const string WhenQuit = "quit";

        /// <summary>Temporary folder name suffix of a case-only rename (Windows can't rename "a" to "A" directly).</summary>
        public const string RenameTempSuffix = ".exscoring-rename";

        private static FolderOpsFile data;
        private static bool loaded;

        private static string FilePath =>
            Path.Combine(Application.dataPath, "..", "UserData", "ExScoring_PendingFolderChanges.json");

        // ── State ─────────────────────────────────────────────────────────────

        public static int Count
        {
            get { EnsureLoaded(); return data.Ops.Count; }
        }

        public static bool HasPending => Count > 0;

        public static IReadOnlyList<FolderOp> Ops
        {
            get { EnsureLoaded(); return data.Ops; }
        }

        public static void EnsureLoaded()
        {
            if (loaded) return;
            loaded = true;
            data = null;

            string path = FilePath;
            // The .tmp copy only matters if a save was cut off between writing it and replacing.
            foreach (string candidate in new[] { path, path + ".tmp" })
            {
                try
                {
                    if (!File.Exists(candidate)) continue;
                    FolderOpsFile read = JsonConvert.DeserializeObject<FolderOpsFile>(File.ReadAllText(candidate));
                    if (read == null) continue;
                    data = read;
                    DebugLog.Log($"{LogPrefix} Loaded {(data.Ops != null ? data.Ops.Count : 0)} pending change(s) from {Path.GetFileName(candidate)}");
                    break;
                }
                catch (Exception e)
                {
                    DebugLog.Log($"{LogPrefix} Couldn't read {candidate}: {e.Message}");
                }
            }

            if (data == null) data = new FolderOpsFile();
            if (data.Ops == null) data.Ops = new List<FolderOp>();
            int dropped = data.Ops.RemoveAll(o => !IsValid(o));
            if (dropped > 0) DebugLog.Log($"{LogPrefix} Dropped {dropped} invalid pending change(s)");
        }

        private static bool IsValid(FolderOp op)
        {
            if (op == null) return false;
            switch (op.Kind)
            {
                case FolderOpKind.MoveSong:
                    return !string.IsNullOrEmpty(op.FileName) && op.From != null && op.To != null;
                case FolderOpKind.RenameFolder:
                    return !string.IsNullOrEmpty(op.From) && !string.IsNullOrEmpty(op.To);
                case FolderOpKind.DeleteFolder:
                    return !string.IsNullOrEmpty(op.From);
            }
            return false;
        }

        private static void Save()
        {
            string path = FilePath;
            string tmp = path + ".tmp";
            try
            {
                if (data.Ops.Count == 0)
                {
                    if (File.Exists(path)) File.Delete(path);
                    if (File.Exists(tmp)) File.Delete(tmp);
                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(tmp, JsonConvert.SerializeObject(data, Formatting.Indented));
                if (File.Exists(path))
                {
                    try
                    {
                        File.Replace(tmp, path, null);
                        return;
                    }
                    catch
                    {
                        File.Delete(path);
                    }
                }
                File.Move(tmp, path);
            }
            catch (Exception e)
            {
                DebugLog.Log($"{LogPrefix} Couldn't save the pending changes: {e.Message}");
            }
        }

        // ── Names ─────────────────────────────────────────────────────────────

        /// <summary>Folder as shown in the song list ("Unsorted" = songs folder) → journal key ("").</summary>
        public static string ToKey(string folder)
            => string.IsNullOrEmpty(folder) || folder == SongFolderManager.FolderCustom ? Root : folder;

        /// <summary>Journal key → folder as shown in the song list.</summary>
        public static string ToFolder(string key)
            => string.IsNullOrEmpty(key) ? SongFolderManager.FolderCustom : key;

        public static bool SameFolder(string a, string b)
            => string.Equals(a ?? Root, b ?? Root, StringComparison.OrdinalIgnoreCase);

        private static bool SameFile(string a, string b)
            => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        // ── Virtual layout (what the folders look like once everything is applied) ──

        /// <summary>Where the copy of fileName that's on disk in diskKey ends up (journal keys).</summary>
        public static string VirtualLocation(string fileName, string diskKey)
        {
            EnsureLoaded();
            return Locate(data.Ops, fileName, diskKey);
        }

        private static string Locate(List<FolderOp> ops, string fileName, string diskKey)
        {
            string loc = diskKey ?? Root;
            for (int i = 0; i < ops.Count; i++)
            {
                FolderOp op = ops[i];
                switch (op.Kind)
                {
                    case FolderOpKind.MoveSong:
                        if (SameFile(op.FileName, fileName) && SameFolder(op.From, loc)) loc = op.To;
                        break;
                    case FolderOpKind.RenameFolder:
                        if (loc != Root && SameFolder(op.From, loc)) loc = op.To;
                        break;
                    case FolderOpKind.DeleteFolder:
                        if (loc != Root && SameFolder(op.From, loc)) loc = Root;
                        break;
                }
            }
            return loc;
        }

        /// <summary>The pack folders (journal keys) once the pending renames and deletes are applied.</summary>
        public static List<string> VirtualFolderKeys(IEnumerable<string> diskFolderKeys)
        {
            EnsureLoaded();
            var list = new List<string>();
            foreach (string k in diskFolderKeys)
            {
                if (string.IsNullOrEmpty(k) || k.EndsWith(RenameTempSuffix, StringComparison.OrdinalIgnoreCase)) continue;
                if (IndexOfFolder(list, k) < 0) list.Add(k);
            }

            foreach (FolderOp op in data.Ops)
            {
                if (op.Kind == FolderOpKind.RenameFolder)
                {
                    int idx = IndexOfFolder(list, op.From);
                    if (idx < 0) continue;
                    int existing = IndexOfFolder(list, op.To);
                    if (existing >= 0 && existing != idx) list.RemoveAt(idx); // merged into an existing folder
                    else list[idx] = op.To;
                }
                else if (op.Kind == FolderOpKind.DeleteFolder)
                {
                    int idx = IndexOfFolder(list, op.From);
                    if (idx >= 0) list.RemoveAt(idx);
                }
            }
            return list;
        }

        private static int IndexOfFolder(List<string> list, string key)
        {
            for (int i = 0; i < list.Count; i++)
                if (SameFolder(list[i], key)) return i;
            return -1;
        }

        /// <summary>
        /// The folder's name on disk right now, for a folder name as shown (undoes pending renames).
        /// Used for folder.json lookups, which are keyed by the on-disk name.
        /// </summary>
        public static string DiskNameFor(string folder)
        {
            if (string.IsNullOrEmpty(folder)) return folder;
            EnsureLoaded();
            if (data.Ops.Count == 0) return folder;

            string name = folder;
            for (int i = data.Ops.Count - 1; i >= 0; i--)
            {
                FolderOp op = data.Ops[i];
                if (op.Kind == FolderOpKind.RenameFolder && SameFolder(op.To, name)) name = op.From;
            }
            return name;
        }

        /// <summary>True if any pending op names this folder (journal key), as source or destination.</summary>
        public static bool References(string key)
        {
            EnsureLoaded();
            foreach (FolderOp op in data.Ops)
                if (SameFolder(op.From, key) || (op.Kind != FolderOpKind.DeleteFolder && SameFolder(op.To, key)))
                    return true;
            return false;
        }

        /// <summary>True if a pending op renames or deletes this folder (journal key), so it's still on disk under that name.</summary>
        public static bool IsPendingSource(string key)
        {
            EnsureLoaded();
            foreach (FolderOp op in data.Ops)
                if ((op.Kind == FolderOpKind.RenameFolder || op.Kind == FolderOpKind.DeleteFolder) && SameFolder(op.From, key))
                    return true;
            return false;
        }

        /// <summary>
        /// SongFolderManager's file name → folders map (folder names as shown, "Unsorted" for loose
        /// files), with the pending changes applied. A real pack folder still comes before
        /// "Unsorted" in every list, which SongFolderManager.Rebuild relies on.
        /// </summary>
        public static Dictionary<string, List<string>> Overlay(Dictionary<string, List<string>> diskMap)
        {
            EnsureLoaded();
            var result = new Dictionary<string, List<string>>();

            foreach (var kv in diskMap)
                result[kv.Key] = MapFolders(kv.Key, kv.Value);

            // A file with a pending move that isn't in the songs folder tree (still in Downloads,
            // only moved into the songs folder on the next launch) counts as loose.
            foreach (FolderOp op in data.Ops)
            {
                if (op.Kind != FolderOpKind.MoveSong) continue;
                if (ContainsFile(result, op.FileName)) continue;
                result[op.FileName] = MapFolders(op.FileName, new List<string> { SongFolderManager.FolderCustom });
            }
            return result;
        }

        private static bool ContainsFile(Dictionary<string, List<string>> map, string fileName)
        {
            if (map.ContainsKey(fileName)) return true;
            foreach (string k in map.Keys)
                if (SameFile(k, fileName)) return true;
            return false;
        }

        private static List<string> MapFolders(string fileName, List<string> diskFolders)
        {
            var packs = new List<string>();
            bool loose = false;
            foreach (string folder in diskFolders)
            {
                string loc = VirtualLocation(fileName, ToKey(folder));
                if (loc == Root) loose = true;
                else if (IndexOfFolder(packs, loc) < 0) packs.Add(loc);
            }
            if (loose) packs.Add(SongFolderManager.FolderCustom);
            return packs;
        }

        // ── Recording ─────────────────────────────────────────────────────────

        /// <summary>
        /// Records moving the copy of fileName that's (virtually) in fromKey to toKey.
        /// diskCopies: the folders (keys) the file is really in on disk, used to drop moves that
        /// cancel out (a song moved back where it started).
        /// </summary>
        public static void AddMove(string fileName, string fromKey, string toKey, IList<string> diskCopies)
        {
            EnsureLoaded();
            if (SameFolder(fromKey, toKey)) return;

            data.Ops.Add(new FolderOp
            {
                Kind = FolderOpKind.MoveSong,
                FileName = fileName,
                From = fromKey ?? Root,
                To = toKey ?? Root,
                Recorded = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            });
            DebugLog.Log($"{LogPrefix} Pending: move {fileName} from {ToFolder(fromKey)} to {ToFolder(toKey)}");

            DropRedundantMoves(fileName, diskCopies);
            Save();
        }

        public static void AddRename(string fromKey, string toKey)
        {
            EnsureLoaded();
            data.Ops.Add(new FolderOp
            {
                Kind = FolderOpKind.RenameFolder,
                From = fromKey,
                To = toKey,
                Recorded = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            });
            DebugLog.Log($"{LogPrefix} Pending: rename folder {fromKey} to {toKey}");
            Save();
        }

        public static void AddDelete(string key)
        {
            EnsureLoaded();
            data.Ops.Add(new FolderOp
            {
                Kind = FolderOpKind.DeleteFolder,
                From = key,
                Recorded = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            });
            DebugLog.Log($"{LogPrefix} Pending: delete folder {key} (its songs go to Unsorted)");
            Save();
        }

        /// <summary>True if a pending move changes where this copy ends up (renames/deletes of its folder don't count).</summary>
        public static bool IsMoved(string fileName, string diskKey)
        {
            EnsureLoaded();
            bool any = false;
            foreach (FolderOp op in data.Ops)
                if (op.Kind == FolderOpKind.MoveSong && SameFile(op.FileName, fileName)) { any = true; break; }
            if (!any) return false;

            List<FolderOp> without = data.Ops.FindAll(o => !(o.Kind == FolderOpKind.MoveSong && SameFile(o.FileName, fileName)));
            return !SameFolder(Locate(without, fileName, diskKey), Locate(data.Ops, fileName, diskKey));
        }

        /// <summary>Drops every pending move of this file (Undo Move).</summary>
        public static int UndoMoves(string fileName)
        {
            EnsureLoaded();
            int removed = data.Ops.RemoveAll(o => o.Kind == FolderOpKind.MoveSong && SameFile(o.FileName, fileName));
            if (removed > 0)
            {
                DebugLog.Log($"{LogPrefix} Undid {removed} pending move(s) of {fileName}");
                Save();
            }
            return removed;
        }

        /// <summary>Drops every pending change (Discard Pending Changes).</summary>
        public static void DiscardAll()
        {
            EnsureLoaded();
            DebugLog.Log($"{LogPrefix} Discarded {data.Ops.Count} pending change(s)");
            data.Ops.Clear();
            Save();
        }

        /// <summary>
        /// If the file's moves don't change where any of its copies end up any more (moved back
        /// where it started), they're all removed.
        /// </summary>
        private static void DropRedundantMoves(string fileName, IList<string> diskCopies)
        {
            var copies = new List<string>();
            if (diskCopies != null) copies.AddRange(diskCopies);
            if (copies.Count == 0) copies.Add(Root);

            List<FolderOp> without = data.Ops.FindAll(o => !(o.Kind == FolderOpKind.MoveSong && SameFile(o.FileName, fileName)));
            if (without.Count == data.Ops.Count) return;

            foreach (string copy in copies)
                if (!SameFolder(Locate(without, fileName, copy), Locate(data.Ops, fileName, copy)))
                    return;

            DebugLog.Log($"{LogPrefix} {fileName} is back where it started: its pending moves were dropped");
            data.Ops = without;
        }

        // ── Applying ──────────────────────────────────────────────────────────

        private enum ApplyResult { Done, Skipped, Retry }

        /// <summary>
        /// Applies the pending changes in order. Stops at the first one that fails for a reason that
        /// may go away (a file in use), so later changes never run ahead of an earlier one; it's
        /// retried next time (an op that keeps failing is given up on after MaxAttempts).
        /// when: WhenLaunch or WhenQuit (also shown in the log).
        /// </summary>
        public static void ApplyPending(string songsRoot, string downloadsDir, string backupDir, string when)
        {
            try
            {
                EnsureLoaded();
                if (data.Ops.Count == 0) return;

                if (string.IsNullOrEmpty(songsRoot) || !Directory.Exists(songsRoot))
                {
                    DebugLog.Log($"{LogPrefix} [{when}] Songs folder not found ({songsRoot}): {data.Ops.Count} pending change(s) kept");
                    return;
                }

                string songsDirName = Path.GetFileName(songsRoot.TrimEnd('\\', '/'));
                DebugLog.Log($"{LogPrefix} [{when}] Applying {data.Ops.Count} pending folder change(s)");

                int done = 0, skipped = 0;
                while (data.Ops.Count > 0)
                {
                    FolderOp op = data.Ops[0];
                    ApplyResult result;
                    try
                    {
                        result = ApplyOne(op, songsRoot, songsDirName, downloadsDir, backupDir);
                    }
                    catch (Exception e)
                    {
                        DebugLog.Log($"{LogPrefix} [{when}] {Describe(op)} failed: {e.Message}");
                        result = ApplyResult.Retry;
                    }

                    if (result == ApplyResult.Retry)
                    {
                        // On quit a file the game still has open is expected: only launch
                        // failures (nothing is open yet) count toward giving up.
                        if (when != WhenQuit) op.Attempts++;
                        if (op.Attempts < MaxAttempts)
                        {
                            Save();
                            DebugLog.Log($"{LogPrefix} [{when}] Stopped: will retry (attempt {op.Attempts} of {MaxAttempts}). {data.Ops.Count} change(s) still pending");
                            break;
                        }
                        DebugLog.Log($"{LogPrefix} [{when}] Giving up on {Describe(op)} after {op.Attempts} attempts");
                        result = ApplyResult.Skipped;
                    }

                    data.Ops.RemoveAt(0);
                    if (result == ApplyResult.Done) done++; else skipped++;
                    Save();
                }

                SongCache.SaveIfDirty();
                DebugLog.Log($"{LogPrefix} [{when}] {done} applied, {skipped} skipped, {data.Ops.Count} still pending");
            }
            catch (Exception e)
            {
                DebugLog.Log($"{LogPrefix} [{when}] Applying the pending changes failed: {e}");
            }
        }

        private static string Describe(FolderOp op)
        {
            switch (op.Kind)
            {
                case FolderOpKind.MoveSong: return $"Move {op.FileName} from {ToFolder(op.From)} to {ToFolder(op.To)}";
                case FolderOpKind.RenameFolder: return $"Rename folder {op.From} to {op.To}";
                default: return $"Delete folder {op.From}";
            }
        }

        private static string DirFor(string songsRoot, string key)
            => string.IsNullOrEmpty(key) ? songsRoot : Path.Combine(songsRoot, key);

        private static string Rel(string key, string fileName)
            => string.IsNullOrEmpty(key) ? fileName : key + "/" + fileName;

        private static ApplyResult ApplyOne(FolderOp op, string songsRoot, string songsDirName, string downloadsDir, string backupDir)
        {
            switch (op.Kind)
            {
                case FolderOpKind.MoveSong: return ApplyMove(op, songsRoot, songsDirName, downloadsDir);
                case FolderOpKind.RenameFolder: return ApplyRename(op, songsRoot, songsDirName);
                case FolderOpKind.DeleteFolder: return ApplyDelete(op, songsRoot, songsDirName, backupDir);
            }
            return ApplyResult.Skipped;
        }

        private static ApplyResult ApplyMove(FolderOp op, string songsRoot, string songsDirName, string downloadsDir)
        {
            string src = Path.Combine(DirFor(songsRoot, op.From), op.FileName);
            if (!File.Exists(src) && string.IsNullOrEmpty(op.From) && !string.IsNullOrEmpty(downloadsDir))
            {
                // Downloaded this session and not moved into the songs folder yet.
                string inDownloads = Path.Combine(downloadsDir, op.FileName);
                if (File.Exists(inDownloads)) src = inDownloads;
            }

            if (!File.Exists(src))
            {
                DebugLog.Log($"{LogPrefix} {Describe(op)}: file not found, skipped");
                return ApplyResult.Skipped;
            }

            string dstDir = DirFor(songsRoot, op.To);
            Directory.CreateDirectory(dstDir);
            string dst = Path.Combine(dstDir, op.FileName);

            if (File.Exists(dst))
            {
                if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase))
                    return ApplyResult.Done; // already there

                if (ExScoring.FilesIdentical(src, dst))
                {
                    File.Delete(src);
                    SongCache.RemoveSongsPath(songsDirName, Rel(op.From, op.FileName));
                    DebugLog.Log($"{LogPrefix} {Describe(op)}: an identical copy was already there, removed the duplicate");
                    return ApplyResult.Done;
                }

                DebugLog.Log($"{LogPrefix} {Describe(op)}: a different file with that name is already there, skipped");
                return ApplyResult.Skipped;
            }

            File.Move(src, dst);
            SongCache.MoveSongsPath(songsDirName, Rel(op.From, op.FileName), Rel(op.To, op.FileName));
            DebugLog.Log($"{LogPrefix} {Describe(op)}: done");
            return ApplyResult.Done;
        }

        private static ApplyResult ApplyRename(FolderOp op, string songsRoot, string songsDirName)
        {
            string src = DirFor(songsRoot, op.From);
            string dst = DirFor(songsRoot, op.To);
            bool caseOnly = string.Equals(op.From, op.To, StringComparison.OrdinalIgnoreCase);
            string tmp = src + RenameTempSuffix;

            // A case-only rename goes through a temporary name; finish one cut off half way.
            if (caseOnly && !Directory.Exists(src) && Directory.Exists(tmp))
            {
                Directory.Move(tmp, dst);
                RenameCacheEntries(dst, songsDirName, op.From, op.To);
                return ApplyResult.Done;
            }

            if (!Directory.Exists(src))
            {
                if (Directory.Exists(dst)) return ApplyResult.Done;
                DebugLog.Log($"{LogPrefix} {Describe(op)}: folder not found, skipped");
                return ApplyResult.Skipped;
            }

            if (caseOnly)
            {
                // Already has the new casing (Directory.Exists can't tell, names are case-insensitive).
                foreach (DirectoryInfo di in new DirectoryInfo(songsRoot).GetDirectories())
                    if (string.Equals(di.Name, op.To, StringComparison.Ordinal)) return ApplyResult.Done;
                Directory.Move(src, tmp);
                Directory.Move(tmp, dst);
                RenameCacheEntries(dst, songsDirName, op.From, op.To);
                DebugLog.Log($"{LogPrefix} {Describe(op)}: done");
                return ApplyResult.Done;
            }

            if (Directory.Exists(dst))
            {
                // A folder with the new name appeared outside the game: merge into it.
                DebugLog.Log($"{LogPrefix} {Describe(op)}: {op.To} already exists, merging");
                return MergeInto(src, dst, songsDirName, op.From, op.To);
            }

            Directory.Move(src, dst);
            RenameCacheEntries(dst, songsDirName, op.From, op.To);
            DebugLog.Log($"{LogPrefix} {Describe(op)}: done");
            return ApplyResult.Done;
        }

        private static void RenameCacheEntries(string newDir, string songsDirName, string fromKey, string toKey)
        {
            foreach (string f in Directory.GetFiles(newDir, "*.audica", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(f);
                SongCache.MoveSongsPath(songsDirName, Rel(fromKey, name), Rel(toKey, name));
            }
        }

        private static ApplyResult MergeInto(string src, string dst, string songsDirName, string fromKey, string toKey)
        {
            foreach (string f in Directory.GetFiles(src))
            {
                string name = Path.GetFileName(f);
                string target = Path.Combine(dst, name);
                bool isSong = name.EndsWith(".audica", StringComparison.OrdinalIgnoreCase);

                if (File.Exists(target))
                {
                    if (isSong && ExScoring.FilesIdentical(f, target))
                    {
                        File.Delete(f);
                        SongCache.RemoveSongsPath(songsDirName, Rel(fromKey, name));
                    }
                    else
                    {
                        DebugLog.Log($"{LogPrefix} Merge: {name} is already in {toKey} and differs, kept in {fromKey}");
                    }
                    continue;
                }

                File.Move(f, target);
                if (isSong) SongCache.MoveSongsPath(songsDirName, Rel(fromKey, name), Rel(toKey, name));
            }

            foreach (string d in Directory.GetDirectories(src))
            {
                string target = Path.Combine(dst, Path.GetFileName(d));
                if (!Directory.Exists(target)) Directory.Move(d, target);
            }

            if (Directory.GetFileSystemEntries(src).Length == 0)
                Directory.Delete(src, false);
            else
                DebugLog.Log($"{LogPrefix} Merge: {fromKey} kept, some of its files were already in {toKey}");
            return ApplyResult.Done;
        }

        private static ApplyResult ApplyDelete(FolderOp op, string songsRoot, string songsDirName, string backupDir)
        {
            string dir = DirFor(songsRoot, op.From);
            if (!Directory.Exists(dir)) return ApplyResult.Done;

            bool keep = false;
            foreach (string f in Directory.GetFiles(dir, "*.audica", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(f);
                string dst = Path.Combine(songsRoot, name);
                if (File.Exists(dst))
                {
                    if (ExScoring.FilesIdentical(f, dst))
                    {
                        File.Delete(f);
                        SongCache.RemoveSongsPath(songsDirName, Rel(op.From, name));
                    }
                    else
                    {
                        keep = true;
                        DebugLog.Log($"{LogPrefix} {Describe(op)}: a different {name} is already in Unsorted, it stays in the folder");
                    }
                    continue;
                }
                File.Move(f, dst);
                SongCache.MoveSongsPath(songsDirName, Rel(op.From, name), name);
            }

            if (keep)
            {
                DebugLog.Log($"{LogPrefix} {Describe(op)}: folder kept (see above)");
                return ApplyResult.Done;
            }

            if (Directory.GetFileSystemEntries(dir).Length == 0)
            {
                Directory.Delete(dir, false);
                DebugLog.Log($"{LogPrefix} {Describe(op)}: done");
                return ApplyResult.Done;
            }

            // folder.json, images, anything nested: kept in SongBackups rather than deleted.
            if (string.IsNullOrEmpty(backupDir))
            {
                DebugLog.Log($"{LogPrefix} {Describe(op)}: songs moved, the folder's other files were kept in place");
                return ApplyResult.Done;
            }

            string backupParent = Path.Combine(backupDir, "Deleted Folders");
            Directory.CreateDirectory(backupParent);
            string backup = Path.Combine(backupParent, op.From);
            if (Directory.Exists(backup) || File.Exists(backup))
                backup = Path.Combine(backupParent, op.From + "_" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.Move(dir, backup);
            DebugLog.Log($"{LogPrefix} {Describe(op)}: done, its other files are in {backup}");
            return ApplyResult.Done;
        }
    }
}