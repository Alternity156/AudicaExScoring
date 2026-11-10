using System;
using System.Collections.Generic;
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
    /// Tutorial and Random Song play a song as their own sound (<see cref="MenuTrack"/>), set up the
    /// way SongPreview sets up a preview (PlayArgs with an initial volume of -96 dB, modifyAtStart,
    /// the "event:/shell/song_preview" event, KataConfig's HmxAudioEmitter), at the menu preview
    /// gain (SongPreview.gain). They play everywhere in the menus (song list included) and
    /// crossfade with SongPreview: while a preview fades in, plays or fades out, the track is
    /// lowered by the preview's mFade. While one of them is selected, the environment music is kept
    /// at gain 0. They stop during songs, on the title screen and in the credits, and start/stop
    /// with a 0.7 s fade as soon as the option changes.
    ///  - Tutorial: the tutorial song's 140 s audio is exactly the tutorial's loop (TutorialFlow loops
    ///    measures 0..56 at 96 BPM) and the audio engine loops it natively in the tutorial. If it ever
    ///    ends here instead, it's restarted at once and from then on sought back to 0 just before the end.
    ///  - Random Song: a random installed song (not hidden, not the tutorial, none of the last 10)
    ///    from its beginning; 0.7 s before its end it fades out and the next one fades in. A new song
    ///    is picked each time the menus are entered. Shown by <see cref="NowPlayingLabel"/>.
    ///
    /// Menu Music Volume (0..100 %, relative to the game's Music Level) scales the Tutorial/Random
    /// track's gain and the environment music's gain. Song previews aren't affected. With Normalize
    /// Song Volume on, AudioNormalizer also scales the Tutorial/Random track to its song's loudness
    /// (through MenuTrackHandle / MenuTrackSong).
    /// Sound volume and gain are the same value (volume in dB = 20 * log10(gain), confirmed in game
    /// 2026-11-10), so the environment music can't get a separate volume: native AudioAmbience.Update
    /// sets its gain every frame (preview ducking), and that call is scaled instead (ShellGainPatch).
    ///
    /// Config.MenuMusic: 0 = Environment (native), 1..5 = arena 1..5, 6 = Tutorial, 7 = Random Song.
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
        public const int RandomOption = 7;
        public const int OptionCount = 8;

        public const string TutorialSongId = "tutorial";

        public static int Clamp(int value)
        {
            if (value < 0) return 0;
            if (value >= OptionCount) return OptionCount - 1;
            return value;
        }

        public static bool TutorialSelected => Clamp(Config.MenuMusic) == TutorialOption;
        public static bool RandomSelected => Clamp(Config.MenuMusic) == RandomOption;

        /// <summary>
        /// Next Song button (<see cref="NowPlayingLabel"/>): fades the current random song out and
        /// starts the next one, the same way a song reaching its end does.
        /// </summary>
        public static void NextRandomSong() => MenuTrack.Skip();

        /// <summary>
        /// The Tutorial / Random Song track's sound and song (kInvalidSoundHandle / null when none),
        /// for AudioNormalizer, which scales that track's per-frame gain to the song's loudness.
        /// </summary>
        internal static uint MenuTrackHandle => MenuTrack.Handle;
        internal static SongList.SongData MenuTrackSong => MenuTrack.Song;

        /// <summary>Menu Music Volume as a 0..1 factor.</summary>
        private static float VolumeFactor => Mathf.Clamp01(Config.MenuMusicVolume / 100f);

        /// <summary>
        /// Labels for the options cycle: "Environment", the localized environment names, the
        /// localized "Tutorial", then "Random Song", in the player's current language. Built each
        /// time the options page opens.
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
            labels[RandomOption] = "Random Song";
            return labels;
        }

        /// <summary>The event the chosen option forces, or null for "Environment", "Tutorial" and "Random Song".</summary>
        private static string GetOverrideEvent()
        {
            int choice = Clamp(Config.MenuMusic);
            if (choice == 0 || choice >= TutorialOption) return null;
            return Events[choice - 1];
        }

        /// <summary>
        /// Applies a changed setting at once: restarts the environment music with the event for the
        /// new setting (with Tutorial/Random Song it's restarted too and then held at gain 0). The
        /// menu track starts, switches or fades out on the next AudioAmbience.Update.
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
                MenuTrack.Update(__instance);
            }
        }

        /// <summary>
        /// Menu Music Volume for the environment music. Every frame in the menus, native
        /// AudioAmbience.Update calls Sound.SetGain(mShellMusicHandle, 0 or 1, 1 s fade): 0 while
        /// SongPreview fades in, plays or fades out, 1 otherwise. AudioAmbience.Update is the only
        /// caller of this 3-argument overload, and it's a real call (not inlined), so scaling its gain
        /// here keeps the ducking and its fade exactly as native, at the chosen level. Takes effect
        /// on the next frame when the option changes. With Tutorial/Random Song selected,
        /// MenuTrack.Update (AudioAmbience.Update postfix, so after this call) still forces gain 0.
        /// </summary>
        [HarmonyPatch(typeof(Hmx.Audio.Sound), "SetGain", new Type[] { typeof(uint), typeof(float), typeof(float) })]
        private static class ShellGainPatch
        {
            private static bool loggedError;

            private static void Prefix(uint h, ref float gain)
            {
                try
                {
                    AudioAmbience ambience = AudioAmbience.I;
                    if (ambience == null || h != ambience.mShellMusicHandle) return;
                    gain *= VolumeFactor;
                }
                catch (Exception ex)
                {
                    if (loggedError) return;
                    loggedError = true;
                    DebugLog.Log($"[MenuMusic] environment music gain scaling failed (logged once): {ex.Message}");
                }
            }
        }

        /// <summary>The Tutorial / Random Song track played as menu music.</summary>
        private static class MenuTrack
        {
            private enum Mode { None, Tutorial, Random }

            private const string PreviewEvent = "event:/shell/song_preview";
            private const float FadeSeconds = 0.7f;     // same fade time as SongPreview
            private const float InitialVolumeDb = -96f; // what SongPreview passes to PlayArgs
            private const int RandomHistorySize = 10;
            private const int RandomStartAttempts = 5;

            private static readonly System.Random rand = new System.Random();
            private static readonly List<string> recentRandom = new List<string>();

            private static uint handle = Hmx.Audio.Sound.kInvalidSoundHandle;
            private static Mode mode = Mode.None;          // what's playing (None = nothing)
            private static SongList.SongData song;         // the song being played
            private static float fade;                     // own fade in/out, 0..1
            private static bool advancing;                 // Random: fading out to go to the next song
            private static bool tutorialSeekLoop;          // engine didn't loop the tutorial: seek back before the end
            private static float lastTimelineMs = -1f;
            private static bool loggedNativeLoop;
            private static bool loggedNoSongs;

            public static uint Handle => handle;
            public static SongList.SongData Song => song;

            /// <summary>Random Song: move on to the next song (fade out, then the next one fades in).</summary>
            public static void Skip()
            {
                if (mode != Mode.Random || advancing) return;
                advancing = true;
                DebugLog.Log($"[MenuMusic] next song requested (skipping {(song != null ? song.songID : "?")})");
            }

            private static Mode WantedMode()
            {
                if (!IsMenuState()) return Mode.None;
                if (TutorialSelected) return Mode.Tutorial;
                if (RandomSelected) return Mode.Random;
                return Mode.None;
            }

            public static void Update(AudioAmbience ambience)
            {
                SongPreview preview = SongPreview.I;
                try
                {
                    Mode wanted = WantedMode();
                    float dt = Time.unscaledDeltaTime;

                    if (mode == Mode.None && wanted == Mode.None)
                    {
                        NowPlayingLabel.Hide();
                        return;
                    }

                    // Leaving the menus (song launching, title screen, credits): stop right away.
                    if (mode != Mode.None && !IsMenuState())
                    {
                        Stop("left the menus");
                        NowPlayingLabel.Hide();
                        return;
                    }

                    if (mode == Mode.None && !Start(wanted))
                    {
                        NowPlayingLabel.Hide();
                        return;
                    }

                    if (mode == Mode.Random) RandomEndCheck();
                    else TutorialLoopCheck(dt);

                    // Fade toward 1 while this track is wanted and not moving on, toward 0 otherwise.
                    bool keep = wanted == mode && !advancing;
                    fade = Mathf.MoveTowards(fade, keep ? 1f : 0f, dt / FadeSeconds);

                    if (!keep && (fade <= 0f || !IsAlive(handle)))
                    {
                        Mode next = wanted;
                        bool wasAdvancing = advancing;
                        Stop(wasAdvancing ? "next random song" : "deselected");
                        if (next != Mode.None && !Start(next))
                        {
                            NowPlayingLabel.Hide();
                            return;
                        }
                        if (mode == Mode.None)
                        {
                            NowPlayingLabel.Hide();
                            return;
                        }
                    }

                    float baseGain = preview != null ? preview.gain : 0.4f;
                    float previewLevel = PreviewLevel(preview);
                    Hmx.Audio.Sound.SetGain(handle, baseGain * VolumeFactor * fade * (1f - previewLevel));

                    // Keep the environment music silent while a menu track is the selected music.
                    if (wanted != Mode.None && ambience != null && ambience.mShellMusicHandle != 0)
                        Hmx.Audio.Sound.SetGain(ambience.mShellMusicHandle, 0f);

                    if (mode == Mode.Random && wanted == Mode.Random && previewLevel <= 0f)
                        NowPlayingLabel.Show(song);
                    else
                        NowPlayingLabel.Hide();
                }
                catch (Exception ex)
                {
                    DebugLog.Log($"[MenuMusic] menu track update failed: {ex}");
                    StopSound();
                    NowPlayingLabel.Hide();
                }
            }

            // ── Song end / loop handling ──

            /// <summary>
            /// Random: starts fading out 0.7 s before the end. A sound that already ended or wrapped
            /// back to the start (engine looping it) also counts as the end.
            /// </summary>
            private static void RandomEndCheck()
            {
                if (advancing) return;
                if (!IsAlive(handle))
                {
                    advancing = true;
                    return;
                }

                float timeline = Hmx.Audio.Sound.GetTimelineMs(handle);
                bool wrapped = lastTimelineMs >= 0f && timeline + 1000f < lastTimelineMs;
                lastTimelineMs = timeline;

                float length = Hmx.Audio.Sound.GetLengthMs(handle);
                if (wrapped || (length > 0f && length - timeline <= FadeSeconds * 1000f))
                    advancing = true;
            }

            /// <summary>
            /// Tutorial: logs once when the engine loops it natively; if it ever ends instead, restarts
            /// it and from then on seeks back to 0 when less than ~1.5 frames remain.
            /// </summary>
            private static void TutorialLoopCheck(float dt)
            {
                if (!IsAlive(handle))
                {
                    DebugLog.Log("[MenuMusic] tutorial track ended instead of looping; restarting and seeking back before the end from now on");
                    tutorialSeekLoop = true;
                    float keepFade = fade;
                    StopSound();
                    if (Start(Mode.Tutorial)) fade = keepFade;
                    return;
                }

                float timeline = Hmx.Audio.Sound.GetTimelineMs(handle);
                if (lastTimelineMs >= 0f && timeline + 1000f < lastTimelineMs && !loggedNativeLoop)
                {
                    loggedNativeLoop = true;
                    DebugLog.Log($"[MenuMusic] tutorial track looped natively ({lastTimelineMs:N0} -> {timeline:N0} ms)");
                }
                lastTimelineMs = timeline;

                if (!tutorialSeekLoop) return;
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

            // ── Starting / stopping ──

            private static bool Start(Mode wanted)
            {
                if (wanted == Mode.Tutorial)
                {
                    SongList.SongData tutorial = SongList.I != null ? SongList.I.GetSong(TutorialSongId) : null;
                    return tutorial != null && Play(tutorial, Mode.Tutorial);
                }

                if (wanted == Mode.Random)
                {
                    for (int attempt = 0; attempt < RandomStartAttempts; attempt++)
                    {
                        SongList.SongData pick = PickRandomSong();
                        if (pick == null) return false;
                        if (Play(pick, Mode.Random))
                        {
                            DebugLog.Log($"[MenuMusic] random song: {pick.songID} ({pick.title})");
                            return true;
                        }
                    }
                }
                return false;
            }

            /// <summary>A random installed song: not hidden, not the tutorial, none of the last 10.</summary>
            private static SongList.SongData PickRandomSong()
            {
                List<SongList.SongData> pool = new List<SongList.SongData>();
                List<SongList.SongData> fresh = new List<SongList.SongData>();
                if (SongList.I != null && SongList.I.songs != null)
                {
                    for (int i = 0; i < SongList.I.songs.Count; i++)
                    {
                        SongList.SongData s = SongList.I.songs[i];
                        if (s == null || s.hidden || s.songID == TutorialSongId || string.IsNullOrEmpty(s.moggSong))
                            continue;
                        pool.Add(s);
                        if (!recentRandom.Contains(s.songID)) fresh.Add(s);
                    }
                }

                if (pool.Count == 0)
                {
                    if (!loggedNoSongs)
                    {
                        loggedNoSongs = true;
                        DebugLog.Log("[MenuMusic] no songs available for Random Song");
                    }
                    return null;
                }

                // Every song played recently: start the history over, still avoiding the last one.
                if (fresh.Count == 0)
                {
                    string last = recentRandom.Count > 0 ? recentRandom[recentRandom.Count - 1] : null;
                    recentRandom.Clear();
                    for (int i = 0; i < pool.Count; i++)
                        if (pool.Count == 1 || pool[i].songID != last) fresh.Add(pool[i]);
                }

                SongList.SongData pick = fresh[rand.Next(fresh.Count)];
                recentRandom.Add(pick.songID);
                if (recentRandom.Count > RandomHistorySize) recentRandom.RemoveAt(0);
                return pick;
            }

            private static bool Play(SongList.SongData data, Mode newMode)
            {
                if (data == null || string.IsNullOrEmpty(data.moggSong)) return false;

                KataConfig config = KataConfig.I;
                HmxAudioEmitter emitterComponent = config != null ? config.GetComponent<HmxAudioEmitter>() : null;
                Hmx.Audio.UAudioEmitterCom emitter = emitterComponent != null ? emitterComponent.Get() : null;

                Hmx.Audio.PlayArgs args = new Hmx.Audio.PlayArgs();
                args.volumeSettings.volume = InitialVolumeDb;
                args.volumeSettings.modifyAtStart = true;

                uint h = KataUtil.PlaySound(args, data.moggSong, emitter, PreviewEvent);
                if (h == Hmx.Audio.Sound.kInvalidSoundHandle)
                {
                    DebugLog.Log($"[MenuMusic] PlaySound failed for {data.songID} ({data.moggSong})");
                    return false;
                }

                Hmx.Audio.Sound.SetGain(h, 0f);
                handle = h;
                mode = newMode;
                song = data;
                fade = 0f;
                advancing = false;
                lastTimelineMs = -1f;
                DebugLog.Log($"[MenuMusic] {newMode} track started: {data.songID} (handle {h})");
                return true;
            }

            private static void StopSound()
            {
                if (handle != Hmx.Audio.Sound.kInvalidSoundHandle && handle != 0)
                {
                    try { Hmx.Audio.Sound.Stop(handle); }
                    catch (Exception ex) { DebugLog.Log($"[MenuMusic] stopping menu track failed: {ex.Message}"); }
                }
                handle = Hmx.Audio.Sound.kInvalidSoundHandle;
                mode = Mode.None;
                song = null;
                advancing = false;
            }

            private static void Stop(string reason)
            {
                if (mode != Mode.None) DebugLog.Log($"[MenuMusic] {mode} track stopped ({reason})");
                StopSound();
                fade = 0f;
                lastTimelineMs = -1f;
            }
        }
    }
}