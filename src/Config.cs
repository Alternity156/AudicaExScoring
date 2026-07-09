using MelonLoader;

namespace ExScoringMod
{
    internal static class Config
    {
        public const string Category = "ExScoring";

        public static string typeHeader = "[Header]Scoring Type";
        public static bool AudicaType;
        public static bool ExType;

        public static string calculationHeader = "[Header]Calculation Type";
        public static bool AudicaCalculation;
        public static bool LinearCalculation;

        public static int LastSongCount;
        public static bool SafeSongListReload;

        public static bool DisableMineSounds;

        public static float TimingWindow;

        public static bool DisableTemporalAimAssist;
        public static bool ForceHitSounds;
        public static bool DisableGunBeamRedirection;

        public static string chainArrowHeader = "[Header]Chain Arrow";
        public static int ArrowColorMode;      // 0 = White, 1 = Hand Color
        public static int ChainLineColorMode;  // 0 = Default (Black), 1 = Hand Color
        public static float ArrowWidth;
        public static float ArrowLength;
        public static bool EnableChainArrow;

        public static string particleKillerHeader = "[Header]Particle Killer";
        public static bool ParticleKillerEnabled;
        public static bool ParticleKillerKillCPUParticles;
        public static int ParticleKillerParticleCount;

        public static string menuHeader = "[Header]Menu";
        public static bool DisableMenuGrab;
        public static bool TrippyMenuEnabled;
        public static bool PurpleMenuEnabled;
        public static float TrippyMenuSpeed;
        public static float ScrollSpeedMultiplier;
        public static float ArrowScrollRows;
        public static bool HideScoreData;
        public static bool FirstPlayBlind;
        public static bool PracticeModeMinimizeButtonEnabled;
        public static int RandomSongScope; // 0 = Folder Songs, 1 = All Songs
        public static bool ShowStatsOnFail;

        public static string dataHeader = "[Header]Data";
        public static bool EnableRunDataSaving;
        public static int MaxRunsPerSong;
        public static float MaxRunDataSizeMB;
        public static bool SaveFailedRunData;

        public static string scoringHeader = "[Header]Scoring";
        public static float ExScorePopupSize;
        public static float ExScorePopupOpacity;

        public static string searchKeyboardHeader = "[Header]Search Keyboard";
        public static float SearchKeyboardPosX;
        public static float SearchKeyboardPosY;
        public static float SearchKeyboardPosZ;
        public static float SearchKeyboardTilt;

