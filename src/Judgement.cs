using MelonLoader;
using System;
using System.Linq;
using UnityEngine;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        public static float judgementImpeccableWeight = 2.25f;
        public static float judgementFantasticWeight = 2f;
        public static float judgementExcellentWeight = 1.5f;
        public static float judgementGreatWeight = 1f;
        public static float judgementGoodWeight = 0.75f;

        public static float judgementOkWeight = 1f;

        public static float judgementImpeccableTimingWindowMs = 8.5f;
        public static float judgementFantasticTimingWindowMs = 17f;
        public static float judgementExcellentTimingWindowMs = 25f;
        public static float judgementGreatTimingWindowMs = 50f;
        public static float judgementGoodTimingWindowMs = 100f;

        public static float judgementImpeccableChainAverage = 0.9f;
        public static float judgementFantasticChainAverage = 0.75f;
        public static float judgementExcellentChainAverage = 0.6f;
        public static float judgementGreatChainAverage = 0.4f;
        public static float judgementGoodChainAverage = 0.0f;

        /* Impeccable radius is real target radius
         * The rest is the remaining radius %, whatever remains from Impeccable radius
        */
        public static float judgementImpeccableAimRadius = 0.5f;
        public static float judgementFantasticAimRadiusPercent = 0.1f;
        public static float judgementExcellentAimRadiusPercent = 0.3f;
        public static float judgementGreatAimRadiusPercent = 0.5f;
        public static float judgementGoodAimRadiusPercent = 1f;

        public enum Judgement
        {
            Impeccable,
            Fantastic,
            Excellent,
            Great,
            Good,
            Miss,
            OK
        }

        public static Color GetJudgementColor(Judgement judgement)
        {
            switch (judgement)
            {
                case Judgement.Impeccable:
                    return Color.cyan;
                case Judgement.Fantastic:
                case Judgement.OK:
                    return Color.white;
                case Judgement.Excellent:
                    return new Color(1.0f, 0.84f, 0f); //Gold
                case Judgement.Great:
                    return Color.green;
                case Judgement.Good:
                    return new Color(0.133f, 0.545f, 0.133f); //Forest Green
                case Judgement.Miss:
                    return Color.red;
            }
            return Color.white;
        }

        public static float GetJudgementScore(Judgement judgement)
        {
            switch (judgement)
            {
                case Judgement.Impeccable:
                    return judgementImpeccableWeight;
                case Judgement.Fantastic:
                    return judgementFantasticWeight;
                case Judgement.Excellent:
                    return judgementExcellentWeight;
                case Judgement.Great:
                    return judgementGreatWeight;
                case Judgement.Good:
                    return judgementGoodWeight;
            }
            return 0f;
        }

        public static string GetMeleeJudgementText()
        {
            return "";
        }

        public static string GetChainJudgementText(Judgement judgement)
        {
            string colorHex = "#" + ColorUtility.ToHtmlStringRGB(GetJudgementColor(judgement));

            string judgementText = "<color=" + colorHex + ">";

            switch (judgement)
            {
                case Judgement.Impeccable:
                    judgementText += "Impeccable Tracing!!";
                    break;
                case Judgement.Fantastic:
                    judgementText += "Fantastic Tracing!";
                    break;
                case Judgement.Excellent:
                    judgementText += "Excellent Tracing";
                    break;
                case Judgement.Great:
                    judgementText += "Great Tracing";
                    break;
                case Judgement.Good:
                    judgementText += "Good Tracing";
                    break;
            }

            judgementText += "</color>";

            return judgementText;
        }

        public static string GetJudgementText(Judgement timingJudgement, Judgement aimJudgement)
        {
            string timingColorHex = "#" + ColorUtility.ToHtmlStringRGB(GetJudgementColor(timingJudgement));
            string aimColorHex = "#" + ColorUtility.ToHtmlStringRGB(GetJudgementColor(aimJudgement));

            string judgementText = "<color=" + timingColorHex + ">";

            switch (timingJudgement)
            {
                case Judgement.Impeccable:
                    judgementText += "Impeccable Timing!!";
                    break;
                case Judgement.Fantastic:
                    judgementText += "Fantastic Timing!";
                    break;
                case Judgement.Excellent:
                    judgementText += "Excellent Timing";
                    break;
                case Judgement.Great:
                    judgementText += "Great Timing";
                    break;
                case Judgement.Good:
                    judgementText += "Good Timing";
                    break;
            }

            judgementText += "</color>\n<color=" + aimColorHex + ">";

            switch (aimJudgement)
            {
                case Judgement.Impeccable:
                    judgementText += "Impeccable Aim!!";
                    break;
                case Judgement.Fantastic:
                    judgementText += "Fantastic Aim!";
                    break;
                case Judgement.Excellent:
                    judgementText += "Excellent Aim";
                    break;
                case Judgement.Great:
                    judgementText += "Great Aim";
                    break;
                case Judgement.Good:
                    judgementText += "Good Aim";
                    break;
            }

            judgementText += "</color>";

            MelonLogger.Log(judgementText);

            return judgementText;
        }

        public static Judgement GetTimingJudgement(float msOffset)
        {
            float x = Math.Abs(msOffset);

            if (x <= judgementImpeccableTimingWindowMs) return Judgement.Impeccable;
            if (x <= judgementFantasticTimingWindowMs) return Judgement.Fantastic;
            if (x <= judgementExcellentTimingWindowMs) return Judgement.Excellent;
            if (x <= judgementGreatTimingWindowMs) return Judgement.Great;
            if (x <= judgementGoodTimingWindowMs) return Judgement.Good;
            return Judgement.Miss;
        }

        public static Judgement GetAimJudgement(Target target, Vector3 intersectionPoint)
        {
            Vector3 targetPos = target.GetContactPosition();
            float distanceFromCenter = (targetPos - intersectionPoint).magnitude;

            
            float impeccableRadius = judgementImpeccableAimRadius;   // snap zone = perfect
            float realAimRadius = audicaFullAimRadius;
            float remaining = realAimRadius - impeccableRadius;

            // Remaining space after the perfect circle
            float fantasticRadius = impeccableRadius + remaining * judgementFantasticAimRadiusPercent;
            float excellentRadius = impeccableRadius + remaining * judgementExcellentAimRadiusPercent;
            float greatRadius = impeccableRadius + remaining * judgementGreatAimRadiusPercent;
            float goodRadius = impeccableRadius + remaining * judgementGoodAimRadiusPercent;

            if (distanceFromCenter <= impeccableRadius) return Judgement.Impeccable;
            if (distanceFromCenter <= fantasticRadius) return Judgement.Fantastic;
            if (distanceFromCenter <= excellentRadius) return Judgement.Excellent;
            if (distanceFromCenter <= greatRadius) return Judgement.Great;
            if (distanceFromCenter <= goodRadius) return Judgement.Good;
            return Judgement.Miss;
        }

        public static Judgement GetChainJudgement(float chainAverage)
        {
            if (chainAverage >= judgementImpeccableChainAverage) return Judgement.Impeccable;
            else if (chainAverage >= judgementFantasticChainAverage) return Judgement.Fantastic;
            else if (chainAverage >= judgementExcellentChainAverage) return Judgement.Excellent;
            else if (chainAverage >= judgementGreatChainAverage) return Judgement.Great;
            else if (chainAverage >= judgementGoodChainAverage) return Judgement.Good;
            else return Judgement.Miss;
        }

        public static float GetMaxJudgementScoreForCue(SongCues.Cue cue)
        {
            float score = 0f;

            if (cue.behavior == Target.TargetBehavior.Vertical ||
                cue.behavior == Target.TargetBehavior.Horizontal ||
                cue.behavior == Target.TargetBehavior.ChainStart ||
                cue.behavior == Target.TargetBehavior.Standard ||
                cue.behavior == Target.TargetBehavior.Hold)
            {
                score += judgementImpeccableWeight * 2;
            }
            if (cue.behavior == Target.TargetBehavior.Chain && cue.chainNext == null)
            {
                score += judgementImpeccableWeight;
            }
            if (cue.behavior == Target.TargetBehavior.Hold ||
                cue.behavior == Target.TargetBehavior.Melee)
            {
                score += 1;
            }

            return score;
        }

        public static float GetJudgementScoreFromJudgement(Judgement judgement)
        {
            if (judgement == Judgement.Impeccable) return judgementImpeccableWeight;
            if (judgement == Judgement.Fantastic) return judgementFantasticWeight;
            if (judgement == Judgement.Excellent) return judgementExcellentWeight;
            if (judgement == Judgement.Great) return judgementGreatWeight;
            if (judgement == Judgement.Good) return judgementGoodWeight;
            if (judgement == Judgement.OK) return judgementOkWeight;
            return 0;
        }

        public static float GetMaxPossibleJudgementScore(string songId)
        {
            float maxJudgementScore = 0;

            SongCues.Cue[] cues = SongCues.GetCues(SongList.I.GetSong(songId), KataConfig.Difficulty.Expert).ToArray();

            foreach (SongCues.Cue cue in cues)
            {
                maxJudgementScore += GetMaxJudgementScoreForCue(cue);
            }

            return maxJudgementScore;
        }

        public static float GetCurrentMaxPossibleJudgementPercentage()
        {
            return (judgementScore / currentMaxPossibleJudgementScore) * 100;
        }
    }
}
