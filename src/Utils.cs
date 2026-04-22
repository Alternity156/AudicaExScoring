using MelonLoader;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using static SongCues;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        public static int GetPercentFromRaw(float rawScore)
        {
            return (int)(rawScore * 100);
        }

        public static float GetTimingMsFromCue(Cue cue)
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

        public static List<Cue> GetChainFromLastNode(Cue lastNode)
        {
            List<Cue> chain = new List<Cue>();
            Cue current = lastNode;

            while (current != null)
            {
                chain.Add(current);
                current = current.chainPrevious;
            }

            chain.Reverse();
            return chain;
        }
    }
}
