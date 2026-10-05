using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using MelonLoader;
using Newtonsoft.Json;
using UnityEngine;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        /// <summary>
        /// One saved run, recalculated purely from its .json.gz file's judgement-relevant raw
        /// data (timingMs, contactPos, intersectionPoint, chainAverage, sustainPercent, velocity).
        /// No live song/chart data or exScore/Audica-Linear branching involved.
        /// </summary>
        public class RecalculatedRun
        {
            public string songId;
            public string difficulty;
            public long unixTimestamp;
            public string sourceFileName;

            public float judgementScore;
            public float maxJudgementScore;
            public float judgementPercent => maxJudgementScore > 0f ? (judgementScore / maxJudgementScore) * 100f : 0f;

            public int missCount;
            public bool fullCombo;
            public bool failed;

            /// <summary>
            /// Reconstructed per-cue list, same shape as live ExCue, so it can be handed directly
            /// to the existing GameplayStatsUI string helpers (GetTimingJudgementString,
            /// GetAimJudgementString, GetMiscString) and later to the timing/aim graphs.
            /// </summary>
            public List<ExCue> exCues;

            /// <summary>
            /// The run owner's gun colors, only set for runs fetched from the API (leaderboard stats);
            /// null for local runs and for owners who have never sent colors. See
            /// ChainArrow.GetStatsHandColor.
            /// </summary>
            public GunColorsData gunColors;
        }

        // ------------------------------------------------------------------------------------
        // Run file index
        //
        // songId|difficulty (both sanitized, same as the file names) -> run file names. Built once,
        // on the background loader thread, the first time anything asks for a song's runs, then
        // kept in sync from SaveRunData / DeleteRunFile. Replaces the old per-lookup
        // Directory.GetFiles + FileInfo pass over the WHOLE run data folder, which every song-list
        // row used to pay for separately on the main thread.
        //
        // Only reflects files this mod wrote/deleted itself after the index was built — run files
        // copied in or removed by hand while the game is running are picked up on next launch
        // (a file that vanished just fails to open and is skipped, same as any other bad file).
        // ------------------------------------------------------------------------------------
        private static readonly object runFileIndexLock = new object();
        private static Dictionary<string, List<string>> runFileIndex; // null until first built

        private static string RunFileIndexKey(string sanitizedSongId, string sanitizedDifficulty)
        {
            return sanitizedSongId + "|" + sanitizedDifficulty;
        }

        /// <summary>Caller must hold runFileIndexLock.</summary>
        private static void EnsureRunFileIndexBuilt()
        {
            if (runFileIndex != null) return;

            var index = new Dictionary<string, List<string>>();

            if (Directory.Exists(runDataDirectory))
            {
                foreach (string path in Directory.GetFiles(runDataDirectory, "*.json.gz"))
                {
                    string fileName = Path.GetFileName(path);
                    ParsedRunFileName parsed = ParseRunFileName(fileName);
                    if (parsed.songId == null) continue;

                    string key = RunFileIndexKey(parsed.songId, parsed.difficulty);
                    if (!index.TryGetValue(key, out List<string> list))
                    {
                        list = new List<string>();
                        index[key] = list;
                    }
                    list.Add(fileName);
                }
            }

            runFileIndex = index;
        }

        /// <summary>Registers a freshly written run file. No-op if the index hasn't been built yet
        /// (the eventual build reads the folder and finds it anyway).</summary>
        private static void RunFileIndexAdd(string fileName)
        {
            ParsedRunFileName parsed = ParseRunFileName(fileName);
            if (parsed.songId == null) return;

            lock (runFileIndexLock)
            {
                if (runFileIndex == null) return;

                string key = RunFileIndexKey(parsed.songId, parsed.difficulty);
                if (!runFileIndex.TryGetValue(key, out List<string> list))
                {
                    list = new List<string>();
                    runFileIndex[key] = list;
                }
                if (!list.Contains(fileName)) list.Add(fileName);
            }
        }

        /// <summary>Unregisters a deleted run file. No-op if the index hasn't been built yet.</summary>
        private static void RunFileIndexRemove(string fileName)
        {
            ParsedRunFileName parsed = ParseRunFileName(fileName);
            if (parsed.songId == null) return;

            lock (runFileIndexLock)
            {
                if (runFileIndex == null) return;

                if (runFileIndex.TryGetValue(RunFileIndexKey(parsed.songId, parsed.difficulty), out List<string> list))
                    list.Remove(fileName);
            }
        }

        private static long ParseRunFileTimestamp(string fileName)
        {
            long.TryParse(ParseRunFileName(fileName).timestamp, out long timestamp);
            return timestamp;
        }

        /// <summary>
        /// Lists saved run file names for a song+difficulty, most recent first (by the unix
        /// timestamp in the file name). Thread-safe; touches no Unity/Il2Cpp API, so it's safe on
        /// the background loader thread — which is the only place it's called from.
        /// </summary>
        private static List<string> ListRunFiles(string songId, string difficulty)
        {
            string key = RunFileIndexKey(SanitizeFileName(songId), SanitizeFileName(difficulty));
            List<string> files;

            lock (runFileIndexLock)
            {
                EnsureRunFileIndexBuilt();
                files = runFileIndex.TryGetValue(key, out List<string> list) ? new List<string>(list) : new List<string>();
            }

            files.Sort((a, b) => ParseRunFileTimestamp(b).CompareTo(ParseRunFileTimestamp(a)));
            return files;
        }

        /// <summary>Decompresses and deserializes a single saved run file. Pure managed code —
        /// safe on the background loader thread.</summary>
        private static ScoreSaveData LoadRunData(string fileName)
        {
            using (FileStream fileStream = new FileStream(Path.Combine(runDataDirectory, fileName), FileMode.Open, FileAccess.Read))
            using (GZipStream gzipStream = new GZipStream(fileStream, CompressionMode.Decompress))
            using (StreamReader reader = new StreamReader(gzipStream))
            {
                string json = reader.ReadToEnd();
                return JsonConvert.DeserializeObject<ScoreSaveData>(json, runDataSerializerSettings);
            }
        }

        // ------------------------------------------------------------------------------------
        // Background run loader
        //
        // One long-lived worker thread drains a queue of "load this song+difficulty's runs" jobs.
        // Everything expensive — the folder listing, gzip, JSON parsing and (for song-list rows)
        // the score maths — happens there, so opening a folder costs the main thread nothing but
        // an enqueue per row. Coroutines on the main thread just poll job.done.
        //
        // HARD RULE for anything the worker runs: no UnityEngine / Il2Cpp calls at all (that
        // includes `new Vector3(...)`, Vector3 operators, Quaternion, MelonLogger and anything on
        // SongList/SongCues) — the worker isn't attached to the Il2Cpp runtime. That's why the
        // worker uses SummarizeRun (plain floats) instead of Recalculate (builds ExCues with
        // Vector3s), and why errors are collected as strings and logged from the main thread.
        // ------------------------------------------------------------------------------------

        /// <summary>Score totals for one saved run, without the per-cue ExCue list.</summary>
        public class RunSummary
        {
            public float judgementScore;
            public float maxJudgementScore;
            public float judgementPercent => maxJudgementScore > 0f ? (judgementScore / maxJudgementScore) * 100f : 0f;
            public int missCount;
            public bool failed;
        }

        private class LoadedRunFile
        {
            public string fileName;
            public bool needsChainTailLookup; // legacy save: missed Chain cue(s) without isChainTail
            public RunSummary summary;        // summary jobs only; null when needsChainTailLookup
            public ScoreSaveData data;        // full jobs always; summary jobs only when needsChainTailLookup
        }

        private class RunLoadJob
        {
            public string songId;
            public string difficulty;
            public bool summaryOnly;
            public readonly List<LoadedRunFile> files = new List<LoadedRunFile>(); // most recent first
            public readonly List<string> errors = new List<string>();
            public volatile bool done;
        }

        private static readonly object runLoadQueueLock = new object();
        private static readonly Queue<RunLoadJob> runLoadPriorityQueue = new Queue<RunLoadJob>(); // full history (song page)
        private static readonly Queue<RunLoadJob> runLoadQueue = new Queue<RunLoadJob>();         // row summaries
        private static readonly AutoResetEvent runLoadSignal = new AutoResetEvent(false);
        private static Thread runLoadThread;

        private static readonly Dictionary<float, bool> emptyChainTailLookup = new Dictionary<float, bool>();

        // BuildChainTailLookup loads a chart (SongCues.GetCues) on the main thread — never let more
        // than one of those land in the same frame, however many lookups finish together.
        private static int lastChainTailLookupFrame = -1;

        private static RunLoadJob EnqueueRunLoad(string songId, string difficulty, bool summaryOnly)
        {
            var job = new RunLoadJob { songId = songId, difficulty = difficulty, summaryOnly = summaryOnly };

            lock (runLoadQueueLock)
            {
                // Full-history jobs are for the song the player just opened — let them jump ahead
                // of whatever song-list rows are still queued.
                if (summaryOnly) runLoadQueue.Enqueue(job);
                else runLoadPriorityQueue.Enqueue(job);

                if (runLoadThread == null)
                {
                    runLoadThread = new Thread(RunLoadWorkerLoop)
                    {
                        IsBackground = true, // never keeps the game process alive on quit
                        Name = "ExScoring run loader",
                        Priority = System.Threading.ThreadPriority.BelowNormal
                    };
                    runLoadThread.Start();
                }
            }

            runLoadSignal.Set();
            return job;
        }

        private static void RunLoadWorkerLoop()
        {
            while (true)
            {
                RunLoadJob job = null;

                lock (runLoadQueueLock)
                {
                    if (runLoadPriorityQueue.Count > 0) job = runLoadPriorityQueue.Dequeue();
                    else if (runLoadQueue.Count > 0) job = runLoadQueue.Dequeue();
                }

                if (job == null)
                {
                    runLoadSignal.WaitOne();
                    continue;
                }

                try
                {
                    ProcessRunLoadJob(job);
                }
                catch (Exception ex)
                {
                    job.errors.Add($"Failed to load runs for {job.songId}/{job.difficulty}: {ex}");
                }

                job.done = true; // always, so the waiting coroutine can never hang
            }
        }

        /// <summary>Worker thread only. See the HARD RULE above.</summary>
        private static void ProcessRunLoadJob(RunLoadJob job)
        {
            foreach (string fileName in ListRunFiles(job.songId, job.difficulty))
            {
                try
                {
                    ScoreSaveData data = LoadRunData(fileName);
                    if (data == null) continue;

                    var loaded = new LoadedRunFile
                    {
                        fileName = fileName,
                        needsChainTailLookup = NeedsChainTailLookup(data)
                    };

                    if (job.summaryOnly && !loaded.needsChainTailLookup)
                        loaded.summary = SummarizeRun(data, emptyChainTailLookup); // parsed data dropped right here
                    else
                        loaded.data = data;

                    job.files.Add(loaded);
                }
                catch (Exception ex)
                {
                    job.errors.Add($"Failed to load run file {fileName}: {ex}");
                }
            }
        }

        private static void LogRunLoadErrors(RunLoadJob job)
        {
            for (int i = 0; i < job.errors.Count; i++)
                MelonLogger.Log("[ExScoring] " + job.errors[i]);
        }

        /// <summary>
        /// True only for legacy saves that need BuildChainTailLookup: a missed Chain cue written
        /// before isChainTail was saved for misses. Everything else never consults the lookup
        /// (see RecalculateExCues), so the chart doesn't need loading at all.
        /// </summary>
        private static bool NeedsChainTailLookup(ScoreSaveData data)
        {
            ExCueSaveData[] cues = data.exCues;
            if (cues == null) return false;

            for (int i = 0; i < cues.Length; i++)
            {
                if (cues[i].miss && cues[i].behavior == Target.TargetBehavior.Chain && !cues[i].isChainTail.HasValue)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Score totals for one saved run straight from its raw cue data — the same result as
        /// RecalculateExCues + SumJudgementScore, minus the ExCue list, and with no Unity types
        /// (aim distance is plain float maths rather than Vector3) so it can run on the loader
        /// thread. KEEP IN SYNC with those two methods: any scoring rule changed there has to be
        /// mirrored here, or song-list rows will disagree with the song page.
        /// </summary>
        private static RunSummary SummarizeRun(ScoreSaveData data, Dictionary<float, bool> chainTailLookup)
        {
            var summary = new RunSummary { failed = data.failed };
            ExCueSaveData[] cues = data.exCues;
            if (cues == null) return summary;

            for (int i = 0; i < cues.Length; i++)
            {
                ExCueSaveData saved = cues[i];
                bool isChain = saved.behavior == Target.TargetBehavior.Chain;
                bool isAim = aimBehaviors.Contains(saved.behavior);

                bool isChainTail = false;
                if (isChain)
                {
                    isChainTail = saved.miss
                        ? (saved.isChainTail ?? ResolveIsChainTail(chainTailLookup, saved.tick))
                        : (saved.isChainTail ?? false);
                }

                summary.maxJudgementScore += GetMaxJudgementScoreForSavedCue(saved.behavior, isChainTail);

                if (saved.miss)
                {
                    if (isAim || saved.behavior == Target.TargetBehavior.Melee) summary.missCount++;
                    continue;
                }

                float cueScore = 0f;

                if (isAim)
                {
                    // default(Judgement) mirrors an ExCue whose judgement field was never assigned.
                    Judgement timingJudgement = default(Judgement);
                    Judgement aimJudgement = default(Judgement);

                    if (saved.timingMs.HasValue)
                        timingJudgement = GetTimingJudgement(saved.timingMs.Value);

                    if (saved.intersectionPoint != null && saved.contactPos != null)
                    {
                        float dx = saved.contactPos.x - saved.intersectionPoint.x;
                        float dy = saved.contactPos.y - saved.intersectionPoint.y;
                        float dz = saved.contactPos.z - saved.intersectionPoint.z;
                        aimJudgement = GetAimJudgementFromDistance((float)Math.Sqrt(dx * dx + dy * dy + dz * dz));
                    }

                    cueScore += GetJudgementScore(timingJudgement) + GetJudgementScore(aimJudgement);
                }
                if (isChain && isChainTail)
                {
                    Judgement chainJudgement = default(Judgement);
                    if (saved.chainAverage.HasValue) chainJudgement = GetChainJudgement(saved.chainAverage.Value);
                    cueScore += GetJudgementScore(chainJudgement);
                }
                if (saved.behavior == Target.TargetBehavior.Hold && saved.sustainPercent.HasValue && saved.sustainPercent.Value >= 1f)
                {
                    cueScore += 1f;
                }
                if (saved.behavior == Target.TargetBehavior.Melee && saved.velocity.HasValue && saved.velocity.Value != 0f)
                {
                    cueScore += 1f;
                }

                summary.judgementScore += cueScore;
            }

            return summary;
        }

        /// <summary>
        /// Aim judgement thresholds, ported from Judgement.GetAimJudgement but taking a raw
        /// distance instead of a live Target — we already have contactPos + intersectionPoint
        /// saved, so target.GetContactPosition() is never needed.
        /// </summary>
        private static Judgement GetAimJudgementFromDistance(float distanceFromCenter)
        {
            if (distanceFromCenter <= judgementImpeccableAimRadius) return Judgement.Impeccable;
            if (distanceFromCenter <= judgementFantasticAimRadius) return Judgement.Fantastic;
            if (distanceFromCenter <= judgementExcellentAimRadius) return Judgement.Excellent;
            if (distanceFromCenter <= judgementGreatAimRadius) return Judgement.Great;
            if (distanceFromCenter <= judgementGoodAimRadius) return Judgement.Good;
            return Judgement.Miss;
        }

        /// <summary>
        /// Max possible judgement score for a cue, ported from GetMaxJudgementScoreForCue but
        /// keyed on behavior + isChainTail instead of a live SongCues.Cue (cue.chainNext == null).
        /// </summary>
        private static float GetMaxJudgementScoreForSavedCue(Target.TargetBehavior behavior, bool isChainTail)
        {
            float score = 0f;

            if (behavior == Target.TargetBehavior.Vertical ||
                behavior == Target.TargetBehavior.Horizontal ||
                behavior == Target.TargetBehavior.ChainStart ||
                behavior == Target.TargetBehavior.Standard ||
                behavior == Target.TargetBehavior.Hold)
            {
                score += judgementImpeccableWeight * 2;
            }
            if (behavior == Target.TargetBehavior.Chain && isChainTail)
            {
                score += judgementImpeccableWeight;
            }
            if (behavior == Target.TargetBehavior.Hold || behavior == Target.TargetBehavior.Melee)
            {
                score += 1;
            }

            return score;
        }

        /// <summary>
        /// Legacy-repair fallback: older saved runs never wrote isChainTail for a missed Chain
        /// cue (fixed in BuildExCueSaveData), so it's not recoverable from the file itself. This
        /// rebuilds tick -> isChainTail (cue.chainNext == null) from the live chart as a best-effort
        /// substitute for exactly that gap. Returns an empty lookup (never throws) if the song is
        /// no longer installed, the difficulty string doesn't parse, or anything else goes wrong —
        /// callers treat a miss here the same as before this fix existed.
        /// </summary>
        private static Dictionary<float, bool> BuildChainTailLookup(string songId, string difficultyStr)
        {
            var lookup = new Dictionary<float, bool>();

            try
            {
                if (!Enum.TryParse(difficultyStr, out KataConfig.Difficulty difficulty)) return lookup;

                var songData = SongList.I.GetSong(songId);
                if (songData == null) return lookup;

                var cues = SongCues.GetCues(songData, difficulty);

                // Confirmed via live testing: a cold GetCues() call (i.e. outside an active
                // gameplay session, like from this history screen) returns cues whose chainNext
                // is never populated — it's only assigned as a side effect of HookUpChains,
                // which normally only runs during gameplay setup. Without calling it here first,
                // every Chain cue's chainNext reads null, not just true tails, which silently
                // resolves every ambiguous cue as a tail instead of just the real one.
                SongCues.HookUpChains(cues);

                foreach (SongCues.Cue cue in cues)
                {
                    if (cue.behavior == Target.TargetBehavior.Chain)
                    {
                        lookup[cue.tick] = cue.chainNext == null;
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Log($"[ExScoring] Failed to build chain-tail lookup for {songId}/{difficultyStr}: {ex}");
            }

            return lookup;
        }

        /// <summary>
        /// Tolerant tick lookup into a chain-tail table — saved ticks are the same floats the
        /// chart already had, but a small epsilon guards against any float round-tripping through
        /// JSON. Falls back to false (old buggy behavior) if the tick can't be resolved at all.
        /// </summary>
        private static bool ResolveIsChainTail(Dictionary<float, bool> chainTailLookup, float tick)
        {
            if (chainTailLookup.TryGetValue(tick, out bool exact)) return exact;

            const float epsilon = 0.01f;
            foreach (var kvp in chainTailLookup)
            {
                if (Math.Abs(kvp.Key - tick) < epsilon) return kvp.Value;
            }

            return false;
        }

        /// <summary>
        /// Rebuilds a live-shaped ExCue list from saved raw cue data, deriving timing/aim/chain
        /// judgements purely from numbers already on disk. Missed cues (which only save
        /// behavior/handType/tick/health/miss — see BuildExCueSaveData) are marked Miss outright
        /// rather than left at the enum's default value. chainTailLookup is only consulted for
        /// missed Chain cues whose saved.isChainTail is absent (pre-fix saves).
        /// </summary>
        private static List<ExCue> RecalculateExCues(ExCueSaveData[] savedCues, Dictionary<float, bool> chainTailLookup)
        {
            var result = new List<ExCue>(savedCues.Length);

            foreach (var saved in savedCues)
            {
                var cue = new ExCue
                {
                    behavior = saved.behavior,
                    handType = saved.handType,
                    tick = saved.tick,
                    health = saved.health,
                    miss = saved.miss
                };

                if (saved.miss)
                {
                    cue.timingJudgement = Judgement.Miss;
                    cue.aimJudgement = Judgement.Miss;
                    cue.chainJudgement = Judgement.Miss;

                    if (saved.behavior == Target.TargetBehavior.Chain)
                    {
                        cue.isChainTail = saved.isChainTail ?? ResolveIsChainTail(chainTailLookup, saved.tick);
                    }

                    // A miss can still carry a real recorded aim attempt (player aimed near the
                    // target but failed for other reasons — wrong hand, bad timing, etc.) — this is
                    // for aim-graph plotting only, never affects the judgement itself, which stays Miss.
                    if (saved.hasMissAimData == true && saved.intersectionPoint != null && saved.contactPos != null)
                    {
                        cue.hasMissAimData = true;
                        cue.intersectionPoint = new Vector3(saved.intersectionPoint.x, saved.intersectionPoint.y, saved.intersectionPoint.z);
                        cue.contactPos = new Vector3(saved.contactPos.x, saved.contactPos.y, saved.contactPos.z);
                        cue.contactRotation = saved.contactRotation != null ? saved.contactRotation.ToQuaternion() : Quaternion.identity;
                    }

                    result.Add(cue);
                    continue;
                }

                if (saved.timingMs.HasValue)
                {
                    cue.timingMs = saved.timingMs.Value;
                    cue.timingJudgement = GetTimingJudgement(cue.timingMs);
                }

                if (saved.intersectionPoint != null && saved.contactPos != null)
                {
                    Vector3 intersection = new Vector3(saved.intersectionPoint.x, saved.intersectionPoint.y, saved.intersectionPoint.z);
                    Vector3 contact = new Vector3(saved.contactPos.x, saved.contactPos.y, saved.contactPos.z);

                    cue.intersectionPoint = intersection;
                    cue.contactPos = contact;
                    cue.contactRotation = saved.contactRotation != null ? saved.contactRotation.ToQuaternion() : Quaternion.identity;
                    cue.aimJudgement = GetAimJudgementFromDistance((contact - intersection).magnitude);
                }

                if (saved.behavior == Target.TargetBehavior.Chain)
                {
                    cue.isChainTail = saved.isChainTail ?? false;
                    if (cue.isChainTail && saved.chainAverage.HasValue)
                    {
                        cue.chainAverage = saved.chainAverage.Value;
                        cue.chainJudgement = GetChainJudgement(cue.chainAverage);
                    }
                }

                if (saved.behavior == Target.TargetBehavior.Hold && saved.sustainPercent.HasValue)
                {
                    cue.sustainPercent = saved.sustainPercent.Value;
                }

                if (saved.behavior == Target.TargetBehavior.Melee && saved.velocity.HasValue)
                {
                    cue.velocity = saved.velocity.Value;
                }

                result.Add(cue);
            }

            return result;
        }

        /// <summary>
        /// Sums judgement score/max across a reconstructed ExCue list, mirroring the live per-cue
        /// accumulation in Hooks.cs. Miss counting matches GameplayStatsUI.GetMiscString's relevant
        /// set (aim-behaviors + Melee).
        /// </summary>
        private static void SumJudgementScore(List<ExCue> exCues, out float judgementScore, out float maxJudgementScore, out int missCount)
        {
            judgementScore = 0f;
            maxJudgementScore = 0f;
            missCount = 0;

            foreach (var cue in exCues)
            {
                maxJudgementScore += GetMaxJudgementScoreForSavedCue(cue.behavior, cue.isChainTail);

                bool countsTowardMiss = aimBehaviors.Contains(cue.behavior) || cue.behavior == Target.TargetBehavior.Melee;

                if (cue.miss)
                {
                    if (countsTowardMiss) missCount++;
                    continue;
                }

                float cueScore = 0f;

                if (aimBehaviors.Contains(cue.behavior))
                {
                    cueScore += GetJudgementScore(cue.timingJudgement) + GetJudgementScore(cue.aimJudgement);
                }
                if (cue.behavior == Target.TargetBehavior.Chain && cue.isChainTail)
                {
                    cueScore += GetJudgementScore(cue.chainJudgement);
                }
                if (cue.behavior == Target.TargetBehavior.Hold && cue.sustainPercent >= 1f)
                {
                    cueScore += 1f;
                }
                if (cue.behavior == Target.TargetBehavior.Melee && cue.velocity != 0f)
                {
                    cueScore += 1f;
                }

                judgementScore += cueScore;
            }
        }

        /// <summary>
        /// Recalculates one saved run's judgement stats from raw disk data. chainTailLookup is
        /// the legacy-repair fallback from BuildChainTailLookup — pass an empty dictionary if
        /// you know the run predates none of it, or the shared one built once per song+difficulty
        /// in LoadHistoryForSong. Main thread only (builds Unity Vector3/Quaternion values) — the
        /// background loader uses SummarizeRun instead, which must be kept in sync with this.
        /// </summary>
        public static RecalculatedRun Recalculate(ScoreSaveData data, string sourceFileName, Dictionary<float, bool> chainTailLookup)
        {
            List<ExCue> exCues = RecalculateExCues(data.exCues ?? new ExCueSaveData[0], chainTailLookup);
            SumJudgementScore(exCues, out float judgementScore, out float maxJudgementScore, out int missCount);

            return new RecalculatedRun
            {
                songId = data.songId,
                difficulty = data.difficulty,
                unixTimestamp = data.unixTimestamp,
                sourceFileName = sourceFileName,
                judgementScore = judgementScore,
                maxJudgementScore = maxJudgementScore,
                missCount = missCount,
                fullCombo = missCount == 0,
                failed = data.failed,
                exCues = exCues
            };
        }

        /// <summary>
        /// Builds a RecalculatedRun from a GET /api/runs/:runId response (see ApiContract.md
        /// Section 6, RunDetailApiResponse in Classes.cs) — the leaderboard-stats-panel equivalent
        /// of Recalculate() above. Unlike Recalculate(), which re-derives judgementScore/
        /// maxJudgementScore/missCount/fullCombo purely from raw disk data, this trusts the
        /// server's own values for those so the panel's numbers always match what's already shown
        /// on the leaderboard row that was shot — RecalculateExCues is still used, but only to get
        /// each cue's timing/aim/chain Judgement for the graphs. chainTailLookup is built fresh per
        /// call (same legacy-repair fallback as BuildChainTailLookup) since a leaderboard row can be
        /// for any song+difficulty, not just the one currently selected.
        /// </summary>
        public static RecalculatedRun RecalculateFromApiResponse(RunDetailApiResponse response)
        {
            if (response == null) return null;

            Dictionary<float, bool> chainTailLookup = BuildChainTailLookup(response.songId, response.difficulty);
            List<ExCue> exCues = RecalculateExCues(response.exCues ?? new ExCueSaveData[0], chainTailLookup);

            return new RecalculatedRun
            {
                songId = response.songId,
                difficulty = response.difficulty,
                gunColors = response.gunColors,
                unixTimestamp = response.unixTimestamp,
                sourceFileName = null, // sourced from the API, not a local file
                judgementScore = response.judgementScore,
                maxJudgementScore = response.maxJudgementScore,
                missCount = response.missCount,
                fullCombo = response.fullCombo,
                failed = response.failed,
                exCues = exCues
            };
        }

        /// <summary>
        /// Best (highest judgement percent) saved run for a song+difficulty, totals only. This is
        /// what song-list rows and the in-game top-score readout use: the whole lookup — listing,
        /// gzip, JSON, scoring — runs on the background loader thread, and the main thread only
        /// picks the winner. Invokes onComplete with null when there are no saved runs.
        ///
        /// The one main-thread cost left is the legacy chain-tail repair (BuildChainTailLookup,
        /// which has to load the chart through Il2Cpp): only for songs that actually have old saves
        /// needing it, and never more than one per frame.
        /// </summary>
        public static IEnumerator LoadBestRunSummaryForSong(string songId, string difficulty, Action<RunSummary> onComplete)
        {
            RunLoadJob job = EnqueueRunLoad(songId, difficulty, true);
            while (!job.done) yield return null;

            LogRunLoadErrors(job);

            bool needsLookup = false;
            for (int i = 0; i < job.files.Count; i++)
                if (job.files[i].needsChainTailLookup) { needsLookup = true; break; }

            Dictionary<float, bool> chainTailLookup = emptyChainTailLookup;
            if (needsLookup)
            {
                while (Time.frameCount == lastChainTailLookupFrame) yield return null;
                lastChainTailLookupFrame = Time.frameCount;
                chainTailLookup = BuildChainTailLookup(songId, difficulty);
            }

            RunSummary best = null;
            for (int i = 0; i < job.files.Count; i++)
            {
                LoadedRunFile file = job.files[i];
                RunSummary summary = file.summary;

                if (summary == null)
                {
                    try
                    {
                        summary = SummarizeRun(file.data, chainTailLookup);
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Log($"[ExScoring] Failed to summarize run file {file.fileName}: {ex}");
                        continue;
                    }
                }

                if (best == null || summary.judgementPercent > best.judgementPercent)
                    best = summary;
            }

            onComplete?.Invoke(best);
        }

        /// <summary>
        /// Full history for a song+difficulty (per-cue ExCue lists included, for the song page's
        /// history/top-score/graphs). Reading, decompressing and parsing every run file happens on
        /// the background loader thread; only the ExCue rebuild runs here (it creates Unity
        /// Vector3/Quaternion values, which the worker thread isn't allowed to touch), capped to a
        /// small time slice per frame. Invokes onComplete with the results, most recent run first.
        /// </summary>
        public static IEnumerator LoadHistoryForSong(string songId, string difficulty, Action<List<RecalculatedRun>> onComplete)
        {
            RunLoadJob job = EnqueueRunLoad(songId, difficulty, false);
            while (!job.done) yield return null;

            LogRunLoadErrors(job);

            List<RecalculatedRun> results = new List<RecalculatedRun>(job.files.Count);

            // Built once for the whole batch, and only if some file here is a legacy save that
            // will actually consult it (see NeedsChainTailLookup / ResolveIsChainTail).
            bool needsLookup = false;
            for (int i = 0; i < job.files.Count; i++)
                if (job.files[i].needsChainTailLookup) { needsLookup = true; break; }

            Dictionary<float, bool> chainTailLookup = emptyChainTailLookup;
            if (needsLookup)
            {
                while (Time.frameCount == lastChainTailLookupFrame) yield return null;
                lastChainTailLookupFrame = Time.frameCount;
                chainTailLookup = BuildChainTailLookup(songId, difficulty);
                yield return null; // the chart load was this frame's share of work
            }

            const double frameBudgetMs = 2.0;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            for (int i = 0; i < job.files.Count; i++)
            {
                LoadedRunFile file = job.files[i];

                try
                {
                    results.Add(Recalculate(file.data, file.fileName, chainTailLookup));
                }
                catch (Exception ex)
                {
                    MelonLogger.Log($"[ExScoring] Failed to recalculate run file {file.fileName}: {ex}");
                }

                file.data = null; // parsed JSON no longer needed once the ExCues exist

                if (stopwatch.Elapsed.TotalMilliseconds >= frameBudgetMs && i < job.files.Count - 1)
                {
                    yield return null;
                    stopwatch.Restart();
                }
            }

            onComplete?.Invoke(results);
        }
    }
}