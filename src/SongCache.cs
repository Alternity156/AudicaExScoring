using System;
using System.Collections.Generic;
using System.IO;
using Harmony;
using MelonLoader;
using Newtonsoft.Json;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// Everything we persist for one song file, keyed by its path relative to the
    /// SongSourceDir root it was found under. All fields here are pure functions of the
    /// file's bytes at the time it was scanned — nothing session-dependent (entitlement,
    /// blacklist membership, dedup) is ever cached; those are re-evaluated live on every
    /// hit, against whatever the current session's SongList state actually is.
    ///
    /// Songs that required a DLC entitlement check the last time they were scanned are
    /// deliberately excluded from the cache entirely (see SongCache.HasPendingEntitlement)
    /// rather than cached, so that logic always runs through the real, unmodified
    /// SongList.ProcessSingleSong path.
    /// </summary>
    [Serializable]
    public class CachedTempoChange
    {
        public float Tempo;
        public int Tick;
    }

    [Serializable]
    public class CachedSongEntry
    {
        public long FileSize;
        public long LastWriteTicksUtc;

        // Read directly from the live SongData's plain public properties after a real
        // ProcessSingleSong call — no serialize/deserialize round-trip. (Two earlier
        // attempts tried caching SongData.GetDescriptor()'s output and replaying it
        // through JsonUtility/Newtonsoft; that method turned out to serialize tempo/timing
        // data, not song metadata, and was dropped entirely.)
        public string SongID;
        public string OriginalSongID; // the pre-dynamic-rename base name, NOT equal to SongID
        public string Title;
        public string Artist;
        public string Author;
        public string MidiFile;
        public string TargetDrums;
        public string MoggSong;
        public string ZipPath; // distinct from FoundPath — a short engine-relative path
        public string SongEndEvent;
        public string HighScoreEvent;
        public float SongEndPitchAdjust;
        public float PrerollSeconds;
        public float PreviewStartSeconds;
        public bool UseMidiForCues;
        public bool Hidden;

        public bool HasEasy;
        public bool HasNormal;
        public bool HasHard;
        public bool HasExpert;

        // Best-effort: inferred from GetMoggPathIfValid call order in the decompile, not a
        // confirmed field offset (Ghidra mistyped the object these were assigned to).
        // Recommend verifying against a known test song in UnityExplorer.
        public string SustainSongRight;
        public string SustainSongLeft;
        public string FxSong;

        // Result of SongCues.CalculateChecksumForAllCues, captured once on a miss so hits
        // never need to reopen the zip to get it.
        public string CueChecksum;

        // Populated from the descriptor JSON during the assemble phase itself (confirmed
        // via the FullDump diagnostic — temposLength was non-zero before SetUpTempos ever
        // ran), not solely by the later SetUpTempos call. Reconstructed on a cache hit as
        // an actual Il2CppReferenceArray<SongData.TempoChange>.
        public List<CachedTempoChange> Tempos = new List<CachedTempoChange>();

        // ── Precompute-phase cache (StarThresholds.GetMaxRawScore) ──
        // The four per-difficulty raw score ceilings StarThresholds.CalcMaxRawScore would
        // otherwise recompute from scratch every boot. Captured once after the real
        // GetMaxRawScore call runs for this song (see SongListAssembler.RunPrecomputePhase),
        // then reused on later boots by writing straight into StarThresholds.I.mMaxRawScores
        // under the same "songID + difficulty.ToString()" key GetMaxRawScore uses internally
        // (confirmed via Ghidra decompile) — GetMaxRawScore's own memoization check then
        // finds the key already present and returns it without ever calling the expensive
        // native CalcMaxRawScore. HasCachedRawScores distinguishes "not computed yet" from
        // a legitimately-zero score.
        public bool HasCachedRawScores;
        public int MaxRawScoreEasy;
        public int MaxRawScoreNormal;
        public int MaxRawScoreHard;
        public int MaxRawScoreExpert;

        // True when SongID/CueChecksum were computed with SongDescriptorInvariantPatch active
        // (set by SongCache.Set). Older entries for songs with a tempo of 999.5+ may hold a
        // locale-dependent ID and are dropped on load so they get recomputed once.
        public bool InvariantDescriptorId;
    }

    /// <summary>
    /// Makes custom song IDs independent of the Windows number format.
    ///
    /// A custom song's ID is originalSongID + "_" + an MD5 that includes
    /// SongData.GetDescriptor(): every tempo change written as tempo.ToString("N0") + ";" +
    /// tick + ";". "N0" uses the current culture's thousands separator, so a tempo of 1,000
    /// BPM or more is written "1,002" on an English-format PC but "1 002" (no-break space) on
    /// a French one, and the same map gets two different IDs: different official
    /// leaderboards, and no match in online lobbies. Seen on TWOTORIAL-Continuum
    /// (cd6953... on a French-format PC, 3282f... everywhere else).
    ///
    /// The postfix swaps the culture's separator for ",", giving every player the
    /// English-format ID that the official leaderboards and other players use. N0 shows no
    /// decimals, so the separator is the only culture-dependent character. (Cultures that
    /// group other than every 3 digits only differ from 100,000 up, far above any tempo.)
    /// </summary>
    [HarmonyPatch(typeof(SongList.SongData), "GetDescriptor")]
    internal static class SongDescriptorInvariantPatch
    {
        private static string groupSeparator;

        public static void Postfix(ref string __result)
        {
            if (string.IsNullOrEmpty(__result)) return;
            string sep = GetGroupSeparator();
            if (sep.Length == 0 || __result.IndexOf(sep, StringComparison.Ordinal) < 0) return;
            __result = __result.Replace(sep, ",");
        }

        /// <summary>The game's thousands separator when it isn't ",", else "" (nothing to fix).</summary>
        private static string GetGroupSeparator()
        {
            if (groupSeparator != null) return groupSeparator;

            string sep = null;
            try
            {
                // The game formats on the Il2Cpp side, so ask its culture.
                sep = Il2CppSystem.Globalization.NumberFormatInfo.CurrentInfo.NumberGroupSeparator;
            }
            catch (Exception ex)
            {
                DebugLog.Log($"[SongID] Couldn't read the game's number format ({ex.Message}), using the system's");
            }
            if (sep == null)
                sep = System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberGroupSeparator ?? "";

            bool unusable = sep.Length == 0 || sep == "," || sep.IndexOf(';') >= 0;
            if (!unusable)
                foreach (char c in sep)
                    if (char.IsDigit(c)) { unusable = true; break; }

            if (sep != "," && sep.Length > 0)
            {
                var codes = new List<string>();
                foreach (char c in sep) codes.Add("U+" + ((int)c).ToString("X4"));
                DebugLog.Log(unusable
                    ? $"[SongID] Thousands separator {string.Join(" ", codes.ToArray())} can't be safely replaced: song IDs left as the game makes them"
                    : $"[SongID] Thousands separator is {string.Join(" ", codes.ToArray())}: written as \",\" in song IDs so they match other players");
            }

            groupSeparator = unusable ? "" : sep;
            return groupSeparator;
        }
    }

    /// <summary>
    /// On-disk cache of per-file song descriptor data, used to skip re-reading/re-parsing
    /// unchanged .audica zips on every boot. Plain JSON, human-inspectable/deletable by hand
    /// if something ever needs to be forced back to a full rescan.
    /// </summary>
    internal static class SongCache
    {
        private static readonly string cacheDirectory =
            Application.dataPath.Replace("Audica_Data", "ExScoringSongCache");

        private static readonly string cacheFilePath =
            Path.Combine(cacheDirectory, "song_cache.json");

        private static Dictionary<string, CachedSongEntry> entries;
        private static bool dirty = false;
        private static bool loaded = false;

        // Relative paths of songs that currently have a pending DLC entitlement check.
        // Anything in here is never served from cache — always re-scanned live via the
        // real ProcessSingleSong.
        private static readonly HashSet<string> pendingEntitlementPaths = new HashSet<string>();

        public static void EnsureLoaded()
        {
            if (loaded)
                return;

            loaded = true;
            entries = new Dictionary<string, CachedSongEntry>();

            try
            {
                if (File.Exists(cacheFilePath))
                {
                    string json = File.ReadAllText(cacheFilePath);
                    var loadedEntries = JsonConvert.DeserializeObject<Dictionary<string, CachedSongEntry>>(json);
                    if (loadedEntries != null)
                        entries = loadedEntries;

                    DebugLog.Log($"[SongCache] Loaded {entries.Count} cached song entries");
                    DropLocaleDependentIds();
                }
                else
                {
                    DebugLog.Log("[SongCache] No existing cache file found, starting fresh");
                }
            }
            catch (Exception ex)
            {
                DebugLog.Log($"[SongCache] Failed to load cache, starting fresh: {ex}");
                entries = new Dictionary<string, CachedSongEntry>();
            }
        }

        /// <summary>
        /// Drops entries cached before SongDescriptorInvariantPatch whose ID could depend on
        /// the number format: those with a tempo that "N0" writes as 1,000 or more. They're
        /// rescanned live once; every other entry is unaffected and kept.
        /// </summary>
        private static void DropLocaleDependentIds()
        {
            List<string> stale = new List<string>();
            foreach (var kv in entries)
            {
                CachedSongEntry entry = kv.Value;
                if (entry == null || entry.InvariantDescriptorId || entry.Tempos == null) continue;
                foreach (CachedTempoChange t in entry.Tempos)
                {
                    if (t != null && t.Tempo >= 999.5f) { stale.Add(kv.Key); break; }
                }
            }

            foreach (string key in stale)
            {
                DebugLog.Log($"[SongCache] Rescanning {key}: tempo of 1,000+ BPM, cached ID may depend on the number format");
                entries.Remove(key);
            }
            if (stale.Count > 0) dirty = true;
        }

        /// <summary>
        /// Attempts a cache hit for the given file. Returns null on a miss (unknown path,
        /// changed size/mtime, or a path with a pending entitlement check that must always
        /// be re-scanned live).
        /// </summary>
        public static CachedSongEntry TryGet(string relativePath, long fileSize, long lastWriteTicksUtc)
        {
            EnsureLoaded();

            if (pendingEntitlementPaths.Contains(relativePath))
                return null;

            if (!entries.TryGetValue(relativePath, out CachedSongEntry entry))
                return null;

            if (entry.FileSize != fileSize || entry.LastWriteTicksUtc != lastWriteTicksUtc)
                return null;

            return entry;
        }

        public static void Set(string relativePath, CachedSongEntry entry)
        {
            EnsureLoaded();
            // Harvested from a live ProcessSingleSong this session, so with the patch active.
            if (entry != null) entry.InvariantDescriptorId = true;
            entries[relativePath] = entry;
            dirty = true;
        }

        /// <summary>
        /// Marks a path as having a pending DLC entitlement check this session, so it is
        /// never served from cache (see TryGet) and — if it happened to already have a
        /// stale cache entry from before it became DLC-gated — that entry is dropped too.
        /// </summary>
        public static void MarkPendingEntitlement(string relativePath)
        {
            pendingEntitlementPaths.Add(relativePath);
            if (entries.Remove(relativePath))
                dirty = true;
        }

        /// <summary>
        /// Drops a deleted song from the cache (DeleteButton.cs): the entry stored under its
        /// zipPath (the cache key is the same root-relative path ProcessSingleSong gives
        /// zipPath), plus any other entry recording the same song ID. Returns how many entries
        /// were removed. Call SaveIfDirty afterwards to write it to disk.
        /// </summary>
        public static int RemoveSong(string songID, string zipPath)
        {
            EnsureLoaded();

            string normalizedZip = (zipPath ?? "").Replace('\\', '/');
            List<string> keys = new List<string>();
            foreach (var kv in entries)
            {
                bool samePath = normalizedZip.Length > 0 &&
                    string.Equals(kv.Key.Replace('\\', '/'), normalizedZip, StringComparison.OrdinalIgnoreCase);
                bool sameSong = !string.IsNullOrEmpty(songID) && kv.Value != null && kv.Value.SongID == songID;
                if (samePath || sameSong) keys.Add(kv.Key);
            }

            foreach (string key in keys)
            {
                entries.Remove(key);
                pendingEntitlementPaths.Remove(key);
            }

            if (keys.Count > 0) dirty = true;
            return keys.Count;
        }

        /// <summary>
        /// Writes the cache to disk if anything changed since the last save. Call once at
        /// the end of a scan batch — not per file — to avoid excess disk I/O.
        /// </summary>
        public static void SaveIfDirty()
        {
            if (!dirty)
                return;

            try
            {
                Directory.CreateDirectory(cacheDirectory);
                string json = JsonConvert.SerializeObject(entries, Formatting.Indented);
                File.WriteAllText(cacheFilePath, json);
                dirty = false;
                DebugLog.Log($"[SongCache] Saved {entries.Count} cached song entries");
            }
            catch (Exception ex)
            {
                DebugLog.Log($"[SongCache] Failed to save cache: {ex}");
            }
        }

        /// <summary>
        /// Clears the cache (memory + disk). Every file will be treated as a miss on the
        /// next scan. Does not itself trigger a rescan — call SongList reload logic
        /// separately if an immediate rebuild is desired.
        /// </summary>
        public static void ClearAndRebuild()
        {
            EnsureLoaded();
            entries.Clear();
            pendingEntitlementPaths.Clear();
            dirty = true;
            SaveIfDirty();
            DebugLog.Log("[SongCache] Cache cleared, next scan will be a full rescan");
        }

        public static int CachedCount
        {
            get { EnsureLoaded(); return entries.Count; }
        }
    }
}