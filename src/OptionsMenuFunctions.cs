using Harmony;
using Hmx.Audio;
using MelonLoader;
using System;
using UnityEngine;

namespace ExScoringMod
{
    internal static class OptionsMenuFunctions
    {
        public static readonly string[] ParticleOptions = { "No Particles", "Normal Particles", "Extra Particles" };
        public static readonly string[] WeaponSfxOptions = { "Yes", "Menus Only", "No" };
        public static int particleMode = 1;
        public static bool killCPUParticles;
        public static float particleKillerCount;
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
        public static float timingWindow;
        public static bool disableTemporalAimAssist;
        public static bool forceHitSounds;
        public static bool disableGunBeamRedirection;
        public static readonly string[] ArrowColorOptions = { "White", "Hand Color" };
        public static readonly string[] ChainLineColorOptions = { "Default (Black)", "Hand Color" };
        public static int arrowColorMode;
        public static int chainLineColorMode;
        public static float arrowWidth;
        public static float arrowLength;
        public static bool enableChainArrow;
        public static bool disableMenuGrab;
        public static bool trippyMenuEnabled;
        public static float trippyMenuSpeed;
        public static float scrollSpeedMultiplier;
        public static float arrowScrollRows;
        public static bool hideScoreData;
        public static bool firstPlayBlind;
        public static bool practiceModeMinimizeButtonEnabled;
        public static float maxRunsPerSong;
        public static float maxRunDataSizeMB;
        public static bool enableRunDataSaving;

        public static void GetHideScoreData()
        {
            hideScoreData = Config.HideScoreData;
        }

        public static void SetHideScoreData(bool value)
        {
            hideScoreData = value;
            Config.UpdateHideScoreData(value);
            ExScoring.RefreshScoreDataVisibility();
        }

        public static void GetFirstPlayBlind()
        {
            firstPlayBlind = Config.FirstPlayBlind;
        }

        public static void SetFirstPlayBlind(bool value)
        {
            firstPlayBlind = value;
            Config.UpdateFirstPlayBlind(value);
            ExScoring.RefreshScoreDataVisibility();
        }

        public static void GetPracticeModeMinimizeButtonEnabled()
        {
            practiceModeMinimizeButtonEnabled = Config.PracticeModeMinimizeButtonEnabled;
        }

        public static void SetPracticeModeMinimizeButtonEnabled(bool value)
        {
            practiceModeMinimizeButtonEnabled = value;
            Config.UpdatePracticeModeMinimizeButtonEnabled(value);
        }

        public static void GetMaxRunsPerSong()
        {
            maxRunsPerSong = Config.MaxRunsPerSong;
        }

        public static void SetMaxRunsPerSong(float value)
        {
            maxRunsPerSong = value;
            Config.UpdateMaxRunsPerSong((int)value);
        }

        public static void GetMaxRunDataSizeMB()
        {
            maxRunDataSizeMB = Config.MaxRunDataSizeMB;
        }

        public static void SetMaxRunDataSizeMB(float value)
        {
            maxRunDataSizeMB = value;
            Config.UpdateMaxRunDataSizeMB(value);
        }

        public static void GetEnableRunDataSaving()
        {
            enableRunDataSaving = Config.EnableRunDataSaving;
        }

        public static void SetEnableRunDataSaving(bool value)
        {
            enableRunDataSaving = value;
            Config.UpdateEnableRunDataSaving(value);
        }

        public static void GetArrowScrollRows()
        {
            arrowScrollRows = Config.ArrowScrollRows;
        }

        public static void SetArrowScrollRows(float value)
        {
            arrowScrollRows = value;
            Config.UpdateArrowScrollRows(value);
        }

        public static void GetScrollSpeedMultiplier()
        {
            scrollSpeedMultiplier = Config.ScrollSpeedMultiplier;
        }

        public static void SetScrollSpeedMultiplier(float value)
        {
            scrollSpeedMultiplier = value;
            Config.UpdateScrollSpeedMultiplier(value);
        }

        public static void GetEnableChainArrow()
        {
            enableChainArrow = Config.EnableChainArrow;
        }

        public static void SetEnableChainArrow(bool value)
        {
            enableChainArrow = value;
            Config.UpdateEnableChainArrow(value);
        }

        public static void GetDisableMenuGrab()
        {
            disableMenuGrab = Config.DisableMenuGrab;
        }

        public static void SetDisableMenuGrab(bool value)
        {
            disableMenuGrab = value;
            Config.UpdateDisableMenuGrab(value);
        }

        public static void GetTrippyMenuEnabled()
        {
            trippyMenuEnabled = Config.TrippyMenuEnabled;
        }

        public static void SetTrippyMenuEnabled(bool value)
        {
            trippyMenuEnabled = value;
            Config.UpdateTrippyMenuEnabled(value);
        }

