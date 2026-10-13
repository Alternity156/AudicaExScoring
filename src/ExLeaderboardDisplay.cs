using System;
using System.Collections.Generic;
using System.Linq;
using Harmony;
using MelonLoader;
using UnhollowerBaseLib;
using UnityEngine;
using UnityEngine.UI;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        // Bumped on every new EX leaderboard fetch kicked off below. A response applies itself only
        // if this still matches the value it was started with — lets a fast song-switch (or flipping
        // ExType off mid-fetch) discard a stale, now-irrelevant response instead of overwriting
        // whatever the panel should be showing by the time it lands. Same idea as exRowScoreLoading
        // in SongListHighScoreUI.cs, but simpler since only one leaderboard panel is ever open.
        // Shared between the per-song fetch and the Total-leaderboard fetch below — the two are
        // mutually exclusive (only one leaderboard panel/request is ever active), so one counter is
        // enough to invalidate whichever kind of fetch is in flight.
        private static int leaderboardRequestVersion = 0;

        // Native passes this exact string as leaderboardID when the main-menu "Total Score"
        // leaderboard requests a refresh (confirmed via logging: leaderboardID=all_time_leaders,
        // display.totalLeaderboards=True). It's the only signal available at this call site — the
        // request never has selectedSongData behind it — so it's what routes to the Total
        // leaderboard fetch instead of the per-song one below.
        private const string AllTimeLeadersID = "all_time_leaders";

        // ApiContract.md section 4b: Total leaderboards are scoped per server-managed song-list, not
        // a single implicit "the OST". Hardcoded to "ost" for now since there's no in-game
        // list-switcher UI yet — revisit if/when one exists (GET /api/song-lists would enumerate the
        // options).
        private const string TotalLeaderboardListId = "ost";

        // ApiContract.md section 4/4c `view` values the mod uses.
        private const string ViewTopParam = "top";
        private const string ViewFriendsParam = "friends";
        private const string ViewSelfParam = "self";

        // Text color for the requester's own row (entry.isRequester), on every view — Top, Friends,
        // Self and the Total leaderboard. Yellow, matching how native Audica marks your own row.
        private static readonly Color RequesterHighlightColor = new Color(1f, 0.92f, 0.016f, 1f);

        // Each row text's own color from before we ever recolored it, keyed by the text
        // component's instance ID. Rows are reused across fetches, so a row highlighted for one
        // response has to go back to its original color for the next one — and back again when
        // leaving EX (RestoreLeaderboardRows). Captured lazily the first time a text is touched.
        private static readonly Dictionary<int, Color> leaderboardTextDefaultColors = new Dictionary<int, Color>();

        /// <summary>
        /// Maps a native leaderboard mode to the AudicaEx `view` parameter. Friends and Self both
        /// need an API key (view=friends/view=self require auth), so without one every mode falls
        /// back to Top. Self is also what native uses for the post-song results screen, so that
        /// screen opens centered on the player. Every other mode (Party, ...) shows Top.
        /// </summary>
        private static string GetExLeaderboardView(OnlineLeaderboard.LeaderboardMode mode)
        {
            if (string.IsNullOrEmpty(Config.ApiKey))
                return ViewTopParam;

            if (mode == OnlineLeaderboard.LeaderboardMode.Friends)
                return ViewFriendsParam;
            if (mode == OnlineLeaderboard.LeaderboardMode.Self)
                return ViewSelfParam;
            return ViewTopParam;
        }

        /// <summary>
        /// view=self returns an empty list (requesterRank null) when the player has no run on
        /// this leaderboard yet (ApiContract.md section 4/4c). In that case the panel shows Top
        /// instead of a blank page.
        /// </summary>
        private static bool IsEmptySelfResponse(string view, int entryCount)
        {
            return view == ViewSelfParam && entryCount == 0;
        }

        /// <summary>
        /// Single choke point ViewTop()/ViewSelf()/ViewFriends() and the post-song
        /// ReportScoreAndDisplayLeaderboard() all funnel through to actually fetch/display leaderboard
        /// data — patching here covers both the song-select panel and the post-song results screen
        /// without touching either call site directly.
        ///
        /// When EX scoring is off, native runs exactly as before (Harmonix leaderboards), after first
        /// making sure any EX-mode row/button changes from a previous view are cleaned up. When EX
        /// scoring is on, native is skipped entirely (return false) and the rows are populated from
        /// AudicaEx's leaderboard instead, fetched asynchronously. mode=Friends maps to AudicaEx's
        /// view=friends (you + your friends, re-ranked within the group), mode=Self to view=self (a
        /// page centered on your rank, clamped at the top/bottom of the list); every other mode
        /// shows Top (see GetExLeaderboardView).
        ///
        /// Song switches reset the panel to Top on their own: the song-select hooks (Hooks.cs) call
        /// ViewTop(), which lands here with mode=Top.
        /// </summary>
        [HarmonyPatch(typeof(OnlineLeaderboard), "UpdateLeaderboard")]
        public static class OnlineLeaderboardUpdateLeaderboardPatch
        {
            public static bool Prefix(OnlineLeaderboard __instance, LeaderboardDisplay display, string leaderboardID, OnlineLeaderboard.LeaderboardMode mode)
            {
                DebugLog.Log($"[ExScoring] UpdateLeaderboard called: mode={mode} leaderboardID={leaderboardID} ExType={Config.ExType} totalLeaderboards={display?.totalLeaderboards}");

                // A leaderboard refresh means whatever run was selected in the stats panel (if any)
                // belongs to a now-stale set of rows — close it rather than leave it pointing at a
                // row that's about to show different data. Covers song switch, difficulty switch,
                // and any other trigger that lands here (see LeaderboardStatsButton.cs).
                ResetLeaderboardSelection();

                if (!Config.ExType)
                {
                    RestoreNativeLeaderboardButtons(display);
                    RestoreLeaderboardRows(display);
                    CleanupExLeaderboardStatsUI();
                    return true; // native handles Harmonix leaderboards as normal
                }

                HideNativeLeaderboardButtons(display);

                // AudicaEx has Top, Friends and Self views. Native also calls this with mode=Self on
                // its own for the post-song results screen (confirmed via logging:
                // ReportScoreAndDisplayLeaderboard defaults to Self), which now shows the centered
                // Self page too. Any mode without an AudicaEx equivalent (or Friends/Self with no
                // API key) shows Top rather than a blank panel.
                string view = GetExLeaderboardView(mode);
                if (mode != OnlineLeaderboard.LeaderboardMode.Top && view == ViewTopParam)
                {
                    DebugLog.Log($"[ExScoring] UpdateLeaderboard: mode={mode} requested, showing Top instead (no AudicaEx equivalent, or no API key for Friends/Self).");
                }

                if (display == null)
                {
                    DebugLog.Log("[ExScoring] UpdateLeaderboard: display is NULL, aborting EX leaderboard fetch.");
                    return false;
                }

                // Main-menu Total leaderboard — must be checked before the selectedSongData guard
                // below, since this request never has a selected song behind it (that guard used to
                // catch it and just blank the panel instead, which is the bug this branch fixes).
                if (leaderboardID == AllTimeLeadersID)
                {
                    FetchAndPopulateTotalLeaderboard(display, view);
                    return false;
                }

                if (selectedSongData == null)
                {
                    DebugLog.Log("[ExScoring] UpdateLeaderboard: selectedSongData is NULL, aborting EX leaderboard fetch.");
                    BlankAllLeaderboardRows(display);
                    return false;
                }

                string songId = selectedSongData.songID;
                string difficulty = KataConfig.I.GetDifficulty().ToString();

                FetchAndPopulateSongLeaderboard(display, songId, difficulty, view);

                return false; // skip native — we're driving the rows ourselves
            }
        }

        /// <summary>
        /// Fetches one song+difficulty's AudicaEx leaderboard (ApiContract.md section 4) in the given
        /// `view` and writes it into the panel's rows. view=self asks for kNumRows entries and the
        /// server centers that page on the player's rank, clamping at the top/bottom of the list, so
        /// rank #1 still shows ranks 1..kNumRows. If view=self comes back empty (no run on this
        /// chart yet), this fetches Top instead.
        /// </summary>
        private static void FetchAndPopulateSongLeaderboard(LeaderboardDisplay display, string songId, string difficulty, string view)
        {
            int rowLimit = LeaderboardDisplay.kNumRows;

            int requestVersion = ++leaderboardRequestVersion;
            DebugLog.Log($"[ExScoring] UpdateLeaderboard: EX fetch #{requestVersion} starting songId={songId} difficulty={difficulty} rowLimit={rowLimit} view={view}");

            FetchLeaderboard(songId, difficulty, rowLimit, view, response =>
            {
                if (requestVersion != leaderboardRequestVersion)
                {
                    DebugLog.Log($"[ExScoring] UpdateLeaderboard: EX fetch #{requestVersion} result discarded (stale — current is #{leaderboardRequestVersion}).");
                    return;
                }

                if (!Config.ExType)
                {
                    DebugLog.Log($"[ExScoring] UpdateLeaderboard: EX fetch #{requestVersion} result discarded (scoring type changed mid-fetch).");
                    return;
                }

                if (response == null)
                {
                    DebugLog.Log($"[ExScoring] UpdateLeaderboard: EX fetch #{requestVersion} failed (see ApiClient log above), blanking rows.");
                    BlankAllLeaderboardRows(display);
                    return;
                }

                if (IsEmptySelfResponse(view, response.entries?.Length ?? 0))
                {
                    DebugLog.Log($"[ExScoring] UpdateLeaderboard: EX fetch #{requestVersion} (self) returned no entries (no run on {songId} {difficulty}), showing Top instead.");
                    FetchAndPopulateSongLeaderboard(display, songId, difficulty, ViewTopParam);
                    return;
                }

                if (view == ViewFriendsParam)
                    SortFriendsEntries(response);

                DebugLog.Log($"[ExScoring] UpdateLeaderboard: EX fetch #{requestVersion} ({view}) applying {response.entries?.Length ?? 0} row(s), requesterRank={response.requesterRank?.ToString() ?? "null"}.");
                PopulateLeaderboardRows(display, response);
            });
        }

        /// <summary>
        /// Drops native leaderboard results that arrive while EX scoring is on. A native request
        /// started in Audica mode can still be in flight when the player switches to EX (e.g. via
        /// the Main page's mode buttons); without this its response would land after the EX rows
        /// are written and switch the panel back to native's own rows.
        /// </summary>
        [HarmonyPatch(typeof(LeaderboardDisplay), "OnDataReceived")]
        public static class LeaderboardDisplayOnDataReceivedPatch
        {
            public static bool Prefix()
            {
                if (!Config.ExType) return true;

                DebugLog.Log("[ExScoring] OnDataReceived: native leaderboard data ignored (ExType is on).");
                return false;
            }
        }

        /// <summary>
        /// Handles the main-menu Total leaderboard request (leaderboardID == AllTimeLeadersID).
        /// Unlike the per-song path above, this never depends on selectedSongData — it always
        /// requests TotalLeaderboardListId's Total leaderboard (ApiContract.md section 4c) via the
        /// existing FetchTotalLeaderboard (ApiClient.cs), regardless of what song (if any) happens to
        /// be selected. difficulty is still taken from KataConfig.I.GetDifficulty() even though the
        /// main menu has no difficulty selector of its own — the API requires one per
        /// (listId, difficulty) pair (difficulties are never combined into one Total), so this reuses
        /// the same persistent global difficulty the per-song leaderboard already reads rather than
        /// adding new state. `view` is "top", "friends" or "self" (see GetExLeaderboardView); an
        /// empty "self" response (player not ranked on this list yet) falls back to Top.
        /// </summary>
        private static void FetchAndPopulateTotalLeaderboard(LeaderboardDisplay display, string view)
        {
            string difficulty = KataConfig.I.GetDifficulty().ToString();
            int rowLimit = LeaderboardDisplay.kNumRows;

            int requestVersion = ++leaderboardRequestVersion;
            DebugLog.Log($"[ExScoring] UpdateLeaderboard: EX total fetch #{requestVersion} starting listId={TotalLeaderboardListId} difficulty={difficulty} rowLimit={rowLimit} view={view}");

            FetchTotalLeaderboard(TotalLeaderboardListId, difficulty, rowLimit, view, response =>
            {
                if (requestVersion != leaderboardRequestVersion)
                {
                    DebugLog.Log($"[ExScoring] UpdateLeaderboard: EX total fetch #{requestVersion} result discarded (stale — current is #{leaderboardRequestVersion}).");
                    return;
                }

                if (!Config.ExType)
                {
                    DebugLog.Log($"[ExScoring] UpdateLeaderboard: EX total fetch #{requestVersion} result discarded (scoring type changed mid-fetch).");
                    return;
                }

                if (response == null)
                {
                    DebugLog.Log($"[ExScoring] UpdateLeaderboard: EX total fetch #{requestVersion} failed (see ApiClient log above), blanking rows.");
                    BlankAllLeaderboardRows(display, true);
                    return;
                }

                if (IsEmptySelfResponse(view, response.entries?.Length ?? 0))
                {
                    DebugLog.Log($"[ExScoring] UpdateLeaderboard: EX total fetch #{requestVersion} (self) returned no entries (not ranked on {TotalLeaderboardListId} {difficulty}), showing Top instead.");
                    FetchAndPopulateTotalLeaderboard(display, ViewTopParam);
                    return;
                }

                if (view == ViewFriendsParam)
                    SortFriendsEntries(response);

                // The compare buttons on these rows compare over exactly this list+difficulty
                // (ApiContract.md v2.30, Section 15 filters) — see ExScoreComparison.cs.
                RememberCompareScope(TotalLeaderboardListId, difficulty);

                DebugLog.Log($"[ExScoring] UpdateLeaderboard: EX total fetch #{requestVersion} ({view}) applying {response.entries?.Length ?? 0} row(s), requesterRank={response.requesterRank?.ToString() ?? "null"}.");
                PopulateTotalLeaderboardRows(display, response);
            });
        }

        /// <summary>
        /// Safety net for view=friends on the per-song leaderboard: the server already returns the
        /// friend group sorted and re-ranked 1..N (ApiContract.md section 4), but the rows are
        /// re-sorted here anyway so the top score is always on top. Highest judgementPercent first,
        /// then earliest unixTimestamp (same tie-break the server uses to pick a best run), then
        /// nickname. `rank` is left as the server sent it. OrderBy is stable, so entries the server
        /// already ordered keep that order on exact ties.
        /// </summary>
        private static void SortFriendsEntries(LeaderboardApiResponse response)
        {
            if (response?.entries == null || response.entries.Length < 2) return;

            response.entries = response.entries
                .OrderByDescending(e => e.judgementPercent)
                .ThenBy(e => e.unixTimestamp)
                .ThenBy(e => e.nickname ?? "", StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        /// <summary>
        /// Total-leaderboard counterpart to SortFriendsEntries, ranking by averagePercent (the
        /// list's average %, ApiContract.md section 4c v2.23), then songsPlayed (more first), then
        /// nickname — the same order the server documents. An entry with no averagePercent sorts
        /// last.
        /// </summary>
        private static void SortFriendsEntries(TotalLeaderboardApiResponse response)
        {
            if (response?.entries == null || response.entries.Length < 2) return;

            response.entries = response.entries
                .OrderByDescending(e => e.averagePercent ?? float.MinValue)
                .ThenByDescending(e => e.songsPlayed)
                .ThenBy(e => e.nickname ?? "", StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        /// <summary>
        /// Starts the AudicaEX Total leaderboard fetch for the given panel directly, without going
        /// through native's ViewTop()/UpdateLeaderboard. Used by ModeButtons.cs when switching from
        /// Audica to EX on the Main page: ViewTop() there re-shows the native results the panel
        /// already holds instead of reaching OnlineLeaderboardUpdateLeaderboardPatch, so the EX
        /// fetch never started. Does the same EX-side prep that patch does before fetching.
        /// No-op if EX scoring isn't on or the panel is null.
        /// </summary>
        public static void RefreshExTotalLeaderboard(LeaderboardDisplay display)
        {
            if (display == null)
            {
                DebugLog.Log("[ExScoring] RefreshExTotalLeaderboard: display is NULL, skipping.");
                return;
            }

            if (!Config.ExType)
            {
                DebugLog.Log("[ExScoring] RefreshExTotalLeaderboard: ExType is off, skipping.");
                return;
            }

            ResetLeaderboardSelection();
            HideNativeLeaderboardButtons(display);

            // Keep whichever tab the panel is already on (Top, Friends or Self) rather than always
            // forcing Top, so the shown rows match the native tab state.
            FetchAndPopulateTotalLeaderboard(display, GetExLeaderboardView(display.mode));
        }

        /// <summary>
        /// Finds the leaderboard panel if it's currently instantiated and syncs its native
        /// friends/self/extras buttons (and, when leaving EX, its row star visuals) with the current
        /// Config.ExType. Needed because switching scoring type in Options doesn't otherwise reach an
        /// already-open leaderboard panel until its next native UpdateLeaderboard call — same reasoning
        /// as RefreshAllVisibleSongRowScores in SongListHighScoreUI.cs. Safe no-op if the panel isn't
        /// currently on screen. Call from Config.SetScoringType.
        /// </summary>
        public static void SyncLeaderboardButtonVisibility()
        {
            LeaderboardDisplay leaderboard = UnityEngine.Object.FindObjectOfType<LeaderboardDisplay>();
            if (leaderboard == null)
            {
                DebugLog.Log("[ExScoring] SyncLeaderboardButtonVisibility: no LeaderboardDisplay found (panel not open), skipping.");
                return;
            }

            if (Config.ExType)
            {
                HideNativeLeaderboardButtons(leaderboard);
            }
            else
            {
                RestoreNativeLeaderboardButtons(leaderboard);
                RestoreLeaderboardRows(leaderboard);
            }

            DebugLog.Log($"[ExScoring] SyncLeaderboardButtonVisibility: synced (ExType={Config.ExType})");
        }

        /// <summary>
        /// EX-mode button layout: Extras is hidden (no AudicaEx equivalent). Friends and Self stay
        /// visible when an API key is set, since view=friends and view=self both require auth
        /// (ApiContract.md section 4/4c) — without a key the server can't tell who "you" are, so
        /// both are hidden too.
        /// </summary>
        private static void HideNativeLeaderboardButtons(LeaderboardDisplay display)
        {
            if (display == null) return;

            bool authViewsAvailable = !string.IsNullOrEmpty(Config.ApiKey);

            if (display.friendsButton != null) display.friendsButton.gameObject.SetActive(authViewsAvailable);
            if (display.selfButton != null) display.selfButton.gameObject.SetActive(authViewsAvailable);
            if (display.extrasButton != null) display.extrasButton.gameObject.SetActive(false);

            DebugLog.Log($"[ExScoring][Diag] HideNativeLeaderboardButtons: extras hidden, friends/self {(authViewsAvailable ? "shown" : "hidden (no API key)")}.");
        }

        private static void RestoreNativeLeaderboardButtons(LeaderboardDisplay display)
        {
            if (display == null) return;

            if (display.friendsButton != null) display.friendsButton.gameObject.SetActive(true);
            if (display.selfButton != null) display.selfButton.gameObject.SetActive(true);
            if (display.extrasButton != null) display.extrasButton.gameObject.SetActive(true);

            DebugLog.Log("[ExScoring][Diag] RestoreNativeLeaderboardButtons: friends/self/extras restored.");
        }

        /// <summary>
        /// Populates display.rowsStandard from an AudicaEx leaderboard response: entries fill rows
        /// front-to-back, any remaining rows beyond the response's entry count are blanked. Capped
        /// naturally since FetchLeaderboard was asked for at most kNumRows entries.
        /// </summary>
        private static void PopulateLeaderboardRows(LeaderboardDisplay display, LeaderboardApiResponse response)
        {
            if (display == null)
            {
                DebugLog.Log("[ExScoring] PopulateLeaderboardRows: display is NULL, aborting.");
                return;
            }

            Il2CppReferenceArray<LeaderboardRow> rows = display.rowsStandard;
            if (rows == null)
            {
                DebugLog.Log("[ExScoring] PopulateLeaderboardRows: rowsStandard is NULL, aborting.");
                return;
            }

            LeaderboardApiEntry[] entries = response?.entries;
            int entryCount = entries?.Length ?? 0;
            int rowCount = rows.Length;

            DebugLog.Log($"[ExScoring] PopulateLeaderboardRows: rowCount={rowCount} entryCount={entryCount}");

            for (int i = 0; i < rowCount; i++)
            {
                LeaderboardRow row = rows[i];
                if (row == null)
                {
                    DebugLog.Log($"[ExScoring][Diag] PopulateLeaderboardRows: row[{i}] is NULL, skipping.");
                    continue;
                }

                if (i < entryCount)
                    ApplyLeaderboardEntryToRow(row, entries[i]);
                else
                    ClearLeaderboardRow(row);
            }

            ShowLeaderboardPanelContent(display);
        }

        /// <summary>
        /// HideUntilUpdated() (called somewhere in the native panel-open/refresh flow ahead of
        /// UpdateLeaderboard) shows the spinner and hides the scroll view until native's own
        /// OnDataReceived/UpdateScores runs — which we never reach when EX scoring is on, so nothing
        /// otherwise tells them to flip back. Call this once rows are in their final state (populated
        /// or blanked), success or failure, so the panel never gets stuck spinning.
        /// `totalSet` picks which row set is on screen: rowsTotal for the main-menu Total
        /// leaderboard, rowsStandard for every per-song leaderboard.
        /// </summary>
        private static void ShowLeaderboardPanelContent(LeaderboardDisplay display, bool totalSet = false)
        {
            if (display == null) return;

            // The panel has four row sets (standard/friends/total/totalFriends), each under its own
            // parent, and native switches which one is shown when its own data arrives. EX writes
            // per-song data into rowsStandard and Total data into rowsTotal — the only set whose rows
            // carry a CompareButton (confirmed via UnityExplorer: compareButton is NULL on every
            // rowsStandard/rowsFriends row), which ExScoreComparison.cs needs. Without this, EX rows
            // written after native has loaded once land in a hidden set while the stale native rows
            // stay on screen. Native picks its own set again the next time its data arrives, so
            // nothing needs undoing when leaving EX.
            if (display.rowsStandardParent != null) display.rowsStandardParent.SetActive(!totalSet);
            if (display.rowsFriendsParent != null) display.rowsFriendsParent.SetActive(false);
            if (display.rowsTotalParent != null) display.rowsTotalParent.SetActive(totalSet);
            if (display.rowsTotalFriendsParent != null) display.rowsTotalFriendsParent.SetActive(false);
            DebugLog.Log($"[ExScoring][Diag] ShowLeaderboardPanelContent: {(totalSet ? "rowsTotal" : "rowsStandard")} set shown, other row sets hidden.");

            if (display.spinner != null)
            {
                display.spinner.gameObject.SetActive(false);
                DebugLog.Log("[ExScoring][Diag] ShowLeaderboardPanelContent: spinner hidden.");
            }
            else
            {
                DebugLog.Log("[ExScoring][Diag] ShowLeaderboardPanelContent: spinner is NULL.");
            }

            if (display.scrollRect != null)
            {
                display.scrollRect.enabled = true;
                display.scrollRect.gameObject.SetActive(true);
                DebugLog.Log("[ExScoring][Diag] ShowLeaderboardPanelContent: scrollRect shown/enabled.");
            }
            else
            {
                DebugLog.Log("[ExScoring][Diag] ShowLeaderboardPanelContent: scrollRect is NULL.");
            }
        }

        private static void ApplyLeaderboardEntryToRow(LeaderboardRow row, LeaderboardApiEntry entry)
        {
            int slot = row.gameObject.GetInstanceID();

            if (row.rank != null) row.rank.text = entry.rank.ToString();

            if (row.username != null)
            {
                string nickname = string.IsNullOrEmpty(entry.nickname) ? "???" : entry.nickname;
                row.username.text = LeaderboardDisplay.LaurelWrap(nickname, entry.fullCombo);
            }

            if (row.score != null) row.score.text = $"{entry.judgementPercent:0.00}%";

            if (row.percentile != null) row.percentile.gameObject.SetActive(false);

            ApplyPlatformIcon(row.platform, entry.platform);

            HideLeaderboardRowStars(row.starDisplay);

            string gradeId = entry.grade?.id;
            Grade grade;
            if (string.IsNullOrEmpty(gradeId) || !Enum.TryParse(gradeId, out grade))
            {
                DebugLog.Log($"[ExScoring] ApplyLeaderboardEntryToRow: unrecognized grade id '{gradeId}' for nickname={entry.nickname}, defaulting to F.");
                grade = Grade.F;
            }

            if (row.starDisplay != null)
                CreateOrUpdateLeaderboardRowGradeVisual(slot, row.starDisplay.transform, grade);
            else
                DebugLog.Log($"[ExScoring][Diag] ApplyLeaderboardEntryToRow: starDisplay NULL for row slot={slot}, cannot place grade visual.");

            // Compare is only offered on the Total leaderboard, like native (ExScoreComparison.cs).
            if (row.compareButton != null) row.compareButton.SetActive(false);
            ClearCompareTarget(slot);

            ApplyRequesterHighlight(row, entry.isRequester);

            row.gameObject.SetActive(true);

            // Stats-panel hit-box (LeaderboardStatsButton.cs) — cache this row's runId so shooting
            // it can pull up GET /api/runs/:runId, and ensure the invisible shootable overlay exists
            // over this row the same way Play History's does over its rows.
            leaderboardHitboxRunIds[slot] = entry.runId;
            EnsureLeaderboardHitbox(slot, row);
        }

        /// <summary>
        /// Total-leaderboard counterpart to PopulateLeaderboardRows — same front-to-back fill/blank
        /// behavior, but reading TotalLeaderboardApiEntry (ApiContract.md section 4c) instead of a
        /// per-song LeaderboardApiEntry, so rows go through ApplyTotalLeaderboardEntryToRow.
        /// Writes into display.rowsTotal (native's own Total row set), not rowsStandard: only the
        /// Total rows have a CompareButton, which the EX comparison needs (ExScoreComparison.cs).
        /// Every view (Top, Friends, Self) uses rowsTotal — rowsTotalFriends has the same button, so
        /// one set is enough.
        /// </summary>
        private static void PopulateTotalLeaderboardRows(LeaderboardDisplay display, TotalLeaderboardApiResponse response)
        {
            if (display == null)
            {
                DebugLog.Log("[ExScoring] PopulateTotalLeaderboardRows: display is NULL, aborting.");
                return;
            }

            Il2CppReferenceArray<LeaderboardRow> rows = display.rowsTotal;
            if (rows == null)
            {
                DebugLog.Log("[ExScoring] PopulateTotalLeaderboardRows: rowsTotal is NULL, aborting.");
                return;
            }

            TotalLeaderboardApiEntry[] entries = response?.entries;
            int entryCount = entries?.Length ?? 0;
            int rowCount = rows.Length;

            DebugLog.Log($"[ExScoring] PopulateTotalLeaderboardRows: rowCount={rowCount} entryCount={entryCount}");

            for (int i = 0; i < rowCount; i++)
            {
                LeaderboardRow row = rows[i];
                if (row == null)
                {
                    DebugLog.Log($"[ExScoring][Diag] PopulateTotalLeaderboardRows: row[{i}] is NULL, skipping.");
                    continue;
                }

                if (i < entryCount)
                    ApplyTotalLeaderboardEntryToRow(row, entries[i]);
                else
                    ClearLeaderboardRow(row);
            }

            ShowLeaderboardPanelContent(display, true);
        }

        /// <summary>
        /// Total-leaderboard counterpart to ApplyLeaderboardEntryToRow. The score column shows the
        /// player's averagePercent across the list (ApiContract.md section 4c, v2.23 — unplayed songs
        /// count as 0%), in the same "0.00%" format as a per-song row; the raw totalScore is never
        /// shown. If the server doesn't send averagePercent, the column shows "-" rather than a
        /// misleading 0.00%. The platform icon comes from the entry's per-player platform (v2.24 —
        /// the player's latest known platform, see TotalLeaderboardApiEntry.platform).
        /// TotalLeaderboardApiEntry has no grade/fullCombo/runId — an aggregate across a song-list
        /// isn't tied to any single run — so this hides the star/grade visual rather than
        /// populating it from data that doesn't exist, and deliberately clears any hitbox mapping
        /// instead of creating one (ClearLeaderboardHitboxRun, not EnsureLeaderboardHitbox): there's
        /// no single run behind an aggregate total score to fetch via GET /api/runs/:runId, so this
        /// row shouldn't open the leaderboard-stats panel the way a per-song row does.
        /// </summary>
        private static void ApplyTotalLeaderboardEntryToRow(LeaderboardRow row, TotalLeaderboardApiEntry entry)
        {
            int slot = row.gameObject.GetInstanceID();

            if (row.rank != null) row.rank.text = entry.rank.ToString();

            if (row.username != null)
            {
                string nickname = string.IsNullOrEmpty(entry.nickname) ? "???" : entry.nickname;
                row.username.text = LeaderboardDisplay.LaurelWrap(nickname, false);
            }

            if (row.score != null)
                row.score.text = entry.averagePercent.HasValue ? $"{entry.averagePercent.Value:0.00}%" : "-";

            if (row.percentile != null) row.percentile.gameObject.SetActive(false);

            // The player's current platform (ApiContract.md v2.24, section 4c) — their latest known
            // platform, not tied to a run in this list. "Unknown", or null from a pre-v2.24 server,
            // hits ApplyPlatformIcon's default case and hides the icon, same as on a per-song row.
            ApplyPlatformIcon(row.platform, entry.platform);

            HideLeaderboardRowStars(row.starDisplay);
            ClearLeaderboardRowGradeVisual(slot);

            // Compare button: shown for other players when an API key is set and the server sent a
            // userId (ApiContract.md v2.30) — see ExScoreComparison.cs.
            ApplyCompareButton(row, entry);

            ApplyRequesterHighlight(row, entry.isRequester);

            row.gameObject.SetActive(true);

            // Make sure this row can't still be pointing at a stale runId/hitbox from a previous
            // per-song leaderboard view — see method summary for why no new hitbox is created here.
            ClearLeaderboardHitboxRun(slot);
        }

        /// <summary>
        /// Maps an EX leaderboard entry's platform string (ApiContract.md v2.9) directly to one of
        /// KataConfig's platform sprites and applies it to the row's icon Image. Mirrors native's own
        /// LeaderboardRow.SetData behavior (confirmed via Ghidra decompile): Oculus/Steam/Viveport get
        /// their KataConfig.I texture, anything else (Playstation, Unknown, missing) just disables the
        /// Image rather than showing a wrong or fabricated icon — there's no Playstation or "unknown"
        /// sprite in the base game either, so this isn't a workaround, it's the same fallback native
        /// uses when it has no icon to show. Unlike native, we go straight from our own API's platform
        /// string to a sprite — no need to round-trip through LeaderboardRowMemberData.GetPlatform().
        /// </summary>
        private static void ApplyPlatformIcon(Image platformImage, string platform)
        {
            if (platformImage == null)
            {
                DebugLog.Log("[ExScoring][Diag] ApplyPlatformIcon: platformImage is NULL.");
                return;
            }

            Sprite sprite = null;
            KataConfig kataConfig = KataConfig.I;

            if (kataConfig != null)
            {
                switch (platform)
                {
                    case "Oculus":
                        sprite = kataConfig.oculusTexture;
                        break;
                    case "Steam":
                        sprite = kataConfig.steamTexture;
                        break;
                    case "Viveport":
                        sprite = kataConfig.viveportTexture;
                        break;
                    default:
                        // Playstation, Unknown, null, or anything unrecognized — no icon to show.
                        break;
                }
            }
            else
            {
                DebugLog.Log("[ExScoring][Diag] ApplyPlatformIcon: KataConfig.I is NULL.");
            }

            if (sprite != null)
            {
                platformImage.sprite = sprite;
                platformImage.enabled = true;
            }
            else
            {
                platformImage.enabled = false;
            }
        }

        private static void ClearLeaderboardRow(LeaderboardRow row)
        {
            if (row == null) return;

            // Deliberately NOT row.SetNone() — that fills the row with native's "no score to
            // display" placeholder text (visible), where we want the row itself hidden instead.
            row.gameObject.SetActive(false);
            ApplyRequesterHighlight(row, false);
            ClearLeaderboardRowGradeVisual(row.gameObject.GetInstanceID());
            ClearLeaderboardHitboxRun(row.gameObject.GetInstanceID());
            ClearCompareTarget(row.gameObject.GetInstanceID());
        }

        /// <summary>
        /// Colors the row's rank, nickname and score text yellow when it's the requester's own row
        /// (entry.isRequester, set by the server on every view — Top, Friends, Self, and the Total
        /// leaderboard), or puts them back to their original color otherwise. Rows are reused for
        /// every fetch, so every row goes through this on every populate, not only the highlighted
        /// one.
        /// </summary>
        private static void ApplyRequesterHighlight(LeaderboardRow row, bool isRequester)
        {
            if (row == null) return;

            SetRowTextHighlight(row.rank, isRequester);
            SetRowTextHighlight(row.username, isRequester);
            SetRowTextHighlight(row.score, isRequester);
        }

        private static void SetRowTextHighlight(Graphic text, bool highlighted)
        {
            if (text == null) return;

            int id = text.GetInstanceID();
            Color defaultColor;
            if (!leaderboardTextDefaultColors.TryGetValue(id, out defaultColor))
            {
                defaultColor = text.color;
                leaderboardTextDefaultColors[id] = defaultColor;
            }

            text.color = highlighted ? RequesterHighlightColor : defaultColor;
        }

        /// <summary>
        /// Hides every row of one set and shows that (now empty) set. `totalSet` true blanks
        /// rowsTotal (main-menu Total leaderboard), false blanks rowsStandard (per-song).
        /// </summary>
        private static void BlankAllLeaderboardRows(LeaderboardDisplay display, bool totalSet = false)
        {
            if (display == null) return;

            Il2CppReferenceArray<LeaderboardRow> rows = totalSet ? display.rowsTotal : display.rowsStandard;
            if (rows == null)
            {
                DebugLog.Log($"[ExScoring][Diag] BlankAllLeaderboardRows: {(totalSet ? "rowsTotal" : "rowsStandard")} is NULL.");
                return;
            }

            for (int i = 0; i < rows.Length; i++)
            {
                if (rows[i] != null) ClearLeaderboardRow(rows[i]);
            }

            ShowLeaderboardPanelContent(display, totalSet);
        }

        /// <summary>
        /// Hides exactly the leaf elements StarDisplayUI itself manages (its stars/goldRings/
        /// starMeters Image arrays), plus the 5 separate "star_pip" objects under it (confirmed via
        /// UnityExplorer — one per star position, not a single "star_pips" container like the
        /// song-list's StarDisplay uses; see SetActiveForAllNamed below). Unlike StarDisplay, this is
        /// a single flat set (a leaderboard row only ever shows one score's grade), not five
        /// per-difficulty tiers.
        /// </summary>
        private static void HideLeaderboardRowStars(StarDisplayUI stars)
        {
            if (stars == null)
            {
                DebugLog.Log("[ExScoring][Diag] HideLeaderboardRowStars: stars is NULL");
                return;
            }

            int starCount = SetImageArrayEnabled(stars.stars, false, "stars");
            int ringCount = SetImageArrayEnabled(stars.goldRings, false, "goldRings");
            int meterCount = SetImageArrayEnabled(stars.starMeters, false, "starMeters");
            int pipsHidden = SetActiveForAllNamed(stars.transform, "star_pip", false);

            DebugLog.Log($"[ExScoring][Diag] HideLeaderboardRowStars: hidden stars={starCount} goldRings={ringCount} starMeters={meterCount} pipsHidden={pipsHidden}");
        }

        /// <summary>
        /// Recursively sets active=`active` on every descendant of `root` whose name is exactly
        /// `name`, returning how many were changed. Unlike FindStarPips (SongListHighScoreUI.cs),
        /// which returns the first single match, this handles the leaderboard row's case of several
        /// same-named sibling objects (5x "star_pip") that all need toggling together.
        /// </summary>
        private static int SetActiveForAllNamed(Transform root, string name, bool active)
        {
            int count = 0;
            for (int i = 0; i < root.childCount; i++)
            {
                Transform child = root.GetChild(i);
                if (child.name == name)
                {
                    child.gameObject.SetActive(active);
                    count++;
                }
                count += SetActiveForAllNamed(child, name, active);
            }
            return count;
        }

        /// <summary>
        /// <summary>
        /// Undoes ClearLeaderboardRow/HideLeaderboardRowStars across every row on the panel (row
        /// GameObject reactivated, native stars/goldRings/starMeters/star_pip objects shown again) and
        /// clears any grade visuals we placed — called right before handing control back to native (EX
        /// scoring switched off). Without this, a row we hid entirely (see ClearLeaderboardRow) would
        /// stay hidden forever under native leaderboards too, since native's own SetData/OnDataReceived
        /// presumably assumes rows are always active and only manages their internal contents.
        /// </summary>
        private static void RestoreLeaderboardRows(LeaderboardDisplay display)
        {
            if (display == null) return;

            // Both sets EX writes into: rowsStandard (per-song) and rowsTotal (main-menu Total).
            int restoredRows = RestoreLeaderboardRowSet(display.rowsStandard)
                + RestoreLeaderboardRowSet(display.rowsTotal);

            DebugLog.Log($"[ExScoring][Diag] RestoreLeaderboardRows: restored {restoredRows} row(s).");
        }

        private static int RestoreLeaderboardRowSet(Il2CppReferenceArray<LeaderboardRow> rows)
        {
            if (rows == null) return 0;

            int restoredRows = 0;
            for (int i = 0; i < rows.Length; i++)
            {
                LeaderboardRow row = rows[i];
                if (row == null) continue;

                row.gameObject.SetActive(true);

                // We may have disabled this in ApplyPlatformIcon (Unknown/Playstation/no data). Native's
                // own SetData always explicitly sets platform.enabled itself (true for Oculus/Steam/
                // Viveport, false otherwise), so re-enabling here just restores its starting assumption —
                // its next SetData call will correct it either way, same reasoning as the star visuals.
                if (row.platform != null) row.platform.enabled = true;

                if (row.starDisplay != null)
                {
                    SetImageArrayEnabled(row.starDisplay.stars, true, "stars");
                    SetImageArrayEnabled(row.starDisplay.goldRings, true, "goldRings");
                    SetImageArrayEnabled(row.starDisplay.starMeters, true, "starMeters");
                    SetActiveForAllNamed(row.starDisplay.transform, "star_pip", true);
                }

                // Put the rank/nickname/score text back to its own color so a row we highlighted
                // as the requester's doesn't stay yellow under native leaderboards.
                ApplyRequesterHighlight(row, false);

                ClearLeaderboardRowGradeVisual(row.gameObject.GetInstanceID());
                ClearCompareTarget(row.gameObject.GetInstanceID());
                restoredRows++;
            }

            return restoredRows;
        }

        private static int SetImageArrayEnabled(Il2CppReferenceArray<Image> images, bool enabled, string label)
        {
            if (images == null)
            {
                DebugLog.Log($"[ExScoring][Diag] SetImageArrayEnabled({label}): array is NULL");
                return -1;
            }

            int changed = 0;
            for (int i = 0; i < images.Length; i++)
            {
                if (images[i] != null)
                {
                    images[i].enabled = enabled;
                    changed++;
                }
            }
            return changed;
        }
    }
}