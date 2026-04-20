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
    }
}
