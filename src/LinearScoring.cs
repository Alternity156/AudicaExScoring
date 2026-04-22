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

            if (timing <= audicaPerfectTimingWindowMs) return 1f;
            if (timing >= audicaWholeTimingWindowMs) return 0f;

            return 1f - (timing - audicaPerfectTimingWindowMs) / (audicaWholeTimingWindowMs - audicaPerfectTimingWindowMs);
        }

        public static float GetLinearAimScore(Target target, Vector3 intersectionPoint, bool useSnapZone = true)
        {
            Vector3 targetPos = target.GetContactPosition();
            float distanceFromCenter = (targetPos - intersectionPoint).magnitude;
            float perfectRadius = audicaPerfectAimRadius;
            float realAimRadius = audicaFullAimRadius;

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
