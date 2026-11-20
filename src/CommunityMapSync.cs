using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using MelonLoader;
using Newtonsoft.Json;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;

namespace ExScoringMod
{
    public partial class ExScoring
    {
        /// <summary>The API base URL, for the community map calls in CommunityMapSync.</summary>
        internal static string CommunityMapApiBase => ApiBaseUrl;
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  API shapes (ApiContract v2.58, Section 21)
    // ══════════════════════════════════════════════════════════════════════════

    public class CommunityMapLeaderboardLink
    {
        public string songId;
        public int scoreCount;
        public bool exact;
    }

    public class CommunityMapUploader
    {
        public string userId;
        public string nickname;
    }

    public class CommunityMapApiEntry
    {
        public string fileId;
        public string parentSongId;
        public string parentTitle;
        public string parentArtist;
        public string mapName;
        public string fileName;
        public string fileType;   // "mid" or "cues"
        public long sizeBytes;
        public string sha256;     // lowercase hex
        public string source;     // "web" or "game"
        public long uploadedAt;
        public CommunityMapUploader uploader;
        public string downloadUrl;
        public CommunityMapLeaderboardLink[] leaderboards;
        public bool canDelete;
    }

    public class CommunityMapListResponse
    {
        public CommunityMapApiEntry[] maps;
        public int total;
    }

    public class CommunityMapUploadResponse
    {
        public bool duplicate;
        public CommunityMapApiEntry map;
    }

    public class CommunityMapErrorResponse
    {
        public string error;
        public string message;
        public string expectedPrefix;
    }

    /// <summary>
    /// Community maps on the AudicaEX server (ApiContract v2.58, Section 21):
    ///
    /// - <b>Auto download</b> (Config.AutoDownloadCommunityMaps, on by default): during a full song
    ///   list load (boot, song list reload) SongListAssembler runs <see cref="RunLoadingPhase"/>
    ///   before reporting the list loaded. It lists every map on the server and downloads the ones
    ///   for installed songs (exact parent songID) that aren't in the community maps folder yet,
    ///   by file name or SHA-256. The loading screen waits at most <see cref="BootBudgetSeconds"/>;
    ///   whatever is left finishes in the background, then rebuilds the folders. With the song
    ///   cache off (vanilla loader) the check runs in the background once the list is loaded.
    ///   Options > Data > Community Maps > Check Now runs it by hand.
    /// - <b>On the website?</b> The list also tells which parent/SHA-256 pairs the server already
    ///   has. While a map that isn't on the server is selected, <see cref="CommunityMapUploadButton"/>
    ///   shows an Upload button where the (hidden) difficulty buttons are.
    ///
    /// - <b>Pending cache</b> (UserData/ExScoring_CommunityMapsPending.json): each full check saves
    ///   the server's maps whose song isn't installed (plus failed downloads). After every song list
    ///   reload, quick or full (SongDownloadTracker.PostProcess), <see cref="CheckPending"/>
    ///   downloads the cached maps whose song is now installed, with no list request.
    ///
    /// Local files are never overwritten or deleted.
    /// </summary>
    internal static class CommunityMapSync
    {
        public const long MaxFileBytes = 5L * 1024 * 1024;  // server limit (Section 21)
        public const float BootBudgetSeconds = 20f;         // longest the loading screen waits
        private const int PageLimit = 100;                  // server max
        private const int MaxPages = 200;                   // 20,000 maps, a safety stop
        private const int ListTimeoutSeconds = 15;
        private const int DownloadTimeoutSeconds = 30;
        private const int UploadTimeoutSeconds = 60;

        private const string LogPrefix = "[CommunityMapSync]";

        /// <summary>True while a sync (list + downloads) is running.</summary>
        public static bool Running
        {
            get
            {
                // A sync that threw never clears the flag: don't let it block every later check.
                if (running && Time.realtimeSinceStartup - runStartedAt > StaleRunSeconds)
                {
                    DebugLog.Log($"{LogPrefix} Previous sync looks stuck, ignoring it.");
                    running = false;
                }
                return running;
            }
            private set
            {
                running = value;
                if (value) runStartedAt = Time.realtimeSinceStartup;
            }
        }
        private static bool running;
        private static float runStartedAt;
        private const float StaleRunSeconds = 600f;

        // ── What the server has: parentSongId → SHA-256s ──
        private static readonly Dictionary<string, HashSet<string>> remoteShas =
            new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private static bool fullListKnown;                     // remoteShas covers every parent
        private static readonly HashSet<string> parentsFetched = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> parentsFetching = new HashSet<string>(StringComparer.Ordinal);

        // ── Local file hashes, by path; reused while size + write time are unchanged ──
        private sealed class LocalHash { public long size; public long ticks; public string sha; }
        private static readonly Dictionary<string, LocalHash> localHashes =
            new Dictionary<string, LocalHash>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Raised on the main thread when what the server is known to have changes.</summary>
        public static event Action RemoteStateChanged;

