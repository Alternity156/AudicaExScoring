using MelonLoader;

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
            DebugLog.Log("EX Score: " + exScore.ToString());
            DebugLog.Log("EX Percentage: " + GetCurrentExPercentage().ToString() + "%");
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

        /// <summary>
        /// Max possible EX score for the whole chart of songId at the given difficulty (was always
        /// Expert regardless of what was selected). Main thread only (Il2Cpp). Returns 0 (never
        /// throws) if the song isn't installed, has no chart at that difficulty, or anything else
        /// goes wrong. No HookUpChains needed: GetMaxExScoreForCue never reads chainNext.
        /// </summary>
        public static float GetMaxPossibleExScore(string songId, KataConfig.Difficulty difficulty)
        {
            float maxExScore = 0f;

            try
            {
                if (string.IsNullOrEmpty(songId) || SongList.I == null) return 0f;

                var songData = SongList.I.GetSong(songId);
                if (songData == null) return 0f;

                var cues = SongCues.GetCues(songData, difficulty);
                if (cues == null) return 0f;

                foreach (SongCues.Cue cue in cues)
                {
                    maxExScore += GetMaxExScoreForCue(cue);
                }
            }
            catch (System.Exception ex)
            {
                DebugLog.Log($"[ExScoring] Failed to compute max EX score for {songId}/{difficulty}: {ex}");
                return 0f;
            }

            return maxExScore;
        }

    }
}