        public static void RegisterConfig()
        {
            MelonPrefs.RegisterString(Category, nameof(typeHeader), "", "[Header]Scoring Type");
            MelonPrefs.RegisterBool(Category, nameof(AudicaType), true, "Enables Audica Style Scoring");
            MelonPrefs.RegisterBool(Category, nameof(ExType), false, "Enables EX Style Scoring");

            MelonPrefs.RegisterString(Category, nameof(calculationHeader), "", "[Header]Calculation Type");
            MelonPrefs.RegisterBool(Category, nameof(AudicaCalculation), true, "Enables Audica Style Calculation");
            MelonPrefs.RegisterBool(Category, nameof(LinearCalculation), false, "Enables Linear Calculation");

            MelonPrefs.RegisterInt(Category, nameof(LastSongCount), 0, "");
            LastSongCount = MelonPrefs.GetInt(Category, nameof(LastSongCount));

            MelonPrefs.RegisterBool(Category, nameof(SafeSongListReload), true, "Disables menu buttons while the song list is reloading");

            MelonPrefs.RegisterBool(Category, nameof(DisableMineSounds), false, "Disables mine sounds");

            MelonPrefs.RegisterFloat(Category, nameof(TimingWindow), 1f, "Sets the timing window [0,1,0.05,1] {P}");

            MelonPrefs.RegisterBool(Category, nameof(DisableTemporalAimAssist), false, "Disables temporal aim assist");
            MelonPrefs.RegisterBool(Category, nameof(ForceHitSounds), false, "Forces hit sounds on targets that have none");
            MelonPrefs.RegisterBool(Category, nameof(DisableGunBeamRedirection), true, "Disables the gun beam redirection visual, has no effect on scoring");

            MelonPrefs.RegisterString(Category, nameof(chainArrowHeader), "", "[Header]Chain Arrow");
            MelonPrefs.RegisterBool(Category, nameof(EnableChainArrow), true, "Shows a directional arrow on chain lines");
            MelonPrefs.RegisterInt(Category, nameof(ArrowColorMode), 0, "Chain arrow color: 0 = White, 1 = Hand Color");
            MelonPrefs.RegisterInt(Category, nameof(ChainLineColorMode), 0, "Chain line color: 0 = Default (Black), 1 = Hand Color");
            MelonPrefs.RegisterFloat(Category, nameof(ArrowWidth), 0.5f, "Sets the chain arrow width [0.1,1,0.05,0.5] {P}");
            MelonPrefs.RegisterFloat(Category, nameof(ArrowLength), 0.25f, "Sets the chain arrow length [0.05,1,0.05,0.25] {P}");

            MelonPrefs.RegisterString(Category, nameof(particleKillerHeader), "", "[Header]Particle Killer");
            MelonPrefs.RegisterBool(Category, nameof(ParticleKillerEnabled), true, "Enables the mod.");
            MelonPrefs.RegisterBool(Category, nameof(ParticleKillerKillCPUParticles), true, "Disables a small puff of particles.");
            MelonPrefs.RegisterInt(Category, nameof(ParticleKillerParticleCount), 0, "Amount of GPU particles per shot. [0,50000,1000,0] {G}");

            MelonPrefs.RegisterString(Category, nameof(menuHeader), "", "[Header]Menu");
            MelonPrefs.RegisterBool(Category, nameof(DisableMenuGrab), false, "Disables grabbing the song list scroller in menus");
            MelonPrefs.RegisterBool(Category, nameof(TrippyMenuEnabled), false, "Enables a psychedelic color cycle effect in menus");
            MelonPrefs.RegisterBool(Category, nameof(PurpleMenuEnabled), false, "Enables the purple peak-state stage visual in menus, for aesthetic purposes");
            MelonPrefs.RegisterFloat(Category, nameof(TrippyMenuSpeed), 1.0f, "Sets the trippy menu cycle speed [0.1,100,0.1,1]");
            MelonPrefs.RegisterFloat(Category, nameof(ScrollSpeedMultiplier), 1.0f, "Sets the song list joystick scroll speed multiplier [0.1,10,0.1,1]");
            MelonPrefs.RegisterFloat(Category, nameof(ArrowScrollRows), 3.0f, "Sets how many rows the song list scrolls per arrow shot [1,20,1,3]");
            MelonPrefs.RegisterBool(Category, nameof(HideScoreData), false, "Permanently hides target data, heatmap, and intensity graph on the launch panel");
            MelonPrefs.RegisterBool(Category, nameof(FirstPlayBlind), false, "Hides target data, heatmap, and intensity graph only for songs you've never played");
            MelonPrefs.RegisterBool(Category, nameof(PracticeModeMinimizeButtonEnabled), true, "Adds a button in practice mode to minimize its panel");
            MelonPrefs.RegisterInt(Category, nameof(RandomSongScope), 0, "Random Song source: 0 = Folder Songs, 1 = All Songs");
            MelonPrefs.RegisterBool(Category, nameof(ShowStatsOnFail), false, "Shows the stats screen after failing a song instead of the fail screen");

            MelonPrefs.RegisterString(Category, nameof(dataHeader), "", "[Header]Data");
            MelonPrefs.RegisterBool(Category, nameof(EnableRunDataSaving), false, "Saves raw scoring data for each run to disk, for external recalculation/analysis");
            MelonPrefs.RegisterInt(Category, nameof(MaxRunsPerSong), 10, "Sets how many run data files are kept per song, per difficulty [1,50,1,10] {G}");
            MelonPrefs.RegisterFloat(Category, nameof(MaxRunDataSizeMB), 100f, "Sets the max total disk space (MB) run data files can use [10,2000,10,100] {G}");
            MelonPrefs.RegisterBool(Category, nameof(SaveFailedRunData), false, "Also saves run data for failed songs (requires Save Run Data)");

            MelonPrefs.RegisterString(Category, nameof(scoringHeader), "", "[Header]Scoring");
            MelonPrefs.RegisterFloat(Category, nameof(ExScorePopupSize), 100f, "Sets the EX score popup size [10,100,5,100] {P}");
            MelonPrefs.RegisterFloat(Category, nameof(ExScorePopupOpacity), 100f, "Sets the EX score popup opacity [0,100,5,100] {P}");

            MelonPrefs.RegisterString(Category, nameof(searchKeyboardHeader), "", "[Header]Search Keyboard");
            MelonPrefs.RegisterFloat(Category, nameof(SearchKeyboardPosX), 0f, "Sets the search keyboard X position [-5,5,0.25,0] {P}");
            MelonPrefs.RegisterFloat(Category, nameof(SearchKeyboardPosY), 1.75f, "Sets the search keyboard Y position [0,5,0.25,1.75] {P}");
            MelonPrefs.RegisterFloat(Category, nameof(SearchKeyboardPosZ), 2f, "Sets the search keyboard Z position [-5,5,0.25,2] {P}");
            MelonPrefs.RegisterFloat(Category, nameof(SearchKeyboardTilt), 30f, "Sets the search keyboard tilt angle [0,90,1,30] {P}");

            OnModSettingsApplied();
        }

