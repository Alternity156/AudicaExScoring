using System;
using System.Collections;
using System.Collections.Generic;
using Harmony;
using MelonLoader;

namespace ExScoringMod
{
    /// <summary>
    /// Auto Restart (ported from Grind Mode's highscore mode): restarts the song as soon as the
    /// player can no longer at least tie their best score on the current scoring type.
    ///
    ///   Audica mode: native ScoreKeeper.mScore + a simulated perfect finish over every cue not
    ///                scored yet (from the current streak/multiplier) vs ScoreKeeper.GetHighScore().
    ///                The simulation errs high, so it can only restart a little late, never early.
    ///   EX mode:     exact — judgementScore + whatever max is still unjudged, over the whole-chart
    ///                max (same as GetFinalJudgementPercentage), vs the best EX % the in-game high
    ///                score shows (exRowScoreCache, local + online bests merged).
    ///
    /// Checked one frame after every ScoreKeeper.OnSuccess / OnFailure, so native score and our
    /// EX commit (CommitExCue) have both run, whatever order the Harmony postfixes ran in.
    /// Never fires in practice mode, No Fail, calibration, endless/marathon playlists, online
    /// matches, after a fail, or when there is no best score to compare against.
    /// </summary>
    public partial class ExScoring : MelonMod
    {
        public static class AutoRestart
        {
            // Native Audica point values used for the perfect-finish simulation. Deliberately
            // generous (hold = hit + full sustain, every cue advances the streak) so the estimate
            // is never below what's really reachable.
            private const int AudicaTargetPoints = 2000;
            private const int AudicaChainNodePoints = 125;
            private const int AudicaSustainPoints = 3000;
            private const int AudicaChainSustainBonusPoints = 1000;
            private const int AudicaMaxMultiplier = 4;

            // Small tolerance so a run that can exactly tie the best (float noise) isn't restarted.
            private const float ExPercentEpsilon = 0.001f;

            private static bool fired = false;
            private static bool checkQueued = false;

            // Per-run caches, keyed by songId|difficulty so a stale cache can never be used.
            private static string cacheKey = null;
            private static SongCues.Cue[] cachedCues = null;
            private static float cachedTotalMaxJudgement = -1f;

            /// <summary>Called from ResetExScore (song restart, leaving gameplay).</summary>
            public static void Reset()
            {
                fired = false;
                checkQueued = false;
                cacheKey = null;
                cachedCues = null;
                cachedTotalMaxJudgement = -1f;
            }

            private static void QueueCheck()
            {
                if (!Config.AutoRestartOnHighscoreLoss) return;
                if (fired || checkQueued) return;

                checkQueued = true;
                MelonCoroutines.Start(CheckNextFrame());
            }

            private static IEnumerator CheckNextFrame()
            {
                yield return null;
                checkQueued = false;

                try
                {
                    Check();
                }
                catch (Exception e)
                {
                    DebugLog.Log($"[AutoRestart] Check failed: {e}");
                }
            }

            private static bool CanRun()
            {
                if (!Config.AutoRestartOnHighscoreLoss) return false;
                if (fired) return false;
                if (menuState != MenuState.State.Launched) return false;
                if (launchedOutsideSongFlow) return false;
                if (selectedSongData == null) return false;
                if (KataConfig.I == null || KataConfig.I.practiceMode || KataConfig.I.NoFail()) return false;
                if (PlaylistEndlessManager.EndlessActive) return false;
                if (MpGameplay.InMatchRun || MpGameplay.ShouldBlockRestart()) return false;
                if (ScoreKeeper.I == null || ScoreKeeper.I.mFailed) return false;
                if (InGameUI.I == null) return false;
                return true;
            }

            private static void Check()
            {
                if (!CanRun()) return;

                string songId = selectedSongData.songID;
                KataConfig.Difficulty difficulty = KataConfig.I.GetDifficulty();
                string key = ExRowCacheKey(songId, difficulty);

                if (cacheKey != key)
                {
                    cacheKey = key;
                    cachedCues = null;
                    cachedTotalMaxJudgement = -1f;
                }

                bool cannotTie = Config.ExType
                    ? ExCannotTieBest(songId, difficulty, key)
                    : AudicaCannotTieBest(difficulty);

                if (cannotTie) Restart();
            }

            // ── EX ──────────────────────────────────────────────────────────

            private static bool ExCannotTieBest(string songId, KataConfig.Difficulty difficulty, string key)
            {
                if (!TryGetExBest(songId, difficulty, key, out float bestPercent)) return false;
                if (bestPercent <= 0f) return false;

                if (cachedTotalMaxJudgement < 0f)
                    cachedTotalMaxJudgement = GetMaxPossibleJudgementScore(songId, difficulty);

                float totalMax = Math.Max(cachedTotalMaxJudgement, currentMaxPossibleJudgementScore);
                if (totalMax <= 0f) return false;

                float remainingMax = Math.Max(0f, totalMax - currentMaxPossibleJudgementScore);
                float bestPossiblePercent = (judgementScore + remainingMax) / totalMax * 100f;

                if (bestPossiblePercent + ExPercentEpsilon < bestPercent)
                {
                    DebugLog.Log($"[AutoRestart] EX: best possible {bestPossiblePercent:F2}% < best {bestPercent:F2}%, restarting.");
                    return true;
                }
                return false;
            }

