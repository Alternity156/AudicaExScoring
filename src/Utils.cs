using MelonLoader;
using System.Collections.Generic;
using UnityEngine;
using static ExScoringMod.ExScoring;
using static SongCues;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        public static void ResetExScore()
        {
            processedCuesIndexes.Clear();
            exCues.Clear();
            exScore = 0;
            judgementScore = 0;
            currentMaxPossibleExScore = 0;
            currentMaxPossibleJudgementScore = 0;
        }

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

        public static TargetHitPos GetTargetHitPos(Target target, Vector3 intersectionPoint)
        {
            Vector3 targetPos = target.GetContactPosition();
            Vector3 localPoint = intersectionPoint - targetPos;

            return new TargetHitPos
            {
                x = Vector3.Dot(localPoint, target.transform.right),
                y = Vector3.Dot(localPoint, target.transform.up)
            };
        }

        public static float GetChainAverageFromLastNodeCue(SongCues.Cue cue)
        {
            List<Cue> fullChain = GetChainFromLastNode(cue);

            float total = 0;

            foreach (Cue chainCue in fullChain)
            {
                if (cue.behavior == Target.TargetBehavior.Chain)
                {
                    total += chainCue.aim;
                }
            }

            float chainAverage = total / fullChain.Count;

            return chainAverage;
        }
    }
}
