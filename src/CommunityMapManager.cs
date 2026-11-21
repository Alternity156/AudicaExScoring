using System;
using System.Collections.Generic;
using System.IO;
using Harmony;
using MelonLoader;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// Community maps (alternate charts for an installed song, loose .mid / .cues files in the
    /// game's community_maps folder) listed in their own "Community Maps" folder.
    ///
    /// How the game handles them (see claude/Community-Maps-Findings.md): a community map is NOT
    /// its own SongData. CommunityMaps.I keeps one selected file path (mSelectedMapPath), and
    /// SongCues.GetCuesInternal loads that file instead of the song's own chart whenever the
    /// song's originalSongID matches the file name's song prefix (difficulty ignored; midi maps
    /// parse as Expert). So "playing a community map" = parent song selected + map path set.
    ///
    /// The mod owns that selection: while a map row is selected, CommunityMaps.Reset() is
    /// blocked (the game calls it from SongSelect.OnEnable, DifficultySelect.SelectDifficulty,
    /// the launch transition, ...), so the map survives launching and returning from gameplay.
    /// Selecting any normal song releases it.
    ///
    /// Leaderboard ID (OnlineLeaderboard.GetCurrentSongLeaderboardID):
    ///   parent songID + "_" + SongCues.CalculateChecksumForCues(parent, mapCues) + suffix
    /// The suffix is a game string literal; it is read once at runtime from the game's own
    /// function (the 32-char checksum part is skipped, so a stale SongCues.I can't affect it).
    /// The checksum itself is computed here from freshly loaded map cues, because the game's
    /// function reads SongCues.I.mCues, which in the menu can still hold the last played song.
    /// </summary>
    internal static class CommunityMapManager
    {
        /// <summary>View key prefix for map rows in VirtualSongList ("cm:" + full file path).</summary>
        public const string ViewKeyPrefix = "cm:";

        internal sealed class Entry
        {
            public string path;          // full file path
            public string parentSongID;  // SongData.songID of the song this map is for
            public string name;          // map name (file name minus "<song>_")
            public long writeTicks;      // file write time when scanned
            public string viewKey;       // ViewKeyPrefix + path

            // Leaderboard ID cache. The checksum includes the cue descriptors with mirror mode,
            // so a mirror toggle gives a different ID (same as the game).
            public string leaderboardId;
            public bool idMirror;
        }

        private static readonly List<Entry> entries = new List<Entry>();
        private static readonly Dictionary<string, Entry> byPath = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, Entry> byViewKey = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        // songIDs (SongData.songID) of the songs that have at least one community map.
        private static readonly HashSet<string> parentsWithMaps = new HashSet<string>(StringComparer.Ordinal);

        // Leaderboard IDs computed in earlier scans, kept across rescans by path + write time.
        private static readonly Dictionary<string, Entry> idCache = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

        private static string idSuffix;      // the game's suffix literal, once read
        private static bool idSuffixKnown;

        /// <summary>Path of the map the mod has selected, or null when none is.</summary>
        public static string SelectedPath { get; private set; }

        /// <summary>True while a community map is the current selection.</summary>
        public static bool IsActive => SelectedPath != null;

        /// <summary>Difficulty the player had before picking a map (maps always play on Expert).</summary>
        private static KataConfig.Difficulty? difficultyBeforeMap;

        public static int Count => entries.Count;
        public static IReadOnlyList<Entry> Entries => entries;

        // ══════════════════════════════════════════════════════════════════════
        //  Scan
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Rescans the community map folder. Call after the song list has (re)loaded — map files
        /// are matched against the loaded songs. Called from SongFolderManager.Rebuild.
        /// </summary>
        public static void Rebuild()
        {
            entries.Clear();
            byPath.Clear();
            byViewKey.Clear();
            parentsWithMaps.Clear();

            try
            {
                if (SongList.I == null || SongList.I.songs == null) return;

                string dir = GetMapDir();
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    DebugLog.Log($"[CommunityMaps] No community map folder ({dir ?? "null"}).");
                    return;
                }

                // originalSongID → SongData (the game compares the file prefix to originalSongID
                // when loading the map's cues, so that's the only match that actually plays).
                var byOriginalID = new Dictionary<string, SongList.SongData>();
                for (int i = 0; i < SongList.I.songs.Count; i++)
                {
                    var sd = SongList.I.songs[i];
                    if (sd == null || sd.hidden) continue;
                    string orig = sd.originalSongID;
                    if (!string.IsNullOrEmpty(orig) && !byOriginalID.ContainsKey(orig))
                        byOriginalID.Add(orig, sd);
                }

                string[] files = Directory.GetFiles(dir);
                for (int i = 0; i < files.Length; i++)
                {
                    string file = files[i];

                    // Same two extensions the game accepts (GetCommunityMapsForDir / GetCuesInternal).
                    if (!file.EndsWith(".mid", StringComparison.Ordinal) && !file.EndsWith(".cues", StringComparison.Ordinal))
                        continue;

                    string song, name;
                    if (!CommunityMaps.ExtractCommunityMapInfo(file, out song, out name)) continue;
                    if (string.IsNullOrEmpty(song)) continue;

                    if (!byOriginalID.TryGetValue(song, out SongList.SongData parent))
                    {
                        DebugLog.Log($"[CommunityMaps] Skipping '{Path.GetFileName(file)}': no loaded song with originalSongID '{song}'.");
                        continue;
                    }

                    long ticks = 0;
                    try { ticks = File.GetLastWriteTimeUtc(file).Ticks; } catch { }

                    var e = new Entry
                    {
                        path = file,
                        parentSongID = parent.songID,
                        name = string.IsNullOrEmpty(name) ? Path.GetFileNameWithoutExtension(file) : name,
                        writeTicks = ticks,
                        viewKey = ViewKeyPrefix + file
                    };

                    // Reuse an ID computed in an earlier scan if the file hasn't changed.
                    if (idCache.TryGetValue(file, out Entry old) && old.writeTicks == ticks && old.parentSongID == e.parentSongID)
                    {
                        e.leaderboardId = old.leaderboardId;
                        e.idMirror = old.idMirror;
                    }

                    entries.Add(e);
                    byPath[file] = e;
                    byViewKey[e.viewKey] = e;
                    parentsWithMaps.Add(e.parentSongID);
                }

                // Parent title, then map name.
                entries.Sort((a, b) =>
                {
                    int c = string.Compare(ParentTitle(a), ParentTitle(b), StringComparison.OrdinalIgnoreCase);
                    return c != 0 ? c : string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase);
                });

                // The selected map's file is gone (deleted / renamed): drop the selection.
                if (SelectedPath != null && !byPath.ContainsKey(SelectedPath))
                {
                    DebugLog.Log($"[CommunityMaps] Selected map '{SelectedPath}' no longer exists, deselecting.");
                    Deselect();
                }

                DebugLog.Log($"[CommunityMaps] Rebuilt: {entries.Count} map(s) in '{dir}'.");
            }
            catch (Exception ex)
            {
                DebugLog.Log("[CommunityMaps] Rebuild failed: " + ex);
            }
        }

        private static string GetMapDir()
        {
            try { return RecorderMidiOutput.GetCommunityMapDir(false); }
            catch (Exception ex)
            {
                DebugLog.Log("[CommunityMaps] GetCommunityMapDir failed: " + ex.Message);
                return null;
            }
        }

        private static string ParentTitle(Entry e)
        {
            var sd = SongList.I != null ? SongList.I.GetSong(e.parentSongID) : null;
            return sd != null ? (sd.title ?? "") : "";
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Lookups
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>True when the song (SongData.songID) has at least one community map installed.</summary>
        public static bool HasMaps(string songID)
            => songID != null && parentsWithMaps.Contains(songID);

        public static bool IsViewKey(string key)
            => key != null && key.StartsWith(ViewKeyPrefix, StringComparison.Ordinal);

        public static Entry FindByPath(string path)
            => path != null && byPath.TryGetValue(path, out Entry e) ? e : null;

        public static Entry FindByViewKey(string key)
            => key != null && byViewKey.TryGetValue(key, out Entry e) ? e : null;

        /// <summary>The selected map's entry, or null.</summary>
        public static Entry Selected => FindByPath(SelectedPath);

        /// <summary>Parent songID for a map view key, or null when the key isn't a map row.</summary>
        public static string ParentForViewKey(string key) => FindByViewKey(key)?.parentSongID;

        /// <summary>
        /// The row key that should show the selection highlight: the selected map's view key while
        /// a map is selected, otherwise the selected songID.
        /// </summary>
        public static string SelectedViewKey(string selectedSongID)
        {
            Entry e = Selected;
            if (e != null && e.parentSongID == selectedSongID) return e.viewKey;
            return selectedSongID;
        }

        /// <summary>All map row keys in folder order.</summary>
        public static List<string> ViewKeys()
        {
            var list = new List<string>(entries.Count);
            for (int i = 0; i < entries.Count; i++) list.Add(entries[i].viewKey);
            return list;
        }

        /// <summary>Song list row label for a map: "Parent Title [map name]".</summary>
        public static string RowTitle(Entry e)
        {
            string title = ParentTitle(e);
            return string.IsNullOrEmpty(title) ? e.name : $"{title} [{e.name}]";
        }

        /// <summary>
        /// The songID scores are stored / submitted / looked up under: the selected map's
        /// leaderboard ID when the given song is the selected map's parent, else the songID itself.
        /// </summary>
        public static string EffectiveSongID(string songID)
        {
            if (string.IsNullOrEmpty(songID)) return songID;
            Entry e = Selected;
            if (e == null || e.parentSongID != songID) return songID;
            return GetLeaderboardId(e) ?? songID;
        }

        /// <summary>Song title for saved/submitted runs: "Title [map name]" while a map is selected.</summary>
        public static string EffectiveSongTitle(SongList.SongData songData)
        {
            if (songData == null) return null;
            Entry e = Selected;
            if (e == null || e.parentSongID != songData.songID) return songData.title;
            return $"{songData.title} [{e.name}]";
        }

        /// <summary>
        /// Song mapper for saved/submitted runs: empty while a map is selected (the parent song's
        /// mapper didn't make the community map, and the map file doesn't say who did).
        /// </summary>
        public static string EffectiveSongMapper(SongList.SongData songData)
        {
            if (songData == null) return null;
            Entry e = Selected;
            if (e == null || e.parentSongID != songData.songID) return songData.author;
            return "";
        }

        /// <summary>
        /// The selected map, when a run of <paramref name="songData"/> is being submitted under that
        /// map's leaderboard ID (<paramref name="submittedSongId"/> = EffectiveSongID). Null for a
        /// normal song, or when the map's ID couldn't be computed and the run went under the songID.
        /// </summary>
        public static Entry SubmittedMapFor(SongList.SongData songData, string submittedSongId)
        {
            if (songData == null || string.IsNullOrEmpty(submittedSongId)) return null;
            Entry e = Selected;
            if (e == null || e.parentSongID != songData.songID) return null;
            if (submittedSongId == songData.songID) return null; // ID fell back to the parent's
            if (!submittedSongId.StartsWith(e.parentSongID + "_", StringComparison.Ordinal)) return null;
            return e;
        }

        /// <summary>Leaderboard ID for a map file, or null if it can't be computed yet.</summary>
        public static string LeaderboardIdForPath(string path)
        {
            Entry e = FindByPath(path);
            return e != null ? GetLeaderboardId(e) : null;
        }

        /// <summary>Map entry whose leaderboard ID is <paramref name="id"/>, or null.</summary>
        public static Entry FindByLeaderboardId(string id)
        {
            if (string.IsNullOrEmpty(id) || entries.Count == 0) return null;
            for (int i = 0; i < entries.Count; i++)
            {
                Entry e = entries[i];
                // Cheap reject before computing anything: the ID always starts with "<parent>_".
                if (id.Length <= e.parentSongID.Length || !id.StartsWith(e.parentSongID + "_", StringComparison.Ordinal))
                    continue;
                if (GetLeaderboardId(e) == id) return e;
            }
            return null;
        }

        /// <summary>View key of the map row whose leaderboard ID is <paramref name="id"/>, or null.</summary>
        public static string ViewKeyForLeaderboardId(string id) => FindByLeaderboardId(id)?.viewKey;

        // ══════════════════════════════════════════════════════════════════════
        //  Leaderboard ID
        // ══════════════════════════════════════════════════════════════════════

        public static string GetLeaderboardId(Entry e)
        {
            if (e == null) return null;

            bool mirror = false;
            try { mirror = KataConfig.MirrorMode(); } catch { }

            if (e.leaderboardId != null && e.idMirror == mirror) return e.leaderboardId;

            try
            {
                // Without CommunityMaps.I the map can't be selected, and GetCues would return the
                // parent's own chart — never hash that as the map's ID.
                if (CommunityMaps.I == null) return null;

                var parent = SongList.I != null ? SongList.I.GetSong(e.parentSongID) : null;
                if (parent == null) return null;

                string checksum = null;
                WithMapPath(e.path, () =>
                {
                    var cues = SongCues.GetCues(parent, KataConfig.Difficulty.Expert);
                    checksum = SongCues.CalculateChecksumForCues(parent, cues);
                });
                if (string.IsNullOrEmpty(checksum)) return null;

                if (!EnsureIdSuffix(parent, e.path)) return null; // retried next time

                e.leaderboardId = parent.songID + "_" + checksum + idSuffix;
                e.idMirror = mirror;
                idCache[e.path] = e;

                DebugLog.Log($"[CommunityMaps] ID for '{Path.GetFileName(e.path)}' (mirror={mirror}): {e.leaderboardId}");
                return e.leaderboardId;
            }
            catch (Exception ex)
            {
                DebugLog.Log($"[CommunityMaps] Leaderboard ID failed for '{e.path}': {ex}");
                return null;
            }
        }

        /// <summary>
        /// Reads the game's leaderboard-ID suffix once, by calling its own
        /// GetCurrentSongLeaderboardID with this map selected and the parent in SongDataHolder,
        /// then taking whatever follows "&lt;songID&gt;_&lt;32-char checksum&gt;".
        /// </summary>
        private static bool EnsureIdSuffix(SongList.SongData parent, string path)
        {
            if (idSuffixKnown) return true;

            var holder = SongDataHolder.I;
            if (holder == null) return false;

            string full = null;
            var prevSong = holder.songData;
            try
            {
                holder.songData = parent;
                WithMapPath(path, () => { full = OnlineLeaderboard.GetCurrentSongLeaderboardID(); });
            }
            catch (Exception ex)
            {
                DebugLog.Log("[CommunityMaps] Reading the leaderboard ID suffix failed: " + ex.Message);
            }
            finally
            {
                holder.songData = prevSong;
            }

            string prefix = parent.songID + "_";
            const int checksumLength = 32; // MD5, lowercase hex
            if (full == null || !full.StartsWith(prefix, StringComparison.Ordinal) || full.Length < prefix.Length + checksumLength)
            {
                DebugLog.Log($"[CommunityMaps] Unexpected leaderboard ID '{full ?? "null"}' while reading the suffix.");
                return false;
            }

            idSuffix = full.Substring(prefix.Length + checksumLength);
            idSuffixKnown = true;
            DebugLog.Log($"[CommunityMaps] Leaderboard ID suffix: '{idSuffix}' (from '{full}').");
            return true;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Cues
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Runs <paramref name="action"/> with the game's selected map path temporarily set to
        /// <paramref name="path"/> ("" = no map), then puts the previous value back. Writes the
        /// field directly, so the Reset block below doesn't get in the way. Main thread only.
        /// </summary>
        public static void WithMapPath(string path, Action action)
        {
            var cm = CommunityMaps.I;
            if (cm == null) { action(); return; }

            string prev = cm.mSelectedMapPath;
            cm.mSelectedMapPath = path ?? "";
            try { action(); }
            finally { cm.mSelectedMapPath = prev ?? ""; }
        }

        /// <summary>
        /// Resolves a score ID to the chart it was played on and runs <paramref name="action"/>
        /// with the game set up to load exactly that chart: a map leaderboard ID passes the parent
        /// SongData with that map selected (any difficulty loads the map), a songID passes the
        /// song with no map selected (its own chart, even while one of its maps is the current
        /// selection). Returns false (action not run) when the ID matches nothing installed.
        /// Main thread only.
        /// </summary>
        public static bool WithChartFor(string id, Action<SongList.SongData> action)
        {
            if (string.IsNullOrEmpty(id) || SongList.I == null) return false;

            SongList.SongData song = SongList.I.GetSong(id);
            if (song != null)
            {
                WithMapPath("", () => action(song));
                return true;
            }

            Entry e = FindByLeaderboardId(id);
            if (e == null || CommunityMaps.I == null) return false; // no CommunityMaps.I = the map can't load

            SongList.SongData parent = SongList.I.GetSong(e.parentSongID);
            if (parent == null) return false;

            WithMapPath(e.path, () => action(parent));
            return true;
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Selection
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Called from the SongSelectItem.OnSelect postfix, before anything reads cues for the new
        /// selection. <paramref name="mapPath"/> is the map the shot row shows, or null for a
        /// normal song row.
        /// </summary>
        public static void OnSongSelected(SongList.SongData songData, string mapPath)
        {
            Entry e = FindByPath(mapPath);
            if (e != null && songData != null && e.parentSongID == songData.songID)
            {
                if (!IsActive) difficultyBeforeMap = KataConfig.I.mDifficulty;
                SelectedPath = e.path;
                ApplyToGame();

                // One chart only — always Expert (lookahead / target speed come from the difficulty).
                // The field, not SetDifficulty() (which triggers the game's song-list refresh).
                KataConfig.I.mDifficulty = KataConfig.Difficulty.Expert;

                DebugLog.Log($"[CommunityMaps] Selected '{e.name}' for {e.parentSongID} ({GetLeaderboardId(e) ?? "id pending"}).");
                return;
            }

            Deselect();
        }

        /// <summary>Releases the map selection (if any) and restores the previous difficulty.</summary>
        public static void Deselect()
        {
            if (!IsActive) return;

            DebugLog.Log($"[CommunityMaps] Deselected '{SelectedPath}'.");
            SelectedPath = null;
            ApplyToGame();

            if (difficultyBeforeMap.HasValue && KataConfig.I != null)
                KataConfig.I.mDifficulty = difficultyBeforeMap.Value;
            difficultyBeforeMap = null;

            RestoreLaunchPanel();
        }

        private static void ApplyToGame()
        {
            var cm = CommunityMaps.I;
            if (cm == null)
            {
                DebugLog.Log("[CommunityMaps] CommunityMaps.I is null — can't apply the selection.");
                return;
            }
            cm.mSelectedMapPath = SelectedPath ?? "";
        }

        /// <summary>
        /// The game resets the map selection from many places (SongSelect.OnEnable,
        /// DifficultySelect.SelectDifficulty, the launch transition, ...). While the mod has a map
        /// selected, skip it — the mod releases the selection itself (Deselect).
        /// </summary>
        [HarmonyPatch(typeof(CommunityMaps), "Reset")]
        private static class CommunityMapsResetPatch
        {
            private static bool Prefix(CommunityMaps __instance)
            {
                if (!IsActive) return true;

                // Keep the game's field pointing at our map (something may have written it directly).
                if (__instance != null && __instance.mSelectedMapPath != SelectedPath)
                    __instance.mSelectedMapPath = SelectedPath;
                return false;
            }
        }

        // ══════════════════════════════════════════════════════════════════════
        //  Launch panel
        // ══════════════════════════════════════════════════════════════════════

        private const string LaunchCenterPath = "menu/ShellPage_Launch/page/ShellPanel_Center";

        // Hidden while a map is selected: one chart, no difficulty choice.
        private static readonly string[] difficultyElements =
            { "choosediff", "ChooseDiff_easy", "ChooseDiff_normal", "ChooseDiff_hard", "ChooseDiff_expert" };

        private static Transform launchCenter;
        private static readonly List<GameObject> hiddenByMap = new List<GameObject>();

        private static Transform GetLaunchCenter()
        {
            if (launchCenter == null)
            {
                GameObject go = GameObject.Find(LaunchCenterPath);
                if (go != null) launchCenter = go.transform;
            }
            return launchCenter;
        }

        /// <summary>
        /// While a map is selected, keeps the difficulty buttons hidden. Cheap enough to run every
        /// frame (from SyncDisplayingDifficulty) — the game's LaunchPanel.OnEnable turns them back on.
        /// </summary>
        public static void EnforceLaunchPanel()
        {
            if (!IsActive || MarathonSetup.Active) return;

            Transform center = GetLaunchCenter();
            if (center == null) return;

            for (int i = 0; i < difficultyElements.Length; i++)
            {
                Transform t = center.Find(difficultyElements[i]);
                if (t == null || !t.gameObject.activeSelf) continue;
                t.gameObject.SetActive(false);
                if (!hiddenByMap.Contains(t.gameObject)) hiddenByMap.Add(t.gameObject);
            }
        }

        /// <summary>Shows again whatever EnforceLaunchPanel hid.</summary>
        public static void RestoreLaunchPanel()
        {
            for (int i = 0; i < hiddenByMap.Count; i++)
                if (hiddenByMap[i] != null) hiddenByMap[i].SetActive(true);
            hiddenByMap.Clear();
        }

        /// <summary>Launch panel mapper line for the selected map, or null when none is selected.</summary>
        public static string LaunchPanelMapperText()
        {
            Entry e = Selected;
            return e != null ? $"Community map: {e.name}" : null;
        }
    }
}