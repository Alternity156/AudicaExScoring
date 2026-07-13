using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
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
        }

        /// <summary>Lists saved run files for a song+difficulty, most recent first.</summary>
        private static List<FileInfo> ListRunFiles(string songId, string difficulty)
        {
            if (!Directory.Exists(runDataDirectory)) return new List<FileInfo>();

            string sanitizedSongId = SanitizeFileName(songId);
            string sanitizedDifficulty = SanitizeFileName(difficulty);

            return Directory.GetFiles(runDataDirectory, "*.json.gz")
                .Select(f => new FileInfo(f))
                .Where(f =>
                {
                    var parsed = ParseRunFileName(f.Name);
                    return parsed.songId == sanitizedSongId && parsed.difficulty == sanitizedDifficulty;
                })
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
        }

        /// <summary>Decompresses and deserializes a single saved run file.</summary>
        private static ScoreSaveData LoadRunData(FileInfo file)
        {
            using (FileStream fileStream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read))
            using (GZipStream gzipStream = new GZipStream(fileStream, CompressionMode.Decompress))
            using (StreamReader reader = new StreamReader(gzipStream))
            {
                string json = reader.ReadToEnd();
                return JsonConvert.DeserializeObject<ScoreSaveData>(json, runDataSerializerSettings);
            }
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
        /// Rebuilds a live-shaped ExCue list from saved raw cue data, deriving timing/aim/chain
        /// judgements purely from numbers already on disk. Missed cues (which only save
        /// behavior/handType/tick/health/miss — see BuildExCueSaveData) are marked Miss outright
        /// rather than left at the enum's default value.
        /// </summary>
        private static List<ExCue> RecalculateExCues(ExCueSaveData[] savedCues)
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

        /// <summary>Recalculates one saved run's judgement stats from raw disk data.</summary>
        public static RecalculatedRun Recalculate(ScoreSaveData data, string sourceFileName)
        {
            List<ExCue> exCues = RecalculateExCues(data.exCues ?? new ExCueSaveData[0]);
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
        /// Batched coroutine: lists saved runs for a song+difficulty, then decompresses/parses/
        /// recalculates a few per frame so a stack of runs doesn't stall a frame. Invokes
        /// onComplete with the results, most recent run first.
        /// </summary>
        public static IEnumerator LoadHistoryForSong(string songId, string difficulty, Action<List<RecalculatedRun>> onComplete)
        {
            List<FileInfo> files = ListRunFiles(songId, difficulty);
            List<RecalculatedRun> results = new List<RecalculatedRun>(files.Count);

            const int batchSize = 3;
            int processedThisFrame = 0;

            foreach (FileInfo file in files)
            {
                try
                {
                    ScoreSaveData data = LoadRunData(file);
                    if (data != null)
                    {
                        results.Add(Recalculate(data, file.Name));
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Log($"[ExScoring] Failed to load/recalculate run file {file.Name}: {ex}");
                }

                processedThisFrame++;
                if (processedThisFrame >= batchSize)
                {
                    processedThisFrame = 0;
                    yield return null;
                }
            }

            onComplete?.Invoke(results);
        }
    }
}