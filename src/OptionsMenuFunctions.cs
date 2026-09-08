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
        public static bool unifyTargetSpeed;
        public static readonly string[] ArrowColorOptions = { "White", "Hand Color" };
        public static readonly string[] ChainLineColorOptions = { "Default (Black)", "Hand Color" };
        public static int arrowColorMode;
        public static int chainLineColorMode;
        public static float arrowWidth;
        public static float arrowLength;
        public static float chainArrowMinPitchDistance;
        public static float chainArrowMaxSimultaneous;
        public static bool enableChainArrow;
        public static bool disableMenuGrab;
        public static bool trippyMenuEnabled;
        public static float trippyMenuSpeed;
        public static bool purpleMenuEnabled;
        public static float scrollSpeedMultiplier;
        public static float arrowScrollRows;
        public static bool arrowJumpToEnds;
        public static bool hideScoreData;
        public static bool firstPlayBlind;
        public static bool wrapSongList;
        public static bool practiceModeMinimizeButtonEnabled;
        public static readonly string[] RandomSongScopeOptions = { "Folder Songs", "All Songs" };
        public static int randomSongScope;
        public static float maxRunsPerSong;
        public static float maxRunDataSizeMB;
        public static bool enableRunDataSaving;
        public static bool songCacheEnabled;
        public static bool showStatsOnFail;
        public static bool saveFailedRunData;
        public static float exScorePopupSize;
        public static float exScorePopupOpacity;
        public static float searchKeyboardPosX;
        public static float searchKeyboardPosY;
        public static float searchKeyboardPosZ;
        public static float searchKeyboardTilt;
        public static bool enableScoreUpload;

        public static void GetExScorePopupSize()
        {
            exScorePopupSize = Config.ExScorePopupSize;
        }

        public static void SetExScorePopupSize(float value)
        {
            exScorePopupSize = value;
            Config.UpdateExScorePopupSize(value);
        }

        public static void GetExScorePopupOpacity()
        {
            exScorePopupOpacity = Config.ExScorePopupOpacity;
        }

        public static void SetExScorePopupOpacity(float value)
        {
            exScorePopupOpacity = value;
            Config.UpdateExScorePopupOpacity(value);
        }

        public static void GetSearchKeyboardPosX()
        {
            searchKeyboardPosX = Config.SearchKeyboardPosX;
        }

        public static void SetSearchKeyboardPosX(float value)
        {
            searchKeyboardPosX = value;
            Config.UpdateSearchKeyboardPosX(value);
            SearchKeyboard.ApplyTransform();
        }

        public static void GetSearchKeyboardPosY()
        {
            searchKeyboardPosY = Config.SearchKeyboardPosY;
        }

        public static void SetSearchKeyboardPosY(float value)
        {
            searchKeyboardPosY = value;
            Config.UpdateSearchKeyboardPosY(value);
            SearchKeyboard.ApplyTransform();
        }

        public static void GetSearchKeyboardPosZ()
        {
            searchKeyboardPosZ = Config.SearchKeyboardPosZ;
        }

        public static void SetSearchKeyboardPosZ(float value)
        {
            searchKeyboardPosZ = value;
            Config.UpdateSearchKeyboardPosZ(value);
            SearchKeyboard.ApplyTransform();
        }

        public static void GetSearchKeyboardTilt()
        {
            searchKeyboardTilt = Config.SearchKeyboardTilt;
        }

        public static void SetSearchKeyboardTilt(float value)
        {
            searchKeyboardTilt = value;
            Config.UpdateSearchKeyboardTilt(value);
            SearchKeyboard.ApplyTransform();
        }

        public static readonly string[] ScoringTypeOptions = { "Audica Scoring", "EX Scoring" };

        public static int GetScoringTypeIndex() => Config.ExType ? 1 : 0;

        public static void SetScoringTypeIndex(int idx)
        {
            Config.SetScoringType(idx == 1);
        }

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

        public static void GetWrapSongList()
        {
            wrapSongList = Config.WrapSongList;
        }

        public static void SetWrapSongList(bool value)
        {
            wrapSongList = value;
            Config.UpdateWrapSongList(value);
            FolderRowManager.RefreshList();
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

        public static void GetSongCacheEnabled()
        {
            songCacheEnabled = Config.SongCacheEnabled;
        }

        public static void SetSongCacheEnabled(bool value)
        {
            songCacheEnabled = value;
            Config.UpdateSongCacheEnabled(value);
        }

        public static void GetSaveFailedRunData()
        {
            saveFailedRunData = Config.SaveFailedRunData;
        }

        public static void SetSaveFailedRunData(bool value)
        {
            saveFailedRunData = value;
            Config.UpdateSaveFailedRunData(value);
        }

        public static void GetEnableScoreUpload()
        {
            enableScoreUpload = Config.EnableScoreUpload;
        }

        public static void SetEnableScoreUpload(bool value)
        {
            enableScoreUpload = value;
            Config.UpdateEnableScoreUpload(value);
        }

        /// <summary>Whether an API key is currently set — drives the checkmark on the API Key row.</summary>
        public static bool HasApiKey()
        {
            return !string.IsNullOrEmpty(Config.ApiKey);
        }

        /// <summary>
        /// Reads the system clipboard and, if it holds non-blank text, saves it as the API key,
        /// replacing any existing one. Bound to the API Key row's "toggle" action, so the row
        /// doubles as both a status indicator (checked = key set) and a paste button.
        /// </summary>
        public static void PasteApiKeyFromClipboard()
        {
            string clipboard = GUIUtility.systemCopyBuffer;
            if (string.IsNullOrWhiteSpace(clipboard))
            {
                MelonLogger.Log("[ExScoring] Paste API key: clipboard is empty, ignoring");
                return;
            }

            Config.UpdateApiKey(clipboard.Trim());
            MelonLogger.Log("[ExScoring] API key updated from clipboard");
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

        public static void GetArrowJumpToEnds()
        {
            arrowJumpToEnds = Config.ArrowJumpToEnds;
        }

        public static void SetArrowJumpToEnds(bool value)
        {
            arrowJumpToEnds = value;
            Config.UpdateArrowJumpToEnds(value);
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

        public static void GetPurpleMenuEnabled()
        {
            purpleMenuEnabled = Config.PurpleMenuEnabled;
        }

        public static void SetPurpleMenuEnabled(bool value)
        {
            purpleMenuEnabled = value;
            Config.UpdatePurpleMenuEnabled(value);
        }

        public static void GetRandomSongScope()
        {
            randomSongScope = Config.RandomSongScope;
        }

        public static void SetRandomSongScope(int value)
        {
            randomSongScope = value;
            Config.UpdateRandomSongScope(value);
        }

        public static void GetShowStatsOnFail()
        {
            showStatsOnFail = Config.ShowStatsOnFail;
        }

        public static void SetShowStatsOnFail(bool value)
        {
            showStatsOnFail = value;
            Config.UpdateShowStatsOnFail(value);
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

        public static void GetChainArrowMinPitchDistance()
        {
            chainArrowMinPitchDistance = Config.ChainArrowMinPitchDistance;
        }

        public static void SetChainArrowMinPitchDistance(float value)
        {
            chainArrowMinPitchDistance = value;
            Config.UpdateChainArrowMinPitchDistance(value);
        }

        public static void GetChainArrowMaxSimultaneous()
        {
            chainArrowMaxSimultaneous = Config.ChainArrowMaxSimultaneous;
        }

        public static void SetChainArrowMaxSimultaneous(float value)
        {
            chainArrowMaxSimultaneous = value;
            Config.UpdateChainArrowMaxSimultaneous((int)value);
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

        public static void GetUnifyTargetSpeed()
        {
            unifyTargetSpeed = Config.UnifyTargetSpeed;
        }

        public static void SetUnifyTargetSpeed(bool value)
        {
            unifyTargetSpeed = value;
            Config.UpdateUnifyTargetSpeed(value);
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

        // Non-melee targets: KataConfig.GetSecondsLookahead picks one of
        // secondsLookaheadEasy/Normal/Hard/Expert based on the current difficulty, then divides by
        // a speed factor that is identical across difficulties. That speed factor cancels out, so
        // rescaling the already-computed result by (secondsLookaheadExpert / secondsLookaheadForCurrentDifficulty)
        // reproduces "as if this were Expert" without needing to reimplement the song-speed lookup.
        //
        // Melee targets always use secondsLookaheadMelee regardless of difficulty, but the base game
        // applies an extra x1.2 speed bonus on Hard/Expert only — dividing by that same 1.2 on
        // Easy/Normal brings melee targets in line with Hard/Expert speed too.
        //
        // Tutorial mode is intentionally left untouched.
        [HarmonyPatch(typeof(KataConfig), "GetSecondsLookahead", new Type[] { typeof(Target.TargetBehavior) })]
        private static class UnifyTargetSpeedPatch
        {
            private static void Postfix(KataConfig __instance, Target.TargetBehavior behavior, ref float __result)
            {
                if (!Config.UnifyTargetSpeed) return;
                if (__instance == null) return;
                if (__instance.GetSpecialGameMode() == KataConfig.SpecialGameMode.Tutorial) return;

                var difficulty = __instance.GetDifficulty();
                if (difficulty == KataConfig.Difficulty.Expert) return;

                if (behavior == Target.TargetBehavior.Melee)
                {
                    if (difficulty == KataConfig.Difficulty.Easy || difficulty == KataConfig.Difficulty.Normal)
                    {
                        __result /= 1.2f;
                    }
                    return;
                }

                float currentSeconds;
                switch (difficulty)
                {
                    case KataConfig.Difficulty.Easy: currentSeconds = __instance.secondsLookaheadEasy; break;
                    case KataConfig.Difficulty.Normal: currentSeconds = __instance.secondsLookaheadNormal; break;
                    case KataConfig.Difficulty.Hard: currentSeconds = __instance.secondsLookaheadHard; break;
                    default: return;
                }

                if (currentSeconds <= 0f) return;

                __result *= __instance.secondsLookaheadExpert / currentSeconds;
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

                // Arrow-shot (fixed magnitude 3) with "jump to ends" enabled: skip the normal
                // row-increment scroll entirely and snap straight to the real top or bottom of
                // the list. Sign convention matches the row-increment branch below (positive =
                // toward the bottom, negative = toward the top), so this only changes how far
                // an arrow shot moves, not which arrow moves which way.
                if (Config.ArrowJumpToEnds && Mathf.Approximately(Mathf.Abs(amount), 3f))
                {
                    if (amount > 0f)
                    {
                        // Canonical index of the last full screen of real rows — NOT
                        // float.MaxValue, which clamps to the physical max and (with Wrap Song
                        // List on) lands inside the wraparound ghost copy of the head instead
                        // of the list's true tail.
                        float lastScreen = Mathf.Max(0f, VirtualSongList.CurrentView.Count - VirtualSongList.Scroller.displayCount);
                        VirtualSongList.SetScroll(lastScreen);
                    }
                    else
                    {
                        VirtualSongList.SetScroll(0f);
                    }
                    return false;
                }

                VirtualSongList.MarkScrollDirtiedByInput();

                float effectiveAmount;
                if (Mathf.Approximately(Mathf.Abs(amount), 3f))
                {
                    effectiveAmount = Mathf.Sign(amount) * Config.ArrowScrollRows;
                }
                else
                {
                    effectiveAmount = amount * Config.ScrollSpeedMultiplier;
                }

                float newIndex = VirtualSongList.ResolveScrollIndex(__instance.mIndex + effectiveAmount);

                __instance.SnapTo(newIndex, true);
                __instance.UpdateScroll(-1);

                return false;
            }
        }

        /// <summary>
        /// GrabScroll.Update() drives grab-drag directly via ShellScrollable.SnapTo(index, force: false)
        /// every frame, bypassing Scroll() entirely. Native SnapTo clamps to [0, GetMaxScroll()] when
        /// force is false (force=true, used everywhere else in the mod, skips clamping — untouched here).
        /// This patch takes over that one native, non-forced call so wrapping also applies to
        /// grabbing/dragging the list, not just joystick/arrow scrolling; it also marks real input
        /// even when passing through to vanilla behavior (wrap off), so Teardown knows a genuine
        /// live scroll happened rather than trusting our own last recorded intent.
        /// </summary>
        [HarmonyPatch(typeof(ShellScrollable), "SnapTo", new Type[] { typeof(float), typeof(bool) })]
        private static class ShellScrollableSnapToWrapPatch
        {
            private static bool Prefix(ShellScrollable __instance, float index, bool force)
            {
                if (force) return true;
                if (VirtualSongList.Scroller == null || __instance.Pointer != VirtualSongList.Scroller.Pointer)
                    return true;

                // mGrabbed is set natively only while a real grab is in progress (see GrabScroll) —
                // a more reliable "this is genuine input" signal than "any non-forced SnapTo call",
                // since at least one native call also reaches this method outside of real input.
                if (__instance.mGrabbed) VirtualSongList.MarkScrollDirtiedByInput();

                if (VirtualSongList.WrapBufferSize <= 0) return true;

                float resolved = VirtualSongList.ResolveScrollIndex(index);

                __instance.mIndex = resolved;
                __instance.mDestinationIndex = resolved;

                return false;
            }
        }
    }
}