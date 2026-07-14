using System.Collections;
using System.Collections.Generic;
using Harmony;
using MelonLoader;
using UnhollowerBaseLib;
using UnityEngine;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        private class CachedExRowScore
        {
            public float judgementScore;
            public float judgementPercent;
            public bool failed;
        }

        // Best (highest judgement percent) saved EX run per song+difficulty, keyed "songId|difficulty".
        // Populated lazily the first time a song-list row needs it (never eagerly for the whole
        // library — unlike FolderRowManager's native starCache, this needs a disk read per entry).
        // Invalidated per-key from RunDataSaveHandler.SaveRunData whenever a fresh run is saved.
        private static readonly Dictionary<string, CachedExRowScore> exRowScoreCache = new Dictionary<string, CachedExRowScore>();

        // Confirmed-empty keys (looked up, no saved runs at all) tracked separately from the main
        // cache so "no result" doesn't repeatedly re-trigger a disk read on every rebind.
        private static readonly HashSet<string> exRowScoreEmpty = new HashSet<string>();

        // In-flight lookups, so fast scrolling doesn't kick off duplicate LoadHistoryForSong
        // coroutines for the same song+difficulty while one is already running.
        private static readonly HashSet<string> exRowScoreLoading = new HashSet<string>();

        private static string ExRowCacheKey(string songId, KataConfig.Difficulty difficulty) => songId + "|" + difficulty;

        /// <summary>
        /// Forces every currently on-screen song-list row to redo its high-score/percent/stars
        /// display immediately, by calling UpdateHighScoreInfo() directly on whatever's bound.
        /// Needed because VirtualSongList's pooled rows skip Init() (and so UpdateHighScoreInfo)
        /// when re-bound to the same song they already showed — which a generic list refresh
        /// (FolderRowManager.RefreshList) still hits, since it releases and rebinds without clearing
        /// that per-slot "already showing this song" memory. Call this whenever Config.ExType changes
        /// (see Config.SetScoringType), so returning from the options menu updates the list right
        /// away instead of needing an unrelated rebind (e.g. closing/reopening a folder) first.
        /// </summary>
        public static void RefreshAllVisibleSongRowScores()
        {
            int viewCount = VirtualSongList.CurrentViewSongIDs.Count;
            int boundCount = 0;

            foreach (string songId in VirtualSongList.CurrentViewSongIDs)
            {
                SongSelectItem item = VirtualSongList.GetBoundItem(songId);
                if (item != null)
                {
                    boundCount++;
                    item.UpdateHighScoreInfo();
                }
            }

            MelonLogger.Log($"[ExScoring] RefreshAllVisibleSongRowScores: {boundCount}/{viewCount} row(s) bound and refreshed.");
        }

        // The EX layout nudges highScoreLabel over to make room for the longer judgement-score
        // text. Native position is captured per row (keyed by row GameObject instance ID) the first
        // time we touch it, so reverting to Audica scoring puts it back exactly rather than assuming
        // a hardcoded default — pooled rows all share the same prefab default anyway, but this is
        // correct even if that ever isn't true.
        private static readonly Dictionary<int, Vector3> nativeHighScoreLabelPositions = new Dictionary<int, Vector3>();
        private static readonly Vector3 ExHighScoreLabelLocalPosition = new Vector3(52.5f, 20f, 0f);

        /// <summary>Moves highScoreLabel to the EX layout position, capturing its native position
        /// first (once per row) if we haven't already.</summary>
        private static void ApplyExRowLayout(SongSelectItem item)
        {
            if (item == null || item.highScoreLabel == null) return;

            int key = item.gameObject.GetInstanceID();
            if (!nativeHighScoreLabelPositions.ContainsKey(key))
                nativeHighScoreLabelPositions[key] = item.highScoreLabel.transform.localPosition;

            item.highScoreLabel.transform.localPosition = ExHighScoreLabelLocalPosition;
        }

        /// <summary>Puts highScoreLabel back at its captured native position, if we ever moved it.</summary>
        private static void RestoreNativeRowLayout(SongSelectItem item)
        {
            if (item == null || item.highScoreLabel == null) return;

            int key = item.gameObject.GetInstanceID();
            if (nativeHighScoreLabelPositions.TryGetValue(key, out Vector3 pos))
                item.highScoreLabel.transform.localPosition = pos;
        }

        /// <summary>
        /// Replaces a song-list row's native high-score/percent/stars with our best saved EX run for
        /// that song+difficulty, whenever EX scoring is on. Rows are pooled and rebound to different
        /// songs as the list scrolls (VirtualSongList) rather than rebuilt every frame, but the
        /// library can still be big enough that this needs caching rather than reading from disk on
        /// every bind.
        ///
        /// Back to a Prefix-skip (return false) for the EX branch — a Postfix-based "let native run,
        /// override after" design was tried and reverted: whatever native's own high-score lookup
        /// does isn't fully synchronous (likely a deferred/async leaderboard or save-data fetch), so
        /// it was overwriting our override sometime after this method returned, well after our
        /// override had already applied. Skipping native outright for EX rows avoids starting that
        /// process at all.
        ///
        /// That reintroduces the original "stars stuck hidden after switching to Audica" problem, but
        /// the actual fix for that turned out to be about WHICH GameObjects get hidden, not whether
        /// native gets to run: StarDisplay exposes its five per-difficulty tier arrays directly
        /// (starsEasy/starsNormal/starsHard/starsExpert/starsExpertGold) plus starMeters. Toggling
        /// only those exact leaf objects — rather than every descendant via a blind recursive hide —
        /// means native's own SetStarsForScore (called normally once we return true below, no skip
        /// needed) can always correctly re-manage them later, since we never touch any wrapping
        /// container it assumes stays active.
        ///
        /// With no cached result yet, the row's high-score UI is simply blanked while the lookup runs
        /// in the background — once the lookup resolves, we apply it directly to the row via
        /// VirtualSongList.GetBoundItem if it's still on-screen showing this song+difficulty (a
        /// generic "refresh" doesn't help here: VirtualSongList skips Init()/UpdateHighScoreInfo for
        /// a row already bound to the same song). With no saved run at all for that song+difficulty,
        /// the row's high-score UI stays hidden.
        /// </summary>
        [HarmonyPatch(typeof(SongSelectItem), "UpdateHighScoreInfo")]
        public static class SongSelectItemUpdateHighScoreInfoPatch
        {
            public static bool Prefix(SongSelectItem __instance)
            {
                if (!Config.ExType)
                {
                    RestoreNativeRowLayout(__instance);
                    if (__instance != null)
                    {
                        // Simple leaf labels — safe to force active regardless. If this song
                        // genuinely has no native high score, native's own no-score path hides them
                        // again immediately in this same call, so there's no visible flash either way.
                        if (__instance.highScoreLabel != null) __instance.highScoreLabel.gameObject.SetActive(true);
                        if (__instance.percentLabel != null) __instance.percentLabel.gameObject.SetActive(true);

                        ClearSongRowGradeVisual(__instance.gameObject.GetInstanceID());
                        ShowNativeStarPips(__instance);
                    }
                    return true; // native runs fully — it correctly (re)manages its own star arrays
                }

                if (__instance == null || __instance.mSongData == null) return false;

                string songIdDiag = __instance.mSongData.songID;
                MelonLogger.Log($"[ExScoring][Diag] Prefix ENTER (EX) song={songIdDiag}");

                ApplyExRowLayout(__instance);

                string songId = __instance.mSongData.songID;
                KataConfig.Difficulty difficulty = KataConfig.I.GetDifficulty();
                string key = ExRowCacheKey(songId, difficulty);

                if (exRowScoreCache.TryGetValue(key, out CachedExRowScore cached))
                {
                    ApplySongRowScore(__instance, cached);
                    MelonLogger.Log($"[ExScoring][Diag] Prefix APPLIED song={songIdDiag}");
                    return false;
                }

                HideSongRowScore(__instance);
                MelonLogger.Log($"[ExScoring][Diag] Prefix HIDDEN song={songIdDiag}");

                if (exRowScoreEmpty.Contains(key)) return false; // confirmed no saved runs — stay hidden

                if (!exRowScoreLoading.Contains(key))
                {
                    exRowScoreLoading.Add(key);
                    MelonCoroutines.Start(LoadRowScoreCoroutine(songId, difficulty, key));
                }

                return false;
            }
        }

        // TEMPORARY: watches for anything touching these fields AFTER our patch already ran this
        // frame, since two rounds of guessing have both pointed to something ELSE re-asserting
        // native state afterward. Throttled to only log on an actual change per row, so it won't
        // spam every frame for rows that are stable.
        private static readonly Dictionary<int, string> lastLoggedRowSnapshot = new Dictionary<int, string>();

        [HarmonyPatch(typeof(SongSelectItem), "Update")]
        public static class SongSelectItemUpdateDiagnosticPatch
        {
            public static void Postfix(SongSelectItem __instance)
            {
                if (__instance == null || __instance.mSongData == null) return;

                string songId = __instance.mSongData.songID;
                string hs = __instance.highScoreLabel != null
                    ? $"active={__instance.highScoreLabel.gameObject.activeSelf} text='{__instance.highScoreLabel.text}'"
                    : "<null>";
                string pc = __instance.percentLabel != null
                    ? $"active={__instance.percentLabel.gameObject.activeSelf} text='{__instance.percentLabel.text}'"
                    : "<null>";
                string sd = __instance.starDisplay != null
                    ? $"enabled={__instance.starDisplay.enabled} easyActive={CountActiveArray(__instance.starDisplay.starsEasy)} normalActive={CountActiveArray(__instance.starDisplay.starsNormal)} hardActive={CountActiveArray(__instance.starDisplay.starsHard)} expertActive={CountActiveArray(__instance.starDisplay.starsExpert)} goldActive={CountActiveArray(__instance.starDisplay.starsExpertGold)}"
                    : "<null>";

                string snapshot = $"{songId} | hs: {hs} | pc: {pc} | stars: {sd}";

                int key = __instance.gameObject.GetInstanceID();
                if (!lastLoggedRowSnapshot.TryGetValue(key, out string prev) || prev != snapshot)
                {
                    MelonLogger.Log($"[ExScoring][UpdateDiag] {snapshot}");
                    lastLoggedRowSnapshot[key] = snapshot;
                }
            }
        }

        private static int CountActiveArray(Il2CppReferenceArray<GameObject> arr)
        {
            if (arr == null) return -1;
            int count = 0;
            for (int i = 0; i < arr.Length; i++)
                if (arr[i] != null && arr[i].activeSelf) count++;
            return count;
        }

        private static IEnumerator LoadRowScoreCoroutine(string songId, KataConfig.Difficulty difficulty, string key)
        {
            yield return LoadHistoryForSong(songId, difficulty.ToString(), results =>
            {
                RecalculatedRun best = null;
                for (int i = 0; i < results.Count; i++)
                {
                    if (best == null || results[i].judgementPercent > best.judgementPercent)
                        best = results[i];
                }

                if (best != null)
                {
                    exRowScoreCache[key] = new CachedExRowScore
                    {
                        judgementScore = best.judgementScore,
                        judgementPercent = best.judgementPercent,
                        failed = best.failed
                    };
                }
                else
                {
                    exRowScoreEmpty.Add(key);
                }
            });

            exRowScoreLoading.Remove(key);

            if (!Config.ExType) yield break; // scoring type flipped mid-lookup — leave it to the restore path

            // Apply straight to the row if it's still on-screen and still showing this exact
            // song+difficulty — VirtualSongList skips Init() (and so UpdateHighScoreInfo) for a row
            // already bound to the same song, so forcing a rebind wouldn't reach it; GetBoundItem
            // lets us update it directly instead.
            SongSelectItem item = VirtualSongList.GetBoundItem(songId);
            if (item == null) yield break; // scrolled off-screen — next bind picks up the warm cache
            if (KataConfig.I.GetDifficulty() != difficulty) yield break; // displaying difficulty changed mid-lookup

            if (exRowScoreCache.TryGetValue(key, out CachedExRowScore cached))
                ApplySongRowScore(item, cached);
            else
                HideSongRowScore(item); // confirmed empty — already blanked, just keeping state consistent
        }

        private static void HideSongRowScore(SongSelectItem item)
        {
            if (item == null) return;

            if (item.highScoreLabel != null) item.highScoreLabel.gameObject.SetActive(false);
            if (item.percentLabel != null) item.percentLabel.gameObject.SetActive(false);

            HideNativeStarArrays(item.starDisplay);
            ClearSongRowGradeVisual(item.gameObject.GetInstanceID());
        }

        private static void ApplySongRowScore(SongSelectItem item, CachedExRowScore cached)
        {
            if (item == null) return;

            if (item.highScoreLabel != null)
            {
                item.highScoreLabel.gameObject.SetActive(true);
                item.highScoreLabel.text = cached.judgementScore.ToString("0.00");
            }

            if (item.percentLabel != null)
            {
                item.percentLabel.gameObject.SetActive(true);
                item.percentLabel.text = $"{cached.judgementPercent:0.00}%";
            }

            Grade grade = GetGrade(cached.judgementPercent, cached.failed);

            HideNativeStarArrays(item.starDisplay);
            if (item.starDisplay != null)
                CreateOrUpdateSongRowGradeVisual(item.gameObject.GetInstanceID(), item.starDisplay.transform, grade);
        }

        /// <summary>
        /// Hides exactly the leaf objects StarDisplay itself manages (its five per-difficulty tier
        /// arrays, plus the starMeters renderers) — nothing else. Deliberately NOT a recursive
        /// "hide every descendant" sweep: that also catches wrapping container objects native's own
        /// SetStarsForScore never touches (it assumes they're already active), which is what left
        /// pips permanently stuck invisible in an earlier version of this patch. Native re-managing
        /// these exact same arrays later (once EX is off and this method isn't skipped) always
        /// correctly restores them — no manual "undo" needed on our end at all.
        /// </summary>
        private static void HideNativeStarArrays(StarDisplay stars)
        {
            if (stars == null)
            {
                MelonLogger.Log("[ExScoring][Diag] HideNativeStarArrays: stars is NULL");
                return;
            }

            int easy = HideStarArray(stars.starsEasy, "Easy");
            int normal = HideStarArray(stars.starsNormal, "Normal");
            int hard = HideStarArray(stars.starsHard, "Hard");
            int expert = HideStarArray(stars.starsExpert, "Expert");
            int gold = HideStarArray(stars.starsExpertGold, "ExpertGold");

            var meters = stars.starMeters;
            int meterCount = 0;
            if (meters != null)
            {
                for (int i = 0; i < meters.Length; i++)
                {
                    if (meters[i] != null) { meters[i].enabled = false; meterCount++; }
                }
            }

            // Background "empty star" pip outlines — a sibling GameObject the StarDisplay script
            // itself never references as a field (confirmed by VirtualSongList's own header-pool
            // code elsewhere in this file, which caches this exact same child by name rather than a
            // script reference: "StarDisplay/star_pips (the 5 empty slots)"). Since nothing on the
            // script manages it, native never re-hides OR re-shows it on its own — unlike the five
            // tier arrays above, this one needs an explicit reactivate in the Audica-mode branch too.
            Transform pips = FindStarPips(stars.transform);
            bool pipsFound = pips != null;
            if (pipsFound) pips.gameObject.SetActive(false);

            MelonLogger.Log($"[ExScoring][Diag] HideNativeStarArrays: hidden easy={easy} normal={normal} hard={hard} expert={expert} gold={gold} meters={meterCount} (meters array null={meters == null}) pipsFound={pipsFound}");
        }

        private static void ShowNativeStarPips(SongSelectItem item)
        {
            if (item == null || item.starDisplay == null) return;
            Transform pips = FindStarPips(item.starDisplay.transform);
            if (pips != null) pips.gameObject.SetActive(true);
        }

        private static Transform FindStarPips(Transform root)
        {
            Transform direct = root.Find("star_pips");
            if (direct != null) return direct;

            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindStarPips(root.GetChild(i));
                if (found != null) return found;
            }
            return null;
        }

        private static int HideStarArray(Il2CppReferenceArray<GameObject> stars, string label)
        {
            if (stars == null)
            {
                MelonLogger.Log($"[ExScoring][Diag] HideStarArray({label}): array is NULL");
                return -1;
            }

            int hidden = 0;
            for (int i = 0; i < stars.Length; i++)
            {
                if (stars[i] != null)
                {
                    stars[i].SetActive(false);
                    hidden++;
                }
            }
            return hidden;
        }
    }
}