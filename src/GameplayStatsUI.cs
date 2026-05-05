using MelonLoader;
using System.Collections.Generic;
using System.Linq;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
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
                   $"{Colorize("Impeccable", GetJudgementColor(Judgement.Impeccable))}: {impeccable}/{total}\n" +
                   $"{Colorize("Fantastic", GetJudgementColor(Judgement.Fantastic))}: {fantastic}/{total}\n" +
                   $"{Colorize("Excellent", GetJudgementColor(Judgement.Excellent))}: {excellent}/{total}\n" +
                   $"{Colorize("Great", GetJudgementColor(Judgement.Great))}: {great}/{total}\n" +
                   $"{Colorize("Good", GetJudgementColor(Judgement.Good))}: {good}/{total}\n";
        }

        public static string GetAimJudgementString(List<ExCue> exCues)
        {
            var relevant = exCues.Where(c =>
                c.behavior != Target.TargetBehavior.Chain &&
                c.behavior != Target.TargetBehavior.Melee &&
                !c.miss).ToList();

            int total = relevant.Count;
            int impeccable = relevant.Count(c => c.aimJudgement == Judgement.Impeccable);
            int fantastic = relevant.Count(c => c.aimJudgement == Judgement.Fantastic);
            int excellent = relevant.Count(c => c.aimJudgement == Judgement.Excellent);
            int great = relevant.Count(c => c.aimJudgement == Judgement.Great);
            int good = relevant.Count(c => c.aimJudgement == Judgement.Good);

            return $"Aim\n" +
                   $"{Colorize("Impeccable", GetJudgementColor(Judgement.Impeccable))}: {impeccable}/{total}\n" +
                   $"{Colorize("Fantastic", GetJudgementColor(Judgement.Fantastic))}: {fantastic}/{total}\n" +
                   $"{Colorize("Excellent", GetJudgementColor(Judgement.Excellent))}: {excellent}/{total}\n" +
                   $"{Colorize("Great", GetJudgementColor(Judgement.Great))}: {great}/{total}\n" +
                   $"{Colorize("Good", GetJudgementColor(Judgement.Good))}: {good}/{total}\n";
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
                   $"{Colorize("Impeccable", GetJudgementColor(Judgement.Impeccable))}: {impeccable}/{total}\n" +
                   $"{Colorize("Fantastic", GetJudgementColor(Judgement.Fantastic))}: {fantastic}/{total}\n" +
                   $"{Colorize("Excellent", GetJudgementColor(Judgement.Excellent))}: {excellent}/{total}\n" +
                   $"{Colorize("Great", GetJudgementColor(Judgement.Great))}: {great}/{total}\n" +
                   $"{Colorize("Good", GetJudgementColor(Judgement.Good))}: {good}/{total}\n";
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

            return $"Hold:  {fullHolds}/{totalHolds}\n" +
                   $"Melee: {hitMelee}/{totalMelee}\n" +
                   $"{Colorize("Miss", GetJudgementColor(Judgement.Miss))}: {miss}/{total}";
        }
    }
}
