using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Harmony;
using Hmx.Audio;
using MelonLoader;
using UnhollowerBaseLib;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// Replaces SongList's native <AssembleSongList>c__Iterator0 coroutine, which yields
    /// once per file in the assemble phase and 5 times per song in the precompute phase
    /// regardless of how much real work happened — the actual cause of Audica's slow
    /// startup (see ExScoring startup-performance investigation notes). This reproduces
    /// the same logic, verified against a Ghidra decompile of the original MoveNext, but
    /// yields in batches instead, and adds an on-disk descriptor cache to skip re-reading
    /// unchanged .audica files entirely.
    /// </summary>
    public partial class ExScoring : MelonMod
    {
        [HarmonyPatch(typeof(SongList), "StartAssembleSongList")]
        public static class StartAssembleSongListPatch
        {
            public static bool Prefix()
            {
                if (!Config.SongCacheEnabled)
                {
                    // No benefit to our custom batched coroutine when there's no cache to
                    // skip through — every file gets scanned live either way, and our
                    // larger batch size (traded for less per-file yield overhead) hitches
                    // worse than vanilla's smooth one-file-per-yield cadence. Let the real,
                    // unmodified coroutine run instead. EnsureOfficialSongIDsRecognized is
                    // a separate, unrelated bug fix (see its own doc comment) — not part of
                    // the disk-cache system — so it still needs to run here regardless.
                    SongListAssembler.EnsureOfficialSongIDsRecognized();
                    return true;
                }

                MelonCoroutines.Start(SongListAssembler.CustomAssembleSongList());
                return false;
            }
        }
    }

    internal static class SongListAssembler
    {
        // How many files/songs to process before yielding once. Tune to trade off
        // load-screen smoothness vs. total wall time; unlike the original coroutine,
        // nothing else depends on this cadence (see startup-performance investigation:
        // SongList.AssembleSongsPerFrame is dead code in the native coroutine and is not
        // referenced here either).
        private const int AssembleBatchSize = 30;
        private const int PrecomputeBatchSize = 30;

        // Exposed for the loading-screen UI (progress bar + current song text).
        public static string CurrentStatusText { get; private set; } = "";
        public static int CurrentIndex { get; private set; } = 0;
        public static int TotalCount { get; private set; } = 0;

        /// <summary>
        /// Bridges the assemble phase's per-file CachedSongEntry (which knows the file's
        /// relative path, needed to write back to SongCache) into the precompute phase
        /// (which only has the SongData object, keyed by its final, post-rename songID).
        /// Populated by both TryServeFromCache (hit) and HarvestNewestSong (miss/harvest)
        /// as each song is added to SongList.I.songs, and cleared at the start of every
        /// full assemble pass.
        /// </summary>
        private sealed class PrecomputeCacheLink
        {
            public string RelativePath;
            public CachedSongEntry Entry;
        }

        private static readonly Dictionary<string, PrecomputeCacheLink> songIDToCacheEntry =
            new Dictionary<string, PrecomputeCacheLink>();

        public static IEnumerator CustomAssembleSongList()
        {
            DateTime startTime = DateTime.Now;

            // ── Phase A: init (replaces cases 0-1) ──
            SongList.OnSongListLoaded.mDone = false;
            CurrentStatusText = "Loading songs...";
            SongList.I.LoadingTag = CurrentStatusText;
            yield return null;

            bool doFullAssemble = SongList.sFirstTime;
            MelonLogger.Log($"[SongListAssembler] sFirstTime={SongList.sFirstTime} -> " +
                             (doFullAssemble ? "running full assemble" : "skipping assemble, precompute only"));

            if (doFullAssemble)
            {
                IEnumerator assemblePhase = RunAssemblePhase();
                while (true)
                {
                    bool moved;
                    try { moved = assemblePhase.MoveNext(); }
                    catch (Exception ex)
                    {
                        MelonLogger.Log($"[SongListAssembler] Assemble phase threw, aborting assemble: {ex}");
                        break;
                    }
                    if (!moved) break;
                    yield return assemblePhase.Current;
                }
            }

            // ── Phase C: precompute (replaces cases 7-0xc) ──
            IEnumerator precomputePhase = RunPrecomputePhase();
            while (true)
            {
                bool moved;
                try { moved = precomputePhase.MoveNext(); }
                catch (Exception ex)
                {
                    MelonLogger.Log($"[SongListAssembler] Precompute phase threw, aborting precompute: {ex}");
                    break;
                }
                if (!moved) break;
                yield return precomputePhase.Current;
            }

            // ── Phase D: finish (replaces cases 0xd-0xf) ──
            TimeSpan elapsed = DateTime.Now - startTime;
            MelonLogger.Log($"[SongListAssembler] Song list loaded in {elapsed.TotalSeconds:F2}s " +
                             $"({SongList.I.songs.Count} songs)");
            SongList.OnSongListLoaded.SetLoaded();
            SongList.I.LoadingTag = "";
        }

        // ─────────────────────────── Phase B: assemble ───────────────────────────

        /// <summary>
        /// SongList.I.songIDHashes is meant to contain checksums for every official/DLC
        /// song, so ProcessSingleSong recognizes them and skips the dynamic-rename branch
        /// it otherwise applies to unrecognized (custom) songs. In this environment that
        /// list ends up empty — confirmed via ILSpy's Analyze showing zero managed callers
        /// ever write to it, meaning it's populated by native game code we can't locate or
        /// influence directly. Rather than depend on that, we populate it ourselves from
        /// SongFolderManager's own official/DLC ID lists (the same source of truth this
        /// mod already uses for folder categorization), so ProcessSingleSong's own,
        /// unmodified logic behaves correctly regardless of why the native population
        /// fails here.
        /// </summary>
        internal static void EnsureOfficialSongIDsRecognized()
        {
            Il2CppSystem.Collections.Generic.List<string> songIDHashes = SongList.I.songIDHashes;
            if (songIDHashes == null)
                return;

            int added = 0;
            AddChecksumsFor(SongFolderManager.audicaSongIDs, songIDHashes, ref added);
            AddChecksumsFor(SongFolderManager.audicaDLCSongIDs, songIDHashes, ref added);

            if (added > 0)
                MelonLogger.Log($"[SongListAssembler] Pre-populated {added} official/DLC song checksums into songIDHashes");
        }

        private static void AddChecksumsFor(HashSet<string> ids, Il2CppSystem.Collections.Generic.List<string> songIDHashes, ref int added)
        {
            foreach (string id in ids)
            {
                try
                {
                    string checksum = SongList.GetSongIDChecksum(id);
                    if (!songIDHashes.Contains(checksum))
                    {
                        songIDHashes.Add(checksum);
                        added++;
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Log($"[SongListAssembler] Failed to compute/add checksum for official songID '{id}': {ex}");
                }
            }
        }

        private static IEnumerator RunAssemblePhase()
        {
            if (Config.SongCacheEnabled)
                SongCache.EnsureLoaded();
            EnsureOfficialSongIDsRecognized();

            // Stale links from a previous StartAssembleSongList call in this same process
            // (e.g. a mid-session ReloadSongList) would otherwise point precompute at
            // entries for songs that may no longer be in this pass's results.
            songIDToCacheEntry.Clear();

            SongList.I.CopyRiftDLC();
            yield return null;

            // DLC search-dir registration, faithfully reproducing <>m__0's logic.
            PlatformChooser.DLCDetecter detecter = PlatformChooser.I.GetDLCDetecter();
            if (detecter != null)
            {
                detecter.ForEach(new System.Action<PlatformChooser.DLCDetecter.FoundDLC>(found =>
                {
                    try
                    {
                        string dirName = KataUtil.GetHmxAudioAssetPath("songs", true);
                        SongList.AddSongSearchDir(found.RootPath, dirName);
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Log($"[SongListAssembler] DLC callback failed: {ex}");
                    }
                }));
            }

            SongList.AddSongSearchDir(Application.dataPath, KataUtil.GetHmxAudioAssetPath("songs", true));

            SongList.I.songs.Clear();
            var dedupHash = new Il2CppSystem.Collections.Generic.HashSet<string>();

            int cacheHits = 0;
            int cacheMisses = 0;
            int processedThisBatch = 0;

            for (int dirIndex = 0; dirIndex < SongList.SongSourceDirs.Count; dirIndex++)
            {
                SongList.SongSourceDir dir = SongList.SongSourceDirs[dirIndex];

                Il2CppSystem.Collections.Generic.List<string> files;
                try { files = SongList.I.GetSongFileList(dir); }
                catch (Exception ex)
                {
                    MelonLogger.Log($"[SongListAssembler] GetSongFileList failed for '{dir.dir}': {ex}");
                    continue;
                }
                if (files == null)
                    continue;

                for (int fileIndex = 0; fileIndex < files.Count; fileIndex++)
                {
                    string found = files[fileIndex].Replace('\\', '/');

                    try
                    {
                        bool wasHit = Config.SongCacheEnabled && TryServeFromCache(dir, found, dedupHash);
                        if (wasHit)
                        {
                            cacheHits++;
                        }
                        else
                        {
                            cacheMisses++;
                            int countBeforeProcess = SongList.I.songs.Count;
                            bool accepted = SongList.I.ProcessSingleSong(dir, found, dedupHash);

                            // ProcessSingleSong can return accepted=true for a file it
                            // silently treats as a no-op duplicate of a song already in
                            // the list (confirmed via log evidence: two physically
                            // distinct files ended up harvested with an unrelated,
                            // previously-added song's metadata, because accepted=true
                            // alone doesn't mean *this* file produced the list's last
                            // entry). Only harvest when the list actually grew, so
                            // SongList.I.songs[Count - 1] is guaranteed to be the song
                            // this specific file just produced. Skipped entirely when the
                            // cache is disabled — no writes at all in that case.
                            if (Config.SongCacheEnabled && accepted && SongList.I.songs.Count > countBeforeProcess)
                                HarvestNewestSong(dir, found);
                        }
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Log($"[SongListAssembler] Failed processing '{found}': {ex}");
                    }

                    processedThisBatch++;
                    if (processedThisBatch >= AssembleBatchSize)
                    {
                        processedThisBatch = 0;
                        CurrentStatusText = $"Scanning songs... ({SongList.I.songs.Count} found)";
                        SongList.I.LoadingTag = CurrentStatusText;
                        yield return null;
                    }
                }
            }

            MelonLogger.Log($"[SongListAssembler] Assemble phase done: {cacheHits} cache hits, " +
                             $"{cacheMisses} cache misses, {SongList.I.songs.Count} songs total");

            // SongList.I.songs.Sort has no Comparison<T> overload available across the
            // interop boundary — only IComparer<T>. Sort a plain managed copy instead
            // (ordinary C# lambda, no interop wrapping needed) and repopulate via .Add(),
            // matching the same approach already used in the cache-hit path.
            var sortBuffer = new System.Collections.Generic.List<SongList.SongData>();
            for (int i = 0; i < SongList.I.songs.Count; i++)
                sortBuffer.Add(SongList.I.songs[i]);

            sortBuffer.Sort((a, b) => SongList.I.SongCompare(a, b));

            SongList.I.songs.Clear();
            for (int i = 0; i < sortBuffer.Count; i++)
                SongList.I.songs.Add(sortBuffer[i]);

            if (Config.SongCacheEnabled)
                SongCache.SaveIfDirty();

            SongList.I.LoadingTag = "";
        }

        /// <summary>
        /// Attempts to reconstruct a song from cache without touching its zip file.
        /// Returns true if the file was handled via cache (added, rejected, or found to be
        /// a duplicate) — false means the caller must fall back to the real
        /// SongList.ProcessSingleSong.
        /// </summary>
        private static bool TryServeFromCache(SongList.SongSourceDir dir, string found, Il2CppSystem.Collections.Generic.HashSet<string> dedupHash)
        {

            string relativePath = GetRelativePath(dir, found);

            FileInfo fileInfo;
            try { fileInfo = new FileInfo(found); }
            catch { return false; }
            if (!fileInfo.Exists)
                return false;

            CachedSongEntry cached = SongCache.TryGet(relativePath, fileInfo.Length, fileInfo.LastWriteTimeUtc.Ticks);
            if (cached == null)
                return false;

            // ProcessSingleSong internally mounts the zip into HmxAudioPlugin's native
            // virtual filesystem — audio loading later reads files like
            // "foo.audica%song.moggsong" through that mount. Skipping ProcessSingleSong on
            // a cache hit also skips this mount. Trying cached.ZipPath (a short,
            // engine-relative path, e.g. "StreamingAssets/HmxAudioAssets/songs/X.audica")
            // rather than the raw absolute 'found' path, since the parameter is literally
            // named resourcePath and the earlier attempt with the absolute path did not
            // fix the hang. Logging the actual return value either way this time.
            try
            {
                bool mountedRelative = HmxAudioPlugin.MountZip(cached.ZipPath);
                MelonLogger.Log($"[MountCheck] MountZip(relative='{cached.ZipPath}') -> {mountedRelative}");

                if (!mountedRelative)
                {
                    bool mountedAbsolute = HmxAudioPlugin.MountZip(found);
                    MelonLogger.Log($"[MountCheck] MountZip(absolute='{found}') -> {mountedAbsolute}");

                    if (!mountedAbsolute)
                    {
                        MelonLogger.Log($"[SongListAssembler] MountZip failed (both forms) for '{relativePath}', falling back to live scan");
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Log($"[SongListAssembler] MountZip threw for '{relativePath}', falling back to live scan: {ex}");
                return false;
            }

            // Reconstruct directly from cached field values — no serialization format
            // involved. (Two earlier attempts tried replaying SongData.GetDescriptor()'s
            // output through JsonUtility, then Newtonsoft; GetDescriptor() turned out to
            // serialize tempo/timing data, not song metadata, so the whole
            // serialize/deserialize approach was dropped in favor of this.)
            SongList.SongData songData = new SongList.SongData();
            songData.songID = cached.SongID;
            songData.originalSongID = cached.OriginalSongID;
            songData.title = cached.Title;
            songData.artist = cached.Artist;
            songData.author = cached.Author;
            songData.midiFile = cached.MidiFile;
            songData.targetDrums = cached.TargetDrums;
            songData.moggSong = cached.MoggSong;
            songData.zipPath = cached.ZipPath;
            songData.songEndEvent = cached.SongEndEvent;
            songData.highScoreEvent = cached.HighScoreEvent;
            songData.songEndPitchAdjust = cached.SongEndPitchAdjust;
            songData.prerollSeconds = cached.PrerollSeconds;
            songData.previewStartSeconds = cached.PreviewStartSeconds;
            songData.useMidiForCues = cached.UseMidiForCues;
            songData.hidden = cached.Hidden;

            songData.searchRoot = dir.root;
            songData.searchDir = dir.dir;
            songData.foundPath = found;
            songData.hasEasy = cached.HasEasy;
            songData.hasNormal = cached.HasNormal;
            songData.hasHard = cached.HasHard;
            songData.hasExpert = cached.HasExpert;

            if (cached.Tempos != null && cached.Tempos.Count > 0)
            {
                try
                {
                    var temposArray = new Il2CppReferenceArray<SongList.SongData.TempoChange>(cached.Tempos.Count);
                    for (int i = 0; i < cached.Tempos.Count; i++)
                    {
                        CachedTempoChange tc = cached.Tempos[i];
                        temposArray[i] = new SongList.SongData.TempoChange(tc.Tempo, tc.Tick);
                    }
                    songData.tempos = temposArray;
                }
                catch (Exception ex)
                {
                    MelonLogger.Log($"[SongListAssembler] Failed to reconstruct tempos for '{relativePath}', " +
                                     $"falling back to live scan: {ex}");
                    return false;
                }
            }

            // NOTE: mapping of the three GetMoggPathIfValid results to these fields is
            // inferred from call order in the decompile, not a confirmed offset — flagged
            // in SongCache's harvest step too. Recommend verifying against a known test
            // song in UnityExplorer before relying on this in production.
            songData.sustainSongRight = cached.SustainSongRight;
            songData.sustainSongLeft = cached.SustainSongLeft;
            songData.fxSong = cached.FxSong;

            string songIdChecksum = SongList.GetSongIDChecksum(cached.OriginalSongID);

            if (!SongList.I.songIDHashes.Contains(songIdChecksum))
            {
                if (SongList.I.eliminateChecksums.Contains(cached.CueChecksum))
                {
                    // Rejected, same as vanilla ProcessSingleSong returning false here.
                    MelonLogger.Log($"[RejectCheck] songID={songData.songID} REJECTED via cache-hit path: " +
                                     $"cueChecksum={cached.CueChecksum} was found in eliminateChecksums " +
                                     $"(songIdChecksum={songIdChecksum} not in songIDHashes)");
                    return true;
                }

                songData.MakeDynamicName(cached.CueChecksum);
                songData.isDynamic = true; // best-effort field name, see MakeDynamicName note below
            }

            if (SongList.I.extrasSongIDHashes.Contains(songIdChecksum))
                songData.extrasSong = true;

            songData.unlockable = CampaignStructure.I.IsUnlockable(CampaignStructure.UnlockType.Song, songData.songID);
            songData.communityMapsContest = SongList.IsCommunityMapsContestSong(songData.songID);

            bool isDuplicate = false;
            for (int i = 0; i < SongList.I.songs.Count; i++)
            {
                if (SongList.I.songs[i].songID == songData.songID)
                {
                    isDuplicate = true;
                    break;
                }
            }

            MelonLogger.Log(
                $"[FieldCheck][HIT] songID={songData.songID} " +
                $"isDynamic={songData.isDynamic} extrasSong={songData.extrasSong} " +
                $"sustainSongRight={songData.sustainSongRight} sustainSongLeft={songData.sustainSongLeft} " +
                $"fxSong={songData.fxSong} " +
                $"wasInSongIDHashesBeforeAdd={SongList.I.songIDHashes.Contains(songIdChecksum)} " +
                $"wasInExtrasHashesBeforeAdd={SongList.I.extrasSongIDHashes.Contains(songIdChecksum)}");

            int temposLengthHit = -1;
            try { temposLengthHit = songData.tempos != null ? songData.tempos.Length : 0; }
            catch { /* leave as -1 to signal the property itself threw */ }

            MelonLogger.Log(
                $"[FullDump][HIT] songID={songData.songID} " +
                $"moggSong={songData.moggSong} title={songData.title} artist={songData.artist} author={songData.author} " +
                $"midiFile={songData.midiFile} targetDrums={songData.targetDrums} " +
                $"sustainSongRight={songData.sustainSongRight} sustainSongLeft={songData.sustainSongLeft} fxSong={songData.fxSong} " +
                $"songEndEvent={songData.songEndEvent} highScoreEvent={songData.highScoreEvent} " +
                $"songEndPitchAdjust={songData.songEndPitchAdjust} prerollSeconds={songData.prerollSeconds} " +
                $"previewStartSeconds={songData.previewStartSeconds} useMidiForCues={songData.useMidiForCues} " +
                $"hidden={songData.hidden} zipPath={songData.zipPath} searchRoot={songData.searchRoot} " +
                $"searchDir={songData.searchDir} foundPath={songData.foundPath} " +
                $"hasEasy={songData.hasEasy} hasNormal={songData.hasNormal} hasHard={songData.hasHard} hasExpert={songData.hasExpert} " +
                $"isDynamic={songData.isDynamic} originalSongID={songData.originalSongID} " +
                $"communityMapsContest={songData.communityMapsContest} temposLength={temposLengthHit} " +
                $"extrasSong={songData.extrasSong} dlc={songData.dlc} unlockable={songData.unlockable}");

            if (!isDuplicate)
            {
                SongList.I.songs.Add(songData);
                songIDToCacheEntry[songData.songID] = new PrecomputeCacheLink
                {
                    RelativePath = relativePath,
                    Entry = cached,
                };
            }
            else
            {
                MelonLogger.Log($"[SongListAssembler] Duplicate songID '{songData.songID}' skipped (cache hit)");
            }

            return true;
        }

        /// <summary>
        /// Called right after a real, live SongList.ProcessSingleSong call accepted a file.
        /// Harvests everything needed to serve this file from cache next time, and excludes
        /// it from the cache entirely if it required a DLC entitlement check this session.
        /// </summary>
        private static void HarvestNewestSong(SongList.SongSourceDir dir, string found)
        {
            if (SongList.I.songs.Count == 0)
                return;

            SongList.SongData newest = SongList.I.songs[SongList.I.songs.Count - 1];
            string relativePath = GetRelativePath(dir, found);

            bool hasPendingEntitlement = false;
            for (int i = 0; i < SongList.I.mPendingDLCEntitlementChecks.Count; i++)
            {
                if (SongList.I.mPendingDLCEntitlementChecks[i].songID == newest.songID)
                {
                    hasPendingEntitlement = true;
                    break;
                }
            }

            if (hasPendingEntitlement)
            {
                SongCache.MarkPendingEntitlement(relativePath);
                return;
            }

            FileInfo fileInfo;
            try { fileInfo = new FileInfo(found); }
            catch (Exception ex)
            {
                MelonLogger.Log($"[SongListAssembler] Could not stat '{found}' for caching: {ex}");
                return;
            }

            string cueChecksum;
            try
            {
                cueChecksum = SongCues.CalculateChecksumForAllCues(newest);
            }
            catch (Exception ex)
            {
                MelonLogger.Log($"[SongListAssembler] Failed to harvest cache data for '{relativePath}': {ex}");
                return;
            }

            string songIdChecksum = SongList.GetSongIDChecksum(newest.songID);
            bool wasInSongIdHashes = SongList.I.songIDHashes.Contains(songIdChecksum);
            bool wasInExtrasHashes = SongList.I.extrasSongIDHashes.Contains(songIdChecksum);

            MelonLogger.Log(
                $"[FieldCheck][MISS] songID={newest.songID} " +
                $"isDynamic={newest.isDynamic} extrasSong={newest.extrasSong} " +
                $"sustainSongRight={newest.sustainSongRight} sustainSongLeft={newest.sustainSongLeft} " +
                $"fxSong={newest.fxSong} " +
                $"wasInSongIDHashesBeforeAdd={wasInSongIdHashes} wasInExtrasHashesBeforeAdd={wasInExtrasHashes}");

            // Comprehensive one-time dump of EVERY SongData field from a real, live,
            // ProcessSingleSong-produced object — complete ground truth, so we stop
            // discovering missing cached fields one crash report at a time. tempos.Length
            // is included for completeness but is expected to be 0 here regardless of
            // construction path, since SetUpTempos (which populates it) doesn't run until
            // the precompute phase, after this harvest point.
            int temposLength = -1;
            try { temposLength = newest.tempos != null ? newest.tempos.Length : 0; }
            catch { /* leave as -1 to signal the property itself threw */ }

            MelonLogger.Log(
                $"[FullDump][MISS] songID={newest.songID} " +
                $"moggSong={newest.moggSong} title={newest.title} artist={newest.artist} author={newest.author} " +
                $"midiFile={newest.midiFile} targetDrums={newest.targetDrums} " +
                $"sustainSongRight={newest.sustainSongRight} sustainSongLeft={newest.sustainSongLeft} fxSong={newest.fxSong} " +
                $"songEndEvent={newest.songEndEvent} highScoreEvent={newest.highScoreEvent} " +
                $"songEndPitchAdjust={newest.songEndPitchAdjust} prerollSeconds={newest.prerollSeconds} " +
                $"previewStartSeconds={newest.previewStartSeconds} useMidiForCues={newest.useMidiForCues} " +
                $"hidden={newest.hidden} zipPath={newest.zipPath} searchRoot={newest.searchRoot} " +
                $"searchDir={newest.searchDir} foundPath={newest.foundPath} " +
                $"hasEasy={newest.hasEasy} hasNormal={newest.hasNormal} hasHard={newest.hasHard} hasExpert={newest.hasExpert} " +
                $"isDynamic={newest.isDynamic} originalSongID={newest.originalSongID} " +
                $"communityMapsContest={newest.communityMapsContest} temposLength={temposLength} " +
                $"extrasSong={newest.extrasSong} dlc={newest.dlc} unlockable={newest.unlockable}");

            var tempos = new List<CachedTempoChange>();
            try
            {
                if (newest.tempos != null)
                {
                    for (int i = 0; i < newest.tempos.Length; i++)
                    {
                        SongList.SongData.TempoChange tc = newest.tempos[i];
                        if (tc != null)
                            tempos.Add(new CachedTempoChange { Tempo = tc.tempo, Tick = tc.tick });
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Log($"[SongListAssembler] Failed to harvest tempos for '{relativePath}': {ex}");
            }

            var entry = new CachedSongEntry
            {
                FileSize = fileInfo.Length,
                LastWriteTicksUtc = fileInfo.LastWriteTimeUtc.Ticks,
                SongID = newest.songID,
                OriginalSongID = newest.originalSongID,
                Title = newest.title,
                Artist = newest.artist,
                Author = newest.author,
                MidiFile = newest.midiFile,
                TargetDrums = newest.targetDrums,
                MoggSong = newest.moggSong,
                ZipPath = newest.zipPath,
                SongEndEvent = newest.songEndEvent,
                HighScoreEvent = newest.highScoreEvent,
                SongEndPitchAdjust = newest.songEndPitchAdjust,
                PrerollSeconds = newest.prerollSeconds,
                PreviewStartSeconds = newest.previewStartSeconds,
                UseMidiForCues = newest.useMidiForCues,
                Hidden = newest.hidden,
                HasEasy = newest.hasEasy,
                HasNormal = newest.hasNormal,
                HasHard = newest.hasHard,
                HasExpert = newest.hasExpert,
                SustainSongRight = newest.sustainSongRight,
                SustainSongLeft = newest.sustainSongLeft,
                FxSong = newest.fxSong,
                CueChecksum = cueChecksum,
                Tempos = tempos,
            };

            SongCache.Set(relativePath, entry);
            songIDToCacheEntry[newest.songID] = new PrecomputeCacheLink
            {
                RelativePath = relativePath,
                Entry = entry,
            };
        }

        private static string GetRelativePath(SongList.SongSourceDir dir, string found)
        {
            string root = (dir.root ?? "").Replace('\\', '/').TrimEnd('/');
            string normalizedFound = found.Replace('\\', '/');
            if (!string.IsNullOrEmpty(root) && normalizedFound.StartsWith(root))
                return normalizedFound.Substring(root.Length).TrimStart('/');
            return normalizedFound;
        }

        // ─────────────────────────── Phase C: precompute ───────────────────────────

        private static IEnumerator RunPrecomputePhase()
        {
            int processedThisBatch = 0;
            TotalCount = SongList.I.songs.Count;
            int rawScoreCacheHits = 0;
            int rawScoreCacheMisses = 0;

            for (int i = 0; i < SongList.I.songs.Count; i++)
            {
                SongList.SongData song = SongList.I.songs[i];
                CurrentIndex = i;

                try
                {
                    // SetUpTempos still runs unconditionally for every song, cache hit or
                    // not — not yet confirmed safe to skip on a raw-score cache hit (see
                    // startup-performance investigation notes on not trusting "should be
                    // safe" assumptions without testing).
                    SongList.SetUpTempos(song);

                    PrecomputeCacheLink link = null;
                    CachedSongEntry cachedEntry = null;
                    if (Config.SongCacheEnabled)
                    {
                        songIDToCacheEntry.TryGetValue(song.songID, out link);
                        cachedEntry = link != null ? link.Entry : null;
                    }

                    if (cachedEntry != null && cachedEntry.HasCachedRawScores)
                    {
                        // Pre-populate StarThresholds' own memo dictionary with the cached
                        // values. The GetMaxRawScore calls below are the same, unmodified
                        // calls as before — they'll find these keys already present via
                        // their own ContainsKey check and skip the expensive native
                        // CalcMaxRawScore entirely, with no change to GetMaxRawScore's
                        // own logic or return values.
                        PrimeMaxRawScore(song.songID, KataConfig.Difficulty.Easy, cachedEntry.MaxRawScoreEasy);
                        PrimeMaxRawScore(song.songID, KataConfig.Difficulty.Normal, cachedEntry.MaxRawScoreNormal);
                        PrimeMaxRawScore(song.songID, KataConfig.Difficulty.Hard, cachedEntry.MaxRawScoreHard);
                        PrimeMaxRawScore(song.songID, KataConfig.Difficulty.Expert, cachedEntry.MaxRawScoreExpert);
                        rawScoreCacheHits++;
                    }
                    else
                    {
                        rawScoreCacheMisses++;
                    }

                    int easy = StarThresholds.I.GetMaxRawScore(song.songID, KataConfig.Difficulty.Easy);
                    int normal = StarThresholds.I.GetMaxRawScore(song.songID, KataConfig.Difficulty.Normal);
                    int hard = StarThresholds.I.GetMaxRawScore(song.songID, KataConfig.Difficulty.Hard);
                    int expert = StarThresholds.I.GetMaxRawScore(song.songID, KataConfig.Difficulty.Expert);

                    // Backfill/refresh the on-disk cache entry so a future boot can skip
                    // CalcMaxRawScore for this song too. A harmless no-op write when this
                    // was already a raw-score cache hit (the same values going back in).
                    // Skipped entirely when the cache is disabled — no writes at all.
                    if (Config.SongCacheEnabled && link != null)
                    {
                        cachedEntry.MaxRawScoreEasy = easy;
                        cachedEntry.MaxRawScoreNormal = normal;
                        cachedEntry.MaxRawScoreHard = hard;
                        cachedEntry.MaxRawScoreExpert = expert;
                        cachedEntry.HasCachedRawScores = true;
                        SongCache.Set(link.RelativePath, cachedEntry);
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Log($"[SongListAssembler] Precompute failed for '{song.songID}': {ex}");
                }

                processedThisBatch++;
                if (processedThisBatch >= PrecomputeBatchSize)
                {
                    processedThisBatch = 0;
                    CurrentStatusText = $"Loading song {i + 1} of {SongList.I.songs.Count}: {song.title}";
                    SongList.I.LoadingTag = CurrentStatusText;
                    yield return null;
                }
            }

            MelonLogger.Log($"[SongListAssembler] Precompute phase done: {rawScoreCacheHits} raw-score cache hits, " +
                             $"{rawScoreCacheMisses} raw-score cache misses (of {SongList.I.songs.Count} songs)");

            // The assemble phase's SaveIfDirty() runs before precompute ever starts, so
            // any raw-score backfills written above would otherwise never reach disk.
            if (Config.SongCacheEnabled)
                SongCache.SaveIfDirty();
        }

        /// <summary>
        /// Writes a single cached raw-score value directly into
        /// StarThresholds.I.mMaxRawScores, under the exact key format
        /// ("songID" + difficulty.ToString(), e.g. "destinyEasy") that
        /// StarThresholds.GetMaxRawScore builds and checks internally — confirmed via
        /// Ghidra decompile of GetMaxRawScore, which does string.Concat(songID,
        /// difficulty.ToString()), then a ContainsKey check on mMaxRawScores before ever
        /// calling the expensive CalcMaxRawScore.
        /// </summary>
        private static void PrimeMaxRawScore(string songID, KataConfig.Difficulty difficulty, int maxRawScore)
        {
            Il2CppSystem.Collections.Generic.Dictionary<string, int> maxRawScores = StarThresholds.I.mMaxRawScores;
            if (maxRawScores == null)
                return;

            string key = songID + difficulty.ToString();
            maxRawScores[key] = maxRawScore;
        }
    }
}