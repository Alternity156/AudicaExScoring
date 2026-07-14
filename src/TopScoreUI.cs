using System.Collections;
using System.Collections.Generic;
using Harmony;
using MelonLoader;
using UnityEngine;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        /// <summary>
        /// Replaces each native Song Info top-score row (difficulty/score/percent/stars, built from
        /// HighScoreRecords) with our own best saved run (highest judgement percent, recalculated
        /// from disk via RunDataRecalculator) for that difficulty, whenever EX scoring is on. With
        /// no recorded runs for that difficulty, the row is hidden entirely rather than showing
        /// native or blank data. When EX scoring is off, native SetTopScore runs untouched.
        ///
        /// Unlike the Play History patch, SetTopScore is a plain (non-IEnumerator) method, so we can
        /// skip it outright via a Harmony Prefix instead of racing a native coroutine — no one-frame
        /// native flash here.
        /// </summary>
        [HarmonyPatch(typeof(SongInfoPanel), "SetTopScore")]
        public static class SongInfoPanelSetTopScorePatch
        {
            public static bool Prefix(HighScoreRecords.HighScoreInfo highScore, SongInfoTopScoreItem item)
            {
                if (!Config.ExType)
                {
                    // Was EX mode last time this row populated? Undo it (re-enable StarDisplayUI,
                    // reactivate its native pips/stars, drop our grade visual, reactivate the row
                    // itself in case we'd hidden it) so native SetTopScore has a clean slate.
                    RestoreNativeTopScoreItem(item, highScore);
                    return true; // native behavior
                }

                if (item == null) return false;

                if (highScore == null || SongDataHolder.I == null || SongDataHolder.I.songData == null)
                {
                    item.gameObject.SetActive(false);
                    return false;
                }

                string songId = SongDataHolder.I.songData.songID;
                KataConfig.Difficulty difficulty = highScore.difficulty;

                MelonCoroutines.Start(PopulateTopScoreCoroutine(songId, difficulty, item));

                return false; // skip native SetTopScore entirely — our coroutine populates (or hides) the row
            }
        }

        /// <summary>
        /// Undoes everything SetTopScoreItem does to one row: re-enables the StarDisplayUI script,
        /// reactivates every descendant we'd hidden under it, destroys our grade visual, and makes
        /// sure the row itself is active (we hide it entirely when there's no saved EX run). Safe to
        /// call on a row that was never touched by EX mode — every step is a no-op in that case.
        /// </summary>
        private static void RestoreNativeTopScoreItem(SongInfoTopScoreItem item, HighScoreRecords.HighScoreInfo highScore)
        {
            if (item == null) return;

            item.gameObject.SetActive(true);

            if (item.stars != null)
            {
                item.stars.enabled = true;
                ReactivateAllDescendants(item.stars.transform);
            }

            if (highScore != null)
                ClearTopScoreGradeVisual(highScore.difficulty);
        }

        /// <summary>
        /// Plants our one known local high score directly into HighScoreRecords' own per-difficulty
        /// cache (mDifficultyScores) before native's OnEnable body runs. This makes
        /// RequestDifficultyScores' own early-exit check (if (!ContainsKey(songID)) { ...network
        /// request... }, per its Ghidra decompile) see data already present and skip the network path
        /// entirely, letting native drive its own correct rendering (SetTopScore, star display, all
        /// of it) from the start — no need to fight or reassert against native afterward.
        ///
        /// Must be a Prefix, not a Postfix: native's OnEnable likely starts the UpdateHighScores()
        /// coroutine synchronously up to its first yield, which could mean RequestDifficultyScores
        /// already ran before a Postfix ever got a chance to inject anything.
        ///
        /// Only ever injects the ONE score we can find locally (HighScoreRecords.GetHighScore, a
        /// PlayerPrefs-backed read confirmed via decompile to track exactly one high score per song
        /// regardless of difficulty) — there's no local source for the other three difficulties (only
        /// the network round-trip has that), so this can't make all four rows appear on its own, only
        /// ensure the one we do know about is never clobbered by native finding nothing.
        /// </summary>
        [HarmonyPatch(typeof(SongInfoPanel), "OnEnable")]
        public static class SongInfoPanelInjectNativeScorePatch
        {
            public static void Prefix(SongInfoPanel __instance)
            {
                if (SongDataHolder.I == null || SongDataHolder.I.songData == null) return;
                if (HighScoreRecords.I == null) return;

                string songId = SongDataHolder.I.songData.songID;

                var dict = HighScoreRecords.I.mDifficultyScores;
                if (dict == null) return;
                if (dict.ContainsKey(songId)) return; // native (or an earlier injection) already has something for this song

                HighScoreRecords.HighScoreInfo localBest = HighScoreRecords.GetHighScore(songId);
                if (localBest == null) return; // genuinely no local score at all — nothing to inject

                var list = new Il2CppSystem.Collections.Generic.List<HighScoreRecords.HighScoreInfo>();
                list.Add(localBest);
                dict.Add(songId, list);
            }
        }

        private static IEnumerator PopulateTopScoreCoroutine(string songId, KataConfig.Difficulty difficulty, SongInfoTopScoreItem item)
        {
            yield return LoadHistoryForSong(songId, difficulty.ToString(), results =>
            {
                ApplyTopScoreToItem(item, difficulty, results);
            });
        }

        private static void ApplyTopScoreToItem(SongInfoTopScoreItem item, KataConfig.Difficulty difficulty, List<RecalculatedRun> results)
        {
            if (item == null) return;

            // Best = highest judgement percent, not most recent (LoadHistoryForSong returns
            // most-recent-first, so this can't just take index 0).
            RecalculatedRun best = null;
            for (int i = 0; i < results.Count; i++)
            {
                if (best == null || results[i].judgementPercent > best.judgementPercent)
                    best = results[i];
            }

            if (best == null)
            {
                item.gameObject.SetActive(false);
                ClearTopScoreGradeVisual(difficulty);
                return;
            }

            SetTopScoreItem(item, best, difficulty);
            item.gameObject.SetActive(true);
        }

        // Placeholder formatting — score/percent text is easy to retune once we see it in-game,
        // same as PlayHistoryUI's SetHistoryItem.
        private static void SetTopScoreItem(SongInfoTopScoreItem item, RecalculatedRun run, KataConfig.Difficulty difficulty)
        {
            if (item.difficulty != null)
                item.difficulty.text = run.difficulty;

            if (item.score != null)
            {
                item.score.gameObject.SetActive(true);
                item.score.text = run.judgementScore.ToString("0.00");
            }

            if (item.percent != null)
            {
                item.percent.gameObject.SetActive(true);
                item.percent.text = $"{run.judgementPercent:0.00}%";
            }

            Grade grade = GetGrade(run.judgementPercent, run.failed);

            if (item.stars != null)
            {
                // Keep the StarDisplayUI's own root GameObject active — it's our anchor for the
                // grade visual below — but stop its own script logic and hide every native pip/star
                // underneath it (they sit a couple of levels deep, not directly on the root, per
                // the 10-object hierarchy confirmed in UnityExplorer).
                item.stars.enabled = false;
                HideAllDescendants(item.stars.transform);

                CreateOrUpdateTopScoreGradeVisual(difficulty, item.stars.transform, grade);
            }
        }

        /// <summary>Disables every descendant of `root` (any depth) while leaving `root` itself
        /// active, so it can still host our own child grade visual.</summary>
        private static void HideAllDescendants(Transform root)
        {
            if (root == null) return;

            var all = root.GetComponentsInChildren<Transform>(true);
            foreach (var t in all)
            {
                if (t == root) continue;
                t.gameObject.SetActive(false);
            }
        }

        /// <summary>Inverse of HideAllDescendants — reactivates every descendant of `root` (any
        /// depth), restoring native StarDisplayUI pips/stars to their normal visible state.</summary>
        private static void ReactivateAllDescendants(Transform root)
        {
            if (root == null) return;

            var all = root.GetComponentsInChildren<Transform>(true);
            foreach (var t in all)
            {
                if (t == root) continue;
                t.gameObject.SetActive(true);
            }
        }
    }
}