using MelonLoader;
using UnityEngine;
using System;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        public static float audicaPerfectTimingWindowMs = 8.5f;
        public static float audicaWholeTimingWindowMs = 100f;

        public static float audicaPerfectAimRadius = 0.5f;
        public static float audicaNearPerfectAimRadius = 0.93334f;
        public static float audicaHalfAimRadius = 1f;
        public static float audicaFullAimRadius = 5f;

        public static float GetAudicaTimingScore(float msOffset)
        {
            float x = Math.Abs(msOffset);

            if (x <= audicaPerfectTimingWindowMs) return 1f;
            if (x >= audicaWholeTimingWindowMs) return 0f;

            return 1f - Mathf.Pow(x / audicaWholeTimingWindowMs, 1.8f);
        }

        //Audica will floor that value to 0.700005f
        public static float GetAudicaAimScore(Target target, Vector3 intersectionPoint)
        {
            Vector3 targetPos = target.GetContactPosition();
            float distanceFromCenter = (targetPos - intersectionPoint).magnitude;

            float snapRadius = audicaPerfectAimRadius;
            float halfRadius = audicaHalfAimRadius;
            float realAimRadius = audicaFullAimRadius;

            // Dead center snap zone: lerp from 1.0 down to 0.93334
            if (distanceFromCenter <= halfRadius)
            {
                if (distanceFromCenter <= snapRadius)
                    return 1f;
                float t = (distanceFromCenter - snapRadius) / (halfRadius - snapRadius);
                return Mathf.Lerp(1f, audicaNearPerfectAimRadius, t);
            }

            // Outer zone: lerp from 0.93334 down to 0
            if (distanceFromCenter <= realAimRadius)
            {
                float t = (distanceFromCenter - halfRadius) / (realAimRadius - halfRadius);
                return Mathf.Lerp(audicaNearPerfectAimRadius, 0f, Mathf.Clamp01(t));
            }

            return 0f;
        }
    }
}
