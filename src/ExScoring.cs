using MelonLoader;
using System.Linq;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        public static float exScoringTimingWeight = 1f;
        public static float exScoringAimWeight = 1f;
        public static float exScoringVelocityWeight = 1f;
        public static float exScoringSustainWeight = 1f;
        public static float exScoringChainWeight = 0.1f;

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

        public static float GetMaxExScoreForCue(SongCues.Cue cue)
        {
            if (cue.behavior == Target.TargetBehavior.Standard ||
                    cue.behavior == Target.TargetBehavior.Horizontal ||
                    cue.behavior == Target.TargetBehavior.Vertical ||
                    cue.behavior == Target.TargetBehavior.ChainStart)
            {
                return exScoringAimWeight + exScoringTimingWeight;
            }
            else if (cue.behavior == Target.TargetBehavior.Chain)
            {
                return exScoringChainWeight;
            }
            else if (cue.behavior == Target.TargetBehavior.Hold)
            {
                return exScoringAimWeight + exScoringTimingWeight + exScoringSustainWeight;
            }
            else if (cue.behavior == Target.TargetBehavior.Melee)
            {
                return exScoringVelocityWeight;
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
                return exCue.aim * exScoringChainWeight;
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
