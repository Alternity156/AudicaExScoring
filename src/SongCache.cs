using System;
using System.Collections.Generic;
using System.IO;
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

                    MelonLogger.Log($"[SongCache] Loaded {entries.Count} cached song entries");
                }
                else
                {
                    MelonLogger.Log("[SongCache] No existing cache file found, starting fresh");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Log($"[SongCache] Failed to load cache, starting fresh: {ex}");
                entries = new Dictionary<string, CachedSongEntry>();
            }
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
                MelonLogger.Log($"[SongCache] Saved {entries.Count} cached song entries");
            }
            catch (Exception ex)
            {
                MelonLogger.Log($"[SongCache] Failed to save cache: {ex}");
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
            MelonLogger.Log("[SongCache] Cache cleared, next scan will be a full rescan");
        }

        public static int CachedCount
        {
            get { EnsureLoaded(); return entries.Count; }
        }
    }
}