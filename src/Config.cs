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

        public static bool DisableMineSounds = false;

        public static float TimingWindow;

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