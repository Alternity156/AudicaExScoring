using MelonLoader;
using System.Linq;
using UnityEngine;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        public class CueStatsData
        {
            // General counts (excluding Dodge and Chain)
            public int totalTargets;
            public int leftHandTargets;
            public int rightHandTargets;
            public int eitherHandTargets;

            // Standard
            public int standardTargets;
            public int standardLeftHand;
            public int standardRightHand;
            public int standardEitherHand;

            // Vertical
            public int verticalTargets;
            public int verticalLeftHand;
            public int verticalRightHand;
            public int verticalEitherHand;

            // Horizontal
            public int horizontalTargets;
            public int horizontalLeftHand;
            public int horizontalRightHand;
            public int horizontalEitherHand;

            // Hold
            public int holdTargets;
            public int holdLeftHand;
            public int holdRightHand;
            public int holdEitherHand;

            // ChainStart
            public int chainStartTargets;
            public int chainStartLeftHand;
            public int chainStartRightHand;
            public int chainStartEitherHand;

            // Melee
            public int meleeTargets;
            public int meleeLeftHand;
            public int meleeRightHand;
            public int meleeEitherHand;

            // Dodge
            public int dodgeTargets;

            // Special filters
            public int meleeNotEitherHand;
            public int nonMeleeDodgeChainEitherHand;
        }

        public static CueStatsData GetCueStats(SongList.SongData songData, KataConfig.Difficulty difficulty)
        {
            CueStatsData stats = new CueStatsData();

            SongCues.Cue[] cues = SongCues.GetCues(songData, difficulty).ToArray();

            foreach (SongCues.Cue cue in cues)
            {
                Target.TargetBehavior behavior = cue.behavior;
                Target.TargetHandType hand = cue.handType;

                // Per-behavior counts with hand breakdowns
                switch (behavior)
                {
                    case Target.TargetBehavior.Standard:
                        stats.standardTargets++;
                        if (hand == Target.TargetHandType.Left) stats.standardLeftHand++;
                        else if (hand == Target.TargetHandType.Right) stats.standardRightHand++;
                        else if (hand == Target.TargetHandType.Either) stats.standardEitherHand++;
                        break;
                    case Target.TargetBehavior.Vertical:
                        stats.verticalTargets++;
                        if (hand == Target.TargetHandType.Left) stats.verticalLeftHand++;
                        else if (hand == Target.TargetHandType.Right) stats.verticalRightHand++;
                        else if (hand == Target.TargetHandType.Either) stats.verticalEitherHand++;
                        break;
                    case Target.TargetBehavior.Horizontal:
                        stats.horizontalTargets++;
                        if (hand == Target.TargetHandType.Left) stats.horizontalLeftHand++;
                        else if (hand == Target.TargetHandType.Right) stats.horizontalRightHand++;
                        else if (hand == Target.TargetHandType.Either) stats.horizontalEitherHand++;
                        break;
                    case Target.TargetBehavior.Hold:
                        stats.holdTargets++;
                        if (hand == Target.TargetHandType.Left) stats.holdLeftHand++;
                        else if (hand == Target.TargetHandType.Right) stats.holdRightHand++;
                        else if (hand == Target.TargetHandType.Either) stats.holdEitherHand++;
                        break;
                    case Target.TargetBehavior.ChainStart:
                        stats.chainStartTargets++;
                        if (hand == Target.TargetHandType.Left) stats.chainStartLeftHand++;
                        else if (hand == Target.TargetHandType.Right) stats.chainStartRightHand++;
                        else if (hand == Target.TargetHandType.Either) stats.chainStartEitherHand++;
                        break;
                    case Target.TargetBehavior.Melee:
                        stats.meleeTargets++;
                        if (hand == Target.TargetHandType.Left) stats.meleeLeftHand++;
                        else if (hand == Target.TargetHandType.Right) stats.meleeRightHand++;
                        else if (hand == Target.TargetHandType.Either) stats.meleeEitherHand++;
                        break;
                    case Target.TargetBehavior.Dodge:
                        stats.dodgeTargets++;
                        break;
                }

                // General counts (excluding Dodge and Chain)
                if (behavior != Target.TargetBehavior.Dodge && behavior != Target.TargetBehavior.Chain)
                {
                    stats.totalTargets++;

                    if (hand == Target.TargetHandType.Left)
                        stats.leftHandTargets++;
                    else if (hand == Target.TargetHandType.Right)
                        stats.rightHandTargets++;
                    else if (hand == Target.TargetHandType.Either)
                        stats.eitherHandTargets++;
                }

                // Melee targets that are not either hand
                if (behavior == Target.TargetBehavior.Melee && hand != Target.TargetHandType.Either)
                {
                    stats.meleeNotEitherHand++;
                }

                // Targets (excluding Melee, Dodge, Chain) that are either hand
                if (behavior != Target.TargetBehavior.Melee &&
                    behavior != Target.TargetBehavior.Dodge &&
                    behavior != Target.TargetBehavior.Chain &&
                    hand == Target.TargetHandType.Either)
                {
                    stats.nonMeleeDodgeChainEitherHand++;
                }
            }

            return stats;
        }

        public static string GetCueStatsString(CueStatsData stats)
        {
            return $"{GetTargetIconTag("standard")} {stats.standardTargets}\n" +
                   $"{GetTargetIconTag("horizontal")} {stats.horizontalTargets}\n" +
                   $"{GetTargetIconTag("vertical")} {stats.verticalTargets}\n" +
                   $"{GetTargetIconTag("hold")} {stats.holdTargets}\n" +
                   $"{GetTargetIconTag("chainstart")} {stats.chainStartTargets}\n" +
                   $"{GetTargetIconTag("melee")} {stats.meleeTargets}\n" +
                   $"{GetTargetIconTag("mine")} {stats.dodgeTargets}";
        }

        public static string GetLeftHandCueStatsString(CueStatsData stats)
        {
            Color color = KataConfig.I.leftHandColor;

            return $"{GetColoredTargetIconTag("standard", color)} {stats.standardLeftHand}\n" +
                   $"{GetColoredTargetIconTag("horizontal", color)} {stats.horizontalLeftHand}\n" +
                   $"{GetColoredTargetIconTag("vertical", color)} {stats.verticalLeftHand}\n" +
                   $"{GetColoredTargetIconTag("hold", color)} {stats.holdLeftHand}\n" +
                   $"{GetColoredTargetIconTag("chainstart", color)} {stats.chainStartLeftHand}\n" +
                   $"{GetColoredTargetIconTag("melee", color)} {stats.meleeLeftHand}";
        }

        public static string GetRightHandCueStatsString(CueStatsData stats)
        {
            Color color = KataConfig.I.rightHandColor;

            return $"{GetColoredTargetIconTag("standard", color)} {stats.standardRightHand}\n" +
                   $"{GetColoredTargetIconTag("horizontal", color)} {stats.horizontalRightHand}\n" +
                   $"{GetColoredTargetIconTag("vertical", color)} {stats.verticalRightHand}\n" +
                   $"{GetColoredTargetIconTag("hold", color)} {stats.holdRightHand}\n" +
                   $"{GetColoredTargetIconTag("chainstart", color)} {stats.chainStartRightHand}\n" +
                   $"{GetColoredTargetIconTag("melee", color)} {stats.meleeRightHand}";
        }

        public static string GetEitherHandCueStatsString(CueStatsData stats)
        {
            Color color = KataConfig.I.eitherHandColor;

            return $"{GetColoredTargetIconTag("standard", color)} {stats.standardEitherHand}\n" +
                   $"{GetColoredTargetIconTag("horizontal", color)} {stats.horizontalEitherHand}\n" +
                   $"{GetColoredTargetIconTag("vertical", color)} {stats.verticalEitherHand}\n" +
                   $"{GetColoredTargetIconTag("hold", color)} {stats.holdEitherHand}\n" +
                   $"{GetColoredTargetIconTag("chainstart", color)} {stats.chainStartEitherHand}\n" +
                   $"{GetColoredTargetIconTag("melee", color)} {stats.meleeEitherHand}";
        }
    }
}