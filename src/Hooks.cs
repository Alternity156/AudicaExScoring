using System;
using System.Management.Instrumentation;
using Harmony;
using MelonLoader;
using UnityEngine;
using static MelonLoader.MelonPrefs;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        [HarmonyPatch(typeof(InGameUI), "Restart")]
        public static class InGameUIRestartPatch
        {
            public static void Postfix(InGameUI __instance)
            {
                ResetExScore();
            }
        }

        [HarmonyPatch(typeof(MenuState), "SetState", new Type[] { typeof(MenuState.State) })]
        public static class SetStatePatch
        {

            public static void Postfix(MenuState __instance, MenuState.State state)
            {
                if (menuState == MenuState.State.Launched && state != MenuState.State.Launched)
                {
                    ResetExScore();
                }

                menuState = state;
                if (state == MenuState.State.TitleScreen)
                {
                    gameHasLoaded = true;
                }

                if (state == MenuState.State.MainPage)
                {
                    StartWatching();
                }
                else
                {
                    StopWatching();
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
            }
        }

        [HarmonyPatch(typeof(ScoreKeeper), "OnFailure", new Type[] { typeof(SongCues.Cue), typeof(bool), typeof(bool) })]
        public static class ScoreKeeperOnFailurePatch
        {
            public static void Postfix(SongCues.Cue cue, bool pass, bool failedDodge)
            {
                if (cue == null) return;

                if (!processedCuesIndexes.Contains(cue.index))
                {
                    currentMaxPossibleExScore += GetMaxExScoreForCue(cue);

                    ExCue exCue = new ExCue();

                    exCue.index = cue.index;
                    exCue.handType = cue.handType;
                    exCue.tick = cue.tick;
                    exCue.aimAssist = PlayerPreferences.I.AimAssistAmount.mVal;
                    exCue.miss = true;
                }
            }
        }

        [HarmonyPatch(typeof(Gun), "CalculateAim", new Type[] { typeof(Target), typeof(Vector3) })]
        public static class CalculateAimPatch
        {
            public static void Postfix(ref Gun __instance, Target target, Vector3 intersectionPoint, ref float __result)
            {

            }
        }

        [HarmonyPatch(typeof(ScoreData), "GetScoreForCue", new Type[] { typeof(SongCues.Cue), typeof(float), typeof(float), typeof(float) })]
        public static class GetScoreForCuePatch
        {
            /* This was a test to see what would happend if you prevent the game from doing this when it's
             * loading songs. It breaks the in-song star count.
             */ 
            public static void Prefix(ScoreData __instance, SongCues.Cue cue, ref float timing, ref float aim, ref float extra)
            {
                /*
                if (!gameHasLoaded)
                {
                    return false;
                }
                return true;
                */
            }

            public static void Postfix(ScoreData __instance, SongCues.Cue cue, float timing, float aim, float extra, ref int __result)
            {
                if (menuState == MenuState.State.Launched)
                {
                    /*
                    MelonLogger.Log("Cue index: " + cue.index.ToString());
                    MelonLogger.Log("Cue tick: " + cue.tick.ToString());
                    MelonLogger.Log("Cue successTick: " + cue.successTick.ToString());
                    MelonLogger.Log("Cue slopBeforeTicks: " + cue.slopBeforeTicks.ToString());
                    MelonLogger.Log("Cue slopAfterTicks: " + cue.slopAfterTicks.ToString());
                    MelonLogger.Log("Cue aim: " + cue.aim.ToString());
                    MelonLogger.Log("Cue velocity: " + cue.velocity.ToString());
                    MelonLogger.Log("Cue meleeVelocityAmount: " + cue.meleeVelocityAmount.ToString());
                    MelonLogger.Log("Cue sustainPercent: " + cue.sustainPercent.ToString());
                    MelonLogger.Log("Cue behavior: " + cue.behavior.ToString());
                    MelonLogger.Log("timing input: " + timing.ToString());
                    MelonLogger.Log("aim input: " + aim.ToString());
                    MelonLogger.Log("extra input: " + extra.ToString());
                    MelonLogger.Log("score: " + __result.ToString());
                    */

                    if (!processedCuesIndexes.Contains(cue.index))
                    {
                        currentMaxPossibleExScore += GetMaxExScoreForCue(cue);

                        TargetHitPos targetHitPos = new TargetHitPos();

                        for (int i = 0; i < unprocessedTargetHitPoses.Count; i++)
                        {
                            if (unprocessedTargetHitPoses[i].index == cue.index)
                            {
                                targetHitPos.y = unprocessedTargetHitPoses[i].targetHitPos.y;
                                targetHitPos.x = unprocessedTargetHitPoses[i].targetHitPos.x;

                                unprocessedTargetHitPoses.RemoveAt(i);
                            }
                        }

                        ExCue exCue = new ExCue();

                        exCue.behavior = cue.behavior;
                        exCue.handType = cue.handType;
                        exCue.tick = cue.tick;
                        exCue.successTick = cue.successTick;
                        exCue.timing = timing;
                        exCue.timingMs = GetTimingMsFromCue(cue);
                        exCue.aim = aim;
                        exCue.targetHitPos = targetHitPos;
                        exCue.velocity = cue.meleeVelocityAmount;
                        exCue.sustainPercent = cue.sustainPercent;
                        exCue.aimAssist = PlayerPreferences.I.AimAssistAmount.mVal;
                        exCue.index = cue.index;

                        processedCuesIndexes.Add(cue.index);
                        exCues.Add(exCue);
                        float exCueScore = GetExScoreForExCue(exCue);

                        exScore += exCueScore;
                        nextPopupText = GetPopupText(exCue);

                        //PrintExScore(exScore);
                    }
                }
            }
        }

        [HarmonyPatch(typeof(ScoreKeeperDisplay), "Update")]
        private static class PatchScoreKeeperUpdate
        {
            private static void Postfix(ScoreKeeperDisplay __instance)
            {
                ScoreKeeperDisplayUpdate(__instance);
            }
        }

        [HarmonyPatch(typeof(Target), "CompleteTarget")]
        private static class Target_CompleteTarget
        {
            private static bool Prefix(Target __instance)
            {
                nextPopupIsScore = true;
                return true;
            }
        }

        [HarmonyPatch(typeof(Target), "OnHit", new Type[] { typeof(Gun), typeof(Gun.AttackType), typeof(float), typeof(Vector2), typeof(Vector3), typeof(float), typeof(bool) })]
        private static class TargetOnHitPatch
        {
            private static void Prefix(ref Target __instance, Gun gun, Gun.AttackType attackType, float aim, Vector2 targetHitPos, Vector3 intersectionPoint)
            {
                UnprocessedTargetHitPos unprocessedTargetHitPos = new UnprocessedTargetHitPos();

                unprocessedTargetHitPos.index = __instance.mCue.index;
                unprocessedTargetHitPos.targetHitPos = targetHitPos;

                unprocessedTargetHitPoses.Add(unprocessedTargetHitPos);
            }
        }

        [HarmonyPatch(typeof(TextPopupPool), "CreatePopup", new Type[] { typeof(Vector3), typeof(Quaternion), typeof(Vector3), typeof(string), typeof(string) })]
        private static class TextPopupPool_CreatePopup
        {
            private static bool Prefix(TextPopupPool __instance, Vector3 position, Quaternion rotation, Vector3 scale, ref string text, ref string extraText)
            {
                if (nextPopupIsScore)
                {
                    nextPopupIsScore = false;

                    var index = __instance.mIndex;
                    var popup = __instance.mPopups[index];

                    text = nextPopupText.ToString();
                    extraText = "";

                    nextPopupText = "";
                }

                return true;
            }
        }

    }
}
