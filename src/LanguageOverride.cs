using System;
using Harmony;
using UnityEngine;
using UnhollowerBaseLib;      // TryCast
using UnhollowerRuntimeLib;   // Il2CppType (ML 0.5.7)

namespace ExScoringMod
{
    /// <summary>
    /// Options > Menu > Language: overrides the game's language, live and on every launch.
    ///
    /// Native Localizer.GetKataLanguage() works the language out on every call (a language
    /// command-line argument first, then sLanguageCode from DetermineLocale) and caches nothing, so a
    /// postfix returning the chosen language changes it everywhere at once, command line included.
    /// Loc.Localize(token, overrideLanguage, checkingFallback) couldn't be decompiled, so a prefix
    /// also replaces overrideLanguage == Unknown with the chosen language, whether or not it falls
    /// back to GetKataLanguage itself.
    ///
    /// Localizer components localize their TextMeshPro once, in Start (Localize(GetKataLanguage())),
    /// and Localizer.Localize(language) only sets the text (no font swap). <see cref="Apply"/> runs
    /// that again on every Localizer in the loaded scenes (inactive and DontDestroyOnLoad included,
    /// prefab assets skipped). Text built in code (mod panels, song list rows, the options page)
    /// picks the language up the next time it's rebuilt.
    ///
    /// Config.LanguageOverride: 0 = Game Default (no override), otherwise a Localizer.KataLanguage value.
    /// </summary>
    internal static class LanguageOverride
    {
        /// <summary>Cycle entries, in order. Index 0 = Game Default.</summary>
        private static readonly Localizer.KataLanguage[] Languages =
        {
            Localizer.KataLanguage.Unknown, // Game Default
            Localizer.KataLanguage.English,
            Localizer.KataLanguage.French,
            Localizer.KataLanguage.German,
            Localizer.KataLanguage.Italian,
            Localizer.KataLanguage.Spanish_LA,
            Localizer.KataLanguage.Dutch,
            Localizer.KataLanguage.Finnish,
            Localizer.KataLanguage.Norwegian,
            Localizer.KataLanguage.Polish,
            Localizer.KataLanguage.Portuguese_LA,
            Localizer.KataLanguage.Swedish,
            Localizer.KataLanguage.Japanese,
            Localizer.KataLanguage.Korean,
        };

        /// <summary>Names shown in the cycle, each language in its own language.</summary>
        public static readonly string[] OptionLabels =
        {
            "Game Default",
            "English",
            "Français",
            "Deutsch",
            "Italiano",
            "Español (LA)",
            "Nederlands",
            "Suomi",
            "Norsk",
            "Polski",
            "Português (LA)",
            "Svenska",
            "日本語",
            "한국어",
        };

        /// <summary>A saved Config value, made valid: 0 or one of the languages in the cycle.</summary>
        public static int ClampValue(int value)
        {
            for (int i = 1; i < Languages.Length; i++)
                if ((int)Languages[i] == value) return value;
            return 0;
        }

        /// <summary>Config value (KataLanguage, 0 = Game Default) -> cycle index.</summary>
        public static int ToIndex(int value)
        {
            for (int i = 1; i < Languages.Length; i++)
                if ((int)Languages[i] == value) return i;
            return 0;
        }

        /// <summary>Cycle index -> Config value (KataLanguage, 0 = Game Default).</summary>
        public static int FromIndex(int index)
        {
            if (index <= 0 || index >= Languages.Length) return 0;
            return (int)Languages[index];
        }

        private static bool HasOverride => Config.LanguageOverride != 0;
        private static Localizer.KataLanguage Chosen => (Localizer.KataLanguage)Config.LanguageOverride;

        /// <summary>
        /// Re-localizes every Localizer in the loaded scenes with the current language (the override,
        /// or the game's own language for Game Default). Called when the option changes.
        /// </summary>
        public static void Apply()
        {
            try
            {
                // Goes through the postfix below, so it's the override when one is set.
                Localizer.KataLanguage language = Localizer.GetKataLanguage();
                int count = 0;
                foreach (UnityEngine.Object o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<Localizer>()))
                {
                    Localizer localizer = o.TryCast<Localizer>();
                    if (localizer == null) continue;
                    if (!localizer.gameObject.scene.IsValid()) continue; // prefab asset, not in a scene
                    try
                    {
                        localizer.Localize(language);
                        count++;
                    }
                    catch (Exception ex)
                    {
                        DebugLog.Log($"[Language] couldn't localize {localizer.gameObject.name}: {ex.Message}");
                    }
                }
                DebugLog.Log($"[Language] now {language} ({(HasOverride ? "override" : "game default")}), {count} labels refreshed");
            }
            catch (Exception ex)
            {
                DebugLog.Log($"[Language] Apply failed: {ex}");
            }
        }

        [HarmonyPatch(typeof(Localizer), "GetKataLanguage")]
        private static class GetKataLanguagePatch
        {
            private static void Postfix(ref Localizer.KataLanguage __result)
            {
                if (HasOverride) __result = Chosen;
            }
        }

        [HarmonyPatch(typeof(Loc), "Localize", new Type[] { typeof(Loc.Token), typeof(Localizer.KataLanguage), typeof(bool) })]
        private static class LocLocalizePatch
        {
            private static void Prefix(ref Localizer.KataLanguage overrideLanguage)
            {
                if (HasOverride && overrideLanguage == Localizer.KataLanguage.Unknown)
                    overrideLanguage = Chosen;
            }
        }
    }
}