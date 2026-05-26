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
            /* This was a test to see what would happend if you prevent the game from doing this when it's
             * loading songs. It breaks the in-song star count.
             */ 
            public static void Prefix(
                ScoreData __instance, 
                SongCues.Cue cue, 
                ref float timing, 
                ref float aim, 
                ref float extra
                )
            {
                /*
                if (!gameHasLoaded)
                {
                    return false;
                }
                return true;
                */

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
                        //nextPopupText = GetPopupText(exCue);

                        if(cue.behavior == Target.TargetBehavior.Melee && cue.meleeVelocityAmount > 0)
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
                if(Config.ExType)
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
    }
}
