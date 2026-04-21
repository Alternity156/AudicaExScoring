using MelonLoader;
using System;
using UnityEngine;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        public static float GetLinearTimingScore(float msOffset)
        {
            float timing = Math.Abs(msOffset);

            if (timing <= perfectTimingSlopMs) return 1f;
            if (timing >= KataConfig.kSlopWindowEarlyMs) return 0f;

            return 1f - (timing - perfectTimingSlopMs) / (KataConfig.kSlopWindowEarlyMs - perfectTimingSlopMs);
        }

        public static float GetLinearAimScore(Target target, Vector3 intersectionPoint, bool useSnapZone = true)
        {
            Vector3 targetPos = target.GetContactPosition();
            float distanceFromCenter = (targetPos - intersectionPoint).magnitude;
            const float perfectRadius = 0.5f;
            const float realAimRadius = 5.0f;

            if (distanceFromCenter >= realAimRadius) return 0f;

            if (useSnapZone)
            {
                if (distanceFromCenter <= perfectRadius) return 1f;
                return 1f - (distanceFromCenter - perfectRadius) / (realAimRadius - perfectRadius);
            }

            return 1f - distanceFromCenter / realAimRadius;
        }
    }
}
