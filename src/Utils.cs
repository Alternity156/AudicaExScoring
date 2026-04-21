using MelonLoader;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        public static int GetPercentFromRaw(float rawScore)
        {
            return (int)(rawScore * 100);
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
    }
}
