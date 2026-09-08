using System;
using System.Collections.Generic;
using System.Reflection;
using Harmony;
using MelonLoader;
using static MelonLoader.MelonPrefs;

namespace ExScoringMod
{
    /// <summary>
    /// Hides ExScoring's own settings category ("ExScoring") from the "Mod Settings" mod's menu.
    /// ExScoring has its own in-game options menu, so also listing its raw MelonPreferences
    /// entries in Mod Settings is redundant and confusing. ExScoringPlaylists is intentionally
    /// left visible.
    ///
    /// Mod Settings builds its category button list in UI.AddCategories() by iterating
    /// MelonPreferences.Categories directly, with no opt-out hook of its own. Permanently
    /// removing our category from that list would break Config.cs, since every MelonPrefs
    /// Get/Set call re-resolves the category by identifier via MelonPreferences.GetCategory().
    /// Instead we remove it only for the duration of that one synchronous call (Prefix) and
    /// restore it immediately after (Postfix) — Mod Settings never sees it, but nothing else
    /// (saving, loading, our own WatchPrefs coroutine, etc.) is affected, since AddCategories()
    /// has no yields and nothing else can run between the Prefix and Postfix.
    ///
    /// Uses the legacy Harmony (0Harmony 1.x) API — HarmonyInstance / HarmonyMethod — matching
    /// the rest of ExScoring; HarmonyLib (HarmonyX) is not available in this build. Reflection is
    /// used to find Mod Settings' UI type since ExScoring doesn't reference ModSettings.dll,
    /// matching the approach used in SongRequestBlocker.
    /// </summary>
    internal static class ModSettingsBlocker
    {
        private static bool applied;
        private static readonly List<MelonPreferences_Category> hidden = new List<MelonPreferences_Category>();

        public static void Apply()
        {
            if (applied) return;
            applied = true;

            Type ui = FindType("UI");
            if (ui == null)
            {
                MelonLogger.Log("[ModSettingsBlocker] Mod Settings UI type not found; nothing to patch.");
                return;
            }

            MethodInfo target = ui.GetMethod("AddCategories",
                BindingFlags.Static | BindingFlags.NonPublic,
                null, Type.EmptyTypes, null);

            if (target == null)
            {
                MelonLogger.Log("[ModSettingsBlocker] UI.AddCategories not found; nothing to patch.");
                return;
            }

            try
            {
                HarmonyInstance harmony = HarmonyInstance.Create("ExScoring.ModSettingsBlocker");
                HarmonyMethod prefix = new HarmonyMethod(typeof(ModSettingsBlocker)
                    .GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
                HarmonyMethod postfix = new HarmonyMethod(typeof(ModSettingsBlocker)
                    .GetMethod(nameof(Postfix), BindingFlags.Static | BindingFlags.NonPublic));

                harmony.Patch(target, prefix, postfix);
                MelonLogger.Log("[ModSettingsBlocker] ExScoring category hidden from Mod Settings.");
            }
            catch (Exception e)
            {
                MelonLogger.Log($"[ModSettingsBlocker] Failed to patch UI.AddCategories: {e.Message}");
            }
        }

        // Runs immediately before Mod Settings' UI.AddCategories(). Temporarily pulls our
        // category out of the global list so it isn't listed.
        private static void Prefix()
        {
            hidden.Clear();
            MelonPreferences_Category category = MelonPreferences.GetCategory(Config.Category);
            if (category != null)
            {
                hidden.Add(category);
                MelonPreferences.Categories.Remove(category);
            }
        }

        // Runs immediately after UI.AddCategories() returns. Puts the category straight back
        // so saving/loading and our own settings reads/writes are unaffected.
        private static void Postfix()
        {
            foreach (MelonPreferences_Category category in hidden)
            {
                if (!MelonPreferences.Categories.Contains(category))
                    MelonPreferences.Categories.Add(category);
            }
            hidden.Clear();
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = asm.GetType(fullName); }
                catch { /* ignore assemblies that can't be queried */ }
                if (t != null) return t;
            }
            return null;
        }
    }
}