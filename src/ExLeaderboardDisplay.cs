using System;
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
        private static int leaderboardRequestVersion = 0;

        /// <summary>
        /// Single choke point ViewTop()/ViewSelf()/ViewFriends() and the post-song
        /// ReportScoreAndDisplayLeaderboard() all funnel through to actually fetch/display leaderboard
        /// data — patching here covers both the song-select panel and the post-song results screen
        /// without touching either call site directly.
        ///
        /// When EX scoring is off, native runs exactly as before (Harmonix leaderboards), after first
        /// making sure any EX-mode row/button changes from a previous view are cleaned up. When EX
        /// scoring is on, native is skipped entirely (return false) for the Top mode — AudicaEx has no
        /// Self/Friends/Party equivalent, so those modes just blank the rows defensively (the buttons
        /// that trigger them are also hidden, so this should be unreachable in practice) — and the
        /// rows are populated from AudicaEx's leaderboard instead, fetched asynchronously.
        /// </summary>
        [HarmonyPatch(typeof(OnlineLeaderboard), "UpdateLeaderboard")]
        public static class OnlineLeaderboardUpdateLeaderboardPatch
        {
            public static bool Prefix(OnlineLeaderboard __instance, LeaderboardDisplay display, string leaderboardID, OnlineLeaderboard.LeaderboardMode mode)
            {
                MelonLogger.Log($"[ExScoring] UpdateLeaderboard called: mode={mode} leaderboardID={leaderboardID} ExType={Config.ExType} totalLeaderboards={display?.totalLeaderboards}");

                if (!Config.ExType)
                {
                    RestoreNativeLeaderboardButtons(display);
                    RestoreLeaderboardRows(display);
                    return true; // native handles Harmonix leaderboards as normal
                }

                HideNativeLeaderboardButtons(display);

                // AudicaEx has only one leaderboard ("Top") — there's no Self/Friends/Party
                // equivalent. Native still calls this with mode=Self on its own (confirmed via
                // logging: ReportScoreAndDisplayLeaderboard defaults the post-song results
                // leaderboard to Self), even though the buttons that would let the player manually
                // request those modes are hidden above. Rather than treating a non-Top mode as "no
                // such view" and blanking the panel, just show the Top leaderboard regardless —
                // that's the only leaderboard AudicaEx has, so it's the correct thing to show either
                // way.
                if (mode != OnlineLeaderboard.LeaderboardMode.Top)
                {
                    MelonLogger.Log($"[ExScoring] UpdateLeaderboard: mode={mode} requested, showing Top instead (AudicaEx has a single leaderboard).");
                }

                if (display == null)
                {
                    MelonLogger.Log("[ExScoring] UpdateLeaderboard: display is NULL, aborting EX leaderboard fetch.");
                    return false;
                }

                if (selectedSongData == null)
                {
                    MelonLogger.Log("[ExScoring] UpdateLeaderboard: selectedSongData is NULL, aborting EX leaderboard fetch.");
                    BlankAllLeaderboardRows(display);
                    return false;
                }

                string songId = selectedSongData.songID;
                string difficulty = KataConfig.I.GetDifficulty().ToString();
                int rowLimit = LeaderboardDisplay.kNumRows;

                int requestVersion = ++leaderboardRequestVersion;
                MelonLogger.Log($"[ExScoring] UpdateLeaderboard: EX fetch #{requestVersion} starting songId={songId} difficulty={difficulty} rowLimit={rowLimit}");

                FetchLeaderboard(songId, difficulty, rowLimit, "top", response =>
                {
                    if (requestVersion != leaderboardRequestVersion)
                    {
                        MelonLogger.Log($"[ExScoring] UpdateLeaderboard: EX fetch #{requestVersion} result discarded (stale — current is #{leaderboardRequestVersion}).");
                        return;
                    }

                    if (!Config.ExType)
                    {
                        MelonLogger.Log($"[ExScoring] UpdateLeaderboard: EX fetch #{requestVersion} result discarded (scoring type changed mid-fetch).");
                        return;
                    }

                    if (response == null)
                    {
                        MelonLogger.Log($"[ExScoring] UpdateLeaderboard: EX fetch #{requestVersion} failed (see ApiClient log above), blanking rows.");
                        BlankAllLeaderboardRows(display);
                        return;
                    }

                    MelonLogger.Log($"[ExScoring] UpdateLeaderboard: EX fetch #{requestVersion} applying {response.entries?.Length ?? 0} row(s).");
                    PopulateLeaderboardRows(display, response);
                });

                return false; // skip native — we're driving the rows ourselves
            }
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
                MelonLogger.Log("[ExScoring] SyncLeaderboardButtonVisibility: no LeaderboardDisplay found (panel not open), skipping.");
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

            MelonLogger.Log($"[ExScoring] SyncLeaderboardButtonVisibility: synced (ExType={Config.ExType})");
        }

        private static void HideNativeLeaderboardButtons(LeaderboardDisplay display)
        {
            if (display == null) return;

            if (display.friendsButton != null) display.friendsButton.gameObject.SetActive(false);
            if (display.selfButton != null) display.selfButton.gameObject.SetActive(false);
            if (display.extrasButton != null) display.extrasButton.gameObject.SetActive(false);

            MelonLogger.Log("[ExScoring][Diag] HideNativeLeaderboardButtons: friends/self/extras hidden.");
        }

        private static void RestoreNativeLeaderboardButtons(LeaderboardDisplay display)
        {
            if (display == null) return;

            if (display.friendsButton != null) display.friendsButton.gameObject.SetActive(true);
            if (display.selfButton != null) display.selfButton.gameObject.SetActive(true);
            if (display.extrasButton != null) display.extrasButton.gameObject.SetActive(true);

            MelonLogger.Log("[ExScoring][Diag] RestoreNativeLeaderboardButtons: friends/self/extras restored.");
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
                MelonLogger.Log("[ExScoring] PopulateLeaderboardRows: display is NULL, aborting.");
                return;
            }

            Il2CppReferenceArray<LeaderboardRow> rows = display.rowsStandard;
            if (rows == null)
            {
                MelonLogger.Log("[ExScoring] PopulateLeaderboardRows: rowsStandard is NULL, aborting.");
                return;
            }

            LeaderboardApiEntry[] entries = response?.entries;
            int entryCount = entries?.Length ?? 0;
            int rowCount = rows.Length;

            MelonLogger.Log($"[ExScoring] PopulateLeaderboardRows: rowCount={rowCount} entryCount={entryCount}");

            for (int i = 0; i < rowCount; i++)
            {
                LeaderboardRow row = rows[i];
                if (row == null)
                {
                    MelonLogger.Log($"[ExScoring][Diag] PopulateLeaderboardRows: row[{i}] is NULL, skipping.");
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

            if (display.spinner != null)
            {
                display.spinner.gameObject.SetActive(false);
                MelonLogger.Log("[ExScoring][Diag] ShowLeaderboardPanelContent: spinner hidden.");
            }
            else
            {
                MelonLogger.Log("[ExScoring][Diag] ShowLeaderboardPanelContent: spinner is NULL.");
            }

            if (display.scrollRect != null)
            {
                display.scrollRect.enabled = true;
                display.scrollRect.gameObject.SetActive(true);
                MelonLogger.Log("[ExScoring][Diag] ShowLeaderboardPanelContent: scrollRect shown/enabled.");
            }
            else
            {
                MelonLogger.Log("[ExScoring][Diag] ShowLeaderboardPanelContent: scrollRect is NULL.");
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

            HideLeaderboardRowStars(row.starDisplay);

            string gradeId = entry.grade?.id;
            Grade grade;
            if (string.IsNullOrEmpty(gradeId) || !Enum.TryParse(gradeId, out grade))
            {
                MelonLogger.Log($"[ExScoring] ApplyLeaderboardEntryToRow: unrecognized grade id '{gradeId}' for nickname={entry.nickname}, defaulting to F.");
                grade = Grade.F;
            }

            if (row.starDisplay != null)
                CreateOrUpdateLeaderboardRowGradeVisual(slot, row.starDisplay.transform, grade);
            else
                MelonLogger.Log($"[ExScoring][Diag] ApplyLeaderboardEntryToRow: starDisplay NULL for row slot={slot}, cannot place grade visual.");

            if (row.compareButton != null) row.compareButton.SetActive(false);

            row.gameObject.SetActive(true);
        }

        private static void ClearLeaderboardRow(LeaderboardRow row)
        {
            if (row == null) return;

            // Deliberately NOT row.SetNone() — that fills the row with native's "no score to
            // display" placeholder text (visible), where we want the row itself hidden instead.
            row.gameObject.SetActive(false);
            ClearLeaderboardRowGradeVisual(row.gameObject.GetInstanceID());
        }

        private static void BlankAllLeaderboardRows(LeaderboardDisplay display)
        {
            if (display == null) return;

            Il2CppReferenceArray<LeaderboardRow> rows = display.rowsStandard;
            if (rows == null)
            {
                MelonLogger.Log("[ExScoring][Diag] BlankAllLeaderboardRows: rowsStandard is NULL.");
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
                MelonLogger.Log("[ExScoring][Diag] HideLeaderboardRowStars: stars is NULL");
                return;
            }

            int starCount = SetImageArrayEnabled(stars.stars, false, "stars");
            int ringCount = SetImageArrayEnabled(stars.goldRings, false, "goldRings");
            int meterCount = SetImageArrayEnabled(stars.starMeters, false, "starMeters");
            int pipsHidden = SetActiveForAllNamed(stars.transform, "star_pip", false);

            MelonLogger.Log($"[ExScoring][Diag] HideLeaderboardRowStars: hidden stars={starCount} goldRings={ringCount} starMeters={meterCount} pipsHidden={pipsHidden}");
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

            MelonLogger.Log($"[ExScoring][Diag] RestoreLeaderboardRows: restored {restoredRows} row(s).");
        }

        private static int SetImageArrayEnabled(Il2CppReferenceArray<Image> images, bool enabled, string label)
        {
            if (images == null)
            {
                MelonLogger.Log($"[ExScoring][Diag] SetImageArrayEnabled({label}): array is NULL");
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