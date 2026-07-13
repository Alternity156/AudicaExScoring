using MelonLoader;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        // Fixed horizontal offset (TMP <pos> tag, in the text's own local units) where every row's
        // count/total starts — gives real column alignment regardless of how long each judgement
        // name is, unlike relying on a monospace font. Matches the aligned-column look of ITG-style
        // results screens. Not tunable live via UnityExplorer since it's baked into the string, not
        // a transform — adjust the numberPos value directly here if it needs nudging for your font.
        private static string FormatJudgementRow(string label, Color color, int count, int total, float numberPos = 25f)
        {
            return $"{Colorize(label, color)}<pos={numberPos}>{count}/{total}\n";
        }

        public static string GetTimingJudgementString(List<ExCue> exCues)
        {
            var relevant = exCues.Where(c =>
                c.behavior != Target.TargetBehavior.Chain &&
                c.behavior != Target.TargetBehavior.Melee).ToList();

            int total = relevant.Count;
            int impeccable = relevant.Count(c => c.timingJudgement == Judgement.Impeccable);
            int fantastic = relevant.Count(c => c.timingJudgement == Judgement.Fantastic);
            int excellent = relevant.Count(c => c.timingJudgement == Judgement.Excellent);
            int great = relevant.Count(c => c.timingJudgement == Judgement.Great);
            int good = relevant.Count(c => c.timingJudgement == Judgement.Good);

            return $"Timing\n" +
                   FormatJudgementRow("Impeccable", GetJudgementColor(Judgement.Impeccable), impeccable, total) +
                   FormatJudgementRow("Fantastic", GetJudgementColor(Judgement.Fantastic), fantastic, total) +
                   FormatJudgementRow("Excellent", GetJudgementColor(Judgement.Excellent), excellent, total) +
                   FormatJudgementRow("Great", GetJudgementColor(Judgement.Great), great, total) +
                   FormatJudgementRow("Good", GetJudgementColor(Judgement.Good), good, total);
        }

        public static string GetAimJudgementString(List<ExCue> exCues)
        {
            var relevant = exCues.Where(c =>
                c.behavior != Target.TargetBehavior.Chain &&
                c.behavior != Target.TargetBehavior.Melee).ToList();

            int total = relevant.Count;
            int impeccable = relevant.Count(c => c.aimJudgement == Judgement.Impeccable);
            int fantastic = relevant.Count(c => c.aimJudgement == Judgement.Fantastic);
            int excellent = relevant.Count(c => c.aimJudgement == Judgement.Excellent);
            int great = relevant.Count(c => c.aimJudgement == Judgement.Great);
            int good = relevant.Count(c => c.aimJudgement == Judgement.Good);

            return $"Aim\n" +
                   FormatJudgementRow("Impeccable", GetJudgementColor(Judgement.Impeccable), impeccable, total) +
                   FormatJudgementRow("Fantastic", GetJudgementColor(Judgement.Fantastic), fantastic, total) +
                   FormatJudgementRow("Excellent", GetJudgementColor(Judgement.Excellent), excellent, total) +
                   FormatJudgementRow("Great", GetJudgementColor(Judgement.Great), great, total) +
                   FormatJudgementRow("Good", GetJudgementColor(Judgement.Good), good, total);
        }

        public static string GetChainJudgementString(List<ExCue> exCues)
        {
            var relevant = exCues.Where(c =>
                c.behavior == Target.TargetBehavior.Chain &&
                c.isChainTail &&
                !c.miss).ToList();

            int total = relevant.Count;
            int impeccable = relevant.Count(c => c.chainJudgement == Judgement.Impeccable);
            int fantastic = relevant.Count(c => c.chainJudgement == Judgement.Fantastic);
            int excellent = relevant.Count(c => c.chainJudgement == Judgement.Excellent);
            int great = relevant.Count(c => c.chainJudgement == Judgement.Great);
            int good = relevant.Count(c => c.chainJudgement == Judgement.Good);

            return $"Chain\n" +
                   FormatJudgementRow("Impeccable", GetJudgementColor(Judgement.Impeccable), impeccable, total) +
                   FormatJudgementRow("Fantastic", GetJudgementColor(Judgement.Fantastic), fantastic, total) +
                   FormatJudgementRow("Excellent", GetJudgementColor(Judgement.Excellent), excellent, total) +
                   FormatJudgementRow("Great", GetJudgementColor(Judgement.Great), great, total) +
                   FormatJudgementRow("Good", GetJudgementColor(Judgement.Good), good, total);
        }

        public static string GetMiscString(List<ExCue> exCues)
        {
            var holds = exCues.Where(c => c.behavior == Target.TargetBehavior.Hold).ToList();
            int totalHolds = holds.Count;
            int fullHolds = holds.Count(c => !c.miss && c.sustainPercent >= 1f);

            var melee = exCues.Where(c => c.behavior == Target.TargetBehavior.Melee).ToList();
            int totalMelee = melee.Count;
            int hitMelee = melee.Count(c => !c.miss);

            var relevant = exCues.Where(c =>
                c.behavior == Target.TargetBehavior.Horizontal ||
                c.behavior == Target.TargetBehavior.Vertical ||
                c.behavior == Target.TargetBehavior.Standard ||
                c.behavior == Target.TargetBehavior.Hold ||
                c.behavior == Target.TargetBehavior.ChainStart ||
                c.behavior == Target.TargetBehavior.Melee).ToList();
            int miss = relevant.Count(c => c.miss == true);
            int total = relevant.Count;

            return FormatJudgementRow("Hold", Color.white, fullHolds, totalHolds) +
                   FormatJudgementRow("Melee", Color.white, hitMelee, totalMelee) +
                   FormatJudgementRow("Miss", GetJudgementColor(Judgement.Miss), miss, total);
        }
    }
}