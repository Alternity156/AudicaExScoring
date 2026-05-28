using System;
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
            public static void Prefix()
            {
                suppressShellPageAnimations = false;
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

                if (menuState == MenuState.State.Launched && state != MenuState.State.Launched)
                {
                    ResetExScore();
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

                    SongListUISetup();
                    ShowLaunchPanel();
                    LaunchPanelUISetup();
                    suppressShellPageAnimations = true;

                    if (MenuState.sLastState == MenuState.State.MainPage)
                    {
                        AutoSelectSong();
                    }
                }

                if (state == MenuState.State.SettingsPage)
                {
                    suppressShellPageAnimations = false;
                }
            }
        }

        [HarmonyPatch(typeof(SongPreview), "OnLaunchScreen")]
        public static class SongPreviewOnLaunchScreenPatch
        {
            public static void Postfix(ref bool __result)
            {
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
                    exCue.miss = true;
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
                        SongDownloaderUI.AddPageButton(__instance, 0);
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
            private static void Prefix(OptionsMenu __instance, OptionsMenu.Page page)
            {
                shouldShowKeyboard = false;
                settingsButtonCount = 0;
                SongDownloader.searchString = "";
            }

            private static void Postfix(OptionsMenu __instance, OptionsMenu.Page page)
            {
                if (page == OptionsMenu.Page.Main)
                {
                    PlaylistCreatePanel.SetMenu(__instance);
                    PlaylistSelectPanel.SetMenu(__instance);
                    PlaylistEditPanel.SetMenu(__instance);
                    PlaylistEndlessPanel.SetMenu(__instance);

                    if (SongSearch.searchInProgress)
                    {
                        SongSearchScreen.GoToSearch();
                    }
                    else if (PlaylistManager.state == PlaylistManager.PlaylistState.Selecting ||
                             PlaylistManager.state == PlaylistManager.PlaylistState.Adding)
                    {
                        PlaylistSelectPanel.GoToPanel();
                    }
                    else if (PlaylistManager.state == PlaylistManager.PlaylistState.Endless)
                    {
                        PlaylistEndlessPanel.GoToPanel();
                    }
                }
                else if (page == OptionsMenu.Page.Misc)
                {
                    if (PlaylistManager.state == PlaylistManager.PlaylistState.Creating)
                    {
                        PlaylistCreatePanel.GoToPanel();
                    }
                    else if (PlaylistManager.state == PlaylistManager.PlaylistState.Editing)
                    {
                        PlaylistEditPanel.GoToPanel();
                    }
                }
            }
        }

        /// <summary>
        /// Handles cleanup when backing out of settings pages.
        /// Routes to the correct cancel handler based on current state.
        /// </summary>
        [HarmonyPatch(typeof(OptionsMenu), "BackOut", new Type[0])]
        private static class OptionsMenuBackOutPatch
        {
            private static bool Prefix(OptionsMenu __instance)
            {
                if (SongSearch.searchInProgress)
                {
                    SongSearch.CancelSearch();
                    return false;
                }
                else if (PlaylistManager.state == PlaylistManager.PlaylistState.Selecting ||
                         PlaylistManager.state == PlaylistManager.PlaylistState.Adding)
                {
                    PlaylistSelectPanel.CancelSelect();
                    return false;
                }
                else if (PlaylistManager.state == PlaylistManager.PlaylistState.Creating)
                {
                    PlaylistCreatePanel.CancelCreate();
                    return false;
                }
                else if (PlaylistManager.state == PlaylistManager.PlaylistState.Editing)
                {
                    PlaylistEditPanel.CancelEdit();
                    return false;
                }
                else if (PlaylistManager.state == PlaylistManager.PlaylistState.Endless)
                {
                    PlaylistEndlessPanel.CancelEndless();
                    return false;
                }
                else
                {
                    if (SongDownloaderUI.songItemPanel != null)
                        SongDownloaderUI.songItemPanel.SetPageActive(false);
                    if (SongDownloader.needRefresh)
                        ReloadSongList(false);
                }
                return true;
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
                                __instance.Hide();
                                shouldShowKeyboard = false;
                                SongSearch.OnNewUserSearch();
                                break;
                            case "clear":
                                SongSearch.query = "";
                                break;
                            default:
                                SongSearch.query += label;
                                break;
                        }

                        if (SongSearchScreen.searchText != null)
                        {
                            SongSearchScreen.searchText.text = SongSearch.query;
                        }
                    }
                    else if (PlaylistManager.state == PlaylistManager.PlaylistState.Creating)
                    {
                        switch (label)
                        {
                            case "done":
                                __instance.Hide();
                                shouldShowKeyboard = false;
                                break;
                            case "clear":
                                PlaylistCreatePanel.newName = "";
                                break;
                            default:
                                PlaylistCreatePanel.newName += label;
                                break;
                        }

                        if (PlaylistCreatePanel.playlistText != null)
                        {
                            PlaylistCreatePanel.playlistText.text = PlaylistCreatePanel.newName;
                        }
                    }
                    else
                    {
                        switch (label)
                        {
                            case "done":
                                __instance.Hide();
                                shouldShowKeyboard = false;
                                SongDownloader.StartNewSongSearch();
                                break;
                            case "clear":
                                SongDownloader.searchString = "";
                                break;
                            default:
                                SongDownloader.searchString += label;
                                break;
                        }

                        if (SongDownloaderUI.searchText != null)
                        {
                            SongDownloaderUI.searchText.text = SongDownloader.searchString;
                        }
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
                        if (SongSearch.query == "" || SongSearch.query == null)
                            return false;
                        SongSearch.query = SongSearch.query.Substring(0, SongSearch.query.Length - 1);

                        if (SongSearchScreen.searchText != null)
                            SongSearchScreen.searchText.text = SongSearch.query;
                    }
                    else if (PlaylistManager.state == PlaylistManager.PlaylistState.Creating)
                    {
                        if (PlaylistCreatePanel.newName == "" || PlaylistCreatePanel.newName is null)
                            return false;
                        PlaylistCreatePanel.newName = PlaylistCreatePanel.newName.Substring(0, PlaylistCreatePanel.newName.Length - 1);

                        if (PlaylistCreatePanel.playlistText != null)
                            PlaylistCreatePanel.playlistText.text = PlaylistCreatePanel.newName;
                    }
                    else
                    {
                        if (SongDownloader.searchString == "" || SongDownloader.searchString == null)
                            return false;
                        SongDownloader.searchString = SongDownloader.searchString.Substring(0, SongDownloader.searchString.Length - 1);

                        if (SongDownloaderUI.searchText != null)
                            SongDownloaderUI.searchText.text = SongDownloader.searchString;
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

                        if (SongSearchScreen.searchText != null)
                            SongSearchScreen.searchText.text = SongSearch.query;
                    }
                    else if (PlaylistManager.state == PlaylistManager.PlaylistState.Creating)
                    {
                        PlaylistCreatePanel.newName += " ";

                        if (PlaylistCreatePanel.playlistText != null)
                            PlaylistCreatePanel.playlistText.text = PlaylistCreatePanel.newName;
                    }
                    else
                    {
                        SongDownloader.searchString += " ";

                        if (SongDownloaderUI.searchText != null)
                            SongDownloaderUI.searchText.text = SongDownloader.searchString;
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
                    SongDownloader.StartNewSongSearch();
                    PlaylistManager.OnApplicationStart();
                    FilterPanel.OnApplicationStart();
                    SongDownloadTracker.StartSongListUpdate();
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  Filter / song search hooks
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Applies the active custom filter to the song list results.
        /// </summary>
        [HarmonyPatch(typeof(SongSelect), "GetSongIDs", new Type[] { typeof(bool) })]
        private static class SongSelectGetSongIDsPatch
        {
            private static void Postfix(SongSelect __instance, ref bool extras, ref Il2CppSystem.Collections.Generic.List<string> __result)
            {
                FilterPanel.ApplyFilter(__instance, ref extras, ref __result);
                if (deletedSongs.Count > 0)
                {
                    foreach (var deletedSong in deletedSongs)
                    {
                        __result.Remove(deletedSong);
                    }
                }
                RandomSong.UpdateAvailableSongs(__result, extras);
                MelonLogger.Log($"[RandomSong] UpdateAvailableSongs called: extras={extras}, count={__result.Count}");
            }
        }

        /// <summary>
        /// Initializes the filter panel and creates buttons when the song select page loads.
        /// </summary>
        [HarmonyPatch(typeof(SongSelect), "OnEnable", new Type[0])]
        private static class SongSelectOnEnablePatch
        {
            private static void Postfix(SongSelect __instance)
            {
                FilterPanel.Initialize();
                PlaylistManager.PopulatePlaylistsSongNames();
                PlaylistManager.DownloadMissingSongs();
                MelonCoroutines.Start(UpdateLastSongCount());
            }
        }

        /// <summary>
        /// Disables custom filters when the user clicks "All" in the song list.
        /// </summary>
        [HarmonyPatch(typeof(SongListControls), "FilterAll", new Type[0])]
        private static class SongListControlsFilterAllPatch
        {
            private static void Prefix(SongListControls __instance)
            {
                FilterPanel.DisableCustomFilters();
            }
        }

        /// <summary>
        /// Disables custom filters when the user clicks "Main" in the song list.
        /// </summary>
        [HarmonyPatch(typeof(SongListControls), "FilterMain", new Type[0])]
        private static class SongListControlsFilterMainPatch
        {
            private static void Prefix(SongListControls __instance)
            {
                FilterPanel.DisableCustomFilters();
            }
        }

        /// <summary>
        /// Forces default sort when playlist filter is active to preserve playlist order.
        /// </summary>
        [HarmonyPatch(typeof(SongSelect), "ChangeSort", new Type[] { typeof(SongSelect.Sort) })]
        private static class SongSelectChangeSortPatch
        {
            private static void Prefix(SongSelect __instance, ref SongSelect.Sort newSort)
            {
                if (FilterPanel.IsFiltering("playlists"))
                {
                    newSort = SongSelect.Sort.Default;
                }
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
                    SongSearchButton.CreateSearchButton();
                    RefreshButton.CreateRefreshButton();
                    SelectPlaylistButton.CreatePlaylistButton();
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
                    AddPlaylistButton.CreatePlaylistButton(ButtonUtils.ButtonLocation.Failed);
                    PlaylistEndlessSkipButton.CreateSkipButton(ButtonUtils.ButtonLocation.Failed);
                }
                else if (state == InGameUI.State.PausePage)
                {
                    CreateDeleteButton(ButtonUtils.ButtonLocation.Pause);
                    CreateFavoriteButton(ButtonUtils.ButtonLocation.Pause);
                    AddPlaylistButton.CreatePlaylistButton(ButtonUtils.ButtonLocation.Pause);
                    PlaylistEndlessSkipButton.CreateSkipButton(ButtonUtils.ButtonLocation.Pause);
                }
                else if (state == InGameUI.State.EndGameContinuePage)
                {
                    CreateDeleteButton(ButtonUtils.ButtonLocation.EndGame);
                    CreateFavoriteButton(ButtonUtils.ButtonLocation.EndGame);
                    AddPlaylistButton.CreatePlaylistButton(ButtonUtils.ButtonLocation.EndGame);
                }
                else if (state == InGameUI.State.PracticeModeOverPage)
                {
                    CreateDeleteButton(ButtonUtils.ButtonLocation.PracticeModeOver);
                    CreateFavoriteButton(ButtonUtils.ButtonLocation.PracticeModeOver);
                    AddPlaylistButton.CreatePlaylistButton(ButtonUtils.ButtonLocation.PracticeModeOver);
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  Song download tracker post-process hooks
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// After the song list is loaded/refreshed, populate playlists and enable the back button.
        /// </summary>
        [HarmonyPatch(typeof(SongList), "SongListLoaded", new Type[0])]
        private static class SongListSongListLoadedPatch
        {
            private static void Postfix()
            {
                PlaylistManager.PopulatePlaylistsSongNames();
                PlaylistManager.EnableBackButton();
            }
        }
    }
}