        // ══════════════════════════════════════════════════════════════════════
        //  Entry points
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// The song list loading step (SongListAssembler Phase E). Starts a sync and waits for it,
        /// at most <see cref="BootBudgetSeconds"/>, showing progress through <paramref name="setStatus"/>.
        /// </summary>
        public static IEnumerator RunLoadingPhase(Action<string> setStatus)
        {
            if (!Config.AutoDownloadCommunityMaps) yield break;
            if (Running)
            {
                DebugLog.Log($"{LogPrefix} A sync is already running; not waiting for it during loading.");
                yield break;
            }

            bool done = false;
            bool phaseOver = false; // past this, the loading screen is gone: stop writing its text
            Action<string> status = text => { if (!phaseOver) setStatus?.Invoke(text); };
            MelonCoroutines.Start(SyncCoroutine("song list load", status, () => done = true));

            float start = Time.realtimeSinceStartup;
            while (!done)
            {
                if (Time.realtimeSinceStartup - start > BootBudgetSeconds)
                {
                    DebugLog.Log($"{LogPrefix} Still running after {BootBudgetSeconds:F0}s, finishing in the background.");
                    break;
                }
                yield return null;
            }
            phaseOver = true;
        }

        /// <summary>
        /// Starts a sync in the background (no loading screen). Skipped while one is running.
        /// <paramref name="manual"/>: started by the player (shows a popup with the result, and
        /// runs even with Auto Download off).
        /// </summary>
        public static void StartBackgroundSync(string reason, bool manual = false)
        {
            if (!manual && !Config.AutoDownloadCommunityMaps) return;
            if (Running)
            {
                if (manual) PlaylistUtil.Popup("Already checking for community maps");
                return;
            }
            if (manual) PlaylistUtil.Popup("Checking for community maps...");
            MelonCoroutines.Start(SyncCoroutine(reason, null, null, manual));
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Sync
        // ══════════════════════════════════════════════════════════════════════

        private static IEnumerator SyncCoroutine(string reason, Action<string> setStatus, Action onDone, bool manual = false)
        {
            Running = true;
            int downloaded = 0, failed = 0, wanted = 0;
            bool listOk = false;

            DebugLog.Log($"{LogPrefix} Sync started ({reason}).");

            // ── Installed songs and the map folder ──
            HashSet<string> installed = InstalledSongIDs();
            string dir = GetMapDir();
            if (installed == null || string.IsNullOrEmpty(dir))
            {
                DebugLog.Log($"{LogPrefix} No song list or map folder yet, skipping.");
                Finish(onDone, manual, false, 0, 0, 0);
                yield break;
            }
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex)
            {
                DebugLog.Log($"{LogPrefix} Can't create '{dir}': {ex.Message}");
                Finish(onDone, manual, false, 0, 0, 0);
                yield break;
            }

            // ── Every map on the server, page by page ──
            setStatus?.Invoke("Checking community maps...");
            var all = new List<CommunityMapApiEntry>();
            int offset = 0, total = -1;
            for (int page = 0; page < MaxPages; page++)
            {
                string url = $"{ExScoring.CommunityMapApiBase}/api/community-maps?limit={PageLimit}&offset={offset}";
                var result = new HttpResult();
                yield return Send(UnityWebRequest.Get(url), ListTimeoutSeconds, result, false);

                if (!result.ok)
                {
                    DebugLog.Log($"{LogPrefix} List failed ({result.code}): {result.error} | {result.text}");
                    break;
                }

                CommunityMapListResponse resp = null;
                try { resp = JsonConvert.DeserializeObject<CommunityMapListResponse>(result.text); }
                catch (Exception ex) { DebugLog.Log($"{LogPrefix} List parse failed: {ex.Message}"); }
                if (resp == null || resp.maps == null) break;

                all.AddRange(resp.maps);
                total = resp.total;
                offset += resp.maps.Length;
                if (resp.maps.Length == 0 || offset >= total)
                {
                    listOk = true;
                    break;
                }
            }

            if (!listOk)
            {
                Finish(onDone, manual, false, 0, 0, 0);
                yield break;
            }

            SetRemoteFull(all);
            DebugLog.Log($"{LogPrefix} Server has {all.Count} map(s).");

            // ── What's missing locally ──
            HashSet<string> localNames, localShas;
            IndexLocalFolder(dir, out localNames, out localShas);

            var toGet = new List<CommunityMapApiEntry>();
            var newPending = new List<PendingMap>(); // song not installed: fetched once it is
            var namesTaken = new HashSet<string>(localNames, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < all.Count; i++)
            {
                CommunityMapApiEntry m = all[i];
                if (m == null || string.IsNullOrEmpty(m.fileId) || string.IsNullOrEmpty(m.parentSongId)) continue;
                if (!IsSafeFileName(m.fileName))
                {
                    DebugLog.Log($"{LogPrefix} Skipping map {m.fileId}: unusable file name '{m.fileName}'.");
                    continue;
                }
                if (m.sizeBytes > MaxFileBytes) continue;
                if (!string.IsNullOrEmpty(m.sha256) && localShas.Contains(m.sha256)) continue; // same file, any name

                if (!installed.Contains(m.parentSongId))
                {
                    newPending.Add(PendingMap.From(m));
                    continue;
                }

                if (namesTaken.Contains(m.fileName)) continue; // a different file has the name: keep the player's
                namesTaken.Add(m.fileName);
                toGet.Add(m);
            }
            wanted = toGet.Count;

            // ── Downloads ──
            for (int i = 0; i < toGet.Count; i++)
            {
                CommunityMapApiEntry m = toGet[i];
                setStatus?.Invoke($"Downloading community maps ({i + 1}/{toGet.Count})...");

                string url = $"{ExScoring.CommunityMapApiBase}/api/community-maps/{Uri.EscapeDataString(m.fileId)}/download";
                var result = new HttpResult();
                yield return Send(UnityWebRequest.Get(url), DownloadTimeoutSeconds, result, true);

                if (!result.ok || result.bytes == null || result.bytes.Length == 0)
                {
                    DebugLog.Log($"{LogPrefix} Download of '{m.fileName}' failed ({result.code}): {result.error}");
                    failed++;
                    newPending.Add(PendingMap.From(m)); // retried on the next song list reload
                    continue;
                }

                if (SaveDownloaded(dir, m, result.bytes)) downloaded++;
                else failed++;
            }

            // The server's full list is known: it replaces the cache.
            ReplacePending(newPending);

            Finish(onDone, manual, true, wanted, downloaded, failed);

            if (downloaded > 0) RebuildViewsIfLoaded();
        }