        public static void GetTrippyMenuSpeed()
        {
            trippyMenuSpeed = Config.TrippyMenuSpeed;
        }

        public static void SetTrippyMenuSpeed(float value)
        {
            trippyMenuSpeed = value;
            Config.UpdateTrippyMenuSpeed(value);
        }

        public static void GetArrowColorMode()
        {
            arrowColorMode = Config.ArrowColorMode;
        }

        public static void SetArrowColorMode(int value)
        {
            arrowColorMode = value;
            Config.UpdateArrowColorMode(value);
        }

        public static void GetChainLineColorMode()
        {
            chainLineColorMode = Config.ChainLineColorMode;
        }

        public static void SetChainLineColorMode(int value)
        {
            chainLineColorMode = value;
            Config.UpdateChainLineColorMode(value);
        }

        public static void GetArrowWidth()
        {
            arrowWidth = Config.ArrowWidth;
        }

        public static void SetArrowWidth(float value)
        {
            arrowWidth = value;
            Config.UpdateArrowWidth(value);
        }

        public static void GetArrowLength()
        {
            arrowLength = Config.ArrowLength;
        }

        public static void SetArrowLength(float value)
        {
            arrowLength = value;
            Config.UpdateArrowLength(value);
        }

        public static void GetGunBeamRedirection()
        {
            disableGunBeamRedirection = Config.DisableGunBeamRedirection;
        }

        public static void SetGunBeamRedirection(bool value)
        {
            disableGunBeamRedirection = value;
            Config.UpdateGunBeamRedirection(value);
        }

        public static void GetForceHitSounds()
        {
            forceHitSounds = Config.ForceHitSounds;
        }

        public static void SetForceHitSounds(bool value)
        {
            forceHitSounds |= value;
            Config.UpdateForceHitSounds(value);
        }

        public static void GetTemporalAimAssist()
        {
            disableTemporalAimAssist = Config.DisableTemporalAimAssist;
        }

        public static void SetTemporalAimAssist(bool value)
        {
            disableTemporalAimAssist = value;
            Config.UpdateTemporalAimAssist(value);
        }

        public static void GetTimingWindow()
        {
            timingWindow = Config.TimingWindow;
        }

        public static void SetTimingWindow(float value)
        {
            timingWindow = value;
            Config.UpdateTimingWindow(value);
        }

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
            if (Config.ParticleKillerEnabled) particleMode = 0;
            else
            {
                if (PlayerPreferences.I.ExtraParticles.mVal) particleMode = 2;
                else particleMode = 1;
            }
        }

        public static void SetParticleMode(int value)
        {
            if (value == 0) Config.UpdateParticleKillerEnabled(true);
            else if (value == 1)
            {
                PlayerPreferences.I.ExtraParticles.Set(false);
                Config.UpdateParticleKillerEnabled(false);
            }
            else if (value == 2)
            {
                PlayerPreferences.I.ExtraParticles.Set(true);
                Config.UpdateParticleKillerEnabled(false);
            }
        }

        public static void GetKillCPUParticles()
        {
            killCPUParticles = Config.ParticleKillerKillCPUParticles;
        }

        public static void SetKillCPUParticles(bool value)
        {
            killCPUParticles = value;
            Config.UpdateParticleKillerKillCPUParticles(value);
        }

        public static void GetParticleKillerCount()
        {
            particleKillerCount = Config.ParticleKillerParticleCount;
        }

