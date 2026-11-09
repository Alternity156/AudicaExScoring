using System;
using Harmony;

namespace ExScoringMod
{
    /// <summary>
    /// Lets the player pick the menu music (shell music) independently of the environment.
    ///
    /// Native AudioAmbience.GetShellMusicLoopEvent picks one of five FMOD events from
    /// SceneLoader.I.mCurrentEnvironmentName (StartsWith "environment2".."environment5", anything
    /// else = arena 1). OnEnvironmentChanged stops mShellMusicHandle and plays that event again, so a
    /// postfix on GetShellMusicLoopEvent is enough for the choice to survive environment switches.
    /// Only the shell music is swapped: the ambient loop (GetAmbientLoopEvent) stays tied to the
    /// environment. Song previews still replace the music, since AudioAmbience mutes whatever
    /// mShellMusicHandle points to while SongPreview plays.
    ///
    /// Config.MenuMusic: 0 = Environment (native), 1..5 = arena 1..5.
    /// </summary>
    internal static class MenuMusic
    {
        private static readonly string[] Events =
        {
            "event:/arena/arena_1/arena_1_music",
            "event:/arena/arena_2/arena_2_music",
            "event:/arena/arena_3/arena_3_music",
            "event:/arena/arena_4/arena_4_music",
            "event:/arena/arena_5/arena_5_music",
        };

        private static readonly Loc.Token[] NameTokens =
        {
            Loc.Token.Asset_Environment1,
            Loc.Token.Asset_Environment2,
            Loc.Token.Asset_Environment3,
            Loc.Token.Asset_Environment4,
            Loc.Token.Asset_Environment5,
        };

        public const int OptionCount = 6;

        public static int Clamp(int value)
        {
            if (value < 0) return 0;
            if (value >= OptionCount) return OptionCount - 1;
            return value;
        }

        /// <summary>
        /// Labels for the options cycle: "Environment", then the localized environment names in the
        /// player's current language. Built each time the options page opens.
        /// </summary>
        public static string[] GetOptionLabels()
        {
            string[] labels = new string[OptionCount];
            labels[0] = "Environment";
            Localizer.KataLanguage language = Localizer.KataLanguage.Unknown;
            try { language = Localizer.GetKataLanguage(); }
            catch (Exception ex) { DebugLog.Log($"[MenuMusic] GetKataLanguage failed: {ex.Message}"); }

            for (int i = 0; i < NameTokens.Length; i++)
            {
                string name = null;
                try { name = Loc.Localize(NameTokens[i], language); }
                catch (Exception ex) { DebugLog.Log($"[MenuMusic] Localize {NameTokens[i]} failed: {ex.Message}"); }
                labels[i + 1] = string.IsNullOrEmpty(name) ? "Arena " + (i + 1) : name;
            }
            return labels;
        }

        /// <summary>The event the chosen option forces, or null for "Environment".</summary>
        private static string GetOverrideEvent()
        {
            int choice = Clamp(Config.MenuMusic);
            return choice == 0 ? null : Events[choice - 1];
        }

        /// <summary>
        /// Restarts the menu music with the event for the current setting. Called when the option
        /// changes; only acts in the menus, while the shell music is playing.
        /// </summary>
        public static void Apply()
        {
            try
            {
                AudioAmbience ambience = AudioAmbience.I;
                if (ambience == null) return;

                MenuState.State state = MenuState.GetState();
                if (state == MenuState.State.Launching ||
                    state == MenuState.State.Launched ||
                    state == MenuState.State.TitleScreen)
                    return;

                if (ambience.mShellMusicHandle == 0) return; // shell music isn't running

                // Goes through the postfix below, so it already reflects the new setting.
                string eventName = ambience.GetShellMusicLoopEvent();
                if (string.IsNullOrEmpty(eventName)) return;

                Hmx.Audio.Sound.Stop(ambience.mShellMusicHandle);
                ambience.mShellMusicHandle = KataUtil.PlayFMODEvent(eventName, null);
                DebugLog.Log($"[MenuMusic] now playing {eventName} (handle {ambience.mShellMusicHandle})");
            }
            catch (Exception ex)
            {
                DebugLog.Log($"[MenuMusic] Apply failed: {ex}");
            }
        }

        [HarmonyPatch(typeof(AudioAmbience), "GetShellMusicLoopEvent")]
        private static class GetShellMusicLoopEventPatch
        {
            private static void Postfix(ref string __result)
            {
                try
                {
                    string forced = GetOverrideEvent();
                    if (forced != null) __result = forced;
                }
                catch (Exception ex)
                {
                    DebugLog.Log($"[MenuMusic] GetShellMusicLoopEvent postfix failed: {ex.Message}");
                }
            }
        }
    }
}