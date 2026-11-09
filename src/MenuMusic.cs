using System;
using Harmony;
using UnityEngine;

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
    /// Tutorial: the tutorial song (songID "tutorial") is played as its own sound, set up the way
    /// SongPreview sets up a preview (PlayArgs with an initial volume of -96 dB, modifyAtStart, the
    /// "event:/shell/song_preview" event, KataConfig's HmxAudioEmitter), at the menu preview gain
    /// (SongPreview.gain). Its 140 s audio is exactly the tutorial's loop (TutorialFlow loops
    /// measures 0..56 at 96 BPM) and the audio engine loops it natively in the tutorial. If it ever
    /// ends here instead, it's restarted at once and from then on sought back to 0 just before the
    /// end. It plays everywhere in the menus (song list included) and crossfades with SongPreview:
    /// while a preview fades in, plays or fades out, the tutorial is lowered by the preview's mFade.
    /// While it's selected, the environment music is kept at gain 0. It stops during songs, on the
    /// title screen and in the credits, and starts/stops with a 0.7 s fade as soon as the option
    /// changes.
    ///
    /// Config.MenuMusic: 0 = Environment (native), 1..5 = arena 1..5, 6 = Tutorial.
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

        public const int TutorialOption = 6;
        public const int OptionCount = 7;

        public static int Clamp(int value)
        {
            if (value < 0) return 0;
            if (value >= OptionCount) return OptionCount - 1;
            return value;
        }

        public static bool TutorialSelected => Clamp(Config.MenuMusic) == TutorialOption;

        /// <summary>
        /// Labels for the options cycle: "Environment", the localized environment names, then the
        /// localized "Tutorial", in the player's current language. Built each time the options page opens.
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

            string tutorial = null;
            try { tutorial = Loc.Localize(Loc.Token.MainMenu_Tutorial, language); }
            catch (Exception ex) { DebugLog.Log($"[MenuMusic] Localize MainMenu_Tutorial failed: {ex.Message}"); }
            labels[TutorialOption] = string.IsNullOrEmpty(tutorial) ? "Tutorial" : tutorial;
            return labels;
        }

        /// <summary>The event the chosen option forces, or null for "Environment" and "Tutorial".</summary>
        private static string GetOverrideEvent()
        {
            int choice = Clamp(Config.MenuMusic);
            if (choice == 0 || choice == TutorialOption) return null;
            return Events[choice - 1];
        }

        /// <summary>
        /// Applies a changed setting at once: restarts the environment music with the event for the
        /// new setting (with Tutorial it's restarted too and then held at gain 0). The tutorial track
        /// starts or fades out on the next AudioAmbience.Update.
        /// </summary>
        public static void Apply()
        {
            try
            {
                AudioAmbience ambience = AudioAmbience.I;
                if (ambience == null) return;
                if (!IsMenuState()) return;

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

        // Native MenuState value of the credits (from Ghidra), which has its own music.
        private const int NativeCreditsState = 9;

        /// <summary>True in the menus: not in a song, on the title screen or in the credits.</summary>
        private static bool IsMenuState()
        {
            MenuState.State state = MenuState.GetState();
            if (state == MenuState.State.Launching ||
                state == MenuState.State.Launched ||
                state == MenuState.State.TitleScreen)
                return false;
            return (int)state != NativeCreditsState;
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

        [HarmonyPatch(typeof(AudioAmbience), "Update")]
        private static class AudioAmbienceUpdatePatch
        {
            private static void Postfix(AudioAmbience __instance)
            {
                TutorialTrack.Update(__instance);
            }
        }

        /// <summary>The looping tutorial song played as menu music.</summary>
        private static class TutorialTrack
        {
            private const string SongId = "tutorial";
            private const string PreviewEvent = "event:/shell/song_preview";
            private const float FadeSeconds = 0.7f;   // same fade time as SongPreview
            private const float InitialVolumeDb = -96f; // what SongPreview passes to PlayArgs

            private static uint handle;
            private static bool playing;
            private static float fade;            // own fade in/out, 0..1
            private static bool seekLoop;         // engine didn't loop it: seek back before the end
            private static float lastTimelineMs = -1f;
            private static bool loggedNativeLoop;
            private static bool loggedMissingSong;

            public static void Update(AudioAmbience ambience)
            {
                try
                {
                    bool wanted = TutorialSelected && IsMenuState();
                    float dt = Time.unscaledDeltaTime;

                    if (!wanted && !playing) return;

                    // Leaving the menus (song launching, title screen, credits): stop right away.
                    if (!wanted && !IsMenuState())
                    {
                        Stop("left the menus");
                        return;
                    }

                    if (wanted && !playing && !Start())
                        return;

                    // A sound that has ended: the engine didn't loop it. Restart and switch to seeking.
                    if (!IsAlive(handle))
                    {
                        DebugLog.Log("[MenuMusic] tutorial track ended instead of looping; restarting and seeking back before the end from now on");
                        seekLoop = true;
                        StopSound();
                        if (!Start(keepFade: true)) return;
                    }

                    fade = Mathf.MoveTowards(fade, wanted ? 1f : 0f, dt / FadeSeconds);
                    if (!wanted && fade <= 0f)
                    {
                        Stop("deselected");
                        return;
                    }

                    LoopCheck(dt);

                    SongPreview preview = SongPreview.I;
                    float baseGain = preview != null ? preview.gain : 0.4f;
                    float duck = 1f - PreviewLevel(preview);
                    Hmx.Audio.Sound.SetGain(handle, baseGain * fade * duck);

                    // Keep the environment music silent while the tutorial is the selected music.
                    if (wanted && ambience != null && ambience.mShellMusicHandle != 0)
                        Hmx.Audio.Sound.SetGain(ambience.mShellMusicHandle, 0f);
                }
                catch (Exception ex)
                {
                    DebugLog.Log($"[MenuMusic] tutorial track update failed: {ex}");
                    StopSound();
                }
            }

            /// <summary>How much of SongPreview is audible right now (0 = nothing, 1 = full).</summary>
            private static float PreviewLevel(SongPreview preview)
            {
                if (preview == null) return 0f;
                switch (preview.mState)
                {
                    case SongPreview.State.FadingIn:
                    case SongPreview.State.Playing:
                    case SongPreview.State.FadingOut:
                        return Mathf.Clamp01(preview.mFade);
                    default:
                        return 0f;
                }
            }

            /// <summary>
            /// Watches the playback position: logs once when the engine loops natively, and, after
            /// the engine has once let it end, seeks back to 0 when less than ~1.5 frames remain.
            /// </summary>
            private static void LoopCheck(float dt)
            {
                float timeline = Hmx.Audio.Sound.GetTimelineMs(handle);
                if (lastTimelineMs >= 0f && timeline + 1000f < lastTimelineMs && !loggedNativeLoop)
                {
                    loggedNativeLoop = true;
                    DebugLog.Log($"[MenuMusic] tutorial track looped natively ({lastTimelineMs:N0} -> {timeline:N0} ms)");
                }
                lastTimelineMs = timeline;

                if (!seekLoop) return;
                float length = Hmx.Audio.Sound.GetLengthMs(handle);
                if (length > 0f && length - timeline < dt * 1500f)
                {
                    Hmx.Audio.Sound.SeekToMs(handle, 0f);
                    lastTimelineMs = 0f;
                }
            }

            /// <summary>Same test SongPreview.Update uses to decide its sound is still going.</summary>
            private static bool IsAlive(uint h)
            {
                if (h == Hmx.Audio.Sound.kInvalidSoundHandle) return false;
                uint state = (uint)Hmx.Audio.Sound.GetTransportState(h);
                return (state & 0xFFFFFFFAu) != 0 || state == 1;
            }

            private static bool Start(bool keepFade = false)
            {
                SongList.SongData song = SongList.I != null ? SongList.I.GetSong(SongId) : null;
                if (song == null || string.IsNullOrEmpty(song.moggSong))
                {
                    if (!loggedMissingSong)
                    {
                        loggedMissingSong = true;
                        DebugLog.Log("[MenuMusic] tutorial song not available yet");
                    }
                    return false;
                }

                KataConfig config = KataConfig.I;
                HmxAudioEmitter emitterComponent = config != null ? config.GetComponent<HmxAudioEmitter>() : null;
                Hmx.Audio.UAudioEmitterCom emitter = emitterComponent != null ? emitterComponent.Get() : null;

                Hmx.Audio.PlayArgs args = new Hmx.Audio.PlayArgs();
                args.volumeSettings.volume = InitialVolumeDb;
                args.volumeSettings.modifyAtStart = true;

                handle = KataUtil.PlaySound(args, song.moggSong, emitter, PreviewEvent);
                if (handle == Hmx.Audio.Sound.kInvalidSoundHandle)
                {
                    DebugLog.Log($"[MenuMusic] PlaySound failed for {song.moggSong}");
                    return false;
                }

                Hmx.Audio.Sound.SetGain(handle, 0f);
                playing = true;
                if (!keepFade) fade = 0f;
                lastTimelineMs = -1f;
                DebugLog.Log($"[MenuMusic] tutorial track started (handle {handle})");
                return true;
            }

            private static void StopSound()
            {
                if (handle != 0 && handle != Hmx.Audio.Sound.kInvalidSoundHandle)
                {
                    try { Hmx.Audio.Sound.Stop(handle); }
                    catch (Exception ex) { DebugLog.Log($"[MenuMusic] stopping tutorial track failed: {ex.Message}"); }
                }
                handle = Hmx.Audio.Sound.kInvalidSoundHandle;
                playing = false;
            }

            private static void Stop(string reason)
            {
                StopSound();
                fade = 0f;
                lastTimelineMs = -1f;
                DebugLog.Log($"[MenuMusic] tutorial track stopped ({reason})");
            }
        }
    }
}