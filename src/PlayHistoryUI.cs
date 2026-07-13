using System;
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
        /// Replaces the native play-history list (built from SongPlayHistory) with our own saved
        /// run data (recalculated from disk via RunDataRecalculator), whenever EX scoring is on —
        /// with no recorded runs for this song+difficulty, that means an empty list, not a
        /// fallback to native. When EX scoring is off, native history is left untouched.
        ///
        /// We don't skip the native BuildHistoryList/BuildDisplayHistory coroutines — their IL2CPP
        /// IEnumerator return type makes that awkward to fake from managed code — so when ExType
        /// is on, there's a brief native flash before this Postfix's coroutine overwrites history[]
        /// with our data a couple of frames later.
        /// </summary>
        [HarmonyPatch(typeof(SongInfoPanel), "OnEnable")]
        public static class SongInfoPanelHistoryPatch
        {
            public static void Postfix(SongInfoPanel __instance)
            {
                if (!Config.ExType) return;
                if (SongDataHolder.I == null || SongDataHolder.I.songData == null) return;

                string songId = SongDataHolder.I.songData.songID;
                string difficulty = KataConfig.I.GetDifficulty().ToString();

                MelonCoroutines.Start(PopulateHistoryCoroutine(__instance, songId, difficulty));
            }
        }

        private static IEnumerator PopulateHistoryCoroutine(SongInfoPanel panel, string songId, string difficulty)
        {
            // Let the native BuildHistoryList/BuildDisplayHistory coroutines finish their (soon to
            // be overwritten) pass first, so we're not fighting over history[] on the same frame.
            yield return null;
            yield return null;

            yield return LoadHistoryForSong(songId, difficulty, results =>
            {
                ApplyHistoryToPanel(panel, results);
            });
        }

        private static void ApplyHistoryToPanel(SongInfoPanel panel, List<RecalculatedRun> results)
        {
            if (panel == null) return;

            var items = panel.history;
            if (items == null) return;

            ResetHistorySelection();

            int count = items.Count;
            for (int i = 0; i < count; i++)
            {
                var item = items[i];
                if (item == null) continue;

                if (i < results.Count)
                {
                    SetHistoryItem(item, results[i], i);
                    item.gameObject.SetActive(true);

                    historyHitboxRuns[i] = results[i];
                    EnsureHistoryHitbox(i, item);
                }
                else
                {
                    item.gameObject.SetActive(false);
                    historyHitboxRuns.Remove(i);
                    ClearRowGradeVisual(i);
                }
            }

            if (panel.historyScroll != null)
                panel.historyScroll.QueueUpdateSize();

            MelonLogger.Log($"[ExScoring] Populated {Math.Min(results.Count, count)} history row(s) from saved run data.");
        }

        // Placeholder formatting — score/percent/info text is easy to retune once we see it in-game.
        private static void SetHistoryItem(SongInfoHistoryItem item, RecalculatedRun run, int slot)
        {
            if (item.date != null)
                item.date.text = DateTimeOffset.FromUnixTimeSeconds(run.unixTimestamp).LocalDateTime.ToString("MMM d, yyyy");

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

            if (item.info != null)
            {
                Grade grade = GetGrade(run.judgementPercent, run.failed);
                item.info.gameObject.SetActive(true);

                if (IsStarGrade(grade))
                {
                    // Clear the text and grow the mini star visual as a child of this same
                    // transform instead — keeps info active as an anchor point.
                    item.info.text = "";
                    CreateOrUpdateRowGradeVisual(slot, item.info.transform, grade);
                }
                else
                {
                    ClearRowGradeVisual(slot);
                    item.info.text = GetGradeText(grade);
                    item.info.color = GetGradeColor(grade);
                }
            }

            // No per-run star rating exists yet in our data — hide rather than show something wrong.
            if (item.stars != null)
                item.stars.gameObject.SetActive(false);
        }
    }
}