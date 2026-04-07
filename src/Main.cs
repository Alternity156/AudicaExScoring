using System;
using System.Collections.Generic;
using System.Linq;
using System.Management.Instrumentation;
using System.Security.Permissions;
using Harmony;
using MelonLoader;
using UnhollowerBaseLib;
using UnityEngine;

namespace AudicaModding
{
    public class AudicaMod : MelonMod
    {
        public static MenuState.State menuState;
        public static bool gameHasLoaded = false;
        public static string selectedSong;

        public static float chainAimExDivision = 10;

        public static List<int> processedCuesIndexes = new List<int>();
        public static List<ExCue> exCues = new List<ExCue>();

        public static float maxPossibleExScore;
        public static float exScore = 0;

        public static class BuildInfo
        {
            public const string Name = "ExScoring";  // Name of the Mod.  (MUST BE SET)
            public const string Author = "Alternity"; // Author of the Mod.  (Set as null if none)
            public const string Company = null; // Company that made the Mod.  (Set as null if none)
            public const string Version = "0.1.0"; // Version of the Mod.  (MUST BE SET)
            public const string DownloadLink = null; // Download Link for the Mod.  (Set as null if none)
        }

        public static void PrintExScore(float exScore)
        {
            MelonLogger.Log("EX Score: " + exScore.ToString());
            MelonLogger.Log("EX Percentage: " + ((exScore / maxPossibleExScore) * 100).ToString() + "%");
        }

        public static void ResetExScore()
        {
            processedCuesIndexes.Clear();
            exCues.Clear();
            exScore = 0;
        }

        public static float GetMaxExScoreForCue(SongCues.Cue cue)
        {
            if (cue.behavior == Target.TargetBehavior.Standard ||
                    cue.behavior == Target.TargetBehavior.Horizontal ||
                    cue.behavior == Target.TargetBehavior.Vertical ||
                    cue.behavior == Target.TargetBehavior.ChainStart)
            {
                return 2;
            }
            else if (cue.behavior == Target.TargetBehavior.Chain)
            {
                return 0.1f;
            }
            else if (cue.behavior == Target.TargetBehavior.Hold)
            {
                return 3;
            }
            else if (cue.behavior == Target.TargetBehavior.Melee)
            {
                return 1;
            }
            else
            {
                return 0;
            }
        }

        public static float GetExScoreForExCue(ExCue exCue)
        {
            if (exCue.behavior == Target.TargetBehavior.Standard ||
                    exCue.behavior == Target.TargetBehavior.Horizontal ||
                    exCue.behavior == Target.TargetBehavior.Vertical ||
                    exCue.behavior == Target.TargetBehavior.ChainStart)
            {
                return exCue.aim + exCue.timing;
            }
            else if (exCue.behavior == Target.TargetBehavior.Chain)
            {
                return exCue.aim / chainAimExDivision;
            }
            else if (exCue.behavior == Target.TargetBehavior.Hold)
            {
                return exCue.aim + exCue.timing + exCue.sustainPercent;
            }
            else if (exCue.behavior == Target.TargetBehavior.Melee)
            {
                return exCue.velocity;
            }
            else return 0;
        }

        public static float GetMaxPossibleExScore(string songID)
        {
            float maxExScore = 0;

            SongCues.Cue[] cues = SongCues.GetCues(SongList.I.GetSong(songID), KataConfig.Difficulty.Expert).ToArray();

            foreach (SongCues.Cue cue in cues)
            {
                maxExScore += GetMaxExScoreForCue(cue);
            }

            return maxExScore;
        }

        public class ExCue
        {
            public Target.TargetBehavior behavior;
            public Target.TargetHandType handType;
            public int index;
            public float tick;
            public float timing;
            public float aim;
            public float velocity;
            public float sustainPercent;
            public float aimAssist;
        }

        [HarmonyPatch(typeof(InGameUI), "Restart")]
        public static class InGameUIRestartPatch
        {
            public static void Postfix(InGameUI __instance)
            {
                ResetExScore();
            }
        }

        [HarmonyPatch(typeof(MenuState), "SetState", new Type[] {typeof(MenuState.State) })]
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
            }
        }

        [HarmonyPatch(typeof(SongSelectItem), "OnSelect")]
        private static class PatchSongOnSelect
        {
            private static void Postfix(SongSelectItem __instance)
            {
                selectedSong = __instance.mSongData.songID;
                maxPossibleExScore = GetMaxPossibleExScore(selectedSong);
            }
        }


        [HarmonyPatch(typeof(ScoreData), "GetScoreForCue", new Type[] { typeof(SongCues.Cue), typeof(float), typeof(float), typeof(float) })]
        public static class GetScoreForCuePatch
        {
            /* This was a test to see what would happend if you prevent the game from doing this when it's
             * loading songs. It breaks the in-song star count.
             * 
            public static bool Prefix(ScoreData __instance, SongCues.Cue cue, float timing, float aim, float extra)
            {
                if (!gameHasLoaded)
                {
                    return false;
                }
                return true;
            }
            */

            public static void Postfix(ScoreData __instance, SongCues.Cue cue, float timing, float aim, float extra, ref int __result)
            {
                if(menuState == MenuState.State.Launched)
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

                    if(!processedCuesIndexes.Contains(cue.index))
                    {
                        ExCue exCue = new ExCue();

                        exCue.behavior = cue.behavior;
                        exCue.handType = cue.handType;
                        exCue.tick = cue.tick;
                        exCue.timing = timing;
                        exCue.aim = aim;
                        exCue.velocity = cue.meleeVelocityAmount;
                        exCue.sustainPercent = cue.sustainPercent;
                        exCue.aimAssist = PlayerPreferences.I.AimAssistAmount.mVal;
                        exCue.index = cue.index;

                        processedCuesIndexes.Add(cue.index);
                        exCues.Add(exCue);

                        exScore += GetExScoreForExCue(exCue);

                        PrintExScore(exScore);
                    }
                }
            }
        }
    }
}



