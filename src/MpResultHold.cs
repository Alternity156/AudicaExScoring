using System;
using System.Collections;
using MelonLoader;
using UnityEngine;
using static ExScoringMod.ExScoring;

namespace ExScoringMod
{
    /// <summary>
    /// Holds the end-of-song EX leaderboard and the "Personal Best! / Rank #" label on the results
    /// screen of a multiplayer match until everyone is done, so the leaderboard shows the other
    /// players' runs and the rank accounts for them.
    ///
    /// Starts at this player's own song end in a match run (MpGameplay.OnSongOver). Released at the
    /// first of:
    ///  - matchResults for this match (every participant finished, quit or gone), plus a 2 s grace
    ///    so the others' uploads can land;
    ///  - 10 s after this player's song end (failsafe);
    ///  - right away on matchAborted, leaving the lobby or disconnecting.
    /// Once released, if this player's own upload is still sending, the fetch waits for it (up to
    /// 10 s more), so their own run is on the leaderboard too.
    ///
    /// While held, the leaderboard rows stay blank and the requested view is remembered
    /// (HoldLeaderboard, from the UpdateLeaderboard prefix); the label shows "Waiting for players..."
    /// (GameplayStatsPanel.ApplyApiStatusLabel). On release the remembered fetch runs, and when it
    /// answers the label shows the upload's result. For a personal best, the rank is refreshed from
    /// the fetch's requesterRank (the upload's rank is the fallback). For a run that isn't a personal
    /// best, requesterRank is the rank of the player's older best run, so the upload's rank is kept.
    /// If the results screen was left before release, the fetch is skipped.
    /// </summary>
    internal static class MpResultsHold
    {
        internal const string WaitingText = "Waiting for players...";

        private const long FailsafeMs = 10000;
        private const long GraceAfterResultsMs = 2000;
        private const long UploadWaitMs = 10000;
        private const float PollSeconds = 0.1f;

        private static bool subscribed;
        private static int version;
        private static string matchId;
        private static long songOverMs;
        // Local ms at which the hold may end (matchResults + grace, or now on abort/leave). 0 = not yet.
        private static long releaseAtMs;

        private static bool holdingLeaderboard;
        private static bool holdingLabel;

        // The results screen's leaderboard request, remembered while held.
        private static LeaderboardDisplay pendingDisplay;
        private static string pendingSongId;
        private static string pendingDifficulty;
        private static string pendingView;

        // From this run's upload response (ApiClient), null until it answers.
        private static bool? submittedPersonalBest;
        private static int submittedRank;

        private static long Now => MpSession.LocalNowMs;

        /// <summary>The API status label shows WaitingText instead of the upload's result.</summary>
        public static bool HoldsStatusLabel => holdingLabel;

        /// <summary>From MpGameplay.OnSongOver, in a match run, before "finished" is sent.</summary>
        public static void Begin(string runMatchId)
        {
            EnsureSubscribed();

            int myVersion = ++version;
            matchId = runMatchId;
            songOverMs = Now;
            releaseAtMs = 0;
            holdingLeaderboard = true;
            holdingLabel = true;
            ClearPending();
            submittedPersonalBest = null;
            submittedRank = 0;

            Log($"holding results for match {matchId} (failsafe {FailsafeMs / 1000} s)");
            ApplyApiStatusLabel();
            MelonCoroutines.Start(Run(myVersion));
        }

        /// <summary>From ApiClient's upload response.</summary>
        public static void NoteRunSubmitted(bool isPersonalBest, int rank)
        {
            submittedPersonalBest = isPersonalBest;
            submittedRank = rank;
        }

        /// <summary>
        /// From the UpdateLeaderboard prefix for a per-song leaderboard. While held, remembers the
        /// request and returns true (the caller blanks the rows and skips the fetch).
        /// </summary>
        public static bool HoldLeaderboard(LeaderboardDisplay display, string songId, string difficulty, string view)
        {
            if (!holdingLeaderboard) return false;

            pendingDisplay = display;
            pendingSongId = songId;
            pendingDifficulty = difficulty;
            pendingView = view;
            Log($"leaderboard ({view}) held until everyone is done");
            return true;
        }

