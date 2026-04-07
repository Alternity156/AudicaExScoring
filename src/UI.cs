using MelonLoader;
using System;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        public static string inGameCurrentScoreSize = "120";
        public static string inGameCurrentPercentSize = "75";

        public static void ScoreKeeperDisplayUpdate(ScoreKeeperDisplay scoreKeeperDisplay)
        {
            if (!KataConfig.I.practiceMode)
            {
                float score = exScore;
                float percentage = GetCurrentExPercentage();

                //Make pretty-ish strings
                string scoreString = "<size=" + inGameCurrentScoreSize + ">" + score + "</size>";
                string percentageString = "<size=" + inGameCurrentPercentSize + "> (" + percentage + "%)</size>";

                scoreKeeperDisplay.scoreDisplay.text = scoreString + percentageString;
            }
        }

    }
}