        public static void OnModSettingsApplied()
        {
            AudicaType = MelonPrefs.GetBool(Category, nameof(AudicaType));
            ExType = MelonPrefs.GetBool(Category, nameof(ExType));

            AudicaCalculation = MelonPrefs.GetBool(Category, nameof(AudicaCalculation));
            LinearCalculation = MelonPrefs.GetBool(Category, nameof(LinearCalculation));
            SafeSongListReload = MelonPrefs.GetBool(Category, nameof(SafeSongListReload));
            DisableMineSounds = MelonPrefs.GetBool(Category, nameof(DisableMineSounds));
            TimingWindow = MelonPrefs.GetFloat(Category, nameof(TimingWindow));
            DisableTemporalAimAssist = MelonPrefs.GetBool(Category, nameof(DisableTemporalAimAssist));
            ForceHitSounds = MelonPrefs.GetBool(Category, nameof(ForceHitSounds));
            DisableGunBeamRedirection = MelonPrefs.GetBool(Category, nameof(DisableGunBeamRedirection));
            ArrowColorMode = MelonPrefs.GetInt(Category, nameof(ArrowColorMode));
            ChainLineColorMode = MelonPrefs.GetInt(Category, nameof(ChainLineColorMode));
            ArrowWidth = MelonPrefs.GetFloat(Category, nameof(ArrowWidth));
            ArrowLength = MelonPrefs.GetFloat(Category, nameof(ArrowLength));
            EnableChainArrow = MelonPrefs.GetBool(Category, nameof(EnableChainArrow));
            ParticleKillerEnabled = MelonPrefs.GetBool(Category, nameof(ParticleKillerEnabled));
            ParticleKillerKillCPUParticles = MelonPrefs.GetBool(Category, nameof(ParticleKillerKillCPUParticles));
            ParticleKillerParticleCount = MelonPrefs.GetInt(Category, nameof(ParticleKillerParticleCount));
            DisableMenuGrab = MelonPrefs.GetBool(Category, nameof(DisableMenuGrab));
            TrippyMenuEnabled = MelonPrefs.GetBool(Category, nameof(TrippyMenuEnabled));
            PurpleMenuEnabled = MelonPrefs.GetBool(Category, nameof(PurpleMenuEnabled));
            TrippyMenuSpeed = MelonPrefs.GetFloat(Category, nameof(TrippyMenuSpeed));
            ScrollSpeedMultiplier = MelonPrefs.GetFloat(Category, nameof(ScrollSpeedMultiplier));
            ArrowScrollRows = MelonPrefs.GetFloat(Category, nameof(ArrowScrollRows));
            HideScoreData = MelonPrefs.GetBool(Category, nameof(HideScoreData));
            FirstPlayBlind = MelonPrefs.GetBool(Category, nameof(FirstPlayBlind));
            PracticeModeMinimizeButtonEnabled = MelonPrefs.GetBool(Category, nameof(PracticeModeMinimizeButtonEnabled));
            RandomSongScope = MelonPrefs.GetInt(Category, nameof(RandomSongScope));
            MaxRunsPerSong = MelonPrefs.GetInt(Category, nameof(MaxRunsPerSong));
            MaxRunDataSizeMB = MelonPrefs.GetFloat(Category, nameof(MaxRunDataSizeMB));
            EnableRunDataSaving = MelonPrefs.GetBool(Category, nameof(EnableRunDataSaving));
            ShowStatsOnFail = MelonPrefs.GetBool(Category, nameof(ShowStatsOnFail));
            SaveFailedRunData = MelonPrefs.GetBool(Category, nameof(SaveFailedRunData));
            ExScorePopupSize = MelonPrefs.GetFloat(Category, nameof(ExScorePopupSize));
            ExScorePopupOpacity = MelonPrefs.GetFloat(Category, nameof(ExScorePopupOpacity));
            SearchKeyboardPosX = MelonPrefs.GetFloat(Category, nameof(SearchKeyboardPosX));
            SearchKeyboardPosY = MelonPrefs.GetFloat(Category, nameof(SearchKeyboardPosY));
            SearchKeyboardPosZ = MelonPrefs.GetFloat(Category, nameof(SearchKeyboardPosZ));
            SearchKeyboardTilt = MelonPrefs.GetFloat(Category, nameof(SearchKeyboardTilt));
        }

