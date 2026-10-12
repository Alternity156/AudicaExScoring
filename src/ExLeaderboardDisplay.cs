using System;
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

        /// <summary>
        /// Maps a native leaderboard mode to the AudicaEx `view` parameter. Only Friends has an
        /// AudicaEx equivalent besides Top; it needs an API key (view=friends requires auth), so
        /// without one it falls back to Top. Every other mode (Self — e.g. the post-song results
        /// screen — Party, ...) shows Top, same as before.
        /// </summary>
        private static string GetExLeaderboardView(OnlineLeaderboard.LeaderboardMode mode)
        {
            if (mode == OnlineLeaderboard.LeaderboardMode.Friends && !string.IsNullOrEmpty(Config.ApiKey))
                return ViewFriendsParam;
            return ViewTopParam;
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
        /// view=friends (you + your friends, re-ranked within the group); every other mode shows Top
        /// (see GetExLeaderboardView).
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

                // AudicaEx has Top and Friends views only. Native still calls this with mode=Self on
                // its own (confirmed via logging: ReportScoreAndDisplayLeaderboard defaults the
                // post-song results leaderboard to Self), even though the Self button is hidden
                // above. Rather than blanking the panel for a mode AudicaEx doesn't have, show Top.
                string view = GetExLeaderboardView(mode);
                if (mode != OnlineLeaderboard.LeaderboardMode.Top && view == ViewTopParam)
                {
                    DebugLog.Log($"[ExScoring] UpdateLeaderboard: mode={mode} requested, showing Top instead (no AudicaEx equivalent, or no API key for Friends).");
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

                    if (view == ViewFriendsParam)
                        SortFriendsEntries(response);

                    DebugLog.Log($"[ExScoring] UpdateLeaderboard: EX fetch #{requestVersion} ({view}) applying {response.entries?.Length ?? 0} row(s).");
                    PopulateLeaderboardRows(display, response);
                });

                return false; // skip native — we're driving the rows ourselves
            }
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
        /// adding new state. `view` is "top" or "friends" (see GetExLeaderboardView).
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
                    BlankAllLeaderboardRows(display);
                    return;
                }

                if (view == ViewFriendsParam)
                    SortFriendsEntries(response);

                DebugLog.Log($"[ExScoring] UpdateLeaderboard: EX total fetch #{requestVersion} ({view}) applying {response.entries?.Length ?? 0} row(s).");
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

            // Keep whichever tab the panel is already on (Top or Friends) rather than always
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
        /// EX-mode button layout: Self and Extras are hidden (no AudicaEx equivalent). Friends stays
        /// visible when an API key is set, since view=friends requires auth (ApiContract.md section
        /// 4/4c) — without a key there's no friend list to show, so it's hidden too.
        /// </summary>
        private static void HideNativeLeaderboardButtons(LeaderboardDisplay display)
        {
            if (display == null) return;

            bool friendsAvailable = !string.IsNullOrEmpty(Config.ApiKey);

            if (display.friendsButton != null) display.friendsButton.gameObject.SetActive(friendsAvailable);
            if (display.selfButton != null) display.selfButton.gameObject.SetActive(false);
            if (display.extrasButton != null) display.extrasButton.gameObject.SetActive(false);

            DebugLog.Log($"[ExScoring][Diag] HideNativeLeaderboardButtons: self/extras hidden, friends {(friendsAvailable ? "shown" : "hidden (no API key)")}.");
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
        /// </summary>
        private static void ShowLeaderboardPanelContent(LeaderboardDisplay display)
        {
            if (display == null) return;

            // EX data is always written into rowsStandard, but the panel has four row sets
            // (standard/friends/total/totalFriends), each under its own parent, and native switches
            // which one is shown when its own data arrives (e.g. the Main page's Total leaderboard
            // shows rowsTotal). Without this, EX rows written after native has loaded once land in
            // a hidden set while the stale native rows stay on screen. Native picks its own set
            // again the next time its data arrives, so nothing needs undoing when leaving EX.
            if (display.rowsStandardParent != null) display.rowsStandardParent.SetActive(true);
            if (display.rowsFriendsParent != null) display.rowsFriendsParent.SetActive(false);
            if (display.rowsTotalParent != null) display.rowsTotalParent.SetActive(false);
            if (display.rowsTotalFriendsParent != null) display.rowsTotalFriendsParent.SetActive(false);
            DebugLog.Log("[ExScoring][Diag] ShowLeaderboardPanelContent: rowsStandard set shown, other row sets hidden.");

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

            if (row.compareButton != null) row.compareButton.SetActive(false);

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
        /// </summary>
        private static void PopulateTotalLeaderboardRows(LeaderboardDisplay display, TotalLeaderboardApiResponse response)
        {
            if (display == null)
            {
                DebugLog.Log("[ExScoring] PopulateTotalLeaderboardRows: display is NULL, aborting.");
                return;
            }

            Il2CppReferenceArray<LeaderboardRow> rows = display.rowsStandard;
            if (rows == null)
            {
                DebugLog.Log("[ExScoring] PopulateTotalLeaderboardRows: rowsStandard is NULL, aborting.");
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

            ShowLeaderboardPanelContent(display);
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

            if (row.compareButton != null) row.compareButton.SetActive(false);

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
            ClearLeaderboardRowGradeVisual(row.gameObject.GetInstanceID());
            ClearLeaderboardHitboxRun(row.gameObject.GetInstanceID());
        }

        private static void BlankAllLeaderboardRows(LeaderboardDisplay display)
        {
            if (display == null) return;

            Il2CppReferenceArray<LeaderboardRow> rows = display.rowsStandard;
            if (rows == null)
            {
                DebugLog.Log("[ExScoring][Diag] BlankAllLeaderboardRows: rowsStandard is NULL.");
                return;
            }

            for (int i = 0; i < rows.Length; i++)
            {
                if (rows[i] != null) ClearLeaderboardRow(rows[i]);
            }

            ShowLeaderboardPanelContent(display);
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

            Il2CppReferenceArray<LeaderboardRow> rows = display.rowsStandard;
            if (rows == null) return;

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

                ClearLeaderboardRowGradeVisual(row.gameObject.GetInstanceID());
                restoredRows++;
            }

            DebugLog.Log($"[ExScoring][Diag] RestoreLeaderboardRows: restored {restoredRows} row(s).");
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