        private static void EnsureSubscribed()
        {
            if (subscribed) return;
            subscribed = true;

            MpSession.OnMatchResults += results =>
            {
                if (!holdingLeaderboard || results == null || results.matchId != matchId) return;
                if (releaseAtMs == 0) releaseAtMs = Now + GraceAfterResultsMs;
                Log($"matchResults in, releasing in {GraceAfterResultsMs / 1000} s");
            };
            MpSession.OnMatchAborted += id => ReleaseNow("match aborted");
            MpSession.OnLeftLobby += _ => ReleaseNow("left the lobby");
            MpSession.OnDisconnected += _ => ReleaseNow("disconnected");
        }

        private static void ReleaseNow(string reason)
        {
            if (!holdingLeaderboard) return;
            releaseAtMs = Now;
            Log("releasing now: " + reason);
        }

        private static IEnumerator Run(int myVersion)
        {
            // 1. Until everyone is done (or the failsafe).
            while (true)
            {
                if (myVersion != version) yield break;
                long now = Now;
                if (releaseAtMs != 0 && now >= releaseAtMs) break;
                if (now - songOverMs >= FailsafeMs)
                {
                    Log("failsafe reached, showing results without waiting any longer");
                    break;
                }
                yield return new WaitForSeconds(PollSeconds);
            }

            // 2. This player's own upload, so their run is on the leaderboard.
            long uploadWaitStart = Now;
            while (CurrentApiSubmitStatus == ApiSubmitStatus.Sending && Now - uploadWaitStart < UploadWaitMs)
            {
                if (myVersion != version) yield break;
                yield return new WaitForSeconds(PollSeconds);
            }
            if (myVersion != version) yield break;

            Release(myVersion);
        }

        private static void Release(int myVersion)
        {
            holdingLeaderboard = false;

            LeaderboardDisplay display = pendingDisplay;
            string songId = pendingSongId;
            string difficulty = pendingDifficulty;
            string view = pendingView;
            ClearPending();

            bool displayUsable = false;
            try
            {
                // Unity-null once destroyed; inactive once the results screen was left.
                displayUsable = display != null && display.gameObject.activeInHierarchy && Config.ExType;
            }
            catch (Exception)
            {
                displayUsable = false;
            }

            if (!displayUsable || songId == null)
            {
                Log(songId == null ? "released (no leaderboard was requested)" : "released (results screen gone, fetch skipped)");
                FinishLabel(myVersion, null);
                return;
            }

            Log($"released, fetching the leaderboard ({view})");
            MelonCoroutines.Start(LabelFailsafe(myVersion));
            try
            {
                FetchAndPopulateSongLeaderboard(display, songId, difficulty, view, response => FinishLabel(myVersion, response));
            }
            catch (Exception ex)
            {
                Log("held leaderboard fetch failed: " + ex.Message);
                FinishLabel(myVersion, null);
            }
        }

        /// <summary>Shows the label anyway if the fetch never reports back.</summary>
        private static IEnumerator LabelFailsafe(int myVersion)
        {
            yield return new WaitForSeconds(UploadWaitMs / 1000f);
            if (myVersion == version && holdingLabel)
            {
                Log("leaderboard fetch never answered, showing the label anyway");
                FinishLabel(myVersion, null);
            }
        }

        private static void FinishLabel(int myVersion, LeaderboardApiResponse response)
        {
            if (myVersion != version || !holdingLabel) return;
            holdingLabel = false;

            if (CurrentApiSubmitStatus == ApiSubmitStatus.Success && submittedPersonalBest == true)
            {
                int rank = response != null && response.requesterRank.HasValue ? response.requesterRank.Value : submittedRank;
                if (rank != submittedRank) Log($"personal best rank refreshed: #{submittedRank} -> #{rank}");
                OverrideApiSubmitText($"Personal Best!\nRank #{rank}");
                return;
            }

            ApplyApiStatusLabel();
        }

        private static void ClearPending()
        {
            pendingDisplay = null;
            pendingSongId = null;
            pendingDifficulty = null;
            pendingView = null;
        }

        private static void Log(string message)
        {
            DebugLog.Log("[MpResultsHold] " + message);
        }
    }
}