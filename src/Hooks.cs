using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Harmony;
using MelonLoader;
using TMPro;
using UnityEngine;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        public static string protectedScrollerItem = null;

        // ── Song downloader hook state ──
        private static int settingsButtonCount = 0;

        [HarmonyPatch(typeof(InGameUI), "Restart")]
        public static class InGameUIRestartPatch
        {
            public static void Postfix(
                InGameUI __instance
                )
            {
                ResetExScore();
            }
        }

        [HarmonyPatch(typeof(LaunchPanel), "Play")]
        public static class PlayPatch
        {
            public static bool Prefix()
            {
                suppressShellPageAnimations = false;

                // In marathon setup, Play starts the marathon instead of the selected song.
                if (MarathonSetup.TryStartFromPlay())
                    return false;

                return true;
            }
        }

        [HarmonyPatch(typeof(MenuState), "GoToLaunchPage")]
        public static class GoToLaunchPagePatch
        {
            public static bool Prefix()
            {
                return false;
            }
        }

        [HarmonyPatch(typeof(MenuState), "SetState", new Type[]
        {
            typeof(MenuState.State)
        })]

        public static class SetStatePatch
        {
            public static bool Prefix(MenuState.State state)
            {
                if (state == MenuState.State.LaunchPage && menuState == MenuState.State.SongPage)
                    return false;

                return true;
            }

            public static void Postfix(
                MenuState __instance,
                MenuState.State state
                )
            {
                MelonLogger.Log($"SetState: {menuState} -> {state}");

                if (menuState == MenuState.State.SongPage && state != MenuState.State.SongPage)
                    GlobalOptions.ForceTeardown();

                if (menuState == MenuState.State.Launched && state != MenuState.State.Launched)
                {
                    ResetExScore();
                }

                if (state == MenuState.State.Launched && menuState != MenuState.State.Launched)
                {
                    TrippyMenu.ResetOnSongStart();
                }

                menuState = state;
                if (state == MenuState.State.TitleScreen)
                {
                    gameHasLoaded = true;
                    InitializeTargetIcons();
                }

                if (state == MenuState.State.MainPage)
                {
                    StartWatching();

                    if (MenuState.sLastState == MenuState.State.SongPage)
                    {
                        HideLaunchPanel();
                        suppressShellPageAnimations = false;
                    }
                }
                else
                {
                    StopWatching();
                }

                if (state == MenuState.State.SongPage)
                {
                    if (isAutoSelecting) return;
                    if (songPageSetupQueued) return;

                    songPageSetupQueued = true;
                    MelonCoroutines.Start(SetupSongPageWhenReady(MenuState.sLastState));
                }

                if (state == MenuState.State.SettingsPage)
                {
                    suppressShellPageAnimations = false;
                }
            }
        }

        private static IEnumerator SetupSongPageWhenReady(MenuState.State lastState)
        {
            // The menu hierarchy is torn down when returning straight from gameplay
            // and is rebuilt a few frames later. Wait until the launch page exists
            // before touching any of its objects. On the normal MainPage -> SongPage
            // path the menu is already up, so this loop falls through immediately and
            // the setup runs synchronously this frame.
            int safety = 0;
            while (GameObject.Find("menu")?.transform.Find("ShellPage_Launch") == null)
            {
                if (menuState != MenuState.State.SongPage || ++safety > 600)
                {
                    songPageSetupQueued = false;
                    yield break;
                }
                yield return null;
            }

            // Bail if the user navigated away while we were waiting.
            if (menuState != MenuState.State.SongPage)
            {
                songPageSetupQueued = false;
                yield break;
            }

            SongListUISetup();
            ShowLaunchPanel();
            LaunchPanelUISetup();
            suppressShellPageAnimations = true;

            if (GlobalOptions.HasPendingRestore)
            {
                GlobalOptions.RestoreIfPending();
            }
            else if (lastState == MenuState.State.MainPage)
            {
                AutoSelectSong();
            }
            else
            {
                UpdateLaunchPanelInfo();
            }

            songPageSetupQueued = false;
        }

        [HarmonyPatch(typeof(SongPreview), "OnLaunchScreen")]
        public static class SongPreviewOnLaunchScreenPatch
        {
            public static void Postfix(ref bool __result)
            {
                if (MarathonSetup.Active)
                {
                    __result = false; // no launch-screen preview during marathon setup
                    return;
                }
                if (menuState == MenuState.State.SongPage)
                {
                    __result = true;
                }
            }
        }

        [HarmonyPatch(typeof(GunButton), "OnHit", new Type[] { typeof(Vector3), typeof(Gun) })]
        public static class GunButtonOnHitPatch
        {
            public static bool Prefix(GunButton __instance, Vector3 position, Gun gun)
            {
                Transform parent = __instance.transform.parent;
                if (parent == null) return true;
                DifficultySelectButton diffButton = parent.GetComponent<DifficultySelectButton>();
                if (diffButton == null) return true;
                KataConfig.I.SetDifficulty(diffButton.difficulty);
                var songSelectObj = GameObject.Find("menu/ShellPage_Song/page/ShellPanel_Center/SongSelect");
                var songSelect = songSelectObj.GetComponent<SongSelect>();
                songSelect.mDisplayingForDifficulty = diffButton.difficulty;
                UpdateDifficultyIndicator(diffButton.difficulty);
                RefreshIntensityGraph();
                KataUtil.PlayFMODEvent("event:/shell/button_shatter");
                return false;
            }
        }

        [HarmonyPatch(typeof(SongSelectItem), "Update")]
        public static class SongSelectItemUpdatePatch
        {
            public static void Postfix(SongSelectItem __instance)
            {
                if (__instance.songPreview != null && __instance.songPreview.gameObject.activeSelf)
                {
                    __instance.songPreview.gameObject.SetActive(false);
                }
            }
        }

        [HarmonyPatch(typeof(SongSelectItem), "OnSelect")]
        private static class PatchSongOnSelect
        {
            private static void Postfix(SongSelectItem __instance)
            {
                // Folder rows have no song data — ignore them
                if (__instance.mSongData == null) return;

                // Picking a song exits marathon setup and restores the launch panel.
                MarathonSetup.CancelIfActive();

                selectedSong = __instance.mSongData.songID;
                maxPossibleExScore = GetMaxPossibleExScore(selectedSong);
                selectedSongData = __instance.mSongData;

                if (!isAutoSelecting)
                {
                    MenuState.SetState(MenuState.State.SongPage);
                }

                var songSelect = GameObject.Find("menu/ShellPage_Song/page/ShellPanel_Center/SongSelect");
                var scrollable = songSelect.GetComponent<ShellScrollable>();
                if (scrollable != null)
                    scrollable.enabled = true;

                LaunchPanel launchPanel = GameObject.Find("menu/ShellPage_Launch").GetComponentInChildren<LaunchPanel>();
                if (launchPanel != null)
                    launchPanel.OnEnable();

                UpdateLaunchPanelInfo();
                UpdateSongSelectionIndicator(__instance);
                RefreshIntensityGraph();
                AddPlaylistButton.UpdateForSelection();

                var leaderboard = UnityEngine.Object.FindObjectOfType<LeaderboardDisplay>();
                if (leaderboard != null)
                    leaderboard.ViewTop();

                var songInfo = UnityEngine.Object.FindObjectOfType<SongInfoPanel>();
                if (songInfo != null)
                    songInfo.OnEnable();
            }
        }

        [HarmonyPatch(typeof(Animation), "Play", new Type[] { typeof(string) })]
        public static class AnimationPlayPatch
        {
            public static bool Prefix(Animation __instance, string animation)
            {
                MelonLogger.Log($"Animation.Play({animation}) on {__instance.gameObject.name} suppress={suppressShellPageAnimations}");
                if (suppressShellPageAnimations)
                {
                    if (__instance.gameObject.name == "ShellPage_Launch")
                        return false;
                    if (__instance.gameObject.name == "ShellPage_Song")
                        return false;
                }
                return true;
            }
        }

        [HarmonyPatch(typeof(ScoreKeeper), "OnFailure", new Type[]
        {
            typeof(SongCues.Cue),
            typeof(bool),
            typeof(bool)
        })]
        public static class ScoreKeeperOnFailurePatch
        {
            public static void Postfix(
                SongCues.Cue cue,
                bool pass,
                bool failedDodge
                )
            {
                if (cue == null) return;

                if (!processedCuesIndexes.Contains(cue.index))
                {
                    currentMaxPossibleExScore += GetMaxExScoreForCue(cue);
                    currentMaxPossibleJudgementScore += GetMaxJudgementScoreForCue(cue);

                    ExCue exCue = new ExCue();

                    exCue.index = cue.index;
                    exCue.behavior = cue.behavior;
                    exCue.handType = cue.handType;
                    exCue.tick = cue.tick;
                    exCue.aimAssist = PlayerPreferences.I.AimAssistAmount.mVal;
                    exCue.health = ScoreKeeper.I.GetHealth();
                    exCue.miss = true;

                    processedCuesIndexes.Add(cue.index);
                    exCues.Add(exCue);
                }
            }
        }

        [HarmonyPatch(typeof(Gun), "CalculateAim", new Type[]
        {
            typeof(Target),
            typeof(Vector3)
        })]
        public static class CalculateAimPatch
        {
            public static void Postfix(
                ref Gun __instance,
                Target target,
                Vector3 intersectionPoint,
                ref float __result
                )
            {
                if (Config.LinearCalculation)
                {
                    __result = GetLinearAimScore(target, intersectionPoint);
                }

                int index = target.mCue.index;

                if (!pendingAimResults.ContainsKey(index))
                {
                    pendingAimResults[index] = new List<(float, Vector3)>();
                }

                pendingAimResults[index].Add((__result, intersectionPoint));
            }
        }

        [HarmonyPatch(typeof(ScoreData), "GetScoreForCue", new Type[]
        {
            typeof(SongCues.Cue),
            typeof(float),
            typeof(float),
            typeof(float)
        })]
        public static class GetScoreForCuePatch
        {
            public static void Prefix(
                ScoreData __instance,
                SongCues.Cue cue,
                ref float timing,
                ref float aim,
                ref float extra
                )
            {
                if (Config.LinearCalculation && menuState == MenuState.State.Launched)
                {
                    timing = GetLinearTimingScore(GetTimingMsFromCue(cue));
                }
            }

            public static void Postfix(
                ScoreData __instance,
                SongCues.Cue cue,
                float timing,
                float aim,
                float extra,
                ref int __result
                )
            {
                if (menuState == MenuState.State.Launched)
                {
                    if (!processedCuesIndexes.Contains(cue.index))
                    {
                        currentMaxPossibleExScore += GetMaxExScoreForCue(cue);
                        currentMaxPossibleJudgementScore += GetMaxJudgementScoreForCue(cue);

                        ExCue exCue = new ExCue();

                        ExCue.ScoringCalculation scoringCalculation = ExCue.ScoringCalculation.Audica;
                        float timingMs = GetTimingMsFromCue(cue);

                        if (Config.LinearCalculation)
                        {
                            scoringCalculation = ExCue.ScoringCalculation.Linear;
                        }

                        if (cue.behavior == Target.TargetBehavior.Chain && cue.chainNext == null)
                        {
                            exCue.isChainTail = true;
                            exCue.chainAverage = GetChainAverageFromLastNodeCue(cue);
                            exCue.chainJudgement = GetChainJudgement(exCue.chainAverage);
                        }

                        Vector3 finalIntersection = Vector3.zero;

                        if (pendingAimResults.TryGetValue(cue.index, out var results))
                        {
                            var match = results.FirstOrDefault(r => Mathf.Approximately(r.aimScore, aim));
                            finalIntersection = match.intersectionPoint;
                            pendingAimResults.Remove(cue.index);
                        }

                        Judgement timingJudgement = GetTimingJudgement(timingMs);
                        Judgement aimJudgement = GetAimJudgement(cue.target, finalIntersection);

                        exCue.behavior = cue.behavior;
                        exCue.handType = cue.handType;
                        exCue.tick = cue.tick;
                        exCue.successTick = cue.successTick;
                        exCue.timing = timing;
                        exCue.timingMs = timingMs;
                        exCue.aim = aim;
                        exCue.targetHitPos = GetTargetHitPos(cue.target, finalIntersection);
                        exCue.contactPos = cue.target.GetContactPosition();
                        exCue.intersectionPoint = finalIntersection;
                        exCue.health = ScoreKeeper.I.GetHealth();
                        exCue.velocity = cue.meleeVelocityAmount;
                        exCue.sustainPercent = cue.sustainPercent;
                        exCue.aimAssist = PlayerPreferences.I.AimAssistAmount.mVal;
                        exCue.index = cue.index;
                        exCue.scoringCalculation = scoringCalculation;
                        exCue.timingJudgement = timingJudgement;
                        exCue.aimJudgement = aimJudgement;

                        processedCuesIndexes.Add(cue.index);
                        exCues.Add(exCue);
                        float exCueScore = GetExScoreForExCue(exCue);

                        float judgementCueScore = 0f;

                        if (cue.behavior == Target.TargetBehavior.Vertical ||
                            cue.behavior == Target.TargetBehavior.Horizontal ||
                            cue.behavior == Target.TargetBehavior.ChainStart ||
                            cue.behavior == Target.TargetBehavior.Standard ||
                            cue.behavior == Target.TargetBehavior.Hold)
                        {
                            judgementCueScore += GetJudgementScore(timingJudgement) + GetJudgementScore(aimJudgement);
                        }
                        if (cue.behavior == Target.TargetBehavior.Chain && cue.chainNext == null)
                        {
                            judgementCueScore += GetJudgementScore(exCue.chainJudgement);
                        }
                        if (cue.behavior == Target.TargetBehavior.Hold && exCue.sustainPercent == 1)
                        {
                            judgementCueScore += 1;
                        }
                        if (cue.behavior == Target.TargetBehavior.Melee && exCue.velocity != 0)
                        {
                            judgementCueScore += 1;
                        }

                        judgementScore += judgementCueScore;

                        exScore += exCueScore;

                        if (cue.behavior == Target.TargetBehavior.Melee && cue.meleeVelocityAmount > 0)
                        {
                            nextPopupText = GetMeleeJudgementText();
                        }
                        else if (cue.behavior == Target.TargetBehavior.Chain && cue.chainNext != null)
                        {
                            nextPopupText = "";
                        }
                        else if (cue.behavior == Target.TargetBehavior.Chain && cue.chainNext == null)
                        {
                            nextPopupText = GetChainJudgementText(exCue.chainJudgement);
                        }
                        else
                        {
                            nextPopupText = GetJudgementText(timingJudgement, aimJudgement);
                        }
                    }
                }
            }
        }

        [HarmonyPatch(typeof(ScoreKeeperDisplay), "Update")]
        private static class PatchScoreKeeperUpdate
        {
            private static void Postfix(
                ScoreKeeperDisplay __instance
                )
            {
                if (Config.ExType) ScoreKeeperDisplayUpdate(__instance);
            }
        }

        [HarmonyPatch(typeof(Target), "CompleteTarget")]
        private static class Target_CompleteTarget
        {
            private static void Prefix(
                Target __instance
                )
            {
                nextPopupIsScore = true;
            }
        }

        [HarmonyPatch(typeof(TextPopupPool), "CreatePopup", new Type[] {
            typeof(Vector3),
            typeof(Quaternion),
            typeof(Vector3),
            typeof(string),
            typeof(string)
        })]
        private static class TextPopupPool_CreatePopup
        {
            private static void Prefix(
                TextPopupPool __instance,
                Vector3 position,
                Quaternion rotation,
                Vector3 scale,
                ref string text,
                ref string extraText
                )
            {
                if (nextPopupIsScore && Config.ExType)
                {
                    nextPopupIsScore = false;

                    var index = __instance.mIndex;
                    var popup = __instance.mPopups[index];

                    popup.text.richText = true;

                    text = nextPopupText.ToString();
                    extraText = "";

                    nextPopupText = "";
                }
            }
        }

        [HarmonyPatch(typeof(GameplayStats), "UpdateDisplay")]
        public static class GameplayStatsUpdateDisplayPatch
        {
            public static bool _hasRun = false;

            public static bool Prefix(GameplayStats __instance)
            {
                if (Config.ExType)
                {
                    if (_hasRun) return false;
                    _hasRun = true;

                    // Hide GameplayStats children except frame and header
                    Transform t = __instance.transform;
                    for (int i = 0; i < t.childCount; i++)
                    {
                        Transform child = t.GetChild(i);
                        if (child.name != "frame" && child.name != "header")
                            child.gameObject.SetActive(false);
                    }

                    // Hide siblings on ShellPanel_Center except what we want to keep
                    Transform parent = __instance.transform.parent;
                    for (int i = 0; i < parent.childCount; i++)
                    {
                        Transform sibling = parent.GetChild(i);
                        if (sibling.name != "GameplayStats" &&
                            sibling.name != "continue" &&
                            sibling.name != "Glass" &&
                            sibling.name != "SongAndDifficulty" &&
                            sibling.name != "Reflector")
                            sibling.gameObject.SetActive(false);
                    }

                    Transform songAndDifficulty = parent.Find("SongAndDifficulty");
                    TMP_Text original = songAndDifficulty.GetComponent<TMP_Text>();
                    RectTransform originalRt = songAndDifficulty.GetComponent<RectTransform>();

                    int layer = songAndDifficulty.gameObject.layer;

                    CreateTextObject("ExTimingDisplay", GetTimingJudgementString(exCues), parent, layer, original, originalRt, new Vector3(-203, 290, 0), new Vector3(50, 50, 50));
                    CreateTextObject("ExAimDisplay", GetAimJudgementString(exCues), parent, layer, original, originalRt, new Vector3(100, 290, 0), new Vector3(50, 50, 50));
                    CreateTextObject("ExChainDisplay", GetChainJudgementString(exCues), parent, layer, original, originalRt, new Vector3(405, 290, 0), new Vector3(50, 50, 50));
                    CreateTextObject("ExMiscDisplay", GetMiscString(exCues), parent, layer, original, originalRt, new Vector3(130, 465, 0), new Vector3(50, 50, 50));
                    CreateTextObject("ExScoreDisplay", $"Score: {GetCurrentMaxPossibleJudgementPercentage()}%", parent, layer, original, originalRt, new Vector3(287, 548, 0), new Vector3(120, 120, 120), TextAlignmentOptions.Center);

                    return false;
                }
                else
                {
                    return true;
                }

            }
        }

        [HarmonyPatch(typeof(SongEndSequence), "Update")]
        public static class SongEndSequenceUpdatePatch
        {
            public static void Postfix(SongEndSequence __instance)
            {
                if (Config.ExType)
                {
                    if (__instance.mState == SongEndSequence.State.WaitForScorePercentStars) return;

                    GameObject scoreAndPercent = __instance.scorePercentStars
                        .transform.Find("ScoreAndPercent")?.gameObject;
                    if (scoreAndPercent == null) return;

                    TextMeshPro tmp = scoreAndPercent.GetComponent<TextMeshPro>();
                    if (tmp.text != $"{GetCurrentMaxPossibleJudgementPercentage()}%")
                        tmp.text = $"{GetCurrentMaxPossibleJudgementPercentage()}%";
                }
            }
        }

        /// <summary>
        /// Fires once, exactly when the song actually ends (before the end sequence
        /// animation/UI plays). Used to persist raw run data for completed (non-failed) runs.
        /// </summary>
        [HarmonyPatch(typeof(SongEnd), "OnSongOver", new Type[] { typeof(bool) })]
        public static class SongEndOnSongOverPatch
        {
            public static void Postfix(SongEnd __instance, bool failed)
            {
                if (!failed || Config.SaveFailedRunData)
                {
                    SaveRunData();
                }
            }
        }

        /// <summary>
        /// When enabled, redirects a failed song into the normal results/stats screen
        /// (GameplayStats) instead of the vanilla FailedScreen. GameplayStatsUpdateDisplayPatch
        /// already handles both Config.ExType on (EX breakdown) and off (vanilla Audica stats).
        /// </summary>
        [HarmonyPatch(typeof(FailedScreen), "OnEnable")]
        private static class FailedScreenOnEnablePatch
        {
            private static bool Prefix(FailedScreen __instance)
            {
                if (KataConfig.I.practiceMode) return true;
                if (!Config.ShowStatsOnFail) return true;

                SongEnd.I.ShowResults();
                AudioDriver.I.Pause();

                return true;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  Song downloader hooks
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Injects the "Download Songs" button onto the Settings page.
        /// Counts buttons added to the Main settings page; once the 9th
        /// button appears we know the page is fully built and we add ours.
        /// </summary>
        [HarmonyPatch(typeof(OptionsMenu), "AddButton", new Type[] {
            typeof(int),
            typeof(string),
            typeof(OptionsMenuButton.SelectedActionDelegate),
            typeof(OptionsMenuButton.IsCheckedDelegate),
            typeof(string),
            typeof(OptionsMenuButton)
        })]
        private static class OptionsMenuAddButtonPatch
        {
            private static void Postfix(OptionsMenu __instance, int col, string label,
                OptionsMenuButton.SelectedActionDelegate onSelected,
                OptionsMenuButton.IsCheckedDelegate isChecked)
            {
                if (__instance.mPage == OptionsMenu.Page.Main)
                {
                    settingsButtonCount++;
                    if (settingsButtonCount == 9)
                    {
                        SongSearchScreen.SetMenu(__instance);
                    }
                }
            }
        }

        /// <summary>
        /// Resets state when navigating between settings pages.
        /// Postfix routes to the correct panel based on current state.
        /// </summary>
        [HarmonyPatch(typeof(OptionsMenu), "ShowPage", new Type[] { typeof(OptionsMenu.Page) })]
        private static class OptionsMenuShowPagePatch
        {
            private static bool Prefix(OptionsMenu __instance, OptionsMenu.Page page)
            {
                shouldShowKeyboard = false;
                settingsButtonCount = 0;

                if (OptionsMenuClone.Menu != null &&
                    __instance.Pointer == OptionsMenuClone.Menu.Pointer &&
                    !OptionsMenuClone.allowShowPage)
                {
                    MelonLogger.Log($"[ShowPage CLONE] BLOCKED page={page}");
                    return false;
                }

                return true;
            }

            private static void Postfix(OptionsMenu __instance, OptionsMenu.Page page)
            {
                // Old playlist settings-panel routing retired; the playlist UI now lives
                // entirely in the in-world folder/list navigation.
            }
        }

        /// <summary>
        /// Prevents the keyboard from hiding while the download search field is active.
        /// </summary>
        [HarmonyPatch(typeof(KeyboardEntry), "Hide", new Type[0])]
        private static class KeyboardEntryHidePatch
        {
            private static bool Prefix(KeyboardEntry __instance)
            {
                if (shouldShowKeyboard)
                    return false;
                return true;
            }
        }

        /// <summary>
        /// Routes keyboard input to the appropriate search field.
        /// If a song search is in progress, input goes to SongSearch.
        /// Otherwise input goes to the download search.
        /// </summary>
        [HarmonyPatch(typeof(KeyboardEntry), "OnKey", new Type[] { typeof(KeyCode), typeof(string) })]
        private static class KeyboardEntryOnKeyPatch
        {
            private static bool Prefix(KeyboardEntry __instance, KeyCode keyCode, string label)
            {
                if (shouldShowKeyboard)
                {
                    if (SongSearch.searchInProgress)
                    {
                        switch (label)
                        {
                            case "done":
                                SearchKeyboard.Hide();
                                SongSearch.RunSearch(SongSearch.query);
                                SongSearch.UpdateLiveText();
                                break;
                            case "clear":
                                SongSearch.query = "";
                                SongSearch.UpdateLiveText();
                                break;
                            default:
                                SongSearch.query += label;
                                SongSearch.UpdateLiveText();
                                break;
                        }
                    }
                    else if (PlaylistNav.Creating)
                    {
                        switch (label)
                        {
                            case "done": PlaylistNav.FinishCreate(); break;
                            case "clear": PlaylistNav.SetCreateName(""); break;
                            default: PlaylistNav.AppendCreateName(label); break;
                        }
                    }
                    else
                    {
                        return true;
                    }

                    return false;
                }
                return true;
            }
        }

        /// <summary>
        /// Handles backspace for search, playlist create, and download search fields.
        /// </summary>
        [HarmonyPatch(typeof(KeyboardEntry), "OnBackspace", new Type[0])]
        private static class KeyboardEntryOnBackspacePatch
        {
            private static bool Prefix(KeyboardEntry __instance)
            {
                if (shouldShowKeyboard)
                {
                    if (SongSearch.searchInProgress)
                    {
                        if (string.IsNullOrEmpty(SongSearch.query))
                            return false;
                        SongSearch.query = SongSearch.query.Substring(0, SongSearch.query.Length - 1);
                        SongSearch.UpdateLiveText();
                    }
                    else if (PlaylistNav.Creating)
                    {
                        PlaylistNav.BackspaceCreateName();
                    }
                    else
                    {
                        return true;
                    }
                    return false;
                }
                return true;
            }
        }

        /// <summary>
        /// Handles underscore/space key for search, playlist create, and download search fields.
        /// </summary>
        [HarmonyPatch(typeof(KeyboardEntry), "OnUnderscore", new Type[0])]
        private static class KeyboardEntryOnUnderscorePatch
        {
            private static bool Prefix(KeyboardEntry __instance)
            {
                if (shouldShowKeyboard)
                {
                    if (SongSearch.searchInProgress)
                    {
                        SongSearch.query += " ";
                        SongSearch.UpdateLiveText();
                    }
                    else if (PlaylistNav.Creating)
                    {
                        PlaylistNav.AppendCreateName(" ");
                    }
                    else
                    {
                        return true;
                    }
                    return false;
                }
                return true;
            }
        }

        /// <summary>
        /// Initializes the song download tracker, filter panel, playlists, and triggers initial API search
        /// once the game startup logo is done.
        /// </summary>
        [HarmonyPatch(typeof(StartupLogo), "SetState", new Type[] { typeof(StartupLogo.State) })]
        private static class StartupLogoSetStatePatch
        {
            private static void Postfix(StartupLogo __instance, ref StartupLogo.State state)
            {
                if (state == StartupLogo.State.Done)
                {
                    PlaylistManager.OnApplicationStart();
                    FavoritesStore.Load();
                    SongDownloadTracker.StartSongListUpdate();
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  Filter / song search hooks
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Removes deleted songs and hands the full song set to the folder system to organize.
        /// </summary>
        [HarmonyPatch(typeof(SongSelect), "GetSongIDs", new Type[] { typeof(bool) })]
        private static class SongSelectGetSongIDsPatch
        {
            private static void Postfix(SongSelect __instance, ref bool extras, ref Il2CppSystem.Collections.Generic.List<string> __result)
            {
                if (deletedSongs.Count > 0)
                {
                    foreach (var deletedSong in deletedSongs)
                    {
                        __result.Remove(deletedSong);
                    }
                }

                // Inject ALL songs so the folder system has every song available to organize,
                // regardless of the built-in Main/Extras filter.
                FolderRowManager.InjectAllSongs(__result);

                RandomSong.UpdateAvailableSongs(__result, extras);
                MelonLogger.Log($"[RandomSong] UpdateAvailableSongs called: extras={extras}, count={__result.Count}");
            }
        }

        /// <summary>
        /// All eight SetSort_* buttons funnel through ChangeSort. After the game records its
        /// sListSort, tell the folder system which view to build (Default / A-Z / Z-A).
        /// </summary>
        [HarmonyPatch(typeof(SongSelect), "ChangeSort", new Type[] { typeof(SongSelect.Sort) })]
        private static class SongSelectChangeSortPatch
        {
            private static void Postfix(SongSelect.Sort newSort)
            {
                FolderRowManager.SetSort(newSort);
            }
        }

        /// <summary>
        /// Sets up the song-list notification and folder defaults when the song select page loads.
        /// </summary>
        [HarmonyPatch(typeof(SongSelect), "OnEnable", new Type[0])]
        private static class SongSelectOnEnablePatch
        {
            private static void Postfix(SongSelect __instance)
            {
                SongListNotification.Setup();
                PlaylistManager.PopulatePlaylistsSongNames();
                PlaylistManager.DownloadMissingSongs();
                MelonCoroutines.Start(UpdateLastSongCount());

                // Default to the Main filter (where the folder system works) instead of All
                if (__instance.GetListFilter() == SongSelect.Filter.All &&
                    SongFolderManager.availableFolders != null && SongFolderManager.availableFolders.Count > 0)
                {
                    var slc = GameObject.FindObjectOfType<SongListControls>();
                    if (slc != null)
                        slc.FilterMain();
                }
            }
        }

        /// <summary>
        /// Redirects "All" filter to "Main" filter to avoid the dual main+extras
        /// code path that creates section headers. Our folder system organizes
        /// ALL songs regardless of the built-in filter, so Main mode is sufficient.
        /// </summary>
        [HarmonyPatch(typeof(SongListControls), "FilterAll", new Type[0])]
        private static class SongListControlsFilterAllPatch
        {
            private static bool Prefix(SongListControls __instance)
            {
                // Redirect to FilterMain so ShowSongList takes the single-phase path
                __instance.FilterMain();
                return false; // skip original FilterAll
            }
        }

        /// <summary>
        /// Creates the search and refresh buttons when entering the song page.
        /// </summary>
        [HarmonyPatch(typeof(MenuState), "SetState", new Type[] { typeof(MenuState.State) })]
        private static class MenuStateSetStateFilterPatch
        {
            private static void Postfix(MenuState __instance, ref MenuState.State state)
            {
                if (state == MenuState.State.SongPage)
                {
                    SongSearchField.CreateField();
                    SongRequestQueueToggle.Create();
                    RefreshButton.CreateRefreshButton();
                    RandomSongButton.CreateRandomSongButton();
                    PlaylistEndlessManager.ResetIndex();
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  Playlist endless mode hooks
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Handles endless mode song transitions when score screen is shown.
        /// Triggers fade out and allows score display.
        /// </summary>
        [HarmonyPatch(typeof(SongEndSequence), "SetState", new Type[] { typeof(SongEndSequence.State) })]
        private static class SongEndSequenceSetStatePatch
        {
            private static bool Prefix(SongEndSequence __instance, ref SongEndSequence.State newState)
            {
                if (PlaylistManager.state == PlaylistManager.PlaylistState.Endless)
                {
                    if (PlaylistConfig.ShowScores)
                    {
                        if (newState == SongEndSequence.State.WaitForScorePercentStars)
                        {
                            PlaylistEndlessManager.FadeOut();
                            return true;
                        }
                    }
                }
                return true;
            }
        }

        /// <summary>
        /// Skips the score screen in endless mode when ShowScores is disabled.
        /// Zeroes out delays and hides UI elements, then jumps to SequenceComplete.
        /// </summary>
        [HarmonyPatch(typeof(SongEndSequence), "Start", new Type[0])]
        private static class SongEndSequenceStartPatch
        {
            private static bool Prefix(SongEndSequence __instance)
            {
                if (PlaylistManager.state == PlaylistManager.PlaylistState.Endless)
                {
                    if (!PlaylistConfig.ShowScores)
                    {
                        PlaylistEndlessManager.FadeOut();
                        __instance.startDelay = 0f;
                        __instance.waitDelay = 0f;
                        __instance.endDelay = 0f;
                        __instance.levelComplete.SetActive(false);
                        __instance.newHighScore.SetActive(false);
                        __instance.scorePercentStars.SetActive(false);
                        __instance.fullCombo.SetActive(false);
                        __instance.SetState(SongEndSequence.State.SequenceComplete);
                        return false;
                    }
                }
                return true;
            }
        }

        /// <summary>
        /// Handles endless mode results page skip and creates buttons on in-game screens.
        /// </summary>
        [HarmonyPatch(typeof(InGameUI), "SetState", new Type[] { typeof(InGameUI.State), typeof(bool) })]
        private static class InGameUISetStatePatch
        {
            private static bool Prefix(InGameUI __instance, InGameUI.State state, bool instant)
            {
                if (PlaylistManager.state == PlaylistManager.PlaylistState.Endless)
                {
                    if (state == InGameUI.State.ResultsPage)
                    {
                        PlaylistEndlessManager.NextSong();
                        return false;
                    }
                }
                return true;
            }

            private static void Postfix(InGameUI __instance, InGameUI.State state, bool instant)
            {
                if (state == InGameUI.State.FailedPage)
                {
                    CreateDeleteButton(ButtonUtils.ButtonLocation.Failed);
                    CreateFavoriteButton(ButtonUtils.ButtonLocation.Failed);
                    PlaylistEndlessSkipButton.CreateSkipButton(ButtonUtils.ButtonLocation.Failed);
                }
                else if (state == InGameUI.State.PausePage)
                {
                    CreateDeleteButton(ButtonUtils.ButtonLocation.Pause);
                    CreateFavoriteButton(ButtonUtils.ButtonLocation.Pause);
                    PlaylistEndlessSkipButton.CreateSkipButton(ButtonUtils.ButtonLocation.Pause);
                }
                else if (state == InGameUI.State.EndGameContinuePage)
                {
                    CreateDeleteButton(ButtonUtils.ButtonLocation.EndGame);
                    CreateFavoriteButton(ButtonUtils.ButtonLocation.EndGame);
                }
                else if (state == InGameUI.State.PracticeModeOverPage)
                {
                    CreateDeleteButton(ButtonUtils.ButtonLocation.PracticeModeOver);
                    CreateFavoriteButton(ButtonUtils.ButtonLocation.PracticeModeOver);
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  Song download tracker post-process hooks
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// After ShowSongList starts building, let FolderRowManager inject folder rows
        /// once the async build is complete.
        /// </summary>
        [HarmonyPatch(typeof(SongSelect), "ShowSongList", new Type[0])]
        private static class SongSelectShowSongListPatch
        {
            private static void Postfix(SongSelect __instance)
            {
                FolderRowManager.Rebuild(__instance);
            }
        }

        // ──────────────────────────────────────────────────────────────────
        //  DISABLED: targeted SongList.SongListLoaded, which does not exist in
        //  Audica 1.0.2.1. The real member is the callback object
        //  SongList.OnSongListLoaded (.On(Action) / .mDone), not a method, so
        //  HarmonyLib 2.x threw "Undefined target method" and aborted PatchAll
        //  for the entire ExScoring assembly, silently breaking every patch.
        //
        //  Disabled to let PatchAll complete. Most of this body is already
        //  covered by SongSelectOnEnablePatch (PopulatePlaylistsSongNames +
        //  FilterMain on page entry). TODO (Option B follow-up): re-home the
        //  two unique calls below onto SongList.OnSongListLoaded.On(...):
        //      SongFolderManager.Rebuild(mainSongDirectory);
        //      PlaylistManager.EnableBackButton();
        //
        // [HarmonyPatch(typeof(SongList), "SongListLoaded", new Type[0])]
        // private static class SongListSongListLoadedPatch
        // {
        //     private static void Postfix()
        //     {
        //         SongFolderManager.Rebuild(mainSongDirectory);
        //         PlaylistManager.PopulatePlaylistsSongNames();
        //         PlaylistManager.EnableBackButton();
        //
        //         if (SongFolderManager.availableFolders != null && SongFolderManager.availableFolders.Count > 0)
        //         {
        //             var slc = UnityEngine.GameObject.FindObjectOfType<SongListControls>();
        //             if (slc != null)
        //                 slc.FilterMain();
        //         }
        //     }
        // }

        /*
        [HarmonyPatch(typeof(StartupLoader), "SetState", new Type[] { typeof(StartupLoader.State) })]
        public static class StartupLoaderSetStatePatch
        {
            public static bool Prefix(StartupLoader __instance, ref StartupLoader.State newState)
            {
                if (newState == StartupLoader.State.HMXLogo)
                {
                    MelonLoader.MelonLogger.Log("Skipping HMXLogo state, redirecting to Complete");
                    newState = StartupLoader.State.Complete;
                }
                return true;
            }
        }
        */

        [HarmonyPatch(typeof(StartupLogo), "OnEnable", new Type[0])]
        private static class StartupLogoOnEnablePatch
        {
            private static void Postfix(StartupLogo __instance)
            {
                __instance.displayTime = 0.2f;
                __instance.fadeInTime = 0.2f;
                __instance.fadeOutTime = 0.2f;
                MelonLoader.MelonLogger.Log("Shortened StartupLogo timings (moderate)");
            }
        }

        [HarmonyPatch(typeof(Target), "UpdateChainLineAnim", new Type[] { typeof(float), typeof(float) })]
        public static class TargetUpdateChainLineAnimPatch
        {
            public static void Postfix(Target __instance, float ticksUntil, float animationTime)
            {
                LineRenderer chain = __instance.chainLine;
                if (chain == null || !chain.enabled || chain.positionCount < 2)
                {
                    ChainArrow.Hide(__instance);
                    return;
                }

                Vector3 p0 = chain.GetPosition(0);
                Vector3 p1 = chain.GetPosition(1);

                Vector3 dir = p1 - p0;
                float len = dir.magnitude;
                if (len < 0.0001f)
                {
                    ChainArrow.Hide(__instance);
                    return;
                }
                dir /= len;

                Target.TargetHandType hand = ChainArrow.GetCachedHandType(__instance);

                // Chain line color
                Color chainColor = Config.ChainLineColorMode == 1
                    ? ChainArrow.GetHandColor(hand)
                    : ChainArrow.GetDefaultChainColor(chain);
                ChainArrow.ApplyColor(chain, chainColor);

                if (!Config.EnableChainArrow)
                {
                    ChainArrow.Hide(__instance);
                    return;
                }

                LineRenderer arrow = ChainArrow.GetOrCreate(__instance);
                if (arrow == null)
                    return;

                Vector3 mid = (p0 + p1) * 0.5f;
                Vector3 perp = Vector3.Cross(dir, __instance.transform.forward).normalized;

                float arrowSize = Mathf.Clamp(len * Config.ArrowLength, 0.02f, 0.5f);
                Vector3 back = -dir * arrowSize;
                Vector3 side = perp * arrowSize * 0.6f;

                arrow.SetPosition(0, mid + back + side);
                arrow.SetPosition(1, mid + dir * arrowSize);
                arrow.SetPosition(2, mid + back - side);
                arrow.widthMultiplier = Config.ArrowWidth;

                Color arrowColor = Config.ArrowColorMode == 1
                    ? ChainArrow.GetHandColor(hand)
                    : Color.white;
                ChainArrow.ApplyColor(arrow, arrowColor);

                arrow.enabled = true;
            }
        }

        [HarmonyPatch(typeof(Target), "OnCreated", new Type[] { typeof(Target.TargetBehavior), typeof(Target.TargetHandType) })]
        public static class TargetOnCreatedHandCachePatch
        {
            public static void Postfix(Target __instance, Target.TargetBehavior behavior, Target.TargetHandType handType)
            {
                ChainArrow.CacheHandType(__instance, handType);
            }
        }

        [HarmonyPatch(typeof(Target), "OnDestroy", new Type[0])]
        public static class TargetOnDestroyHandCachePatch
        {
            public static void Prefix(Target __instance)
            {
                ChainArrow.ClearCache(__instance);
            }
        }
    }
}