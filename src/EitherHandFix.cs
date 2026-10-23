using Harmony;
using MelonLoader;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnhollowerBaseLib;      // TryCast
using UnhollowerRuntimeLib;   // Il2CppType (ML 0.5.7)

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        // Original fix colour for either-hand targets when the melee colour override is off.
        private static readonly Color defaultEitherHandColor = new Color(1f, 1f, 1f);

        /// <summary>
        /// While the colour picker is previewing a melee (either-hand) colour, this overrides the saved
        /// Config.EitherHandColor. Null = use the saved colour. Only has an effect when
        /// Config.OverrideMeleeColor is on.
        /// </summary>
        internal static Color? EitherHandColorOverride;

        private static Color eitherHandColor =>
            Config.OverrideMeleeColor ? (EitherHandColorOverride ?? Config.EitherHandColor) : defaultEitherHandColor;

        // Melee override bookkeeping: a backup of the vanilla either melee material (taken before we ever
        // modify it), so turning the override off restores it exactly.
        private static Material meleeEitherBackup;
        private static IntPtr meleeEitherBackupSource = IntPtr.Zero;
        private static bool meleeEitherOverridden;

        // KataConfig.eitherHandColor: melee colour while the override is on, default white otherwise
        // (the game tints either targets — incl. the melee sphere — from it).

        private static void ApplyEitherHandColor(Material mat)
        {
            if (mat == null) return;
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
                ApplyEitherHandColorToAll(__instance);
            }
        }

        /// <summary>
        /// Re-applies the current either-hand state to every either-hand target material, without going
        /// through SetColors. Used by the colour picker for live previews and by the override toggle.
        /// </summary>
        internal static void RefreshEitherHandColors()
        {
            var setter = TargetColorSetter.I;
            if (setter != null) ApplyEitherHandColorToAll(setter);
        }

        private static void ApplyEitherHandColorToAll(TargetColorSetter __instance)
        {
            if (__instance == null) return;

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

            ApplyMeleeEither(__instance);
            ApplyKataEitherColor();
            RefreshMeleeTrails();
        }

        /// <summary>
        /// Melee either material (ShooterTargetMeleeSphere). Vanilla unless the override is on; when on it
        /// takes the hand-melee material's look (ShooterTargetMeleeSphereColor, same ShinyFresnal shader)
        /// and the melee colour on _Color etc. Turning the override off restores the vanilla backup.
        /// </summary>
        private static void ApplyMeleeEither(TargetColorSetter setter)
        {
            Material mat = setter.mMeleeTargetEitherMat;
            if (mat == null) return;

            // (Re)take the vanilla backup the first time we see this material — before any modification.
            // A new TargetColorSetter/material (e.g. after a scene reload) gets its own fresh backup.
            if (meleeEitherBackup == null || meleeEitherBackupSource != mat.Pointer)
            {
                meleeEitherBackup = new Material(mat);
                meleeEitherBackupSource = mat.Pointer;
                meleeEitherOverridden = false;
            }

            if (Config.OverrideMeleeColor)
            {
                Material colorSource = setter.mMeleeTargetLeftMat;
                if (colorSource != null) mat.CopyPropertiesFromMaterial(colorSource);
                ApplyEitherHandColor(mat);
                meleeEitherOverridden = true;
            }
            else if (meleeEitherOverridden)
            {
                mat.CopyPropertiesFromMaterial(meleeEitherBackup);
                meleeEitherOverridden = false;
            }
        }

        private static void ApplyKataEitherColor()
        {
            if (KataConfig.I == null) return;
            // eitherHandColor already resolves to the melee colour (override on) or white (override off).
            KataConfig.I.eitherHandColor = eitherHandColor;
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  Either melee trails
        // ══════════════════════════════════════════════════════════════════════════════════════
        //
        // Hand melee trails use the colourable TrailMeleeColor material (DefaultParticleColorID shader:
        // the colour comes from the _LeftHandColor/_RightHandColor globals picked by _ColorID). Either
        // melee trails use TrailMelee, which always renders white. With the override on, each spawned
        // either-melee trail renderer is switched to TrailMeleeColor and given a per-renderer property
        // block with _ColorID = 0 and _RightHandColor = melee colour (the block value wins over the
        // global, so the real right-hand colour is untouched). Override off restores the original.
        //
        // ParticleSystem / ParticleSystemRenderer types are deliberately never referenced (they crashed
        // the UnityExplorer console); trails are found as plain Renderers by material name.

        private class MeleeTrailEntry
        {
            public Renderer renderer;
            public Material original;
            public bool swapped;
        }

        private const string EitherTrailMaterialPrefix = "TrailMelee";
        private const string ColorTrailMaterialPrefix = "TrailMeleeColor";

        private static readonly List<MeleeTrailEntry> meleeTrails = new List<MeleeTrailEntry>();
        private static Material trailColorMaterial;
        private static MaterialPropertyBlock trailBlock;

        [HarmonyPatch(typeof(Target), "OnCreated", new Type[] { typeof(Target.TargetBehavior), typeof(Target.TargetHandType) })]
        private static class EitherMeleeTrailOnCreatedPatch
        {
            private static void Postfix(Target __instance, Target.TargetBehavior behavior, Target.TargetHandType handType)
            {
                if (behavior != Target.TargetBehavior.Melee || handType != Target.TargetHandType.Either) return;
                // Trail may not exist yet at OnCreated time — pick it up next frame.
                MelonCoroutines.Start(TrackMeleeTrailNextFrame(__instance));
            }
        }

        private static IEnumerator TrackMeleeTrailNextFrame(Target target)
        {
            yield return null;
            if (target == null) yield break;
            TrackMeleeTrails(target);
        }

        private static void TrackMeleeTrails(Target target)
        {
            PruneMeleeTrails();

            var renderers = target.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null) continue;
                Material mat = r.sharedMaterial;
                if (mat == null) continue;

                string name = mat.name;
                if (!name.StartsWith(EitherTrailMaterialPrefix) || name.StartsWith(ColorTrailMaterialPrefix)) continue;
                if (IsTracked(r)) continue;

                var entry = new MeleeTrailEntry { renderer = r, original = mat, swapped = false };
                meleeTrails.Add(entry);
                ApplyMeleeTrail(entry);
            }
        }

        private static bool IsTracked(Renderer r)
        {
            for (int i = 0; i < meleeTrails.Count; i++)
                if (meleeTrails[i].renderer != null && meleeTrails[i].renderer.Pointer == r.Pointer) return true;
            return false;
        }

        private static void PruneMeleeTrails()
        {
            for (int i = meleeTrails.Count - 1; i >= 0; i--)
                if (meleeTrails[i].renderer == null) meleeTrails.RemoveAt(i); // Unity-null: target destroyed
        }

        /// <summary>Re-applies the current override state/colour to every tracked either-melee trail.</summary>
        private static void RefreshMeleeTrails()
        {
            PruneMeleeTrails();
            for (int i = 0; i < meleeTrails.Count; i++) ApplyMeleeTrail(meleeTrails[i]);
        }

        private static void ApplyMeleeTrail(MeleeTrailEntry entry)
        {
            Renderer r = entry.renderer;
            if (r == null) return;

            if (Config.OverrideMeleeColor)
            {
                Material colorMat = GetTrailColorMaterial();
                if (colorMat == null) return;

                if (!entry.swapped)
                {
                    r.sharedMaterial = colorMat;
                    entry.swapped = true;
                }

                if (trailBlock == null) trailBlock = new MaterialPropertyBlock();
                r.GetPropertyBlock(trailBlock);
                trailBlock.SetFloat("_ColorID", 0f);
                trailBlock.SetColor("_RightHandColor", eitherHandColor);
                r.SetPropertyBlock(trailBlock);
            }
            else if (entry.swapped)
            {
                // TrailMelee ignores the colour entries left in the block, so restoring the material is enough.
                if (entry.original != null) r.sharedMaterial = entry.original;
                entry.swapped = false;
            }
        }

        private static Material GetTrailColorMaterial()
        {
            if (trailColorMaterial != null) return trailColorMaterial; // Unity-null: re-find after unload

            Material fallback = null;
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Material>()))
            {
                var m = o.TryCast<Material>();
                if (m == null) continue;
                if (m.name == ColorTrailMaterialPrefix) { trailColorMaterial = m; break; }
                if (fallback == null && m.name.StartsWith(ColorTrailMaterialPrefix)) fallback = m;
            }
            if (trailColorMaterial == null) trailColorMaterial = fallback;

            if (trailColorMaterial == null) DebugLog.Log("[EitherHandFix] TrailMeleeColor material not found");
            return trailColorMaterial;
        }
    }
}