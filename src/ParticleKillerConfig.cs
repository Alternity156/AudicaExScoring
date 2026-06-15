using MelonLoader;

namespace ExScoringMod
{
    internal class ParticleKillerConfig
    {
        public const string Category = "ParticleKiller";

        public static bool Enabled;
        public static bool KillCPUParticles;
        public static int ParticleCount;

        public static void RegisterConfig()
        {
            MelonPrefs.RegisterBool(Category, nameof(Enabled), true, "Enables the mod.");

            MelonPrefs.RegisterBool(Category, nameof(KillCPUParticles), true, "Disables a small puff of particles.");

            MelonPrefs.RegisterInt(Category, nameof(ParticleCount), 0, "Amount of GPU particles per shot. [0,50000,1000,0] {G}");

            OnModSettingsApplied();
        }

        public static void OnModSettingsApplied()
        {
            Enabled = MelonPrefs.GetBool(Category, nameof(Enabled));
            KillCPUParticles = MelonPrefs.GetBool(Category, nameof(KillCPUParticles));
            ParticleCount = MelonPrefs.GetInt(Category, nameof(ParticleCount));
        }
    }
}
