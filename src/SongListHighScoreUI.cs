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

        // In-flight lookups, so fast scrolling doesn't queue duplicate background lookups
        // (LoadBestRunSummaryForSong) for the same song+difficulty while one is already running.
        private static readonly HashSet<string> exRowScoreLoading = new HashSet<string>();

        private static string ExRowCacheKey(string songId, KataConfig.Difficulty difficulty) => songId + "|" + difficulty;

        // ══════════════════════════════════════════════════════════════════
        //  Online (leaderboard-only) best scores
        // ══════════════════════════════════════════════════════════════════
        //
        // The song list, the High/Low grade folders (FolderRowManager.cs), the Song Info top-score
        // rows (TopScoreUI.cs) and the in-game high score (InGameUI.cs) all read "best run" from
        // local run files. After a clean install those are gone, even though every uploaded run is
        // still on the AudicaEx server. So once per launch (and again if the API key changes) the
        // player's best uploaded run per song+difficulty is fetched in ONE request
        // (GET /api/users/me/bests — ApiContract.md v2.36, Section 5b) and merged with the local
        // result everywhere: the better of the two wins (IsBetterExScore).
        //
        // Fallback: if the server doesn't have /me/bests yet (404), the whole uploaded history is
        // fetched instead (GET /api/users/me/runs, no filters — Section 5) and reduced to the best
        // run per song+difficulty here. Both paths go through StoreOnlineBest, so they merge the
        // same way. (Before v2.36 the server capped that history at the 50 most recent runs, which
        // left random gaps in the song list — that's what /me/bests replaces.)
        //
        // Freshly played runs don't need a refetch: they're saved locally right away, and the merge
        // always takes the better of local vs. online.

        public class OnlineBestScore
        {
            public float judgementPercent;
            public bool failed;
        }

        // songId|difficulty (same key format as exRowScoreCache) -> best uploaded run.
        private static readonly Dictionary<string, OnlineBestScore> onlineBestScores = new Dictionary<string, OnlineBestScore>();

        // API key the current onlineBestScores (or the in-flight fetch) belongs to. null = never
        // fetched, or the last attempt failed and may be retried after OnlineBestRetryDelaySeconds.
        private static string onlineBestFetchedForKey;
        private static bool onlineBestFetchInFlight;
        private static bool onlineBestReady; // a fetch for onlineBestFetchedForKey has finished (success or failure)
        private static int onlineBestRequestVersion; // drops a stale response after an API key change
        private static float onlineBestRetryAfter; // realtimeSinceStartup before which a failed fetch isn't retried

        private const float OnlineBestRetryDelaySeconds = 60f;
        private const float OnlineBestWaitTimeoutSeconds = 15f;

        /// <summary>
        /// Starts the once-per-launch fetch of the player's uploaded best scores if it hasn't run yet
        /// for the current API key. Cheap to call often (every row bind calls it): it returns right
        /// away once a fetch has started or finished. With no API key, any previously fetched
        /// scores are dropped and nothing is requested.
        /// </summary>
        public static void EnsureOnlineBestScores()
        {
            string apiKey = Config.ApiKey;

            if (string.IsNullOrEmpty(apiKey))
            {
                if (onlineBestFetchedForKey != null || onlineBestScores.Count > 0)
                {
                    onlineBestScores.Clear();
                    onlineBestFetchedForKey = null;
                    onlineBestFetchInFlight = false;
                    onlineBestReady = false;
                    onlineBestRequestVersion++;
                }
                return;
            }

            if (onlineBestFetchedForKey == apiKey) return; // already fetched / fetching for this key

            if (onlineBestFetchedForKey == null && Time.realtimeSinceStartup < onlineBestRetryAfter)
                return; // last attempt failed recently — don't hammer the server from every row bind

            bool keyChanged = onlineBestFetchedForKey != null;

            onlineBestFetchedForKey = apiKey;
            onlineBestFetchInFlight = true;
            onlineBestReady = false;
            onlineBestScores.Clear();
            int version = ++onlineBestRequestVersion;

            if (keyChanged)
            {
                // Scores from the previous account may already be merged into the caches below —
                // drop them so everything is rebuilt from local files + the new account's scores.
                ClearMergedBestScoreCaches();
            }

            FetchMyBests((response, statusCode) => OnMyBestsFetched(response, statusCode, version));
        }

        /// <summary>True once there's nothing more to wait for: no API key, or the fetch for the
        /// current key has finished (successfully or not).</summary>
        public static bool OnlineBestScoresSettled =>
            string.IsNullOrEmpty(Config.ApiKey)
            || (onlineBestFetchedForKey == Config.ApiKey && onlineBestReady)
            || (onlineBestFetchedForKey == null && !onlineBestFetchInFlight); // failed, waiting to retry

        /// <summary>
        /// Coroutine that waits (up to a timeout) for the online best scores to be fetched, starting
        /// the fetch if needed. Used where re-applying after a late arrival is awkward (the grade
        /// folders and the Song Info top-score rows), so they're built once with both sources.
        /// </summary>
        public static IEnumerator WaitForOnlineBestScores()
        {
            EnsureOnlineBestScores();

            float deadline = Time.realtimeSinceStartup + OnlineBestWaitTimeoutSeconds;
            while (!OnlineBestScoresSettled && Time.realtimeSinceStartup < deadline)
                yield return null;
        }

        /// <summary>Best uploaded run for this song+difficulty, if the server has one.</summary>
        public static bool TryGetOnlineBest(string songId, string difficulty, out OnlineBestScore best)
        {
            best = null;
            if (string.IsNullOrEmpty(songId) || string.IsNullOrEmpty(difficulty)) return false;
            return onlineBestScores.TryGetValue(songId + "|" + difficulty, out best);
        }

        /// <summary>
        /// Whether score A beats score B, using the same "best run" rule as everywhere else: higher
        /// judgement percent wins, and on an exact tie a passed run beats a failed one.
        /// </summary>
        public static bool IsBetterExScore(float percentA, bool failedA, float percentB, bool failedB)
        {
            if (percentA != percentB) return percentA > percentB;
            return !failedA && failedB;
        }

        /// <summary>
        /// Main path: GET /api/users/me/bests (Section 5b). Entries are already one per
        /// song+difficulty, but they still go through StoreOnlineBest so a duplicate key (shouldn't
        /// happen) can't overwrite a better entry. A 404 means the server predates v2.36, so the
        /// whole run history is fetched instead under the same request version — the fetch stays
        /// in flight until that fallback answers.
        /// </summary>
        private static void OnMyBestsFetched(MyBestsApiResponse response, long statusCode, int version)
        {
            if (version != onlineBestRequestVersion) return; // API key changed while this was in flight

            if (response == null && statusCode == 404)
            {
                DebugLog.Log("[ExScoring] Online best scores: /me/bests not found (server older than v2.36), falling back to the full run history.");
                FetchMyRuns(runsResponse => OnMyRunsFetched(runsResponse, version));
                return;
            }

            onlineBestFetchInFlight = false;

            if (response == null || response.bests == null)
            {
                OnOnlineBestFetchFailed();
                return;
            }

            onlineBestScores.Clear();
            for (int i = 0; i < response.bests.Length; i++)
            {
                MyBestsApiEntry best = response.bests[i];
                if (best == null) continue;
                StoreOnlineBest(best.songId, best.difficulty, best.judgementPercent, best.failed);
            }

            onlineBestReady = true;
            DebugLog.Log($"[ExScoring] Online best scores: {onlineBestScores.Count} song+difficulty best(s) from /me/bests (server total={response.total}).");

            ApplyOnlineBestScoresToCaches();
        }

        /// <summary>
        /// Fallback path: GET /api/users/me/runs with no filters (Section 5), only used when the
        /// server doesn't have /me/bests yet. Reduced to the best run per song+difficulty here.
        /// </summary>
        private static void OnMyRunsFetched(MyRunsApiResponse response, int version)
        {
            if (version != onlineBestRequestVersion) return; // API key changed while this was in flight

            onlineBestFetchInFlight = false;

            if (response == null || response.runs == null)
            {
                OnOnlineBestFetchFailed();
                return;
            }

            onlineBestScores.Clear();
            for (int i = 0; i < response.runs.Length; i++)
            {
                MyRunsApiEntry run = response.runs[i];
                if (run == null) continue;
                StoreOnlineBest(run.songId, run.difficulty, run.judgementPercent, run.failed);
            }

            onlineBestReady = true;
            DebugLog.Log($"[ExScoring] Online best scores (fallback): {onlineBestScores.Count} song+difficulty best(s) from {response.runs.Length} uploaded run(s).");

            ApplyOnlineBestScoresToCaches();
        }

        /// <summary>Keeps the better of this score and any already stored for the same
        /// song+difficulty (IsBetterExScore). Entries missing a songId or difficulty are skipped.</summary>
        private static void StoreOnlineBest(string songId, string difficulty, float judgementPercent, bool failed)
        {
            if (string.IsNullOrEmpty(songId) || string.IsNullOrEmpty(difficulty)) return;

            string key = songId + "|" + difficulty;
            if (onlineBestScores.TryGetValue(key, out OnlineBestScore existing)
                && !IsBetterExScore(judgementPercent, failed, existing.judgementPercent, existing.failed))
                return;

            onlineBestScores[key] = new OnlineBestScore { judgementPercent = judgementPercent, failed = failed };
        }

        /// <summary>
        /// Shared failure handling for both fetch paths: allow a retry later (e.g. the game started
        /// offline) instead of giving up for the whole launch, but not right away.
        /// </summary>
        private static void OnOnlineBestFetchFailed()
        {
            onlineBestFetchedForKey = null;
            onlineBestReady = false;
            onlineBestRetryAfter = Time.realtimeSinceStartup + OnlineBestRetryDelaySeconds;
            DebugLog.Log($"[ExScoring] Online best scores: fetch failed, will retry in {OnlineBestRetryDelaySeconds:0}s at the earliest.");
        }

        /// <summary>
        /// Folds freshly arrived online bests into everything that already cached a local-only
        /// result, then refreshes what's on screen. Keys never looked up yet are left alone — their
        /// first lookup merges both sources itself (LoadRowScoreCoroutine).
        /// </summary>
        private static void ApplyOnlineBestScoresToCaches()
        {
            int upgraded = 0;

            foreach (var kvp in onlineBestScores)
            {
                string key = kvp.Key;
                OnlineBestScore online = kvp.Value;

                if (exRowScoreCache.TryGetValue(key, out CachedExRowScore cached))
                {
                    if (IsBetterExScore(online.judgementPercent, online.failed, cached.judgementPercent, cached.failed))
                    {
                        cached.judgementPercent = online.judgementPercent;
                        cached.failed = online.failed;
                        upgraded++;
                    }
                }
                else if (exRowScoreEmpty.Remove(key))
                {
                    exRowScoreCache[key] = new CachedExRowScore { judgementPercent = online.judgementPercent, failed = online.failed };
                    upgraded++;
                }
            }

            DebugLog.Log($"[ExScoring] Online best scores: {upgraded} cached song-list result(s) upgraded.");

            FolderRowManager.ApplyOnlineBestScores();

            if (Config.ExType && VirtualSongList.IsActive)
                RefreshAllVisibleSongRowScores();
        }

        /// <summary>Drops every cached best result (song list + grade folders) so they're rebuilt
        /// from scratch — used when the API key changes mid-session.</summary>
        private static void ClearMergedBestScoreCaches()
        {
            exRowScoreCache.Clear();
            exRowScoreEmpty.Clear();
            FolderRowManager.ClearGradeCache();
        }

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

            DebugLog.Log($"[ExScoring] RefreshAllVisibleSongRowScores: {boundCount}/{viewCount} row(s) bound and refreshed.");
        }

        /// <summary>
        /// Replaces a song-list row's native high-score/percent/stars with our best saved EX run for
        /// that song+difficulty, whenever EX scoring is on. EX rows show only the judgement percent
        /// and grade — the raw judgement score is never shown, so highScoreLabel stays hidden for
        /// every EX row (see ApplySongRowScore). Rows are pooled and rebound to different
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

                // Once-per-launch fetch of leaderboard-only bests (no-op after the first call).
                EnsureOnlineBestScores();

                string songId = __instance.mSongData.songID;
                KataConfig.Difficulty difficulty = KataConfig.I.GetDifficulty();
                string key = ExRowCacheKey(songId, difficulty);

                if (exRowScoreCache.TryGetValue(key, out CachedExRowScore cached))
                {
                    ApplySongRowScore(__instance, cached);
                    return false;
                }

                HideSongRowScore(__instance);

                if (exRowScoreEmpty.Contains(key)) return false; // confirmed no saved runs — stay hidden

                if (!exRowScoreLoading.Contains(key))
                {
                    exRowScoreLoading.Add(key);
                    MelonCoroutines.Start(LoadRowScoreCoroutine(songId, difficulty, key));
                }

                return false;
            }
        }

        /// <summary>
        /// Native SongSelectItem.Init() calls UpdateHighScoreInfo() once, then unconditionally calls
        /// UpdateLeaderboardInfo() (when not in party/campaign) — which does its own global/friends
        /// leaderboard rank lookups (GetGlobalPositionForSong / GetFriendsPosition), sets the rank
        /// labels/icons, and THEN calls UpdateHighScoreInfo() a second time. Confirmed via decompile +
        /// [VList-PERF] logging: those two rank lookups cost ~20-40 ms PER SONG, synchronously, on
        /// every row that gets freshly Init()'d (i.e. every row landing on a song its pool slot wasn't
        /// already showing) — with ~9-24 rows binding in a single SetView call when a folder opens,
        /// that alone produced the ~300ms main-thread stall behind the VR reprojection "catch-up" itch.
        ///
        /// The EX layout never reads globalPositionLabel / friendsPositionLabel / globalIcon /
        /// friendsIcon (grep confirms zero references anywhere in this mod), so none of that
        /// computation's output is ever shown. Skip it outright for EX rows — explicitly hiding the
        /// four elements first so a pooled row reused from a previous song can't show a stale rank —
        /// rather than just spreading the cost across frames, since the work itself is unnecessary here.
        /// Audica-native scoring (ExType false) is untouched: native runs exactly as before.
        /// </summary>
        [HarmonyPatch(typeof(SongSelectItem), "UpdateLeaderboardInfo")]
        public static class SongSelectItemUpdateLeaderboardInfoPatch
        {
            public static bool Prefix(SongSelectItem __instance)
            {
                if (!Config.ExType) return true; // native runs fully — unrelated to Audica-scoring mode

                if (__instance != null)
                {
                    if (__instance.globalPositionLabel != null) __instance.globalPositionLabel.gameObject.SetActive(false);
                    if (__instance.friendsPositionLabel != null) __instance.friendsPositionLabel.gameObject.SetActive(false);
                    if (__instance.globalIcon != null) __instance.globalIcon.SetActive(false);
                    if (__instance.friendsIcon != null) __instance.friendsIcon.SetActive(false);
                }

                return false; // skip native: rank lookups + label/icon updates + its own UpdateHighScoreInfo call
            }
        }

        private static IEnumerator LoadRowScoreCoroutine(string songId, KataConfig.Difficulty difficulty, string key)
        {
            // Totals only, computed entirely on the background loader thread (RunDataRecalculator.cs)
            // — this coroutine's first step is just an enqueue, so however many rows bind in the
            // same frame when a folder opens, none of them read or score anything on the main thread.
            //
            // The local result is then merged with the player's uploaded best (if the online
            // fetch has already landed): the better of the two is cached. If the online fetch lands
            // later instead, ApplyOnlineBestScoresToCaches upgrades this entry then.
            string difficultyName = difficulty.ToString();
            yield return LoadBestRunSummaryForSong(songId, difficultyName, best =>
            {
                CachedExRowScore result = null;

                if (best != null)
                {
                    result = new CachedExRowScore
                    {
                        judgementPercent = best.judgementPercent,
                        failed = best.failed
                    };
                }

                if (TryGetOnlineBest(songId, difficultyName, out OnlineBestScore online)
                    && (result == null || IsBetterExScore(online.judgementPercent, online.failed, result.judgementPercent, result.failed)))
                {
                    result = new CachedExRowScore
                    {
                        judgementPercent = online.judgementPercent,
                        failed = online.failed
                    };
                }

                if (result != null)
                {
                    exRowScoreCache[key] = result;
                    exRowScoreEmpty.Remove(key);
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

            // EX shows the percent only — the raw judgement score label stays hidden. The !ExType
            // branch of SongSelectItemUpdateHighScoreInfoPatch reactivates it for native scoring.
            if (item.highScoreLabel != null) item.highScoreLabel.gameObject.SetActive(false);

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
                DebugLog.Log("[ExScoring][Diag] HideNativeStarArrays: stars is NULL");
                return;
            }

            HideStarArray(stars.starsEasy);
            HideStarArray(stars.starsNormal);
            HideStarArray(stars.starsHard);
            HideStarArray(stars.starsExpert);
            HideStarArray(stars.starsExpertGold);

            var meters = stars.starMeters;
            if (meters != null)
            {
                for (int i = 0; i < meters.Length; i++)
                    if (meters[i] != null) meters[i].enabled = false;
            }

            // Background "empty star" pip outlines — a sibling GameObject the StarDisplay script
            // itself never references as a field (confirmed by VirtualSongList's own header-pool
            // code elsewhere in this file, which caches this exact same child by name rather than a
            // script reference: "StarDisplay/star_pips (the 5 empty slots)"). Since nothing on the
            // script manages it, native never re-hides OR re-shows it on its own — unlike the five
            // tier arrays above, this one needs an explicit reactivate in the Audica-mode branch too.
            Transform pips = FindStarPips(stars.transform);
            if (pips != null) pips.gameObject.SetActive(false);
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

        private static void HideStarArray(Il2CppReferenceArray<GameObject> stars)
        {
            if (stars == null) return;

            for (int i = 0; i < stars.Length; i++)
                if (stars[i] != null) stars[i].SetActive(false);
        }
    }
}