        public static void UpdateExScorePopupSize(float value)
        {
            if (value < 10f) value = 10f;
            if (value > 100f) value = 100f;
            MelonPrefs.SetFloat(Category, nameof(ExScorePopupSize), value);
            ExScorePopupSize = value;
            MelonPrefs.SaveConfig();
        }

        public static void UpdateExScorePopupOpacity(float value)
        {
            if (value < 0f) value = 0f;
            if (value > 100f) value = 100f;
            MelonPrefs.SetFloat(Category, nameof(ExScorePopupOpacity), value);
            ExScorePopupOpacity = value;
            MelonPrefs.SaveConfig();
        }

        public static void SetScoringType(bool ex)
        {
            MelonPrefs.SetBool(Category, nameof(ExType), ex);
            MelonPrefs.SetBool(Category, nameof(AudicaType), !ex);
            ExType = ex;
            AudicaType = !ex;
            MelonPrefs.SaveConfig();
        }

        public static void UpdateHideScoreData(bool value)
        {
            MelonPrefs.SetBool(Category, nameof(HideScoreData), value);
            HideScoreData = value;
            MelonPrefs.SaveConfig();
        }

        public static void UpdateFirstPlayBlind(bool value)
        {
            MelonPrefs.SetBool(Category, nameof(FirstPlayBlind), value);
            FirstPlayBlind = value;
            MelonPrefs.SaveConfig();
        }

        public static void UpdatePracticeModeMinimizeButtonEnabled(bool value)
        {
            MelonPrefs.SetBool(Category, nameof(PracticeModeMinimizeButtonEnabled), value);
            PracticeModeMinimizeButtonEnabled = value;
            MelonPrefs.SaveConfig();
        }

        public static void UpdateRandomSongScope(int value)
        {
            MelonPrefs.SetInt(Category, nameof(RandomSongScope), value);
            RandomSongScope = value;
            MelonPrefs.SaveConfig();
        }

        public static void UpdateShowStatsOnFail(bool value)
        {
            MelonPrefs.SetBool(Category, nameof(ShowStatsOnFail), value);
            ShowStatsOnFail = value;
            MelonPrefs.SaveConfig();
        }

        public static void UpdateArrowScrollRows(float value)
        {
            if (value < 1f) value = 1f;
            if (value > 20f) value = 20f;
            MelonPrefs.SetFloat(Category, nameof(ArrowScrollRows), value);
            ArrowScrollRows = value;
            MelonPrefs.SaveConfig();
        }

