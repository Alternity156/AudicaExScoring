using Harmony;
using Hmx.Audio;
using System;
using UnityEngine;

namespace ExScoringMod
{
    internal static class OptionsMenuFunctions
    {
        public static readonly string[] ParticleOptions = { "No Particles", "Normal Particles", "Extra Particles" };
        public static readonly string[] WeaponSfxOptions = { "Yes", "Menus Only", "No" };
        public static int particleMode = 1;
        public static int weaponSfxMode = 0;
        public static float musicLevel;
        public static float sfxLevel;
        public static bool missFilter;
        public static float inputOffset;
        public static float videoOffset;
        public static float gunPitch;
        public static float gunRoll;
        public static float gunYaw;
        public static float targetSpeedMultiplier;
        public static float meleeSpeedMultiplier;
        public static float dartPreGlowAmount;
        public static float dartSpeedMultiplier;
        public static float controllerPositionSmoothing;
        public static float controllerRotationSmoothing;
        public static bool mirrorMode;
        public static bool flipSlotTargets;
        public static float targetingHapticsStrength;
        public static float aimAssist;
        public static bool disableMineSounds;

        public static void GetMineSounds()
        {
            disableMineSounds = Config.DisableMineSounds;
        }

        public static void SetMineSounds(bool value)
        {
            disableMineSounds = value;
            Config.UpdateMineSoundDisabler(value);
        }

        public static void GetGunPitch()
        {
            gunPitch = PlayerPreferences.I.GunAnglePitch.mVal;
        }

        public static void SetGunPitch(float value)
        {
            PlayerPreferences.I.GunAnglePitch.Set(value);
        }

        public static void GetGunRoll()
        {
            gunRoll = PlayerPreferences.I.GunAngleRoll.mVal;
        }

        public static void SetGunRoll(float value)
        {
            PlayerPreferences.I.GunAngleRoll.Set(value);
        }

        public static void GetGunYaw()
        {
            gunYaw = PlayerPreferences.I.GunAngleYaw.mVal;
        }

        public static void SetGunYaw(float value)
        {
            PlayerPreferences.I.GunAngleYaw.Set(value);
        }

        public static void GetVideoOffset()
        {
            videoOffset = PlayerPreferences.I.VideoOffsetMs.mVal;
        }

        public static void SetVideoOffset(float value)
        {
            PlayerPreferences.I.VideoOffsetMs.Set(value);
        }

        public static void GetInputOffset()
        {
            inputOffset = PlayerPreferences.I.InputOffsetMs.mVal;
        }

        public static void SetInputOffset(float value)
        {
            PlayerPreferences.I.InputOffsetMs.Set(value);
        }

        public static void GetWeaponSfxMode()
        {
            weaponSfxMode = (int)PlayerPreferences.I.GunslingSfxMode.mVal;
        }

        public static void SetWeaponSfxMode(int value)
        {
            PlayerPreferences.I.GunslingSfxMode.Set((float)value);
        }

        public static void GetMissFilter()
        {
            missFilter = PlayerPreferences.I.TrackFiltering.mVal;
        }

        public static void SetMissFilter(bool filter)
        {
            PlayerPreferences.I.TrackFiltering.Set(filter);
        }

        public static void GetMusicLevel()
        {
            musicLevel = PlayerPreferences.I.MusicLevel.mVal;
        }

        public static void SetMusicLevel(float level)
        {
            PlayerPreferences.I.MusicLevel.Set(level);
        }

        public static void GetSfxLevel()
        {
            sfxLevel = PlayerPreferences.I.SfxLevel.mVal;
        }

        public static void SetSfxLevel(float level)
        {
            PlayerPreferences.I.SfxLevel.Set(level);
        }

        public static void GetParticleMode()
        {
            if (ParticleKillerConfig.Enabled) particleMode = 0;
            else
            {
                if (PlayerPreferences.I.ExtraParticles.mVal) particleMode = 2;
                else particleMode = 1;
            }
        }

        public static void SetParticleMode(int value)
        {
            if (value == 0) ParticleKillerConfig.Enabled = true;
            else if (value == 1)
            {
                PlayerPreferences.I.ExtraParticles.Set(false);
                ParticleKillerConfig.Enabled = false;
            }
            else if (value == 2)
            {
                PlayerPreferences.I.ExtraParticles.Set(true);
                ParticleKillerConfig.Enabled = false;
            }
        }


        public static void GetTargetSpeedMultiplier()
        {
            targetSpeedMultiplier = PlayerPreferences.I.TargetSpeedMultiplier.mVal;
        }