        public static void SetParticleKillerCount(float value)
        {
            particleKillerCount = value;
            Config.UpdateParticleKillerParticleCount((int)value);
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
                if (!Config.ParticleKillerEnabled) return;
                count = Config.ParticleKillerParticleCount;
            }
        }

        [HarmonyPatch(typeof(UGPUEmitter), "EmitBurst", new Type[] { typeof(int) })]
        private static class ParticleEmmisionBurst
        {
            private static void Prefix(UGPUEmitter __instance, ref int count)
            {
                if (!Config.ParticleKillerEnabled) return;
                count = Config.ParticleKillerParticleCount;
            }
        }

        [HarmonyPatch(typeof(ParticlePool), "Play", new Type[] { typeof(Vector3), typeof(Quaternion), typeof(float) })]
        private static class CPUParticleEmmisionNoParams
        {
            private static bool Prefix(ParticlePool __instance)
            {
                if (!Config.ParticleKillerEnabled) return true;
                if (Config.ParticleKillerKillCPUParticles) return false;
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

        [HarmonyPatch(typeof(AudioDriver), "StartPlaying", new Type[0])]
        private static class SetCueTimingWindow
        {
            private static void Postfix(AudioDriver __instance)
            {
                if (Config.TimingWindow != 1f)
                {
                    SongCues.Cue[] cues = SongCues.I.GetCues();
                    SongList.SongData song = SongList.I.GetSong(SongDataHolder.I.songData.songID);
                    SongList.SongData.TempoChange[] tempos = song.tempos;

                    for (int i = 0; i < tempos.Length; i++)
                    {
                        float timingWindowMs = 200 * Mathf.Lerp(0.07f, 1.0f, Config.TimingWindow);

                        float ticks = timingWindowMs / (60000 / (tempos[i].tempo * 480));
                        float halfTicks = ticks / 2;

                        for (int j = 0; j < cues.Length; j++)
                        {
                            if (cues[j].behavior != Target.TargetBehavior.Chain && cues[j].behavior != Target.TargetBehavior.Dodge && cues[j].behavior != Target.TargetBehavior.Melee)
                            {
                                void UpdateTarget(SongCues.Cue cue)
                                {
                                    cue.slopAfterTicks = halfTicks;
                                    cue.slopBeforeTicks = halfTicks;
                                }
                                if (cues[j].tick >= tempos[i].tick)
                                {
                                    if (tempos.Length >= tempos.Length + 1 && cues[j].tick < tempos[i + 1].tick)
                                    {
                                        UpdateTarget(cues[j]);
                                    }
                                    else if (tempos.Length < tempos.Length + 1)
                                    {
                                        UpdateTarget(cues[j]);
                                    }
                                }
                            }
                        }
                    }
                }

                if (Config.ForceHitSounds)
                {
                    SongCues.Cue[] cues = SongCues.I.GetCues();

                    for (int i = 0; i < cues.Length; i++)
                    {
                        if (cues[i].behavior != Target.TargetBehavior.Dodge && cues[i].behavior != Target.TargetBehavior.Melee)
                        {
                            if (cues[i].velocity != 1 && cues[i].velocity != 2 && cues[i].velocity != 20 && cues[i].velocity != 60 && cues[i].velocity != 127)
                            {
                                cues[i].velocity = 2;
                            }
                        }
                        else if (cues[i].behavior == Target.TargetBehavior.Melee)
                        {
                            if (cues[i].velocity != 3)
                            {
                                cues[i].velocity = 3;
                            }
                        }
                    }
                }
            }
        }

        [HarmonyPatch(typeof(Gun), "FindBestIntersection")]
        private static class GunFindBestIntersectionPatch
        {
            private static void Prefix(Gun __instance, Target target, float aimRadius, ref bool temporalAssist)
            {
                if (Config.DisableTemporalAimAssist)
                {
                    temporalAssist = false;
                }
            }
        }

        [HarmonyPatch(typeof(Gun), "AdjustAutoaimedPosition", new Type[] { typeof(Target), typeof(Vector3), typeof(int), typeof(bool) })]
        private static class PatchAdjustPosition
        {
            private static bool Prefix(Gun __instance, Target target, Vector3 intersection, int firepointHistoryIndex, bool forceForAutoplay, ref Vector3 __result)
            {
                if (Config.DisableGunBeamRedirection) { return false; }
                return true;
            }
            private static void Postfix(Gun __instance, Target target, Vector3 intersection, int firepointHistoryIndex, bool forceForAutoplay, ref Vector3 __result)
            {
                if (Config.DisableGunBeamRedirection) { __result = intersection; }
            }
        }

        [HarmonyPatch(typeof(GrabScroll), "OnGrab", new Type[] { typeof(Gun), typeof(Vector3) })]
        private static class MenuGrabDisablerPatch
        {
            private static bool Prefix(GrabScroll __instance, Gun gun, Vector3 grabPos)
            {
                if (Config.DisableMenuGrab) return false;
                if (__instance.isArrow) return false; // arrow buttons never grab-scroll
                return true;
            }
        }

        [HarmonyPatch(typeof(ShellScrollable), "Scroll", new Type[] { typeof(float) })]
        private static class ShellScrollableScrollSpeedPatch
        {
            private static bool Prefix(ShellScrollable __instance, float amount)
            {
                if (VirtualSongList.Scroller == null || __instance.Pointer != VirtualSongList.Scroller.Pointer)
                    return true;

                float effectiveAmount;
                if (Mathf.Approximately(Mathf.Abs(amount), 3f))
                {
                    effectiveAmount = Mathf.Sign(amount) * Config.ArrowScrollRows;
                }
                else
                {
                    effectiveAmount = amount * Config.ScrollSpeedMultiplier;
                }

                float maxScroll = Mathf.Max(0f, VirtualSongList.CurrentView.Count - __instance.displayCount);
                float newIndex = Mathf.Clamp(__instance.mIndex + effectiveAmount, 0f, maxScroll);

                __instance.SnapTo(newIndex, true);
                __instance.UpdateScroll(-1);

                return false;
            }
        }
    }
}