        public static void UpdateScrollSpeedMultiplier(float value)
        {
            if (value < 0.1f) value = 0.1f;
            if (value > 10f) value = 10f;
            MelonPrefs.SetFloat(Category, nameof(ScrollSpeedMultiplier), value);
            ScrollSpeedMultiplier = value;
            MelonPrefs.SaveConfig();
        }

        public static void UpdateMaxRunsPerSong(int value)
        {
            if (value < 1) value = 1;
            if (value > 50) value = 50;
            MelonPrefs.SetInt(Category, nameof(MaxRunsPerSong), value);
            MaxRunsPerSong = value;
            MelonPrefs.SaveConfig();
        }

        public static void UpdateEnableRunDataSaving(bool value)
        {
            MelonPrefs.SetBool(Category, nameof(EnableRunDataSaving), value);
            EnableRunDataSaving = value;
            MelonPrefs.SaveConfig();
        }

        public static void UpdateSaveFailedRunData(bool value)
        {
            MelonPrefs.SetBool(Category, nameof(SaveFailedRunData), value);
            SaveFailedRunData = value;
            MelonPrefs.SaveConfig();
        }

        public static void UpdateMaxRunDataSizeMB(float value)
        {
            if (value < 10f) value = 10f;
            if (value > 2000f) value = 2000f;
            MelonPrefs.SetFloat(Category, nameof(MaxRunDataSizeMB), value);
            MaxRunDataSizeMB = value;
            MelonPrefs.SaveConfig();
        }

        public static void UpdateParticleKillerEnabled(bool value)
        {
            MelonPrefs.SetBool(Category, nameof(ParticleKillerEnabled), value);
            ParticleKillerEnabled = value;
            MelonPrefs.SaveConfig();
        }

        public static void UpdateParticleKillerKillCPUParticles(bool value)
        {
            MelonPrefs.SetBool(Category, nameof(ParticleKillerKillCPUParticles), value);
            ParticleKillerKillCPUParticles = value;
            MelonPrefs.SaveConfig();
        }

        public static void UpdateParticleKillerParticleCount(int value)
        {
            MelonPrefs.SetInt(Category, nameof(ParticleKillerParticleCount), value);
            ParticleKillerParticleCount = value;
            MelonPrefs.SaveConfig();
        }

        public static void UpdateDisableMenuGrab(bool value)
        {
            MelonPrefs.SetBool(Category, nameof(DisableMenuGrab), value);
            DisableMenuGrab = value;
            MelonPrefs.SaveConfig();
        }

        public static void UpdateTrippyMenuEnabled(bool value)
        {
            MelonPrefs.SetBool(Category, nameof(TrippyMenuEnabled), value);
            TrippyMenuEnabled = value;
            MelonPrefs.SaveConfig();
        }

        public static void UpdatePurpleMenuEnabled(bool value)
        {
            MelonPrefs.SetBool(Category, nameof(PurpleMenuEnabled), value);
            PurpleMenuEnabled = value;
            MelonPrefs.SaveConfig();
        }

        public static void UpdateTrippyMenuSpeed(float value)
        {
            if (value < 0.1f) value = 0.1f;
            if (value > 100f) value = 100f;
            MelonPrefs.SetFloat(Category, nameof(TrippyMenuSpeed), value);
            TrippyMenuSpeed = value;
            MelonPrefs.SaveConfig();
        }

        public static void UpdateEnableChainArrow(bool value)
        {
            MelonPrefs.SetBool(Category, nameof(EnableChainArrow), value);
            EnableChainArrow = value;
            MelonPrefs.SaveConfig();
        }

        public static void UpdateArrowColorMode(int value)
        {
            MelonPrefs.SetInt(Category, nameof(ArrowColorMode), value);
            ArrowColorMode = value;
            MelonPrefs.SaveConfig();
        }

        public static void UpdateChainLineColorMode(int value)
        {
            MelonPrefs.SetInt(Category, nameof(ChainLineColorMode), value);
            ChainLineColorMode = value;
            MelonPrefs.SaveConfig();
        }

        public static void UpdateArrowWidth(float value)
        {
            MelonPrefs.SetFloat(Category, nameof(ArrowWidth), value);
            ArrowWidth = value;
            MelonPrefs.SaveConfig();
        }

