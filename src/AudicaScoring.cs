using MelonLoader;
using UnityEngine;
using System;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        public static float GetAudicaTimingScore(float msOffset)
        {
            float x = Math.Abs(msOffset);

            if (x <= 8.5f) return 1f;
            if (x >= 100f) return 0f;

            return 1f - Mathf.Pow(x / 100f, 1.8f);
        }

        //Audica will floor that value to 0.700005f
        public static float GetAudicaAimScore(Target target, Vector3 intersectionPoint)
        {
            Vector3 targetPos = target.GetContactPosition();
            float distanceFromCenter = (targetPos - intersectionPoint).magnitude;

            const float snapRadius = 0.5f;
            const float halfRadius = 1.0f;
            const float realAimRadius = 5.0f;

            // Dead center snap zone: lerp from 1.0 down to 0.93334
            if (distanceFromCenter <= halfRadius)
            {
                if (distanceFromCenter <= snapRadius)
                    return 1f;
                float t = (distanceFromCenter - snapRadius) / (halfRadius - snapRadius);
                return Mathf.Lerp(1f, 0.93334f, t);
            }

            // Outer zone: lerp from 0.93334 down to 0
            if (distanceFromCenter <= realAimRadius)
            {
                float t = (distanceFromCenter - halfRadius) / (realAimRadius - halfRadius);
                return Mathf.Lerp(0.93334f, 0f, Mathf.Clamp01(t));
            }

            return 0f;
        }
    }
}
