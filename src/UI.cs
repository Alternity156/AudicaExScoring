using MelonLoader;
using MelonLoader.Tomlyn;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

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
                //float score = exScore;
                //float percentage = GetCurrentMaxPossibleExPercentage();

                float score = judgementScore;
                float percentage = GetCurrentMaxPossibleJudgementPercentage();

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

        private static string Colorize(string text, Color color)
        {
            string hex = ColorUtility.ToHtmlStringRGB(color);
            return $"<color=#{hex}>{text}</color>";
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
            int hitHolds = holds.Count(c => !c.miss);
            int fullHolds = holds.Count(c => !c.miss && c.sustainPercent >= 1f);

            var melee = exCues.Where(c => c.behavior == Target.TargetBehavior.Melee).ToList();
            int totalMelee = melee.Count;
            int hitMelee = melee.Count(c => !c.miss);

            var relevant = exCues.ToList();
            int miss = relevant.Count(c => c.timingJudgement == Judgement.Miss);
            int total = relevant.Count;

            return $"Hold:  {fullHolds}/{totalHolds}\n" +
                   $"Melee: {hitMelee}/{totalMelee}\n" +
                   $"{Colorize("Miss", GetJudgementColor(Judgement.Miss))}: {miss}/{total}";
        }

        public static GameObject CreateTextObject(string name, string text, Transform parent, int layer, TMP_Text original, RectTransform originalRt, Vector3 localPosition, Vector3 localScale, TextAlignmentOptions options = TextAlignmentOptions.Left)
        {
            GameObject obj = new GameObject(name);
            obj.transform.SetParent(parent, false);
            obj.layer = layer;

            TextMeshPro tmp = obj.AddComponent<TextMeshPro>();
            tmp.text = text;
            tmp.fontSize = original.fontSize;
            tmp.richText = true;
            tmp.font = original.font;
            tmp.fontStyle = original.fontStyle;
            tmp.color = original.color;

            RectTransform rt = obj.GetComponent<RectTransform>();
            rt.sizeDelta = originalRt.sizeDelta;
            rt.anchoredPosition = originalRt.anchoredPosition;
            rt.localScale = localScale;
            rt.localPosition = localPosition;

            return obj;
        }
    }
}
