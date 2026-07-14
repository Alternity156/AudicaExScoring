using MelonLoader;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        public static void ScoreKeeperDisplayUpdate(ScoreKeeperDisplay scoreKeeperDisplay)
        {
            if (!KataConfig.I.practiceMode)
            {
                float percentage = currentMaxPossibleJudgementScore > 0f
                    ? GetCurrentMaxPossibleJudgementPercentage()
                    : 0f;

                scoreKeeperDisplay.scoreDisplay.text = percentage.ToString("F2") + "%";

                if (scoreKeeperDisplay.highScoreDisplay != null)
                    scoreKeeperDisplay.highScoreDisplay.text = GetExTopScoreDisplayText();
            }
        }

        // Reuses SongListHighScoreUI's exRowScoreCache/exRowScoreEmpty/exRowScoreLoading (keyed
        // songId|difficulty) and LoadRowScoreCoroutine as-is — it already no-ops safely when there's
        // no bound song-list row, which is always the case here during gameplay.
        private static string GetExTopScoreDisplayText()
        {
            if (selectedSongData == null) return "";

            string songId = selectedSongData.songID;
            KataConfig.Difficulty difficulty = KataConfig.I.GetDifficulty();
            string key = ExRowCacheKey(songId, difficulty);

            if (exRowScoreCache.TryGetValue(key, out CachedExRowScore cached))
                return cached.judgementPercent.ToString("F2") + "%";

            if (exRowScoreEmpty.Contains(key))
                return ""; // confirmed: no saved runs for this song+difficulty

            if (!exRowScoreLoading.Contains(key))
            {
                exRowScoreLoading.Add(key);
                MelonCoroutines.Start(LoadRowScoreCoroutine(songId, difficulty, key));
            }

            return ""; // load in flight — resolves within a frame or two, picked up next Update
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