using Harmony;
using MelonLoader;
using System;
using UnityEngine;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        private static readonly Color eitherHandColor = new Color(1f, 1f, 1f);

        private static void ApplyEitherHandColor(Material mat)
        {
            mat.color = eitherHandColor;
            mat.SetColor("_Color0", eitherHandColor);
            mat.SetColor("_Color1", eitherHandColor);
            mat.SetColor("_Color", eitherHandColor);
            mat.SetColor("_EmissionColor", eitherHandColor);
            mat.SetColor("_EmisColor", eitherHandColor);
            mat.SetColor("_Emission", eitherHandColor);
            mat.SetColor("_GlowColor", eitherHandColor);
            mat.SetColor("_OutlineColor", eitherHandColor);
            mat.SetColor("_RimColor", eitherHandColor);
            mat.SetColor("_SpecColor", eitherHandColor);
            mat.SetColor("_center_highlight_color", eitherHandColor);
            mat.SetColor("_TintColor", eitherHandColor);
            mat.SetColor("_swirl_alpha", eitherHandColor);
        }

        [HarmonyPatch(typeof(TargetColorSetter), "SetColors", new Type[] { typeof(Color), typeof(Color), typeof(bool) })]
        private static class FixEitherHandColorsPatch
        {
            private static void Postfix(TargetColorSetter __instance, Color colorLeft, Color colorRight, bool simpleChange = false)
            {
                ApplyEitherHandColor(__instance.telegraphEitherMat);
                ApplyEitherHandColor(__instance.telegraphSustainEitherMat);
                ApplyEitherHandColor(__instance.telegraphSlotEitherMat);
                ApplyEitherHandColor(__instance.telegraphRingEitherMat);
                ApplyEitherHandColor(__instance.telegraphCenterEitherMat);
                ApplyEitherHandColor(__instance.telegraphCenterSustainEitherMat);
                ApplyEitherHandColor(__instance.telegraphCenterHorizontalEitherMat);
                ApplyEitherHandColor(__instance.telegraphCenterVerticalEitherMat);
                ApplyEitherHandColor(__instance.telegraphDartLineEitherMat);
                ApplyEitherHandColor(__instance.telegraphGlowEitherMat);
                ApplyEitherHandColor(__instance.mChainTargetEitherMat);
                ApplyEitherHandColor(__instance.mChainStartTargetEitherMat);
                ApplyEitherHandColor(__instance.mHoldTargetEitherMat);
                ApplyEitherHandColor(__instance.mHorizontalTargetEitherMat);
                ApplyEitherHandColor(__instance.mVerticalTargetEitherMat);
                ApplyEitherHandColor(__instance.mStandardTargetEitherMat);
            }
        }
    }
}