            /// <summary>
            /// Same lookup the in-game high score uses (GetExTopScoreDisplayText): starts the load
            /// if needed and returns false until it has resolved, or when there's no saved best.
            /// </summary>
            private static bool TryGetExBest(string songId, KataConfig.Difficulty difficulty, string key, out float bestPercent)
            {
                bestPercent = 0f;

                if (exRowScoreCache.TryGetValue(key, out CachedExRowScore cached))
                {
                    bestPercent = cached.judgementPercent;
                    return true;
                }

                if (exRowScoreEmpty.Contains(key)) return false;

                if (!exRowScoreLoading.Contains(key))
                {
                    exRowScoreLoading.Add(key);
                    MelonCoroutines.Start(LoadRowScoreCoroutine(songId, difficulty, key));
                }
                return false;
            }

            // ── Audica ──────────────────────────────────────────────────────

            private static bool AudicaCannotTieBest(KataConfig.Difficulty difficulty)
            {
                ScoreKeeper scoreKeeper = ScoreKeeper.I;

                int highscore = scoreKeeper.GetHighScore();
                if (highscore <= 0) return false;

                SongCues.Cue[] cues = GetCachedCues(difficulty);
                if (cues == null || cues.Length == 0) return false;

                long bestPossible = (long)scoreKeeper.mScore + SimulatePerfectFinish(cues, scoreKeeper.mStreak, scoreKeeper.mMultiplier);

                if (bestPossible < highscore)
                {
                    DebugLog.Log($"[AutoRestart] Audica: best possible {bestPossible} < highscore {highscore}, restarting.");
                    return true;
                }
                return false;
            }

            private static SongCues.Cue[] GetCachedCues(KataConfig.Difficulty difficulty)
            {
                if (cachedCues != null) return cachedCues;

                var nativeCues = SongCues.GetCues(selectedSongData, difficulty);
                if (nativeCues == null) return null;

                var list = new List<SongCues.Cue>(nativeCues.Length);
                foreach (SongCues.Cue cue in nativeCues)
                {
                    if (cue != null) list.Add(cue);
                }
                list.Sort((a, b) => a.tick.CompareTo(b.tick));

                cachedCues = list.ToArray();
                return cachedCues;
            }

            /// <summary>
            /// Points still reachable if every cue not scored yet is hit perfectly, starting from the
            /// current streak and multiplier. A cue counts as scored once it's in
            /// processedCuesIndexes (holds only land there when the sustain is finished).
            /// </summary>
            private static long SimulatePerfectFinish(SongCues.Cue[] cues, int streak, int multiplier)
            {
                var scored = new HashSet<int>(processedCuesIndexes);

                if (multiplier < 1) multiplier = 1;
                long total = 0;

                for (int i = 0; i < cues.Length; i++)
                {
                    SongCues.Cue cue = cues[i];
                    if (scored.Contains(cue.index)) continue;

                    // Streak first, like native IncreaseStreak: every 10th bumps the multiplier.
                    streak++;
                    if (streak % 10 == 0 && multiplier < AudicaMaxMultiplier) multiplier++;

                    int points;
                    if (ChainSustain.IsChainSustain(cue))
                    {
                        // Chain node + sustain bonus (its live behavior is Chain, then Hold once hit).
                        total += (long)(AudicaChainNodePoints + AudicaChainSustainBonusPoints) * multiplier;
                        continue;
                    }

                    switch (cue.behavior)
                    {
                        case Target.TargetBehavior.Chain:
                            points = AudicaChainNodePoints;
                            break;
                        case Target.TargetBehavior.Hold:
                            points = AudicaTargetPoints + AudicaSustainPoints;
                            break;
                        case Target.TargetBehavior.Dodge:
                            points = 0;
                            break;
                        default:
                            points = AudicaTargetPoints;
                            break;
                    }

                    total += (long)points * multiplier;
                }

                return total;
            }

            // ── Restart ─────────────────────────────────────────────────────

            private static void Restart()
            {
                fired = true;

                try
                {
                    KataUtil.PlayFMODEvent("event:/gameplay/overdrive_complete");
                }
                catch (Exception e)
                {
                    DebugLog.Log($"[AutoRestart] Sound failed: {e}");
                }

                InGameUI.I.Restart();
            }

            // ── Hooks ───────────────────────────────────────────────────────

            [HarmonyPatch(typeof(ScoreKeeper), "OnSuccess", new Type[] { typeof(SongCues.Cue) })]
            private static class ScoreKeeperOnSuccessPatch
            {
                private static void Postfix()
                {
                    QueueCheck();
                }
            }

            [HarmonyPatch(typeof(ScoreKeeper), "OnFailure", new Type[] { typeof(SongCues.Cue), typeof(bool), typeof(bool) })]
            private static class ScoreKeeperOnFailurePatch
            {
                private static void Postfix()
                {
                    QueueCheck();
                }
            }
        }
    }
}