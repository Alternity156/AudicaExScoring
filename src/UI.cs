using MelonLoader;

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
                float percentage = GetCurrentMaxPossibleExPercentage();

                //Make pretty-ish strings
                string scoreString = "<size=" + inGameCurrentScoreSize + ">" + score + "</size>";
                string percentageString = "<size=" + inGameCurrentPercentSize + "> (" + percentage + "%)</size>";

                scoreKeeperDisplay.scoreDisplay.text = scoreString + percentageString;
            }
        }

        public static string GetPopupText(ExCue exCue)
        {
            string text = "";

            switch (exCue.behavior)
            {
                case Target.TargetBehavior.Standard:
                case Target.TargetBehavior.Horizontal:
                case Target.TargetBehavior.Vertical:
                case Target.TargetBehavior.ChainStart:
                case Target.TargetBehavior.Hold:
                    text = $"T: {GetPercentFromRaw(exCue.timing)}%\nA: {GetPercentFromRaw(exCue.aim)}%";
                    break;
                case Target.TargetBehavior.Chain:
                    text = $"A: {GetPercentFromRaw(exCue.aim)}%";
                    break;
                case Target.TargetBehavior.Melee:
                    text = $"V: {GetPercentFromRaw(exCue.velocity)}%";
                    break;
            }

            if (exCue.behavior == Target.TargetBehavior.Hold)
            {
                text += $"\nS: {GetPercentFromRaw(exCue.sustainPercent)}%";
            }

            return text;
        }
    }
}
