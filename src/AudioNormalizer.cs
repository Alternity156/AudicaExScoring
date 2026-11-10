using System;
using Harmony;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// Song volume normalization (Config.NormalizeSongVolume): every song plays at the same
    /// loudness, from SongLoudness.TryGetGainDb (official table, cache or a measurement).
    ///
    /// Engine facts this relies on (confirmed in game 2026-11-10, see the project doc
    /// Audio-Normalization-Findings.md):
    ///  - Sound volume and gain are the same value: volume in dB = 20 * log10(gain).
    ///  - The engine ramps volume changes, so readings right after a write are still moving.
    ///  - The game never writes the gameplay handles' volume after starting them. The fallback
    ///    sustain track (song without sustain moggs) starts at -6 dB (Gun.StartSongTracks).
    ///  - SongPreview.Update and MenuMusic's Tutorial / Random Song track write their sound's gain
    ///    every frame through the 2-argument Sound.SetGain.
    ///
    /// Gameplay (AudioDriver.Update postfix): the main song, FX and both sustain tracks of
    /// SongDataHolder's song. A new handle is left alone for ObserveSeconds, its volume then
    /// becomes the base, and base + gain is written once, and again whenever the gain changes
    /// (measurement arrived, option or target changed). Never every frame.
    ///
    /// Preview and menu track (prefix on Sound.SetGain(uint, float)): the game's own per-frame
    /// gain is multiplied by the song's gain. The song is latched when a handle first shows up,
    /// so a preview fading out keeps its own level while the next song is already selected. If
    /// the song is still being measured, it plays at 0 dB and eases to its gain (EaseDbPerSecond)
    /// once the result arrives.
    ///
    /// Not touched: gun fire / crackle / spin SFX, the menu ambience and the environment music.
    /// The player's Music Level, per-song Music Level, Song Preview Volume and Menu Music Volume
    /// all still apply on top.
    /// </summary>
    internal static class AudioNormalizer
    {
        private const string Tag = "[Normalizer]";
        private const float ObserveSeconds = 0.2f;
        private const float MinBaseDb = -40f;
        private const float GainEpsilonDb = 0.01f;
        private const float EaseDbPerSecond = 20f;

        private static readonly Slot Main = new Slot("Main");
        private static readonly Slot Fx = new Slot("Fx");
        private static readonly Slot SustainR = new Slot("SustainR");
        private static readonly Slot SustainL = new Slot("SustainL");

        private static readonly EasedSound Preview = new EasedSound("Preview");
        private static readonly EasedSound MenuTrack = new EasedSound("MenuTrack");

        private static bool IsValidHandle(uint h)
        {
            if (h == 0 || h == Hmx.Audio.Sound.kInvalidSoundHandle) return false;
            try { return Hmx.Audio.Sound.IsValid(h); }
            catch { return false; }
        }

        private static float DbToLinear(float db) => (float)Math.Pow(10.0, db / 20.0);

        // ── Gameplay sounds ──

        private sealed class Slot
        {
            private readonly string name;
            private uint handle;
            private float seenTime;
            private bool applied;
            private bool skipped;
            private float baseDb;
            private float appliedGainDb;

            public Slot(string name) { this.name = name; }

            public void Tick(uint h, float gainDb)
            {
                try
                {
                    if (!IsValidHandle(h))
                    {
                        handle = 0;
                        return;
                    }

                    if (h != handle)
                    {
                        handle = h;
                        seenTime = Time.unscaledTime;
                        applied = false;
                        skipped = false;
                        return;
                    }

                    if (skipped) return;

                    if (!applied)
                    {
                        if (Time.unscaledTime - seenTime < ObserveSeconds) return;
                        baseDb = Hmx.Audio.Sound.GetVolume(h);
                        if (baseDb < MinBaseDb)
                        {
                            skipped = true;
                            DebugLog.Log($"{Tag} {name}: volume {baseDb:0.00} dB, below {MinBaseDb} dB, left alone");
                            return;
                        }
                        applied = true;
                        Write(gainDb);
                        return;
                    }

                    if (Mathf.Abs(gainDb - appliedGainDb) > GainEpsilonDb) Write(gainDb);
                }
                catch (Exception ex)
                {
                    DebugLog.Log($"{Tag} {name}: failed: {ex.Message}");
                    handle = 0;
                }
            }

            private void Write(float gainDb)
            {
                appliedGainDb = gainDb;
                Hmx.Audio.Sound.SetVolume(handle, baseDb + gainDb);
                DebugLog.Log($"{Tag} {name}: {baseDb + gainDb:0.00} dB (base {baseDb:0.00}, gain {gainDb:+0.00;-0.00;0.00})");
            }
        }

        private static float CurrentSongGainDb()
        {
            SongDataHolder holder = SongDataHolder.I;
            if (holder == null) return 0f;
            float gain;
            SongLoudness.TryGetGainDb(holder.songData, out gain);
            return gain;
        }

        [HarmonyPatch(typeof(AudioDriver), "Update")]
        private static class AudioDriverUpdatePatch
        {
            private static void Postfix(AudioDriver __instance)
            {
                try
                {
                    float gain = CurrentSongGainDb();
                    Main.Tick(AudioDriver.sSongHandle, gain);
                    if (__instance != null) Fx.Tick(__instance.mFxSongHandle, gain);

                    uint right = 0, left = 0;
                    KataConfig config = KataConfig.I;
                    if (config != null && config.guns != null)
                    {
                        for (int i = 0; i < config.guns.Length; i++)
                        {
                            Gun gun = config.guns[i];
                            if (gun == null) continue;
                            if (gun.hand == Target.TargetHandType.Right) right = gun.mSustainFusionHandle;
                            else left = gun.mSustainFusionHandle;
                        }
                    }
                    SustainR.Tick(right, gain);
                    SustainL.Tick(left, gain);
                }
                catch (Exception ex)
                {
                    DebugLog.Log($"{Tag} AudioDriver.Update postfix failed: {ex.Message}");
                }
            }
        }

        // ── Preview and menu track ──

        /// <summary>A sound whose gain the game writes every frame; ours is eased in on top.</summary>
        private sealed class EasedSound
        {
            private readonly string name;
            private uint handle;
            private SongList.SongData song;
            private float currentDb;
            private float lastTime;
            private bool known;

            public EasedSound(string name) { this.name = name; }

            public float Factor(uint h, SongList.SongData currentSong)
            {
                float now = Time.unscaledTime;
                float target;
                if (h != handle)
                {
                    handle = h;
                    song = currentSong;
                    known = SongLoudness.TryGetGainDb(song, out target);
                    currentDb = target; // 0 dB while it's being measured
                    lastTime = now;
                    DebugLog.Log($"{Tag} {name} {(song != null ? song.songID : "?")}: " +
                                 (known ? $"gain {target:+0.00;-0.00;0.00} dB" : "measuring, 0 dB for now"));
                    return DbToLinear(currentDb);
                }

                bool nowKnown = SongLoudness.TryGetGainDb(song, out target);
                if (nowKnown && !known)
                    DebugLog.Log($"{Tag} {name} {(song != null ? song.songID : "?")}: measured, easing to {target:+0.00;-0.00;0.00} dB");
                known = nowKnown;
                if (!known) target = currentDb;

                float dt = Mathf.Max(0f, now - lastTime);
                lastTime = now;
                currentDb = Mathf.MoveTowards(currentDb, target, EaseDbPerSecond * dt);
                return DbToLinear(currentDb);
            }
        }

        [HarmonyPatch(typeof(Hmx.Audio.Sound), "SetGain", new Type[] { typeof(uint), typeof(float) })]
        private static class SetGainPatch
        {
            private static bool loggedError;

            private static void Prefix(uint h, ref float gain)
            {
                try
                {
                    if (h == 0 || h == Hmx.Audio.Sound.kInvalidSoundHandle) return;

                    SongPreview preview = SongPreview.I;
                    if (preview != null && h == preview.mSoundHandle)
                    {
                        gain *= Preview.Factor(h, preview.mCurrentSong);
                        return;
                    }

                    if (h == MenuMusic.MenuTrackHandle)
                        gain *= MenuTrack.Factor(h, MenuMusic.MenuTrackSong);
                }
                catch (Exception ex)
                {
                    if (loggedError) return;
                    loggedError = true;
                    DebugLog.Log($"{Tag} preview / menu track gain failed (logged once): {ex.Message}");
                }
            }
        }
    }
}