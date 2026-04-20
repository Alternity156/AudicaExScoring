using MelonLoader;
using System;
using System.Linq;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        public static int GetPercentFromRaw(float rawScore)
        {
            return (int)(rawScore * 100);
        }

        public static float GetCurrentExPercentage()
        {
            return (exScore / maxPossibleExScore) * 100;
        }

        public static float GetCurrentMaxPossibleExPercentage()
        {
            return (exScore / currentMaxPossibleExScore) * 100;
        }

        public static void PrintExScore(float exScore)
        {
            MelonLogger.Log("EX Score: " + exScore.ToString());
            MelonLogger.Log("EX Percentage: " + GetCurrentExPercentage().ToString() + "%");
        }

        public static void ResetExScore()
        {
            processedCuesIndexes.Clear();
            exCues.Clear();
            exScore = 0;
            currentMaxPossibleExScore = 0;
        }

        public static bool ValidateExScore()
        {
            float aimAssist = exCues[0].aimAssist;

            for (int i = 0; i < exCues.Count; i++)
            {
                if (exCues[i].aimAssist != aimAssist)
                {
                    return false;
                }
            }
            return true;
        }

        public static float GetTimingMsFromCue(SongCues.Cue cue)
        {
            float startTick;
            float endTick;
            float tickSpan;

            if (cue.tick < cue.successTick)
            {
                startTick = cue.tick;
                endTick = cue.successTick;
                tickSpan = AudioDriver.TickSpanToMs(selectedSongData, startTick, endTick);
            }
            else if (cue.tick > cue.successTick)
            {
                startTick = cue.successTick;
                endTick = cue.tick;
                tickSpan = -AudioDriver.TickSpanToMs(selectedSongData, startTick, endTick);
            }
            else
            {
                tickSpan = 0;
            }

            return tickSpan;
        }

        public static float GetLinearTimingScore(float timingMs)
        {
            float timingMsAbs = Math.Abs(timingMs);

            if (timingMsAbs < perfectTimingSlopMs)
            {
                return 1;
            }
            else
            {
                float timing = KataConfig.kSlopWindowEarlyMs - timingMsAbs;

                return (timing - perfectTimingSlopMs) / (KataConfig.kSlopWindowEarlyMs - perfectTimingSlopMs);
            }
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
                return 1 / chainAimExDivision;
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

    }
}
