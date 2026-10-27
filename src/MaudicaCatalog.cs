using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using MelonLoader;
using MelonLoader.TinyJSON;
using Newtonsoft.Json;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>One maudica.com map as stored in the catalog cache.</summary>
    [Serializable]
    public class CatalogMap
    {
        public int id;
        public string title;
        public string artist;
        public string author;
        public string filename;
        public string embed_url;
        public string created_at;
        public string updated_at;
        public string[] difficulties;

        // ── Derived (rebuilt after load / every change, never saved) ──
        [JsonIgnore] public string stem;          // filename without ".audica"
        [JsonIgnore] public string normStem;      // stem, lowercase letters/digits only
        [JsonIgnore] public string searchText;    // lowercase title|artist|author|stem (apostrophes removed)
        [JsonIgnore] public string searchTextNoSpaces;
        [JsonIgnore] private Song song;

        /// <summary>This map as the Song model the rest of the mod uses (cached).</summary>
        public Song ToSong()
        {
            if (song != null) return song;

            var s = new Song
            {
                title = title,
                artist = artist,
                author = author,
                filename = filename,
                song_id = stem,
                download_url = string.Format(SongDownloader.downloadUrlFormat, id),
                preview_url = string.Format(SongDownloader.previewUrlFormat, id),
                upload_time = created_at,
                update_time = updated_at,
                video_url = embed_url
            };
            if (difficulties != null)
            {
                foreach (string d in difficulties)
                {
                    if (d == "beginner") s.beginner = true;
                    else if (d == "moderate") s.standard = true;
                    else if (d == "advanced") s.advanced = true;
                    else if (d == "expert") s.expert = true;
                }
            }
            song = s;
            return s;
        }

        internal static CatalogMap From(SongV2 m)
        {
            return new CatalogMap
            {
                id = m.id,
                title = m.title,
                artist = m.artist,
                author = m.author,
                filename = m.filename,
                embed_url = m.embed_url,
                created_at = m.created_at,
                updated_at = m.updated_at,
                difficulties = m.difficulties
            };
        }
    }

    [Serializable]
    public class CatalogFile
    {
        public int version = 1;
        public long lastFullSyncUtcTicks;
        public int countOffset;  // maudica's total minus maps kept, at the last full sync (maps without a filename are skipped)
        public List<CatalogMap> maps = new List<CatalogMap>();
    }

    /// <summary>
    /// A local copy of the whole maudica.com catalog (UserData/ExScoring_MaudicaCatalog.json), so
    /// every maudica feature (playlist downloads, song search, Latest Maudica, song requests, lobby
    /// lookups) answers from memory instead of searching the site.
    ///
    /// Launch (Initialize, once the startup logo is done):
    ///  - The cache file is read on a worker thread. With no cache, the whole catalog is fetched.
    ///  - Otherwise, new uploads: newest-first pages until one contains a map we already have
    ///    (usually a single request). The number of new maps is shown on the song page panel.
    ///  - Edits: pages sorted by updated_at until a map is unchanged.
    ///  - If maudica's total then disagrees with the cache (deletions / anything missed; maps
    ///    without a filename are skipped, which countOffset accounts for), or the last full sync is
    ///    over FullResyncDays old, the whole catalog is fetched again in the background.
    ///
    /// On demand (RequestRefresh): the same new/edited check, at most once per RefreshThrottleSeconds
    /// unless forced. Used when something isn't found, so a song uploaded mid-session turns up.
    ///
    /// Everything runs on the main thread (coroutines) except reading/writing the file. A failed sync
    /// keeps the previous cache; IsReady stays true as long as there is one.
    /// </summary>
    internal static class MaudicaCatalog
    {
        private const string ListUrl = "https://maudica.com/api/maps?per_page={0}&page={1}";
        private const int PageSize = 100;
        private const int MaxNewPages = 30;
        private const int MaxUpdatedPages = 10;
        private const int MaxFullPages = 500;
        private const int RequestAttempts = 3;
        private const float RequestTimeoutSeconds = 30f;
        private const float RefreshThrottleSeconds = 60f;
        private const double FullResyncDays = 7;

        private static readonly string CachePath =
            Path.Combine(Application.dataPath, "..", "UserData", "ExScoring_MaudicaCatalog.json");

        // ── Data ──────────────────────────────────────────────────────────────
        private static Dictionary<int, CatalogMap> byId = new Dictionary<int, CatalogMap>();
        private static Dictionary<string, CatalogMap> byStem = new Dictionary<string, CatalogMap>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, List<CatalogMap>> byNormStem = new Dictionary<string, List<CatalogMap>>();
        private static List<CatalogMap> newestFirst = new List<CatalogMap>();
        private static long lastFullSyncUtcTicks = 0;
        private static int countOffset = 0;

        // ── State ─────────────────────────────────────────────────────────────
        private static bool initialized = false;

        /// <summary>True once the catalog has data (from the cache file or a sync).</summary>
        public static bool IsReady { get; private set; }

        /// <summary>True while any sync is running.</summary>
        public static bool IsSyncing { get; private set; }

        /// <summary>True when the most recent sync failed (the previous data is still used).</summary>
        public static bool LastSyncFailed { get; private set; }

        /// <summary>Bumped on every change, so callers can tell when cached answers went stale.</summary>
        public static int Version { get; private set; }

        public static int Count => newestFirst.Count;

        /// <summary>Every map, newest upload first. Don't modify.</summary>
        public static IReadOnlyList<CatalogMap> NewestFirst => newestFirst;

        // Launch check result, for the song page panel.
        public static bool LaunchCheckDone { get; private set; }
        public static bool LaunchCheckFailed { get; private set; }
        public static bool LaunchWasFirstSync { get; private set; }
        public static int LaunchNewMaps { get; private set; }

        /// <summary>Raised on the main thread after the catalog content changed.</summary>
        public static event Action Changed;

        private static float lastRefreshAt = -1000f;
        private static readonly List<Action> refreshWaiters = new List<Action>();

        // ── Startup ───────────────────────────────────────────────────────────

        /// <summary>Loads the cache and runs the launch check. Safe to call more than once.</summary>
        public static void Initialize()
        {
            if (initialized) return;
            initialized = true;
            IsSyncing = true; // before the coroutine runs, so an early RequestRefresh waits for it
            MelonCoroutines.Start(InitializeCo());
        }

        private static IEnumerator InitializeCo()
        {
            IsSyncing = true;

            // Read + parse the cache file off the main thread.
            CatalogFile loaded = null;
            bool loadDone = false;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (File.Exists(CachePath))
                        loaded = JsonConvert.DeserializeObject<CatalogFile>(File.ReadAllText(CachePath));
                }
                catch (Exception ex)
                {
                    MelonLogger.Log("[MaudicaCatalog] cache file could not be read: " + ex.Message);
                    loaded = null;
                }
                loadDone = true;
            });
            while (!loadDone) yield return null;

            if (loaded != null && loaded.maps != null && loaded.maps.Count > 0)
            {
                ReplaceAll(loaded.maps);
                lastFullSyncUtcTicks = loaded.lastFullSyncUtcTicks;
                countOffset = loaded.countOffset;
                DebugLog.Log($"[MaudicaCatalog] loaded {Count} maps from cache");
                RaiseChanged();
            }

            if (!IsReady)
            {
                // First run (or unreadable cache): fetch everything.
                LaunchWasFirstSync = true;
                bool ok = false;
                yield return FullSyncCo(r => ok = r);
                LaunchCheckFailed = !ok;
                LaunchNewMaps = 0;
                LaunchCheckDone = true;
                lastRefreshAt = Time.unscaledTime;
                FinishSync(!ok);
                yield break;
            }

            // Cache present: look for new uploads and edits.
            int newMaps = 0;
            int remoteCount = -1;
            bool okNew = false;
            yield return CheckNewCo((n, total, ok) => { newMaps = n; remoteCount = total; okNew = ok; });

            bool okUpdated = false;
            if (okNew)
                yield return CheckUpdatedCo((total, ok) => { okUpdated = ok; if (total >= 0) remoteCount = total; });

            LaunchNewMaps = newMaps;
            LaunchCheckFailed = !okNew;
            LaunchCheckDone = true;
            lastRefreshAt = Time.unscaledTime;
            DebugLog.Log($"[MaudicaCatalog] launch check: {(okNew ? newMaps + " new" : "failed")}, maudica total {remoteCount}, cached {Count}");

            if (okNew && newMaps > 0 || okUpdated) SaveAsync();
            RaiseChanged();

            // Background full resync when the totals disagree or the last one is old.
            bool stale = (DateTime.UtcNow - new DateTime(lastFullSyncUtcTicks, DateTimeKind.Utc)).TotalDays > FullResyncDays;
            bool mismatch = remoteCount >= 0 && remoteCount - Count != countOffset;
            if (okNew && (mismatch || stale))
            {
                DebugLog.Log($"[MaudicaCatalog] full resync ({(mismatch ? "count mismatch" : "weekly")})");
                bool okFull = false;
                yield return FullSyncCo(r => okFull = r);
                FinishSync(!okFull);
            }
            else
            {
                FinishSync(!okNew);
            }
        }

        // ── On-demand refresh ─────────────────────────────────────────────────

        /// <summary>
        /// Checks maudica for new and edited maps. Throttled to once per RefreshThrottleSeconds
        /// unless <paramref name="force"/>; a throttled call returns without syncing (onDone is
        /// still called). While a sync is running, onDone waits for it.
        /// </summary>
        public static void RequestRefresh(Action onDone = null, bool force = false)
        {
            Initialize();

            if (IsSyncing)
            {
                if (onDone != null) refreshWaiters.Add(onDone);
                return;
            }
            if (!force && Time.unscaledTime - lastRefreshAt < RefreshThrottleSeconds)
            {
                onDone?.Invoke();
                return;
            }

            if (onDone != null) refreshWaiters.Add(onDone);
            MelonCoroutines.Start(RefreshCo());
        }

        /// <summary>True when RequestRefresh would actually sync now (not throttled, not running).</summary>
        public static bool CanRefreshNow => !IsSyncing && Time.unscaledTime - lastRefreshAt >= RefreshThrottleSeconds;

        private static IEnumerator RefreshCo()
        {
            IsSyncing = true;
            lastRefreshAt = Time.unscaledTime;
            RefreshViews(); // show "Searching..." states

            bool ok;
            if (!IsReady)
            {
                bool okFull = false;
                yield return FullSyncCo(r => okFull = r);
                ok = okFull;
            }
            else
            {
                bool okNew = false;
                int newMaps = 0;
                yield return CheckNewCo((n, total, r) => { okNew = r; newMaps = n; });
                bool okUpdated = false;
                if (okNew) yield return CheckUpdatedCo((total, r) => okUpdated = r);
                ok = okNew;
                if (newMaps > 0 || okUpdated) SaveAsync();
                DebugLog.Log($"[MaudicaCatalog] refresh: {(okNew ? newMaps + " new" : "failed")}");
            }
            FinishSync(!ok);
        }

        private static void FinishSync(bool failed)
        {
            IsSyncing = false;
            LastSyncFailed = failed;

            var waiters = new List<Action>(refreshWaiters);
            refreshWaiters.Clear();
            foreach (Action w in waiters)
            {
                try { w(); }
                catch (Exception ex) { MelonLogger.Log("[MaudicaCatalog] refresh callback threw: " + ex); }
            }

            RefreshViews();
        }

        // ── Sync steps ────────────────────────────────────────────────────────

        /// <summary>Newest-first pages until one contains a known map. Reports (new maps, maudica total, ok).</summary>
        private static IEnumerator CheckNewCo(Action<int, int, bool> onDone)
        {
            int added = 0;
            int total = -1;
            var fresh = new List<CatalogMap>();

            for (int page = 1; page <= MaxNewPages; page++)
            {
                NewAPISongList list = null;
                yield return FetchPageCo(string.Format(ListUrl, PageSize, page), r => list = r);
                if (list == null) { onDone(0, -1, false); yield break; }
                total = list.count;

                bool sawKnown = false;
                if (list.maps != null)
                {
                    foreach (SongV2 m in list.maps)
                    {
                        if (m == null || string.IsNullOrEmpty(m.filename)) continue;
                        if (byId.ContainsKey(m.id)) { sawKnown = true; continue; }
                        fresh.Add(CatalogMap.From(m));
                    }
                }

                if (sawKnown || list.maps == null || list.maps.Length == 0 || !list.has_more) break;
            }

            if (fresh.Count > 0)
            {
                var seen = new HashSet<int>();
                foreach (CatalogMap m in fresh)
                {
                    if (!seen.Add(m.id)) continue;
                    byId[m.id] = m;
                    added++;
                }
                RebuildIndexes();
                RaiseChanged();
            }
            onDone(added, total, true);
        }

        /// <summary>Pages sorted by updated_at until a map is unchanged. Reports (maudica total, anything changed).</summary>
        private static IEnumerator CheckUpdatedCo(Action<int, bool> onDone)
        {
            int total = -1;
            bool changed = false;

            for (int page = 1; page <= MaxUpdatedPages; page++)
            {
                NewAPISongList list = null;
                yield return FetchPageCo(string.Format(ListUrl, PageSize, page) + "&sort=updated_at", r => list = r);
                if (list == null) break; // keep what we have; edits are caught next time
                total = list.count;

                bool reachedUnchanged = false;
                if (list.maps != null)
                {
                    foreach (SongV2 m in list.maps)
                    {
                        if (m == null || string.IsNullOrEmpty(m.filename)) continue;
                        if (byId.TryGetValue(m.id, out CatalogMap existing) &&
                            existing.updated_at == m.updated_at && existing.filename == m.filename)
                        {
                            reachedUnchanged = true;
                            break;
                        }
                        byId[m.id] = CatalogMap.From(m);
                        changed = true;
                    }
                }

                if (reachedUnchanged || list.maps == null || list.maps.Length == 0 || !list.has_more) break;
            }

            if (changed)
            {
                RebuildIndexes();
                RaiseChanged();
            }
            onDone(total, changed);
        }

        /// <summary>The whole catalog, replacing the cache only if every page arrived.</summary>
        private static IEnumerator FullSyncCo(Action<bool> onDone)
        {
            var maps = new List<CatalogMap>();
            int expected = -1;

            for (int page = 1; page <= MaxFullPages; page++)
            {
                NewAPISongList list = null;
                yield return FetchPageCo(string.Format(ListUrl, PageSize, page), r => list = r);
                if (list == null)
                {
                    DebugLog.Log($"[MaudicaCatalog] full sync failed at page {page}");
                    onDone(false);
                    yield break;
                }
                if (expected < 0) expected = list.count;

                if (list.maps != null)
                {
                    foreach (SongV2 m in list.maps)
                    {
                        if (m != null && !string.IsNullOrEmpty(m.filename)) maps.Add(CatalogMap.From(m));
                    }
                }
                if (list.maps == null || list.maps.Length == 0 || !list.has_more) break;
            }

            ReplaceAll(maps);
            lastFullSyncUtcTicks = DateTime.UtcNow.Ticks;
            countOffset = expected >= 0 ? expected - Count : 0;
            DebugLog.Log($"[MaudicaCatalog] full sync: {Count} maps (maudica reported {expected})");
            SaveAsync();
            RaiseChanged();
            onDone(true);
        }

        /// <summary>One listing page, retried a few times. Hands back null on failure.</summary>
        private static IEnumerator FetchPageCo(string url, Action<NewAPISongList> onDone)
        {
            for (int attempt = 1; attempt <= RequestAttempts; attempt++)
            {
                WWW www = new WWW(url);
                float deadline = Time.unscaledTime + RequestTimeoutSeconds;
                while (!www.isDone && Time.unscaledTime < deadline) yield return null;

                NewAPISongList list = null;
                string error = null;
                if (!www.isDone) error = "timed out";
                else if (!string.IsNullOrEmpty(www.error)) error = www.error;
                else
                {
                    try { list = JSON.Load(www.text).Make<NewAPISongList>(); }
                    catch (Exception ex) { error = "unreadable response: " + ex.Message; }
                }

                if (list != null && error == null)
                {
                    onDone(list);
                    yield break;
                }

                DebugLog.Log($"[MaudicaCatalog] request failed ({error}), attempt {attempt}/{RequestAttempts}: {url}");
                if (attempt < RequestAttempts)
                {
                    float wait = Time.unscaledTime + 1.5f * attempt;
                    while (Time.unscaledTime < wait) yield return null;
                }
            }
            onDone(null);
        }

        // ── Indexes ───────────────────────────────────────────────────────────

        private static void ReplaceAll(List<CatalogMap> maps)
        {
            byId = new Dictionary<int, CatalogMap>();
            foreach (CatalogMap m in maps)
            {
                if (m == null || string.IsNullOrEmpty(m.filename)) continue;
                byId[m.id] = m;
            }
            RebuildIndexes();
        }

        private static void RebuildIndexes()
        {
            var list = new List<CatalogMap>(byId.Values);
            list.Sort((a, b) => b.id.CompareTo(a.id)); // ids grow with uploads: newest first

            var stems = new Dictionary<string, CatalogMap>(StringComparer.OrdinalIgnoreCase);
            var norms = new Dictionary<string, List<CatalogMap>>();

            foreach (CatalogMap m in list)
            {
                string fn = m.filename.Trim();
                m.stem = fn.EndsWith(".audica", StringComparison.OrdinalIgnoreCase) ? fn.Substring(0, fn.Length - 7) : fn;
                m.normStem = Normalize(m.stem);

                string text = CleanForSearch((m.title ?? "") + "|" + (m.artist ?? "") + "|" + (m.author ?? "") + "|" + m.stem);
                m.searchText = text;
                m.searchTextNoSpaces = text.Replace(" ", "");

                if (!stems.ContainsKey(m.stem)) stems[m.stem] = m; // newest wins
                if (m.normStem.Length > 0)
                {
                    if (!norms.TryGetValue(m.normStem, out List<CatalogMap> bucket))
                    {
                        bucket = new List<CatalogMap>();
                        norms[m.normStem] = bucket;
                    }
                    bucket.Add(m);
                }
            }

            newestFirst = list;
            byStem = stems;
            byNormStem = norms;
            IsReady = list.Count > 0;
            Version++;
        }

        // ── Lookups ───────────────────────────────────────────────────────────

        public static CatalogMap GetById(int id)
        {
            return byId.TryGetValue(id, out CatalogMap m) ? m : null;
        }

        public static CatalogMap GetById(string id)
        {
            return int.TryParse((id ?? "").Trim(), out int n) ? GetById(n) : null;
        }

        /// <summary>
        /// The map whose filename is stem + ".audica": exact (ignoring case), then the same letters
        /// and digits ignoring punctuation/spacing, then with a trailing copy suffix like " (1)"
        /// removed. The fuzzy steps only answer when exactly one map fits.
        /// </summary>
        public static CatalogMap FindByStem(string stem)
        {
            if (string.IsNullOrEmpty(stem)) return null;
            stem = stem.Trim();
            if (stem.EndsWith(".audica", StringComparison.OrdinalIgnoreCase)) stem = stem.Substring(0, stem.Length - 7);

            if (byStem.TryGetValue(stem, out CatalogMap exact)) return exact;

            CatalogMap unique = UniqueNorm(Normalize(stem));
            if (unique != null) return unique;

            string stripped = copySuffix.Replace(stem, "");
            if (stripped.Length > 0 && stripped != stem)
            {
                if (byStem.TryGetValue(stripped, out CatalogMap strippedExact)) return strippedExact;
                return UniqueNorm(Normalize(stripped));
            }
            return null;
        }

        private static readonly Regex copySuffix = new Regex(@"(\s*\(\d+\)|_\d+)$");

        private static CatalogMap UniqueNorm(string norm)
        {
            if (string.IsNullOrEmpty(norm)) return null;
            return byNormStem.TryGetValue(norm, out List<CatalogMap> bucket) && bucket.Count == 1 ? bucket[0] : null;
        }

        /// <summary>The normalised form FindByStem compares (lowercase letters and digits only).</summary>
        public static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Maps whose title, artist, mapper or filename contain the query (case-insensitive,
        /// apostrophes ignored, also with spaces removed), newest upload first.
        /// </summary>
        public static List<CatalogMap> Search(string query, int max = int.MaxValue)
        {
            var result = new List<CatalogMap>();
            string q = CleanForSearch(query ?? "").Trim();
            if (q.Length == 0) return result;
            string qNoSpaces = q.Replace(" ", "");

            foreach (CatalogMap m in newestFirst)
            {
                if (m.searchText.Contains(q) || (qNoSpaces.Length > 0 && m.searchTextNoSpaces.Contains(qNoSpaces)))
                {
                    result.Add(m);
                    if (result.Count >= max) break;
                }
            }
            return result;
        }

        /// <summary>Search, as Song models.</summary>
        public static List<Song> SearchSongs(string query, int max = int.MaxValue)
        {
            var songs = new List<Song>();
            foreach (CatalogMap m in Search(query, max)) songs.Add(m.ToSong());
            return songs;
        }

        private static string CleanForSearch(string s)
        {
            return (s ?? "").ToLowerInvariant().Replace("'", "");
        }

        // ── Saving / notifications ────────────────────────────────────────────

        private static void SaveAsync()
        {
            var file = new CatalogFile
            {
                lastFullSyncUtcTicks = lastFullSyncUtcTicks,
                countOffset = countOffset,
                maps = new List<CatalogMap>(newestFirst) // snapshot: maps are replaced, never edited
            };
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string dir = Path.GetDirectoryName(CachePath);
                    if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                    string tmp = CachePath + ".tmp";
                    File.WriteAllText(tmp, JsonConvert.SerializeObject(file));
                    if (File.Exists(CachePath)) File.Delete(CachePath);
                    File.Move(tmp, CachePath);
                }
                catch (Exception ex)
                {
                    MelonLogger.Log("[MaudicaCatalog] cache file could not be saved: " + ex.Message);
                }
            });
        }

        private static void RaiseChanged()
        {
            try { Changed?.Invoke(); }
            catch (Exception ex) { MelonLogger.Log("[MaudicaCatalog] Changed handler threw: " + ex); }
        }

        /// <summary>Redraws the song list if it's on screen (cheap no-op otherwise).</summary>
        private static void RefreshViews()
        {
            try { FolderRowManager.RefreshList(); }
            catch (Exception ex) { DebugLog.Log("[MaudicaCatalog] list refresh failed: " + ex.Message); }
        }
    }
}