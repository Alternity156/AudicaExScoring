using MelonLoader;
using System;
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

        public static float judgementImpeccableTimingWindowMs = 8.5f;
        public static float judgementFantasticTimingWindowMs = 17f;
        public static float judgementExcellentTimingWindowMs = 25f;
        public static float judgementGreatTimingWindowMs = 50f;
        public static float judgementGoodTimingWindowMs = 100f;

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
            Miss
        }

        public static Color GetJudgementColor(Judgement judgement)
        {
            switch (judgement)
            {
                case Judgement.Impeccable:
                    return Color.blue;
                case Judgement.Fantastic:
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

            judgementText += judgementText + "</color>\n<color=" + aimColorHex + ">";

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
    }
}
