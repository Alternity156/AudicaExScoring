using MelonLoader;

namespace ExScoringMod
{
    internal static class PlaylistConfig
    {
        public const string Category = "ExScoringPlaylists";

        public static bool Shuffle;
        public static bool ShowScores;
        public static bool NoFail;
        public static bool ResetHealth;

        public static void RegisterConfig()
        {
            MelonPrefs.RegisterBool(Category, nameof(Shuffle), false, "Shuffle songs in marathon");
            Shuffle = MelonPrefs.GetBool(Category, nameof(Shuffle));

            MelonPrefs.RegisterBool(Category, nameof(ShowScores), false, "Show scores between songs");
            ShowScores = MelonPrefs.GetBool(Category, nameof(ShowScores));

            MelonPrefs.RegisterBool(Category, nameof(NoFail), false, "NoFail during marathon");
            NoFail = MelonPrefs.GetBool(Category, nameof(NoFail));

            MelonPrefs.RegisterBool(Category, nameof(ResetHealth), false, "Reset health between songs");
            ResetHealth = MelonPrefs.GetBool(Category, nameof(ResetHealth));
        }
    }
}