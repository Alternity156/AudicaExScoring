using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader.TinyJSON;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// Per-song setting offsets (UserData/ExScoring_PerSongSettings.json), keyed by songID.
    ///
    /// Every numeric setting is a signed offset added on top of the player's global value for the
    /// duration of the song: global Input Offset 3 + song offset -1 = 2 while that song plays, then
    /// back to 3. Force Hit Sounds is a toggle, so it is a 3-state override instead (Use Global / On
    /// / Off).
    ///
    /// Applying writes FloatPref.mVal DIRECTLY and never calls Set()/Adjust(), so a per-song value
    /// can never be persisted to disk as the player's global setting — even if the game crashes
    /// mid-song, the saved globals are untouched. Same pattern PlaylistEndlessManager already uses
    /// for its music fade.
    ///
    /// Lifecycle (driven from Hooks.cs):
    ///  - Apply: MenuState -> Launching (normal launch, practice, marathon's first song), and the
    ///    InGameUI.Restart prefix (plain restarts, and the endless/marathon "next song" which is a
    ///    restart with a new SongDataHolder song). Applied before the song loads, so it doesn't
    ///    matter whether the game reads a value live or caches it at load.
    ///  - Restore: MenuState leaves Launching/Launched for anything else.
    ///
    /// Global values are captured once on the first Apply and reused for every later Apply until
    /// Restore, so back-to-back songs (endless) never stack offsets on top of each other.
    /// </summary>
    internal static class PerSongSettings
    {
        public enum Field
        {
            InputOffset,
            MusicLevel,
            SfxLevel,
            TargetSpeed,
            MeleeSpeed,
            DartSpeed,
            DartPreGlow,
        }

        /// <summary>Force Hit Sounds override values stored in PerSongEntry.forceHitSounds.</summary>
        public const int ForceHitSoundsGlobal = 0;
        public const int ForceHitSoundsOn = 1;
        public const int ForceHitSoundsOff = 2;

        [Serializable]
        public class PerSongEntry
        {
            public string songID;

            // Raw units (same as the global sliders).
            public float inputOffsetMs;
            public float musicLevel;
            public float sfxLevel;

            // Percent points (+10 = +10%), applied as /100 to the multiplier prefs. Stored as whole
            // percents rather than fractions so repeated slider steps never accumulate float drift.
            public float targetSpeedPercent;
            public float meleeSpeedPercent;
            public float dartSpeedPercent;
            public float dartPreGlowPercent;

            public int forceHitSounds; // ForceHitSoundsGlobal / On / Off
        }

        [Serializable]
        public class Store
        {
            public List<PerSongEntry> songs = new List<PerSongEntry>();
        }

        private static Store store;
        private static readonly Dictionary<string, PerSongEntry> bySong = new Dictionary<string, PerSongEntry>();

        private static readonly string path =
            Path.Combine(Application.dataPath, "..", "UserData", "ExScoring_PerSongSettings.json");

        // ── Applied state ──────────────────────────────────────────────────────

        private static bool applied = false;
        private static string appliedSongID = null;
        private static int appliedForceHitSounds = ForceHitSoundsGlobal;
        private static float appliedMusicLevel;

        private static float baseInputOffset;
        private static float baseMusicLevel;
        private static float baseSfxLevel;
        private static float baseTargetSpeed;
        private static float baseMeleeSpeed;
        private static float baseDartSpeed;
        private static float baseDartPreGlow;

        public static bool IsApplied => applied;
        public static string AppliedSongID => appliedSongID;

        /// <summary>
        /// What the hit-sound forcing in OptionsMenuFunctions should use: the current song's
        /// override while one is applied, otherwise the global Config value.
        /// </summary>
        public static bool EffectiveForceHitSounds
        {
            get
            {
                if (applied)
                {
                    if (appliedForceHitSounds == ForceHitSoundsOn) return true;
                    if (appliedForceHitSounds == ForceHitSoundsOff) return false;
                }
                return Config.ForceHitSounds;
            }
        }

        /// <summary>
        /// The music level the current song should play at. Used by PlaylistEndlessManager when it
        /// un-fades the music for the next marathon song (that apply skips music so the fade-out
        /// isn't undone early). Falls back to the given value when nothing is applied.
        /// </summary>
        public static float GetEffectiveMusicLevel(float fallback)
        {
            return applied ? appliedMusicLevel : fallback;
        }

        // ── Storage ────────────────────────────────────────────────────────────

        private static void EnsureLoaded()
        {
            if (store != null) return;
            store = new Store();
            try
            {
                if (File.Exists(path))
                    store = JSON.Load(File.ReadAllText(path)).Make<Store>() ?? new Store();
            }
            catch (Exception e)
            {
                DebugLog.Log($"[PerSongSettings] Load failed, starting empty: {e.Message}");
                try
                {
                    // Keep the unreadable file around instead of overwriting it on the next save.
                    string backup = path + "." + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".bak";
                    File.Copy(path, backup, true);
                }
                catch { }
                store = new Store();
            }

            if (store.songs == null) store.songs = new List<PerSongEntry>();

            bySong.Clear();
            foreach (var entry in store.songs)
            {
                if (entry == null || string.IsNullOrEmpty(entry.songID)) continue;
                bySong[entry.songID] = entry;
            }
        }

        private static void Save()
        {
            try
            {
                // Rebuild from the lookup so removed/blank entries are dropped.
                store.songs = new List<PerSongEntry>(bySong.Values);

                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, JSON.Dump(store));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception e)
            {
                DebugLog.Log($"[PerSongSettings] Save failed: {e.Message}");
            }
        }

        private static PerSongEntry GetEntry(string songID)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(songID)) return null;
            bySong.TryGetValue(songID, out PerSongEntry entry);
            return entry;
        }

        private static bool IsDefault(PerSongEntry e)
        {
            return e.inputOffsetMs == 0f && e.musicLevel == 0f && e.sfxLevel == 0f &&
                   e.targetSpeedPercent == 0f && e.meleeSpeedPercent == 0f &&
                   e.dartSpeedPercent == 0f && e.dartPreGlowPercent == 0f &&
                   e.forceHitSounds == ForceHitSoundsGlobal;
        }

        /// <summary>True if the song has at least one non-default per-song setting.</summary>
        public static bool HasCustomSettings(string songID)
        {
            var e = GetEntry(songID);
            return e != null && !IsDefault(e);
        }

        public static float GetOffset(string songID, Field field)
        {
            var e = GetEntry(songID);
            if (e == null) return 0f;
            switch (field)
            {
                case Field.InputOffset: return e.inputOffsetMs;
                case Field.MusicLevel: return e.musicLevel;
                case Field.SfxLevel: return e.sfxLevel;
                case Field.TargetSpeed: return e.targetSpeedPercent;
                case Field.MeleeSpeed: return e.meleeSpeedPercent;
                case Field.DartSpeed: return e.dartSpeedPercent;
                case Field.DartPreGlow: return e.dartPreGlowPercent;
            }
            return 0f;
        }

        public static void SetOffset(string songID, Field field, float value)
        {
            if (string.IsNullOrEmpty(songID)) return;
            EnsureLoaded();

            value = Mathf.Round(value); // every field steps in whole units / whole percents

            if (!bySong.TryGetValue(songID, out PerSongEntry e))
            {
                if (value == 0f) return; // nothing to store
                e = new PerSongEntry { songID = songID };
                bySong[songID] = e;
            }

            switch (field)
            {
                case Field.InputOffset: e.inputOffsetMs = value; break;
                case Field.MusicLevel: e.musicLevel = value; break;
                case Field.SfxLevel: e.sfxLevel = value; break;
                case Field.TargetSpeed: e.targetSpeedPercent = value; break;
                case Field.MeleeSpeed: e.meleeSpeedPercent = value; break;
                case Field.DartSpeed: e.dartSpeedPercent = value; break;
                case Field.DartPreGlow: e.dartPreGlowPercent = value; break;
            }

            if (IsDefault(e)) bySong.Remove(songID);
            Save();
        }

        public static int GetForceHitSoundsMode(string songID)
        {
            var e = GetEntry(songID);
            return e != null ? e.forceHitSounds : ForceHitSoundsGlobal;
        }

        public static void SetForceHitSoundsMode(string songID, int mode)
        {
            if (string.IsNullOrEmpty(songID)) return;
            EnsureLoaded();

            if (mode < ForceHitSoundsGlobal || mode > ForceHitSoundsOff) mode = ForceHitSoundsGlobal;

            if (!bySong.TryGetValue(songID, out PerSongEntry e))
            {
                if (mode == ForceHitSoundsGlobal) return;
                e = new PerSongEntry { songID = songID };
                bySong[songID] = e;
            }

            e.forceHitSounds = mode;
            if (IsDefault(e)) bySong.Remove(songID);
            Save();
        }

        public static void ResetSong(string songID)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(songID)) return;
            if (bySong.Remove(songID)) Save();
        }

        // ── Global values / results (for the options page display) ─────────────

        private static PlayerPreferences.FloatPref GetPref(Field field)
        {
            var prefs = PlayerPreferences.I;
            if (prefs == null) return null;
            switch (field)
            {
                case Field.InputOffset: return prefs.InputOffsetMs;
                case Field.MusicLevel: return prefs.MusicLevel;
                case Field.SfxLevel: return prefs.SfxLevel;
                case Field.TargetSpeed: return prefs.TargetSpeedMultiplier;
                case Field.MeleeSpeed: return prefs.MeleeSpeedMultiplier;
                case Field.DartSpeed: return prefs.DartSpeedMultiplier;
                case Field.DartPreGlow: return prefs.DartPreGlowAmount;
            }
            return null;
        }

        public static bool IsPercentField(Field field)
        {
            return field == Field.TargetSpeed || field == Field.MeleeSpeed ||
                   field == Field.DartSpeed || field == Field.DartPreGlow;
        }

        /// <summary>The player's global value, in the pref's own units (multipliers stay 1.5 etc.).</summary>
        public static float GetGlobalValue(Field field)
        {
            if (applied)
            {
                switch (field)
                {
                    case Field.InputOffset: return baseInputOffset;
                    case Field.MusicLevel: return baseMusicLevel;
                    case Field.SfxLevel: return baseSfxLevel;
                    case Field.TargetSpeed: return baseTargetSpeed;
                    case Field.MeleeSpeed: return baseMeleeSpeed;
                    case Field.DartSpeed: return baseDartSpeed;
                    case Field.DartPreGlow: return baseDartPreGlow;
                }
            }
            var pref = GetPref(field);
            return pref != null ? pref.mVal : 0f;
        }

        /// <summary>Global + this song's offset, clamped to the game's range, in the pref's units.</summary>
        public static float GetResultValue(string songID, Field field)
        {
            float offset = GetOffset(songID, field);
            if (IsPercentField(field)) offset /= 100f;
            return Clamp(GetPref(field), GetGlobalValue(field) + offset);
        }

        /// <summary>Clamps to the FloatPref's own min/max (skipped if the pref doesn't define a range).</summary>
        private static float Clamp(PlayerPreferences.FloatPref pref, float value)
        {
            if (pref == null) return value;
            float min = pref.mMin, max = pref.mMax;
            if (max > min) value = Mathf.Clamp(value, min, max);
            return value;
        }

        // ── Apply / restore ────────────────────────────────────────────────────

        /// <summary>Applies the offsets of the song currently in SongDataHolder (the launching song).</summary>
        public static void ApplyForCurrentSong(bool skipMusic = false)
        {
            string songID = null;
            try
            {
                var holder = SongDataHolder.I;
                songID = holder != null && holder.songData != null ? holder.songData.songID : null;
            }
            catch (Exception e)
            {
                DebugLog.Log($"[PerSongSettings] Couldn't read the launching song: {e.Message}");
            }
            Apply(songID, skipMusic);
        }

        /// <summary>
        /// Applies a song's offsets on top of the global values. skipMusic computes the music
        /// level but leaves MusicLevel.mVal alone (endless keeps the music faded out while loading
        /// the next song, then sets GetEffectiveMusicLevel itself).
        /// </summary>
        public static void Apply(string songID, bool skipMusic = false)
        {
            var prefs = PlayerPreferences.I;
            if (prefs == null) return;

            try
            {
                if (!applied)
                {
                    baseInputOffset = prefs.InputOffsetMs.mVal;
                    baseMusicLevel = prefs.MusicLevel.mVal;
                    baseSfxLevel = prefs.SfxLevel.mVal;
                    baseTargetSpeed = prefs.TargetSpeedMultiplier.mVal;
                    baseMeleeSpeed = prefs.MeleeSpeedMultiplier.mVal;
                    baseDartSpeed = prefs.DartSpeedMultiplier.mVal;
                    baseDartPreGlow = prefs.DartPreGlowAmount.mVal;
                    applied = true;
                }

                appliedSongID = songID;
                PerSongEntry e = GetEntry(songID);

                prefs.InputOffsetMs.mVal = Clamp(prefs.InputOffsetMs, baseInputOffset + (e != null ? e.inputOffsetMs : 0f));
                prefs.SfxLevel.mVal = Clamp(prefs.SfxLevel, baseSfxLevel + (e != null ? e.sfxLevel : 0f));
                prefs.TargetSpeedMultiplier.mVal = Clamp(prefs.TargetSpeedMultiplier, baseTargetSpeed + (e != null ? e.targetSpeedPercent / 100f : 0f));
                prefs.MeleeSpeedMultiplier.mVal = Clamp(prefs.MeleeSpeedMultiplier, baseMeleeSpeed + (e != null ? e.meleeSpeedPercent / 100f : 0f));
                prefs.DartSpeedMultiplier.mVal = Clamp(prefs.DartSpeedMultiplier, baseDartSpeed + (e != null ? e.dartSpeedPercent / 100f : 0f));
                prefs.DartPreGlowAmount.mVal = Clamp(prefs.DartPreGlowAmount, baseDartPreGlow + (e != null ? e.dartPreGlowPercent / 100f : 0f));

                appliedMusicLevel = Clamp(prefs.MusicLevel, baseMusicLevel + (e != null ? e.musicLevel : 0f));
                if (!skipMusic) prefs.MusicLevel.mVal = appliedMusicLevel;

                prefs.UpdateAudioLevels();

                appliedForceHitSounds = e != null ? e.forceHitSounds : ForceHitSoundsGlobal;

                if (e != null)
                {
                    DebugLog.Log($"[PerSongSettings] Applied for {songID}: input {prefs.InputOffsetMs.mVal}, " +
                                 $"music {appliedMusicLevel}{(skipMusic ? " (deferred)" : "")}, sfx {prefs.SfxLevel.mVal}, " +
                                 $"target {prefs.TargetSpeedMultiplier.mVal:0.##}, melee {prefs.MeleeSpeedMultiplier.mVal:0.##}, " +
                                 $"dart {prefs.DartSpeedMultiplier.mVal:0.##}, preglow {prefs.DartPreGlowAmount.mVal:0.##}, " +
                                 $"hitsounds {appliedForceHitSounds}");
                }
                else
                {
                    DebugLog.Log($"[PerSongSettings] No per-song settings for {songID ?? "NULL"}, using globals.");
                }
            }
            catch (Exception ex)
            {
                DebugLog.Log($"[PerSongSettings] Apply failed: {ex}");
            }
        }

        /// <summary>Puts the captured global values back. No-op if nothing is applied.</summary>
        public static void Restore()
        {
            if (!applied) return;

            var prefs = PlayerPreferences.I;
            applied = false;
            appliedSongID = null;
            appliedForceHitSounds = ForceHitSoundsGlobal;
            PlaylistEndlessManager.VolumeFadedOut = false;

            if (prefs == null) return;

            try
            {
                prefs.InputOffsetMs.mVal = baseInputOffset;
                prefs.MusicLevel.mVal = baseMusicLevel;
                prefs.SfxLevel.mVal = baseSfxLevel;
                prefs.TargetSpeedMultiplier.mVal = baseTargetSpeed;
                prefs.MeleeSpeedMultiplier.mVal = baseMeleeSpeed;
                prefs.DartSpeedMultiplier.mVal = baseDartSpeed;
                prefs.DartPreGlowAmount.mVal = baseDartPreGlow;
                prefs.UpdateAudioLevels();

                DebugLog.Log("[PerSongSettings] Restored global values.");
            }
            catch (Exception ex)
            {
                DebugLog.Log($"[PerSongSettings] Restore failed: {ex}");
            }
        }
    }
}