        public static void SetTargetSpeedMultiplier(float value)
        {
            PlayerPreferences.I.TargetSpeedMultiplier.Set(value);
        }

        public static void GetMeleeSpeedMultiplier()
        {
            meleeSpeedMultiplier = PlayerPreferences.I.MeleeSpeedMultiplier.mVal;
        }

        public static void SetMeleeSpeedMultiplier(float value)
        {
            PlayerPreferences.I.MeleeSpeedMultiplier.Set(value);
        }

        public static void GetDartPreGlowAmount()
        {
            dartPreGlowAmount = PlayerPreferences.I.DartPreGlowAmount.mVal;
        }

        public static void SetDartPreGlowAmount(float value)
        {
            PlayerPreferences.I.DartPreGlowAmount.Set(value);
        }

        public static void GetDartSpeedMultiplier()
        {
            dartSpeedMultiplier = PlayerPreferences.I.DartSpeedMultiplier.mVal;
        }

        public static void SetDartSpeedMultiplier(float value)
        {
            PlayerPreferences.I.DartSpeedMultiplier.Set(value);
        }

        public static void GetControllerPositionSmoothing()
        {
            controllerPositionSmoothing = PlayerPreferences.I.ControllerPositionSmoothing.mVal;
        }

        public static void SetControllerPositionSmoothing(float value)
        {
            PlayerPreferences.I.ControllerPositionSmoothing.Set(value);
        }

        public static void GetControllerRotationSmoothing()
        {
            controllerRotationSmoothing = PlayerPreferences.I.ControllerRotationSmoothing.mVal;
        }

        public static void SetControllerRotationSmoothing(float value)
        {
            PlayerPreferences.I.ControllerRotationSmoothing.Set(value);
        }

        public static void GetMirrorMode()
        {
            mirrorMode = PlayerPreferences.I.MirrorMode.mVal;
        }

        public static void SetMirrorMode(bool value)
        {
            PlayerPreferences.I.MirrorMode.Set(value);
        }

        public static void GetFlipSlotTargets()
        {
            flipSlotTargets = PlayerPreferences.I.FlipSlotTargets.mVal;
        }

        public static void SetFlipSlotTargets(bool value)
        {
            PlayerPreferences.I.FlipSlotTargets.Set(value);
        }

        public static void GetTargetingHapticsStrength()
        {
            targetingHapticsStrength = PlayerPreferences.I.TargetingHapticsStrength.mVal;
        }

        public static void SetTargetingHapticsStrength(float value)
        {
            PlayerPreferences.I.TargetingHapticsStrength.Set(value);
        }

        public static void GetAimAssist()
        {
            aimAssist = PlayerPreferences.I.AimAssistAmount.mVal;
        }

        public static void SetAimAssist(float value)
        {
            PlayerPreferences.I.AimAssistAmount.Set(value);
        }


        [HarmonyPatch(typeof(UGPUEmitter), "Emit", new Type[] { typeof(int), typeof(bool) })]
        private static class ParticleEmmision
        {
            private static void Prefix(UGPUEmitter __instance, ref int count, bool immediate)
            {
                if (!ParticleKillerConfig.Enabled) return;
                count = ParticleKillerConfig.ParticleCount;
            }
        }

        [HarmonyPatch(typeof(UGPUEmitter), "EmitBurst", new Type[] { typeof(int) })]
        private static class ParticleEmmisionBurst
        {
            private static void Prefix(UGPUEmitter __instance, ref int count)
            {
                if (!ParticleKillerConfig.Enabled) return;
                count = ParticleKillerConfig.ParticleCount;
            }
        }

        [HarmonyPatch(typeof(ParticlePool), "Play", new Type[] { typeof(Vector3), typeof(Quaternion), typeof(float) })]
        private static class CPUParticleEmmisionNoParams
        {
            private static bool Prefix(ParticlePool __instance)
            {
                if (!ParticleKillerConfig.Enabled) return true;
                if (ParticleKillerConfig.KillCPUParticles) return false;
                else return true;
            }
        }

        [HarmonyPatch(typeof(KataUtil), "PlayFMODEvent", new Type[] { typeof(string), typeof(UAudioEmitterCom) })]
        private static class InterceptSounds
        {
            private static bool Prefix(KataUtil __instance, string eventName, UAudioEmitterCom emitter)
            {
                if (eventName == "event:/gameplay/dodge_success" && Config.DisableMineSounds)
                {
                    return false;
                }
                else return true;
            }

        }
    }
}