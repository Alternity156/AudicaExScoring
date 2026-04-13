using System;
using Harmony;
using MelonLoader;

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

        [HarmonyPatch(typeof(ScoreKeeper), "OnFailure", new Type[] { typeof(SongCues.Cue), typeof(bool), typeof(bool) })]
        public static class TargetOnPassPatch
        {
            public static void Postfix(SongCues.Cue cue, bool pass, bool failedDodge)
            {
                if (cue == null) return;

                if (!processedCuesIndexes.Contains(cue.index))
                {
                    ExCue exCue = new ExCue();

                    exCue.index = cue.index;
                    exCue.handType = cue.handType;
                    exCue.tick = cue.tick;
                    exCue.aimAssist = PlayerPreferences.I.AimAssistAmount.mVal;
                    exCue.miss = true;

                    MelonLogger.Log("OnFailure: Missed");
                }
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

        [HarmonyPatch(typeof(ScoreKeeperDisplay), "Update")]
        private static class PatchScoreKeeperUpdate
        {
            private static void Postfix(ScoreKeeperDisplay __instance)
            {
                ScoreKeeperDisplayUpdate(__instance);
            }
        }

    }
}
