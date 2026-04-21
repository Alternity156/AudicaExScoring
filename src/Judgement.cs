using MelonLoader;
using System;
using UnityEngine;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
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
                    return 2.25f;
                case Judgement.Fantastic:
                    return 2f;
                case Judgement.Excellent:
                    return 1.5f;
                case Judgement.Great:
                    return 1f;
                case Judgement.Good:
                    return 0.75f;
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

            if (x <= 8.5f) return Judgement.Impeccable;
            if (x <= 17f) return Judgement.Fantastic;
            if (x <= 25f) return Judgement.Excellent;
            if (x <= 50f) return Judgement.Great;
            if (x <= 100f) return Judgement.Good;
            return Judgement.Miss;
        }

        public static Judgement GetAimJudgement(Target target, Vector3 intersectionPoint)
        {
            Vector3 targetPos = target.GetContactPosition();
            float distanceFromCenter = (targetPos - intersectionPoint).magnitude;

            // Remaining space after the perfect circle
            const float impeccableRadius = 0.5f;   // snap zone = perfect
            const float realAimRadius = 5.0f;
            float remaining = realAimRadius - impeccableRadius; // 4.5f

            float fantasticRadius = impeccableRadius + remaining * 0.10f; // 0.95f
            float excellentRadius = impeccableRadius + remaining * 0.30f; // 1.85f
            float greatRadius = impeccableRadius + remaining * 0.50f; // 2.75f
            float goodRadius = impeccableRadius + remaining * 1.00f; // 5.0f  (= realAimRadius)

            if (distanceFromCenter <= impeccableRadius) return Judgement.Impeccable;
            if (distanceFromCenter <= fantasticRadius) return Judgement.Fantastic;
            if (distanceFromCenter <= excellentRadius) return Judgement.Excellent;
            if (distanceFromCenter <= greatRadius) return Judgement.Great;
            if (distanceFromCenter <= goodRadius) return Judgement.Good;
            return Judgement.Miss;
        }
    }
}