        private static void Finish(Action onDone, bool manual, bool listOk, int wanted, int downloaded, int failed)
        {
            Running = false;
            DebugLog.Log($"{LogPrefix} Sync done: list {(listOk ? "ok" : "failed")}, {downloaded}/{wanted} downloaded, {failed} failed.");

            if (manual)
            {
                if (!listOk) PlaylistUtil.Popup("Couldn't reach AudicaEX for community maps");
                else if (wanted == 0) PlaylistUtil.Popup("Community maps are up to date");
                else if (failed == 0) PlaylistUtil.Popup($"Downloaded {downloaded} community map{(downloaded == 1 ? "" : "s")}");
                else PlaylistUtil.Popup($"Downloaded {downloaded} community map{(downloaded == 1 ? "" : "s")}, {failed} failed");
            }

            try { onDone?.Invoke(); } catch { }
            RaiseRemoteChanged();
            RunQueuedPendingCheck();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Pending maps: on the server, but their song isn't installed
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>A map whose song wasn't installed at the last full check (or whose download failed).</summary>
        public class PendingMap
        {
            public string fileId;
            public string parentSongId;
            public string fileName;
            public string sha256;
            public long sizeBytes;

            public static PendingMap From(CommunityMapApiEntry m) => new PendingMap
            {
                fileId = m.fileId,
                parentSongId = m.parentSongId,
                fileName = m.fileName,
                sha256 = m.sha256,
                sizeBytes = m.sizeBytes
            };

            public CommunityMapApiEntry ToEntry() => new CommunityMapApiEntry
            {
                fileId = fileId,
                parentSongId = parentSongId,
                fileName = fileName,
                sha256 = sha256,
                sizeBytes = sizeBytes
            };
        }

        public class PendingFile
        {
            public long savedAt;
            public List<PendingMap> maps = new List<PendingMap>();
        }

        private static string PendingPath =>
            Path.Combine(Application.dataPath, "..", "UserData", "ExScoring_CommunityMapsPending.json");

        private static List<PendingMap> pending; // null until loaded from disk
        private static bool pendingCheckQueued;  // a pending check was asked for while a sync ran

        private static List<PendingMap> GetPending()
        {
            if (pending != null) return pending;
            pending = new List<PendingMap>();
            try
            {
                if (File.Exists(PendingPath))
                {
                    var file = JsonConvert.DeserializeObject<PendingFile>(File.ReadAllText(PendingPath));
                    if (file?.maps != null)
                        foreach (PendingMap m in file.maps)
                            if (m != null && !string.IsNullOrEmpty(m.fileId) && !string.IsNullOrEmpty(m.parentSongId))
                                pending.Add(m);
                }
            }
            catch (Exception ex)
            {
                DebugLog.Log($"{LogPrefix} Reading the pending map cache failed: {ex.Message}");
            }
            return pending;
        }

        private static void ReplacePending(List<PendingMap> maps)
        {
            pending = maps ?? new List<PendingMap>();
            SavePending();
            DebugLog.Log($"{LogPrefix} Pending map cache: {pending.Count} map(s) for songs that aren't installed.");
        }

        private static void SavePending()
        {
            try
            {
                var file = new PendingFile
                {
                    savedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    maps = pending ?? new List<PendingMap>()
                };
                string path = PendingPath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temp = path + ".tmp";
                File.WriteAllText(temp, JsonConvert.SerializeObject(file, Formatting.Indented));
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch (Exception ex)
            {
                DebugLog.Log($"{LogPrefix} Saving the pending map cache failed: {ex.Message}");
            }
        }

        /// <summary>
        /// After a song list (re)load: downloads the cached maps whose song is now installed.
        /// Only file downloads, no list request. Skipped with Auto Download off. If a full sync is
        /// running, it runs once that one is done (the sync may have started before the new song).
        /// </summary>
        public static void CheckPending(string reason)
        {
            if (!Config.AutoDownloadCommunityMaps) return;
            if (Running)
            {
                // Runs when the current sync / pending download ends: it may have read the song
                // list before the new song was in it.
                pendingCheckQueued = true;
                return;
            }

            List<CommunityMapApiEntry> ready = ReadyPending();
            if (ready.Count == 0) return;

            DebugLog.Log($"{LogPrefix} {ready.Count} pending map(s) now have their song ({reason}).");
            MelonCoroutines.Start(DownloadPendingCoroutine(ready));
        }

        private static void RunQueuedPendingCheck()
        {
            if (!pendingCheckQueued) return;
            pendingCheckQueued = false;
            CheckPending("queued during a sync");
        }

        private static List<CommunityMapApiEntry> ReadyPending()
        {
            var ready = new List<CommunityMapApiEntry>();
            List<PendingMap> list = GetPending();
            if (list.Count == 0) return ready;

            HashSet<string> installed = InstalledSongIDs();
            if (installed == null) return ready;

            for (int i = 0; i < list.Count; i++)
                if (installed.Contains(list[i].parentSongId)) ready.Add(list[i].ToEntry());
            return ready;
        }

        private static IEnumerator DownloadPendingCoroutine(List<CommunityMapApiEntry> ready)
        {
            Running = true;
            int downloaded = 0, failed = 0;
            var done = new HashSet<string>(StringComparer.Ordinal); // fileIds to drop from the cache

            string dir = GetMapDir();
            bool dirOk = !string.IsNullOrEmpty(dir);
            if (dirOk)
            {
                try { Directory.CreateDirectory(dir); }
                catch (Exception ex) { DebugLog.Log($"{LogPrefix} Can't create '{dir}': {ex.Message}"); dirOk = false; }
            }

            if (dirOk)
            {
                HashSet<string> localNames, localShas;
                IndexLocalFolder(dir, out localNames, out localShas);

                for (int i = 0; i < ready.Count; i++)
                {
                    CommunityMapApiEntry m = ready[i];

                    // Same checks as the full sync. Already there (same bytes) or name taken by
                    // another file: nothing to fetch, drop it from the cache.
                    if (!IsSafeFileName(m.fileName) || m.sizeBytes > MaxFileBytes ||
                        (!string.IsNullOrEmpty(m.sha256) && localShas.Contains(m.sha256)) ||
                        localNames.Contains(m.fileName))
                    {
                        done.Add(m.fileId);
                        continue;
                    }

                    string url = $"{ExScoring.CommunityMapApiBase}/api/community-maps/{Uri.EscapeDataString(m.fileId)}/download";
                    var result = new HttpResult();
                    yield return Send(UnityWebRequest.Get(url), DownloadTimeoutSeconds, result, true);

                    if (result.code == 404)
                    {
                        DebugLog.Log($"{LogPrefix} Pending map '{m.fileName}' is gone from the server.");
                        done.Add(m.fileId);
                        continue;
                    }
                    if (!result.ok || result.bytes == null || result.bytes.Length == 0)
                    {
                        DebugLog.Log($"{LogPrefix} Pending download of '{m.fileName}' failed ({result.code}): {result.error}");
                        failed++; // stays cached, tried again on the next reload
                        continue;
                    }

                    if (SaveDownloaded(dir, m, result.bytes))
                    {
                        downloaded++;
                        localNames.Add(m.fileName);
                        if (!string.IsNullOrEmpty(m.sha256)) localShas.Add(m.sha256);
                        AddRemote(m);
                    }
                    else failed++;
                    done.Add(m.fileId); // saved, or refused for good (hash mismatch / name appeared)
                }
            }

            if (done.Count > 0)
            {
                GetPending().RemoveAll(p => done.Contains(p.fileId));
                SavePending();
            }

            Running = false;
            DebugLog.Log($"{LogPrefix} Pending maps: {downloaded} downloaded, {failed} failed, {GetPending().Count} still cached.");

            if (downloaded > 0)
            {
                PlaylistUtil.Popup($"Downloaded {downloaded} community map{(downloaded == 1 ? "" : "s")}");
                RebuildViewsIfLoaded();
            }
            RaiseRemoteChanged();
            RunQueuedPendingCheck(); // a reload during these downloads
        }

        private static bool SaveDownloaded(string dir, CommunityMapApiEntry m, byte[] bytes)
        {
            try
            {
                string sha = Sha256Hex(bytes);
                if (!string.IsNullOrEmpty(m.sha256) && !string.Equals(sha, m.sha256, StringComparison.OrdinalIgnoreCase))
                {
                    DebugLog.Log($"{LogPrefix} '{m.fileName}': SHA-256 mismatch ({sha} vs {m.sha256}), not saved.");
                    return false;
                }

                string target = Path.Combine(dir, m.fileName);
                if (File.Exists(target))
                {
                    DebugLog.Log($"{LogPrefix} '{m.fileName}' appeared meanwhile, not overwritten.");
                    return false;
                }

                // Write next to it under a name the game ignores, then move into place, so a crash
                // mid-write never leaves a broken map.
                string temp = target + ".download";
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, target);

                var fi = new FileInfo(target);
                localHashes[target] = new LocalHash { size = fi.Length, ticks = fi.LastWriteTimeUtc.Ticks, sha = sha };

                DebugLog.Log($"{LogPrefix} Saved '{m.fileName}' ({bytes.Length} bytes) for {m.parentSongId}.");
                return true;
            }
            catch (Exception ex)
            {
                DebugLog.Log($"{LogPrefix} Saving '{m.fileName}' failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>Rebuilds the folders (and so the Community Maps folder) once the song list is up.</summary>
        private static void RebuildViewsIfLoaded()
        {
            try
            {
                if (SongList.I == null || !SongList.OnSongListLoaded.mDone) return; // the load's own rebuild picks them up
                SongFolderManager.Rebuild(ExScoring.mainSongDirectory);
                if (SongFolderManager.searchFolderName != null) SongSearch.Search();
            }
            catch (Exception ex)
            {
                DebugLog.Log($"{LogPrefix} Rebuilding the folders failed: {ex.Message}");
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  "Is this map on the website?"
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// True / false when it's known whether the server has a file with this SHA-256 for this
        /// parent, null while unknown (call <see cref="EnsureParentKnown"/>).
        /// </summary>
        public static bool? IsOnServer(string parentSongId, string sha256)
        {
            if (string.IsNullOrEmpty(parentSongId) || string.IsNullOrEmpty(sha256)) return null;
            if (remoteShas.TryGetValue(parentSongId, out HashSet<string> set) && set.Contains(sha256)) return true;
            if (fullListKnown || parentsFetched.Contains(parentSongId)) return false;
            return null;
        }

        /// <summary>Fetches the server's maps for one parent, unless that's already known or in flight.</summary>
        public static void EnsureParentKnown(string parentSongId)
        {
            if (string.IsNullOrEmpty(parentSongId)) return;
            if (fullListKnown || parentsFetched.Contains(parentSongId) || parentsFetching.Contains(parentSongId)) return;
            parentsFetching.Add(parentSongId);
            MelonCoroutines.Start(FetchParentCoroutine(parentSongId));
        }

        private static IEnumerator FetchParentCoroutine(string parentSongId)
        {
            var shas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool ok = false;
            int offset = 0;

            for (int page = 0; page < MaxPages; page++)
            {
                string url = $"{ExScoring.CommunityMapApiBase}/api/community-maps?parentSongId={Uri.EscapeDataString(parentSongId)}&limit={PageLimit}&offset={offset}";
                var result = new HttpResult();
                yield return Send(UnityWebRequest.Get(url), ListTimeoutSeconds, result, false);
                if (!result.ok)
                {
                    DebugLog.Log($"{LogPrefix} List for {parentSongId} failed ({result.code}): {result.error}");
                    break;
                }

                CommunityMapListResponse resp = null;
                try { resp = JsonConvert.DeserializeObject<CommunityMapListResponse>(result.text); }
                catch (Exception ex) { DebugLog.Log($"{LogPrefix} List parse failed: {ex.Message}"); }
                if (resp == null || resp.maps == null) break;

                for (int i = 0; i < resp.maps.Length; i++)
                    if (!string.IsNullOrEmpty(resp.maps[i]?.sha256)) shas.Add(resp.maps[i].sha256);

                offset += resp.maps.Length;
                if (resp.maps.Length == 0 || offset >= resp.total) { ok = true; break; }
            }

            parentsFetching.Remove(parentSongId);
            if (ok)
            {
                if (remoteShas.TryGetValue(parentSongId, out HashSet<string> existing)) existing.UnionWith(shas);
                else remoteShas[parentSongId] = shas;
                parentsFetched.Add(parentSongId);
            }
            RaiseRemoteChanged();
        }

        private static void SetRemoteFull(List<CommunityMapApiEntry> all)
        {
            remoteShas.Clear();
            for (int i = 0; i < all.Count; i++)
                AddRemote(all[i]);
            fullListKnown = true;
        }

        private static void AddRemote(CommunityMapApiEntry m)
        {
            if (m == null || string.IsNullOrEmpty(m.parentSongId) || string.IsNullOrEmpty(m.sha256)) return;
            if (!remoteShas.TryGetValue(m.parentSongId, out HashSet<string> set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                remoteShas[m.parentSongId] = set;
            }
            set.Add(m.sha256);
        }

        private static void RaiseRemoteChanged()
        {
            try { RemoteStateChanged?.Invoke(); }
            catch (Exception ex) { DebugLog.Log($"{LogPrefix} RemoteStateChanged handler threw: {ex.Message}"); }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Upload (Section 21.3)
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>Outcome of an upload, for the button label.</summary>
        public sealed class UploadResult
        {
            public bool ok;          // stored now, or already there (duplicate)
            public bool duplicate;
            public long code;
            public string error;     // server error code (BAD_FILE, RATE_LIMITED, ...) or null
            public string expectedPrefix;
        }

        /// <summary>
        /// Uploads a map file with the player's ApiKey. Calls <paramref name="onDone"/> exactly once.
        /// </summary>
        public static void Upload(CommunityMapManager.Entry e, Action<UploadResult> onDone)
        {
            MelonCoroutines.Start(UploadCoroutine(e, onDone));
        }

        private static IEnumerator UploadCoroutine(CommunityMapManager.Entry e, Action<UploadResult> onDone)
        {
            var outcome = new UploadResult();

            byte[] bytes = null;
            string fileName = null, sha = null, leaderboardId = null;
            try
            {
                fileName = Path.GetFileName(e.path);
                bytes = File.ReadAllBytes(e.path);
                sha = Sha256Hex(bytes);
                leaderboardId = CommunityMapManager.GetLeaderboardId(e); // null = leave it out
            }
            catch (Exception ex)
            {
                DebugLog.Log($"{LogPrefix} Can't read '{e.path}' for upload: {ex.Message}");
            }

            if (bytes == null || bytes.Length == 0) { outcome.error = "EMPTY_FILE"; onDone?.Invoke(outcome); yield break; }
            if (bytes.Length > MaxFileBytes) { outcome.error = "TOO_LARGE"; onDone?.Invoke(outcome); yield break; }
            if (string.IsNullOrEmpty(Config.ApiKey)) { outcome.error = "NO_API_KEY"; onDone?.Invoke(outcome); yield break; }

            string url = $"{ExScoring.CommunityMapApiBase}/api/community-maps" +
                         $"?fileName={Uri.EscapeDataString(fileName)}" +
                         $"&parentSongId={Uri.EscapeDataString(e.parentSongID)}" +
                         (string.IsNullOrEmpty(leaderboardId) ? "" : $"&leaderboardId={Uri.EscapeDataString(leaderboardId)}");

            DebugLog.Log($"{LogPrefix} Uploading '{fileName}' ({bytes.Length} bytes) for {e.parentSongID}, leaderboardId={leaderboardId ?? "none"}.");

            var request = new UnityWebRequest(url, "POST");
            request.uploadHandler = new UploadHandlerRaw(bytes);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/octet-stream");
            request.SetRequestHeader("Authorization", "ApiKey " + Config.ApiKey);

            var result = new HttpResult();
            yield return Send(request, UploadTimeoutSeconds, result, false);

            outcome.code = result.code;
            if (result.ok)
            {
                CommunityMapUploadResponse resp = null;
                try { resp = JsonConvert.DeserializeObject<CommunityMapUploadResponse>(result.text); }
                catch (Exception ex) { DebugLog.Log($"{LogPrefix} Upload reply parse failed: {ex.Message}"); }

                outcome.ok = true;
                outcome.duplicate = resp != null && resp.duplicate;
                if (resp?.map != null) AddRemote(resp.map);
                else AddRemote(new CommunityMapApiEntry { parentSongId = e.parentSongID, sha256 = sha });

                DebugLog.Log($"{LogPrefix} Upload ok ({result.code}, duplicate={outcome.duplicate}, fileId={resp?.map?.fileId ?? "?"}).");
            }
            else
            {
                CommunityMapErrorResponse err = null;
                try { if (!string.IsNullOrEmpty(result.text)) err = JsonConvert.DeserializeObject<CommunityMapErrorResponse>(result.text); }
                catch { }
                outcome.error = err?.error ?? (result.code == 0 ? "NETWORK" : "HTTP_" + result.code);
                outcome.expectedPrefix = err?.expectedPrefix;
                DebugLog.Log($"{LogPrefix} Upload failed ({result.code}): {outcome.error} {err?.message} {outcome.expectedPrefix} | {result.error}");
            }

            onDone?.Invoke(outcome);
            RaiseRemoteChanged();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Helpers
        // ══════════════════════════════════════════════════════════════════════

        private sealed class HttpResult
        {
            public bool ok;
            public long code;
            public string error;
            public string text;
            public byte[] bytes;
        }

        /// <summary>
        /// Sends a request and fills <paramref name="result"/>, then disposes it. Polls isDone
        /// rather than yielding the async operation, so it works the same wherever it's driven from.
        /// </summary>
        private static IEnumerator Send(UnityWebRequest request, int timeoutSeconds, HttpResult result, bool wantBytes)
        {
            try
            {
                request.timeout = timeoutSeconds;
                var op = request.SendWebRequest();
                while (!op.isDone) yield return null;

                result.code = request.responseCode;
                result.error = request.error;
                result.ok = !request.isNetworkError && !request.isHttpError;

                try
                {
                    if (request.downloadHandler != null)
                    {
                        if (wantBytes && result.ok) result.bytes = CopyBytes(request.downloadHandler.data);
                        else result.text = request.downloadHandler.text;
                    }
                }
                catch (Exception ex)
                {
                    DebugLog.Log($"{LogPrefix} Reading the reply failed: {ex.Message}");
                    if (wantBytes) result.ok = false;
                }
            }
            finally
            {
                request.Dispose();
            }
        }

        // The interop property may be an Il2Cpp array: copy it into a managed one.
        private static byte[] CopyBytes(UnhollowerBaseLib.Il2CppStructArray<byte> data)
        {
            if (data == null) return null;
            int n = data.Length;
            var bytes = new byte[n];
            for (int i = 0; i < n; i++) bytes[i] = data[i];
            return bytes;
        }

        public static string Sha256Hex(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                var sb = new StringBuilder(64);
                foreach (byte b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        /// <summary>SHA-256 of a local file, cached while its size and write time don't change. Null on error.</summary>
        public static string LocalSha(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                if (!fi.Exists) return null;
                long ticks = fi.LastWriteTimeUtc.Ticks;
                if (localHashes.TryGetValue(path, out LocalHash h) && h.size == fi.Length && h.ticks == ticks) return h.sha;
                if (fi.Length > 4 * MaxFileBytes) return null; // not something we'd upload or compare

                string sha = Sha256Hex(File.ReadAllBytes(path));
                localHashes[path] = new LocalHash { size = fi.Length, ticks = ticks, sha = sha };
                return sha;
            }
            catch (Exception ex)
            {
                DebugLog.Log($"{LogPrefix} Hashing '{path}' failed: {ex.Message}");
                return null;
            }
        }

        private static void IndexLocalFolder(string dir, out HashSet<string> names, out HashSet<string> shas)
        {
            names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            shas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string file in Directory.GetFiles(dir))
                {
                    names.Add(Path.GetFileName(file));
                    if (!file.EndsWith(".mid", StringComparison.Ordinal) && !file.EndsWith(".cues", StringComparison.Ordinal)) continue;
                    string sha = LocalSha(file);
                    if (sha != null) shas.Add(sha);
                }
            }
            catch (Exception ex)
            {
                DebugLog.Log($"{LogPrefix} Listing '{dir}' failed: {ex.Message}");
            }
        }

        /// <summary>Installed (non-hidden) songIDs, or null before the song list exists.</summary>
        private static HashSet<string> InstalledSongIDs()
        {
            try
            {
                if (SongList.I == null || SongList.I.songs == null) return null;
                var set = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < SongList.I.songs.Count; i++)
                {
                    var sd = SongList.I.songs[i];
                    if (sd == null || sd.hidden || string.IsNullOrEmpty(sd.songID)) continue;
                    set.Add(sd.songID);
                }
                return set;
            }
            catch (Exception ex)
            {
                DebugLog.Log($"{LogPrefix} Reading the song list failed: {ex.Message}");
                return null;
            }
        }

        private static string GetMapDir()
        {
            try { return RecorderMidiOutput.GetCommunityMapDir(false); }
            catch (Exception ex)
            {
                DebugLog.Log($"{LogPrefix} GetCommunityMapDir failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// A plain file name the game would pick up (lowercase .mid / .cues) and that can't leave
        /// the map folder. The server enforces the same rules, this only guards against surprises.
        /// </summary>
        private static bool IsSafeFileName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.Length > 255) return false;
            if (!name.EndsWith(".mid", StringComparison.Ordinal) && !name.EndsWith(".cues", StringComparison.Ordinal)) return false;
            if (name != Path.GetFileName(name) || name.StartsWith(".", StringComparison.Ordinal)) return false;
            if (name.Trim() != name) return false;
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            if (name.IndexOfAny(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }) >= 0) return false;
            if (name.IndexOf('_') <= 0) return false; // "<song>_<map name>"
            return true;
        }
    }

    /// <summary>
    /// "Upload Map" on the launch panel: a copy of the Expert difficulty button, shown where the
    /// difficulty buttons were (they're hidden while a map is selected) when the selected map isn't
    /// on the AudicaEX server yet and an API key is set. Hidden again once the server has it.
    /// Updated every frame from OnLateUpdate (cheap: cached objects, no Find after setup).
    /// </summary>
    internal static class CommunityMapUploadButton
    {
        private const string CenterPath = "menu/ShellPage_Launch/page/ShellPanel_Center";
        private const string SourceName = "ChooseDiff_expert";
        // Centered under the four difficulty buttons (-3.5/3.5, -7.25/-9.5, see SongListUI).
        private static readonly Vector3 ButtonPosition = new Vector3(0f, -8.375f, 0f);

        private enum State { Idle, Uploading, Done, Failed }

        private static GameObject button;
        private static TextMeshPro label;
        private static GunButton gunButton;
        private static Transform center;
        private static Transform playButton;
        private static float nextFindTime;

        private static string currentPath;    // map the state below is for
        private static long currentTicks;
        private static string currentSha;
        private static State state = State.Idle;
        private static string failText;
        private static bool? lastInteractable;

        public static void Tick()
        {
            try { TickInternal(); }
            catch (Exception ex)
            {
                DebugLog.Log("[CommunityMapUpload] Tick failed: " + ex.Message);
                if (button != null) button.SetActive(false);
            }
        }

        private static void TickInternal()
        {
            CommunityMapManager.Entry e = CommunityMapManager.IsActive ? CommunityMapManager.Selected : null;

            if (e == null)
            {
                if (button != null && button.activeSelf) button.SetActive(false);
                return;
            }

            if (!EnsureButton()) return;

            // New selection (or the file changed): start over.
            if (e.path != currentPath || e.writeTicks != currentTicks)
            {
                currentPath = e.path;
                currentTicks = e.writeTicks;
                currentSha = CommunityMapSync.LocalSha(e.path);
                state = State.Idle;
                failText = null;
                CommunityMapSync.EnsureParentKnown(e.parentSongID);
            }

            bool show = ShouldShow(e);
            if (button.activeSelf != show) button.SetActive(show);
            if (!show) return;

            string text;
            bool interactable;
            switch (state)
            {
                case State.Uploading: text = "Uploading..."; interactable = false; break;
                case State.Failed: text = failText ?? "Upload failed"; interactable = true; break;
                default: text = "Upload Map"; interactable = true; break;
            }
            if (label != null && label.text != text) label.text = text;
            if (gunButton != null && interactable != lastInteractable)
            {
                gunButton.SetInteractable(interactable);
                lastInteractable = interactable;
            }
        }

        private static bool ShouldShow(CommunityMapManager.Entry e)
        {
            if (MarathonSetup.Active) return false;
            if (string.IsNullOrEmpty(Config.ApiKey)) return false;
            if (playButton == null || !playButton.gameObject.activeSelf) return false; // panel is blank
            if (currentSha == null) return false;
            if (state == State.Done) return false;
            if (state == State.Uploading || state == State.Failed) return true;

            bool? onServer = CommunityMapSync.IsOnServer(e.parentSongID, currentSha);
            return onServer == false; // unknown: wait for the list
        }

        private static void OnShot()
        {
            CommunityMapManager.Entry e = CommunityMapManager.Selected;
            if (e == null || state == State.Uploading) return;

            state = State.Uploading;
            string path = e.path;
            CommunityMapSync.Upload(e, result =>
            {
                if (currentPath != path) return; // another map was selected meanwhile

                if (result.ok)
                {
                    state = State.Done;
                    PlaylistUtil.Popup(result.duplicate ? "This map was already on AudicaEX" : "Map uploaded to AudicaEX");
                    return;
                }

                state = State.Failed;
                failText = FailLabel(result);
                PlaylistUtil.Popup(FailPopup(result));
            });
        }

        private static string FailLabel(CommunityMapSync.UploadResult r)
        {
            switch (r.error)
            {
                case "RATE_LIMITED": return "Try later";
                case "SUBMISSIONS_BLOCKED": return "Blocked";
                default: return "Retry upload";
            }
        }

        private static string FailPopup(CommunityMapSync.UploadResult r)
        {
            switch (r.error)
            {
                case "TOO_LARGE": return "Map file is over 5 MB";
                case "EMPTY_FILE": return "Map file is empty";
                case "BAD_FILE": return "The server couldn't read this map file";
                case "BAD_FILE_NAME": return "The map's file name isn't accepted";
                case "FILE_NAME_MISMATCH": return "File name must start with " + (r.expectedPrefix ?? "the song ID");
                case "PARENT_NOT_FOUND": return "The server doesn't know this song yet - play it once first";
                case "BAD_LEADERBOARD_ID": return "Leaderboard ID rejected";
                case "SUBMISSIONS_BLOCKED": return "Your uploads are blocked";
                case "RATE_LIMITED": return "Too many uploads, try again later";
                case "NO_API_KEY": return "Set an API key to upload maps";
                case "NETWORK": return "Couldn't reach AudicaEX";
                default: return "Upload failed (" + (r.error ?? r.code.ToString()) + ")";
            }
        }

        private static bool EnsureButton()
        {
            if (button != null) return true;
            if (Time.realtimeSinceStartup < nextFindTime) return false;
            nextFindTime = Time.realtimeSinceStartup + 1f;

            GameObject centerGo = GameObject.Find(CenterPath);
            if (centerGo == null) return false;
            center = centerGo.transform;
            playButton = center.Find("play");

            Transform source = center.Find(SourceName);
            if (source == null) return false;

            button = GameObject.Instantiate(source.gameObject, center);
            button.SetActive(false);
            button.name = "ExCommunityMapUpload";
            button.transform.localPosition = ButtonPosition;

            // Drop what makes it a difficulty button: the selection indicator and the localizer.
            // The shot handler is replaced below (same as the Favorite button's clone).
            Transform indicator = button.transform.Find("SelectedIndicator");
            if (indicator != null) GameObject.Destroy(indicator.gameObject);
            Localizer localizer = button.GetComponentInChildren<Localizer>(true);
            if (localizer != null) GameObject.Destroy(localizer);

            label = button.GetComponentInChildren<TextMeshPro>(true);
            if (label != null) { label.text = "Upload Map"; label.alpha = 1f; }

            gunButton = button.GetComponentInChildren<GunButton>(true);
            if (gunButton != null)
            {
                gunButton.destroyOnShot = false;
                gunButton.disableOnShot = false;
                gunButton.doMeshExplosion = false;
                gunButton.doParticles = false;
                gunButton.onHitEvent = new UnityEvent();
                gunButton.onHitEvent.AddListener(new Action(OnShot));
            }
            lastInteractable = null;

            DebugLog.Log("[CommunityMapUpload] Upload button created.");
            return true;
        }
    }
}