        public static void UpdateArrowLength(float value)
        {
            MelonPrefs.SetFloat(Category, nameof(ArrowLength), value);
            ArrowLength = value;
            MelonPrefs.SaveConfig();
        }

        public static void UpdateGunBeamRedirection(bool value)
        {
            MelonPrefs.SetBool(Category, nameof(DisableGunBeamRedirection), value);
            DisableGunBeamRedirection = value;
            MelonPrefs.SaveConfig();
        }

        public static void UpdateForceHitSounds(bool value)
        {
            MelonPrefs.SetBool(Category, nameof(ForceHitSounds), value);
            ForceHitSounds = value;
            MelonPrefs.SaveConfig();
        }

        public static void UpdateTemporalAimAssist(bool value)
        {
            MelonPrefs.SetBool(Category, nameof(DisableTemporalAimAssist), value);
            DisableTemporalAimAssist = value;
            MelonPrefs.SaveConfig();
        }

        public static void UpdateTimingWindow(float value)
        {
            if (value < 0.05f) value = 0.05f;
            if (value > 1f) value = 1f;
            MelonPrefs.SetFloat(Category, nameof(TimingWindow), value);
            TimingWindow = value;
            MelonPrefs.SaveConfig();
        }

        public static void UpdateMineSoundDisabler(bool value)
        {
            MelonPrefs.SetBool(Category, nameof(DisableMineSounds), value);
            DisableMineSounds = value;
            MelonPrefs.SaveConfig();
        }

        public static void UpdateSongCount(int newCount)
        {
            MelonPrefs.SetInt(Category, nameof(LastSongCount), newCount);
            LastSongCount = newCount;
            MelonPrefs.SaveConfig();
        }

        public static void UpdateSearchKeyboardPosX(float value)
        {
            if (value < -5f) value = -5f;
            if (value > 5f) value = 5f;
            MelonPrefs.SetFloat(Category, nameof(SearchKeyboardPosX), value);
            SearchKeyboardPosX = value;
            MelonPrefs.SaveConfig();
        }

        public static void UpdateSearchKeyboardPosY(float value)
        {
            if (value < 0f) value = 0f;
            if (value > 5f) value = 5f;
            MelonPrefs.SetFloat(Category, nameof(SearchKeyboardPosY), value);
            SearchKeyboardPosY = value;
            MelonPrefs.SaveConfig();
        }

        public static void UpdateSearchKeyboardPosZ(float value)
        {
            if (value < -5f) value = -5f;
            if (value > 5f) value = 5f;
            MelonPrefs.SetFloat(Category, nameof(SearchKeyboardPosZ), value);
            SearchKeyboardPosZ = value;
            MelonPrefs.SaveConfig();
        }

        public static void UpdateSearchKeyboardTilt(float value)
        {
            if (value < 0f) value = 0f;
            if (value > 90f) value = 90f;
            MelonPrefs.SetFloat(Category, nameof(SearchKeyboardTilt), value);
            SearchKeyboardTilt = value;
            MelonPrefs.SaveConfig();
        }

        public static void EnforceMutualExclusion(string category, string identifier)
        {
            if (category != Category) return;

            switch (identifier)
            {
                case nameof(AudicaType):
                    if (MelonPrefs.GetBool(Category, nameof(AudicaType)))
                        MelonPrefs.SetBool(Category, nameof(ExType), false);
                    break;
                case nameof(ExType):
                    if (MelonPrefs.GetBool(Category, nameof(ExType)))
                        MelonPrefs.SetBool(Category, nameof(AudicaType), false);
                    break;
                case nameof(AudicaCalculation):
                    if (MelonPrefs.GetBool(Category, nameof(AudicaCalculation)))
                        MelonPrefs.SetBool(Category, nameof(LinearCalculation), false);
                    break;
                case nameof(LinearCalculation):
                    if (MelonPrefs.GetBool(Category, nameof(LinearCalculation)))
                        MelonPrefs.SetBool(Category, nameof(AudicaCalculation), false);
                    break;
            }
        }
    }
}