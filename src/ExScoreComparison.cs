using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Harmony;
using MelonLoader;
using TMPro;
using UnityEngine;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        // ---------------------------------------------------------------------------------------
        // EX score comparison (ApiContract.md v2.30, Sections 4c/7.3/15).
        //
        // Reuses the native compare page (MenuState.State.CompareScoresPage / ScoreComparisonPanel)
        // and its own back button (GunButton -> MenuState.BackOut -> MainPage, confirmed via
        // UnityExplorer). Like native, compare is only offered on the main-menu Total leaderboard.
        //
        // Native flow (Ghidra): LeaderboardRow.CompareScores -> SetState(CompareScoresPage) ->
        // ScoreComparisonPanel.SetUp, which resets the page and asks Harmonix for data;
        // OnLeaderboardDataResponse later destroys the old lines, re-shows `parent`, SnapTo(0) and
        // AddLine()s every song, then shows the total line. Under EX we skip SetUp entirely (no
        // Harmonix request) and do the same reset/fill ourselves from GET /api/users/me/compare.
        //
        // Page objects (UnityExplorer dump):
        //   parent (Contents)         — VerticalLayoutGroup + ContentSizeFitter, holds only the
        //                               ScoreComparisonLine(Clone) rows. comparisonSpinner's
        //                               leaderboardContents IS this object, so hiding it shows the
        //                               loading spinner and showing it hides the spinner.
        //   comparisonLineTotal       — sibling of the scroll view, NOT under parent. Kept hidden.
        //   comparisonLinePrefab      — asset (not in scene), safe to Instantiate.
        //   addFriendSpinner          — LeaderboardLoadingSpinner whose leaderboardContents is
        //                               addFriendSuccess, so it hides itself once success shows.
        // ---------------------------------------------------------------------------------------

        /// <summary>Who a Total leaderboard row's compare button opens a comparison with.</summary>
        private class CompareTarget
        {
            public string userId;
            public string nickname;
        }

        // Keyed by the LeaderboardRow GameObject's instance ID — same convention as
        // leaderboardHitboxRunIds (rowsStandard is a small fixed pool of reused rows).
        private static readonly Dictionary<int, CompareTarget> compareRowTargets = new Dictionary<int, CompareTarget>();

        // compareButton GameObjects we've already hooked a listener onto (see EnsureCompareButtonWired).
        private static readonly HashSet<int> compareButtonsWired = new HashSet<int>();

        // listId/difficulty the Total leaderboard currently on screen was fetched with. Sent as the
        // Section 15 filters so the comparison covers exactly that Total's songs.
        private static string compareTotalListId;
        private static string compareTotalDifficulty;

        // The requester's own EX nickname, picked up from their isRequester Total row. Shown in the
        // page header while the comparison loads (replaced by the response's me.nickname after).
        private static string compareOwnNickname;

        // Bumped every time a comparison is opened; a response only applies if it still matches
        // (same idea as leaderboardRequestVersion in ExLeaderboardDisplay.cs).
        private static int compareRequestVersion = 0;

        // userId of the comparison currently open (null when none). Used by Add Friend.
        private static string compareOpenUserId;
        private static bool compareAddFriendInFlight;

        // Loc token native SetUp uses for the title prefix ("Comparing with " / "Comparaison avec "),
        // from the Ghidra decompile: Loc.Localize(0xb8, 0, false) + playerName.
        private const int CompareTitleLocToken = 0xb8;
        private const string CompareTitleFallbackPrefix = "Comparing with ";
        private static MethodInfo locLocalizeMethod;
        private static bool locLocalizeLookedUp;

        /// <summary>
        /// Called from ApplyTotalLeaderboardEntryToRow (ExLeaderboardDisplay.cs) for every Total row.
        /// Shows the row's native compare button only when a comparison is possible: an API key is
        /// set (Section 15 requires auth), the server sent a userId (v2.30+), and the row isn't the
        /// requester's own. Caches who the row points at for the CompareScores prefix below.
        /// </summary>
        private static void ApplyCompareButton(LeaderboardRow row, TotalLeaderboardApiEntry entry)
        {
            if (row == null) return;

            int slot = row.gameObject.GetInstanceID();

            if (entry != null && entry.isRequester && !string.IsNullOrEmpty(entry.nickname))
                compareOwnNickname = entry.nickname;

            bool canCompare = entry != null
                && !string.IsNullOrEmpty(Config.ApiKey)
                && !string.IsNullOrEmpty(entry.userId)
                && !entry.isRequester;

            if (canCompare)
            {
                compareRowTargets[slot] = new CompareTarget
                {
                    userId = entry.userId,
                    nickname = string.IsNullOrEmpty(entry.nickname) ? "???" : entry.nickname
                };
            }
            else
            {
                compareRowTargets.Remove(slot);
            }

            if (row.compareButton != null)
            {
                row.compareButton.SetActive(canCompare);
                if (canCompare) EnsureCompareButtonWired(row);
            }
        }

        /// <summary>
        /// Per-song rows and blanked rows never offer compare — drops any cached target for the slot.
        /// The caller is responsible for hiding the button itself.
        /// </summary>
        private static void ClearCompareTarget(int slot)
        {
            compareRowTargets.Remove(slot);
        }

        /// <summary>
        /// Remembers which Total leaderboard (list + difficulty) is on screen. Called right before the
        /// Total rows are populated (FetchAndPopulateTotalLeaderboard).
        /// </summary>
        private static void RememberCompareScope(string listId, string difficulty)
        {
            compareTotalListId = listId;
            compareTotalDifficulty = difficulty;
        }

        /// <summary>
        /// The native compare button calls LeaderboardRow.CompareScores through a persistent
        /// UnityEvent listener on its GunButton — confirmed via UnityExplorer on every rowsTotal row
        /// (the set EX writes the Total leaderboard into; rowsStandard rows have no compareButton at
        /// all). Kept as a safety net: if a row's button ever has no persistent listener, add one that
        /// calls CompareScores (which then goes through the prefix below). Never double-wires.
        /// </summary>
        private static void EnsureCompareButtonWired(LeaderboardRow row)
        {
            try
            {
                GameObject button = row.compareButton;
                if (button == null) return;

                int id = button.GetInstanceID();
                if (compareButtonsWired.Contains(id)) return;

                GunButton gunButton = button.GetComponentInChildren<GunButton>(true);
                if (gunButton == null)
                {
                    DebugLog.Log("[ExScoring] EnsureCompareButtonWired: compareButton has no GunButton, leaving as-is.");
                    compareButtonsWired.Add(id);
                    return;
                }

                if (gunButton.onHitEvent != null && gunButton.onHitEvent.GetPersistentEventCount() > 0)
                {
                    // Native wiring present — nothing to add.
                    compareButtonsWired.Add(id);
                    return;
                }

                if (gunButton.onHitEvent == null) gunButton.onHitEvent = new UnityEngine.Events.UnityEvent();

                LeaderboardRow capturedRow = row;
                gunButton.onHitEvent.AddListener(new Action(() =>
                {
                    if (capturedRow != null) capturedRow.CompareScores();
                }));
                compareButtonsWired.Add(id);
                DebugLog.Log("[ExScoring] EnsureCompareButtonWired: compareButton had no persistent listener, added one calling CompareScores.");
            }
            catch (Exception ex)
            {
                DebugLog.Log($"[ExScoring] EnsureCompareButtonWired failed: {ex}");
            }
        }

        /// <summary>
        /// Native compare entry point. With EX scoring on, the native flow (SetUp -> Harmonix request)
        /// is skipped and the page is driven from the AudicaEx API instead. With EX off, native runs
        /// untouched.
        /// </summary>
        [HarmonyPatch(typeof(LeaderboardRow), "CompareScores")]
        public static class LeaderboardRowCompareScoresPatch
        {
            public static bool Prefix(LeaderboardRow __instance)
            {
                if (!Config.ExType) return true;

                try
                {
                    OpenExComparison(__instance);
                }
                catch (Exception ex)
                {
                    DebugLog.Log($"[ExScoring] CompareScores (EX) failed: {ex}");
                }

                return false;
            }
        }

        /// <summary>
        /// Add Friend on the compare page. With EX scoring on, adds the player to the AudicaEx
        /// friends/watch list (POST /api/friends { userId }) instead of the Harmonix FriendsList.
        /// Same visuals as native: spinner on while the request runs, success label on when it lands.
        /// </summary>
        [HarmonyPatch(typeof(ScoreComparisonPanel), "AddFriend")]
        public static class ScoreComparisonPanelAddFriendPatch
        {
            public static bool Prefix(ScoreComparisonPanel __instance)
            {
                if (!Config.ExType) return true;

                try
                {
                    OnExAddFriend(__instance);
                }
                catch (Exception ex)
                {
                    DebugLog.Log($"[ExScoring] AddFriend (EX) failed: {ex}");
                }

                return false;
            }
        }

        private static void OpenExComparison(LeaderboardRow row)
        {
            if (row == null) return;

            int slot = row.gameObject.GetInstanceID();
            if (!compareRowTargets.TryGetValue(slot, out CompareTarget target) || target == null || string.IsNullOrEmpty(target.userId))
            {
                DebugLog.Log($"[ExScoring] OpenExComparison: no compare target cached for row slot={slot}, ignoring.");
                return;
            }

            string listId = string.IsNullOrEmpty(compareTotalListId) ? TotalLeaderboardListId : compareTotalListId;
            string difficulty = string.IsNullOrEmpty(compareTotalDifficulty)
                ? KataConfig.I.GetDifficulty().ToString()
                : compareTotalDifficulty;

            MenuState.SetState(MenuState.State.CompareScoresPage);

            ScoreComparisonPanel panel = MenuState.I != null ? MenuState.I.scoreComparisonPanel : null;
            if (panel == null)
            {
                DebugLog.Log("[ExScoring] OpenExComparison: MenuState.I.scoreComparisonPanel is NULL, aborting.");
                return;
            }

            int version = ++compareRequestVersion;
            compareOpenUserId = target.userId;
            compareAddFriendInFlight = false;

            DebugLog.Log($"[ExScoring] OpenExComparison #{version}: userId={target.userId} nickname={target.nickname} listId={listId} difficulty={difficulty}");

            ResetComparisonPanel(panel, target);

            FetchCompare(target.userId, listId, difficulty, (response, error) =>
            {
                if (version != compareRequestVersion)
                {
                    DebugLog.Log($"[ExScoring] OpenExComparison #{version}: result discarded (stale — current is #{compareRequestVersion}).");
                    return;
                }

                if (!Config.ExType || MenuState.sState != MenuState.State.CompareScoresPage)
                {
                    DebugLog.Log($"[ExScoring] OpenExComparison #{version}: result discarded (ExType={Config.ExType}, state={MenuState.sState}).");
                    return;
                }

                ScoreComparisonPanel livePanel = MenuState.I != null ? MenuState.I.scoreComparisonPanel : null;
                if (livePanel == null)
                {
                    DebugLog.Log($"[ExScoring] OpenExComparison #{version}: panel gone, discarding result.");
                    return;
                }

                if (response == null)
                {
                    ShowComparisonError(livePanel, error);
                    return;
                }

                FillComparisonPanel(livePanel, response);
            });
        }

        /// <summary>
        /// Puts the page in its "loading" state — the same reset native SetUp does (title, names,
        /// lines hidden so the spinner shows, total line hidden, spinner error cleared, Add Friend
        /// objects reset). Add Friend stays hidden until the response says whether the player is
        /// already watched.
        /// </summary>
        private static void ResetComparisonPanel(ScoreComparisonPanel panel, CompareTarget target)
        {
            panel.mComparePlayerId = target.userId;

            if (panel.title != null) panel.title.text = GetCompareTitlePrefix() + target.nickname;
            if (panel.ownName != null) panel.ownName.text = GetOwnCompareName();
            if (panel.otherName != null) panel.otherName.text = target.nickname;

            if (panel.parent != null) panel.parent.gameObject.SetActive(false);
            if (panel.comparisonLineTotal != null) panel.comparisonLineTotal.gameObject.SetActive(false);

            if (panel.comparisonSpinner != null)
            {
                try { panel.comparisonSpinner.ClearError(); }
                catch (Exception ex) { DebugLog.Log($"[ExScoring] ResetComparisonPanel: ClearError failed: {ex}"); }
            }

            if (panel.addFriendButton != null) panel.addFriendButton.SetActive(false);
            if (panel.addFriendSpinner != null) panel.addFriendSpinner.SetActive(false);
            if (panel.addFriendSuccess != null) panel.addFriendSuccess.SetActive(false);
        }

        /// <summary>
        /// Same as native OnLeaderboardDataResponse, but with EX percentages: clears the old lines,
        /// shows `parent` (which hides the spinner), scrolls to the top and adds one line per row in
        /// the order the server sent them. No total line.
        /// </summary>
        private static void FillComparisonPanel(ScoreComparisonPanel panel, CompareApiResponse response)
        {
            RectTransform parent = panel.parent;
            ScoreComparisonLine prefab = panel.comparisonLinePrefab;
            if (parent == null || prefab == null)
            {
                DebugLog.Log($"[ExScoring] FillComparisonPanel: parent={(parent != null)} prefab={(prefab != null)} — cannot build lines.");
                ShowComparisonError(panel, "Compare page unavailable");
                return;
            }

            ClearComparisonLines(parent);

            parent.gameObject.SetActive(true);
            if (panel.comparisonLineTotal != null) panel.comparisonLineTotal.gameObject.SetActive(false);

            if (response.me != null && !string.IsNullOrEmpty(response.me.nickname) && panel.ownName != null)
                panel.ownName.text = response.me.nickname;
            if (response.them != null && !string.IsNullOrEmpty(response.them.nickname) && panel.otherName != null)
                panel.otherName.text = response.them.nickname;

            CompareRow[] rows = response.rows ?? new CompareRow[0];
            int added = 0;
            for (int i = 0; i < rows.Length; i++)
            {
                CompareRow row = rows[i];
                if (row == null) continue;

                GameObject lineObject = UnityEngine.Object.Instantiate(prefab.gameObject, parent.transform, false);
                ScoreComparisonLine line = lineObject.GetComponent<ScoreComparisonLine>();
                if (line == null)
                {
                    DebugLog.Log("[ExScoring] FillComparisonPanel: instantiated line has no ScoreComparisonLine, skipping.");
                    continue;
                }

                SetComparisonLineText(line,
                    string.IsNullOrEmpty(row.songTitle) ? row.songId : row.songTitle,
                    FormatComparePercent(row.mine),
                    FormatComparePercent(row.theirs),
                    FormatCompareDifference(row));
                added++;
            }

            if (panel.scrollable != null)
            {
                try { panel.scrollable.SnapTo(0f); }
                catch (Exception ex) { DebugLog.Log($"[ExScoring] FillComparisonPanel: SnapTo failed: {ex}"); }
            }

            bool alreadyWatched = response.them != null && response.them.isWatched == true;
            if (panel.addFriendButton != null) panel.addFriendButton.SetActive(!alreadyWatched);
            if (panel.addFriendSpinner != null) panel.addFriendSpinner.SetActive(false);
            if (panel.addFriendSuccess != null) panel.addFriendSuccess.SetActive(false);

            DebugLog.Log($"[ExScoring] FillComparisonPanel: {added} line(s) added, them.isWatched={response.them?.isWatched?.ToString() ?? "null"}.");
        }

        /// <summary>
        /// Destroys every line under `parent`. Deactivated first so the VerticalLayoutGroup drops
        /// them immediately (Destroy only happens at end of frame), so new lines lay out from the top.
        /// </summary>
        private static void ClearComparisonLines(RectTransform parent)
        {
            for (int i = parent.childCount - 1; i >= 0; i--)
            {
                Transform child = parent.GetChild(i);
                if (child == null) continue;
                child.gameObject.SetActive(false);
                UnityEngine.Object.Destroy(child.gameObject);
            }
        }

        private static void SetComparisonLineText(ScoreComparisonLine line, string song, string self, string other, string delta)
        {
            if (line.song != null) line.song.text = song ?? "";
            if (line.scoreSelf != null) line.scoreSelf.text = self;
            if (line.scoreOther != null) line.scoreOther.text = other;
            if (line.scoreDelta != null) line.scoreDelta.text = delta;
        }

        private static string FormatComparePercent(CompareRunRef run)
        {
            return run == null ? "-" : $"{run.judgementPercent:0.00}%";
        }

        /// <summary>
        /// Signed difference in percentage points, green when ahead and red when behind — the same
        /// &lt;color=green&gt;/&lt;color=red&gt; rich-text tags native uses on its Delta column. "-" when
        /// either side has no score (server sends difference: null then).
        /// </summary>
        private static string FormatCompareDifference(CompareRow row)
        {
            if (row.difference.HasValue)
            {
                float d = row.difference.Value;
                if (d > 0f) return $"<color=green>+{d:0.00}%</color>";
                if (d < 0f) return $"<color=red>{d:0.00}%</color>";
                return $"{0f:0.00}%";
            }

            return "-";
        }

        private static void ShowComparisonError(ScoreComparisonPanel panel, string error)
        {
            string message = string.IsNullOrEmpty(error) ? "Couldn't load comparison" : error;
            DebugLog.Log($"[ExScoring] ShowComparisonError: {message}");

            // `parent` stays hidden (loading state); the spinner shows the error the same way native
            // OnLeaderboardFailed does.
            if (panel.comparisonSpinner != null)
            {
                try { panel.comparisonSpinner.ShowError(message); }
                catch (Exception ex) { DebugLog.Log($"[ExScoring] ShowComparisonError: ShowError failed: {ex}"); }
            }
        }

        private static void OnExAddFriend(ScoreComparisonPanel panel)
        {
            string userId = compareOpenUserId;
            if (string.IsNullOrEmpty(userId))
            {
                DebugLog.Log("[ExScoring] OnExAddFriend: no comparison open, ignoring.");
                return;
            }

            if (compareAddFriendInFlight)
            {
                DebugLog.Log("[ExScoring] OnExAddFriend: request already in flight, ignoring.");
                return;
            }

            compareAddFriendInFlight = true;
            if (panel.addFriendSpinner != null) panel.addFriendSpinner.SetActive(true);

            AddFriendByUserId(userId, response =>
            {
                compareAddFriendInFlight = false;

                // Still the same comparison on screen? Otherwise the friend is added (server side)
                // but there's nothing on screen to update.
                if (compareOpenUserId != userId || MenuState.sState != MenuState.State.CompareScoresPage)
                {
                    DebugLog.Log($"[ExScoring] OnExAddFriend: result for {userId} arrived after leaving that comparison (success={response != null}).");
                    return;
                }

                ScoreComparisonPanel livePanel = MenuState.I != null ? MenuState.I.scoreComparisonPanel : null;
                if (livePanel == null) return;

                if (response != null)
                {
                    if (livePanel.addFriendSuccess != null) livePanel.addFriendSuccess.SetActive(true);
                    if (livePanel.addFriendSpinner != null) livePanel.addFriendSpinner.SetActive(false);
                    if (livePanel.addFriendButton != null) livePanel.addFriendButton.SetActive(false);
                }
                else
                {
                    // Native has no failure path; just stop the spinner so it doesn't spin forever.
                    if (livePanel.addFriendSpinner != null) livePanel.addFriendSpinner.SetActive(false);
                }
            });
        }

        /// <summary>
        /// Title prefix from the game's own localization (token 0xb8, the one native SetUp uses), so
        /// it reads "Comparaison avec ..." in French etc. Looked up by reflection so a mismatch in
        /// the unhollowed Loc/Token signatures can't break the build — falls back to English.
        /// </summary>
        private static string GetCompareTitlePrefix()
        {
            try
            {
                if (!locLocalizeLookedUp)
                {
                    locLocalizeLookedUp = true;
                    Type locType = typeof(MenuState).Assembly.GetType("Loc");
                    if (locType != null)
                    {
                        locLocalizeMethod = locType
                            .GetMethods(BindingFlags.Public | BindingFlags.Static)
                            .FirstOrDefault(m => m.Name == "Localize"
                                && m.ReturnType == typeof(string)
                                && m.GetParameters().Length == 3
                                && m.GetParameters()[2].ParameterType == typeof(bool));
                    }
                    DebugLog.Log($"[ExScoring] GetCompareTitlePrefix: Loc type {(locType != null ? "found" : "NOT found")}, Localize(3) {(locLocalizeMethod != null ? "found" : "NOT found")}.");
                }

                if (locLocalizeMethod != null)
                {
                    ParameterInfo[] ps = locLocalizeMethod.GetParameters();
                    object token = ToParameterValue(ps[0].ParameterType, CompareTitleLocToken);
                    object count = ToParameterValue(ps[1].ParameterType, 0);
                    string localized = locLocalizeMethod.Invoke(null, new object[] { token, count, false }) as string;
                    if (!string.IsNullOrEmpty(localized)) return localized;
                }
            }
            catch (Exception ex)
            {
                DebugLog.Log($"[ExScoring] GetCompareTitlePrefix: Loc.Localize failed, using fallback: {ex}");
            }

            return CompareTitleFallbackPrefix;
        }

        private static object ToParameterValue(Type type, int value)
        {
            if (type.IsEnum) return Enum.ToObject(type, value);
            return Convert.ChangeType(value, type);
        }

        /// <summary>
        /// Header name for the requester while the comparison loads: their EX nickname if their own
        /// Total row has been seen, otherwise the platform username (what native shows).
        /// </summary>
        private static string GetOwnCompareName()
        {
            if (!string.IsNullOrEmpty(compareOwnNickname)) return compareOwnNickname;

            try
            {
                if (PlatformChooser.I != null)
                {
                    string platformName = PlatformChooser.I.GetUserName();
                    if (!string.IsNullOrEmpty(platformName)) return platformName;
                }
            }
            catch (Exception ex)
            {
                DebugLog.Log($"[ExScoring] GetOwnCompareName: GetUserName failed: {ex}");
            }

            return "";
        }
    }
}