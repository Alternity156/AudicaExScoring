using System;
using MelonLoader;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// The watched player's target sounds and sustain sound in the spectator window, heard as if
    /// this player stood where they stand: every sound is placed relative to this player's head
    /// the way its source sits relative to the watched player's head.
    ///
    /// Found in game 2026-11-14 (UnityExplorer tests and the [SoundProbe] hook):
    ///  - A target's sound is a note (the cue's pitch and velocity) on the song's target drum kit,
    ///    which is two instruments: for SongData.targetDrums "fusion/target_drums/destruct.json"
    ///    the game loads "fusion/target_drums/destruct/destruct_spatial.fusion" through
    ///    "event:/gameplay/target_3d" (8 positional emitters) and ".../destruct_sub.fusion"
    ///    through "event:/gameplay/target_2d" (8 non-positional ones), both with
    ///    KataUtil.PlayMusic(path, emitter, event, no master). In a song the note is scheduled on
    ///    the song's clock and the emitter unmuted on a hit (TargetEmitter). In the menus both
    ///    kits load the same way and FusionGenerator.NoteOn(note, velocity) plays at once (the
    ///    positional kit was heard; the other one, a sub layer, wasn't noticed).
    ///  - The sustain sound is a track of its own per hand (SongData.sustainSongLeft / Right),
    ///    playable in the menus with KataUtil.PlaySound through "event:/gameplay/sustain_right"
    ///    (left assumed alike), and positional: it is heard from where its emitter object is.
    ///
    /// Target sounds: as in the game, a target's note belongs to the map, not to the shot. The
    /// game schedules every cue's note at the cue's own tick and the hit only unmutes it. Here:
    /// every cue's note is started when the song as heard (SpectatorSong.TryGetAudiblePositionMs,
    /// the sound's own position) reaches the cue's time (less NoteLeadMs). If the
    /// cue's hit is already known by then, the note is audible from its start, so it is exactly
    /// on the beat whenever the shot was fired. SpectatorTargets reports a hit (HitKnown) when
    /// the result is RECEIVED, which is about MpPresence.RenderDelayMs before the window's song
    /// position reaches it: that is what makes even a late shot's hit known before its cue's
    /// time. A hit that only arrives after its note started unmutes the note there (its start is
    /// lost, as in the game); a cue that is never hit rings silently. The note goes to one of
    /// SpatialVoices positional kits, whose emitter object is moved to the target's place, and,
    /// when the hit is known, to the non-positional kit. Gain as TargetEmitter.AssignTarget: 1,
    /// or 0.707 for one of a simultaneous pair, times the level below.
    ///
    /// Sustain sound: each hand's track is kept at the watched player's song position the way
    /// SpectatorSong keeps the song (one seek, then playback speed, paused with them), silent
    /// unless that hand is holding a sustain (SpectatorTargets.IsHolding), and its emitter object
    /// follows the watched player's gun.
    ///
    /// Level: SongPreview.gain x Spectator Volume, the same as the watched player's song.
    ///
    /// The kit paths are derived from targetDrums by that one example's pattern
    /// (dir/name.json -> dir/name/name_spatial.fusion, dir/name/name_sub.fusion).
    ///
    /// From the UnityExplorer console: ExScoringMod.SpectatorSounds.DebugDump();
    /// </summary>
    internal static class SpectatorSounds
    {
        private const string Target3dEvent = "event:/gameplay/target_3d";
        private const string Target2dEvent = "event:/gameplay/target_2d";
        private const string SustainLeftEvent = "event:/gameplay/sustain_left";
        private const string SustainRightEvent = "event:/gameplay/sustain_right";
        private const int SpatialVoices = 6;
        private const float FallbackGain = 0.4f;
        private const float HoldFadeSeconds = 0.08f;
        private const float StartRetrySeconds = 2f;

        // Sync, as SpectatorSong.
        private const double SeekThresholdMs = 1000, SeekLeadMs = 300, SeekSettledMs = 700, SpeedGainMs = 1000;
        private const double CatchUpSpeed = 0.15, FineSpeed = 0.03, SpeedDeadbandMs = 8, InSyncEnterMs = 100, InSyncLeaveMs = 200;
        private const float SeekTimeoutSeconds = 5f, GapSmoothing = 8f;

        /// <summary>One sustain track, kept at a song position.</summary>
        private class Track
        {
            public uint Handle = Hmx.Audio.Sound.kInvalidSoundHandle;
            public GameObject EmitterObject;
            public HmxAudioEmitter Emitter;
            public bool SeekedOnce, SeekPending, Paused, InSync, HasGap;
            public float SeekAt, Speed = 1f, NextStart, Fade;
            public double Gap, LastGap;
        }

        private static SongList.SongData song;
        private static string spatialPath, subPath;
        private static bool kitsTried;
        private static uint subHandle = Hmx.Audio.Sound.kInvalidSoundHandle;
        private static readonly uint[] spatialHandles = new uint[SpatialVoices];
        private static readonly GameObject[] spatialObjects = new GameObject[SpatialVoices];
        private static int spatialNext;
        private static readonly Track[] sustain = { new Track(), new Track() };
        private static int notesPlayed;
        private static string lastError;

        /// <summary>A hit reported before its cue's note was started.</summary>
        private class KnownHit
        {
            public double DueMs;
            public bool HasPosition, Simultaneous;
            public Vector3 Position;
        }

        private static readonly System.Collections.Generic.Dictionary<long, KnownHit> known = new System.Collections.Generic.Dictionary<long, KnownHit>();
        private static readonly System.Collections.Generic.List<long> knownOld = new System.Collections.Generic.List<long>();

        // What each positional kit is playing: the cue (CueKey), when it started (unscaled time)
        // and whether it was made audible.
        private static readonly long[] voiceKey = new long[SpatialVoices];
        private static readonly float[] voiceStart = new float[SpatialVoices];
        private static readonly bool[] voiceAudible = new bool[SpatialVoices];

        // The chart's cues in time order, and the next one whose note is to be started.
        private static SongCues.Cue[] scheduleCues;
        private static double[] scheduleMs;
        private static int scheduleCount = -1, scheduleNext;
        private static bool schedulePlaced;
        private static double schedulePosition;
        private static int notesStarted, lateUnmutes, hitsTooLate;

        // How early a note is started, ms: about half a frame, so it is triggered on the frame
        // nearest the cue's time (the game schedules it on the exact sample).
        private const double NoteLeadMs = 6;
        private const float VoiceBusySeconds = 0.7f;   // an audible note is left ringing this long before its kit is taken again
        private const double LateStartMs = 120;        // a hit for a note that was never started is still played this long after its cue
        private const double JumpBackMs = 200, JumpAheadMs = 1000; // the position moving this much = a seek, not playing

        private static long CueKey(SongCues.Cue cue)
        {
            return cue.tick * 4096L + (cue.pitch & 0x3FF) * 4L + ((int)cue.handType & 3);
        }

        private static void Log(string message) => DebugLog.Log("[SpectatorSounds] " + message);

        private static float Level()
        {
            SongPreview preview = SongPreview.I;
            float baseGain = preview != null ? preview.gain : FallbackGain;
            return baseGain * Mathf.Clamp01(Config.SpectatorVolume / 100f);
        }

        // ── Tick ─────────────────────────────────────────────────────────────

        /// <summary>Once per frame from ExScoring.OnUpdate, after SpectatorTargets.Tick.</summary>
        public static void Tick()
        {
            try
            {
                SongList.SongData current = SpectatorSong.Song;
                if (current == null)
                {
                    if (song != null) StopAll("no song being watched");
                    return;
                }
                if (current != song)
                {
                    StopAll("another song");
                    song = current;
                    DeriveKitPaths(current.targetDrums);
                }

                bool has = SpectatorSong.TryGetAudioTarget(out double wantMs, out bool playing);
                float dt = Time.unscaledDeltaTime;

                UpdateSchedule(has, wantMs);

                float level = Level();
                for (int h = 0; h < 2; h++)
                {
                    string path = h == 0 ? song.sustainSongLeft : song.sustainSongRight;
                    if (string.IsNullOrEmpty(path)) continue;
                    Track track = sustain[h];

                    PlaceAtGun(track, h);
                    UpdateTrack(track, path, h == 0 ? SustainLeftEvent : SustainRightEvent, wantMs, has && playing, dt, h);

                    if (track.Handle == Hmx.Audio.Sound.kInvalidSoundHandle) continue;
                    bool audible = track.InSync && !track.Paused && SpectatorTargets.IsHolding(h);
                    track.Fade = Mathf.MoveTowards(track.Fade, audible ? 1f : 0f, dt / HoldFadeSeconds);
                    Hmx.Audio.Sound.SetGain(track.Handle, level * track.Fade);
                }
            }
            catch (Exception ex)
            {
                if (ex.Message != lastError)
                {
                    lastError = ex.Message;
                    MelonLogger.Log("[SpectatorSounds] tick failed: " + ex);
                }
            }
        }

        /// <summary>dir/name.json -> dir/name/name_spatial.fusion and dir/name/name_sub.fusion.</summary>
        private static void DeriveKitPaths(string targetDrums)
        {
            spatialPath = null;
            subPath = null;
            kitsTried = false;
            if (string.IsNullOrEmpty(targetDrums)) return;

            string stem = targetDrums.EndsWith(".json") ? targetDrums.Substring(0, targetDrums.Length - 5) : targetDrums;
            int slash = stem.LastIndexOf('/');
            string name = slash >= 0 ? stem.Substring(slash + 1) : stem;
            spatialPath = stem + "/" + name + "_spatial.fusion";
            subPath = stem + "/" + name + "_sub.fusion";
        }

        // ── Placing sounds as the watched player hears them ──────────────────

        /// <summary>A world position in the watched player's scene, moved to the same spot relative to this player's head.</summary>
        private static Vector3 AsHeardByMe(Vector3 worldPosition)
        {
            Transform theirHead = SpectatorWindow.ShownWatchedHead;
            KataConfig config = KataConfig.I;
            if (theirHead == null || config == null || config.cam == null) return worldPosition;
            Transform myHead = config.cam.transform;
            // Position and rotation only: the avatar's head copy is scaled, and the scale must not
            // stretch the distances (log 2026-11-14: the sustain emitter ended up above the head).
            Vector3 local = Quaternion.Inverse(theirHead.rotation) * (worldPosition - theirHead.position);
            return myHead.position + myHead.rotation * local;
        }

        private static GameObject NewEmitterObject(string name, out HmxAudioEmitter emitter)
        {
            GameObject go = new GameObject(name);
            GameObject.DontDestroyOnLoad(go);
            emitter = go.AddComponent<HmxAudioEmitter>();
            return go;
        }

        private static void PlaceAtGun(Track track, int hand)
        {
            if (track.EmitterObject == null) track.EmitterObject = NewEmitterObject("ExScoring_SpectatorSustain" + (hand == 0 ? "L" : "R"), out track.Emitter);
            if (MpPresence.TryGetRemoteFirepoint(SpectatorSong.WatchedUserId, hand, out Vector3 position, out Vector3 forward))
                track.EmitterObject.transform.position = AsHeardByMe(position);
        }

        // ── Target sounds ────────────────────────────────────────────────────

        private static void EnsureKits()
        {
            if (kitsTried) return;
            kitsTried = true;
            if (spatialPath == null) return;

            try
            {
                KataConfig config = KataConfig.I;
                HmxAudioEmitter flat = config != null ? config.GetComponent<HmxAudioEmitter>() : null;
                if (flat != null)
                    subHandle = KataUtil.PlayMusic(subPath, flat.Get(), Target2dEvent, Hmx.Audio.Sound.kInvalidSoundHandle);

                int loaded = 0;
                for (int i = 0; i < SpatialVoices; i++)
                {
                    spatialObjects[i] = NewEmitterObject("ExScoring_SpectatorTargetSound" + i, out HmxAudioEmitter emitter);
                    spatialHandles[i] = KataUtil.PlayMusic(spatialPath, emitter.Get(), Target3dEvent, Hmx.Audio.Sound.kInvalidSoundHandle);
                    if (spatialHandles[i] != Hmx.Audio.Sound.kInvalidSoundHandle && Hmx.Audio.Sound.IsValid(spatialHandles[i])) loaded++;
                }
                Log($"target drum kits: {spatialPath} x{loaded}/{SpatialVoices}, {subPath} {(Hmx.Audio.Sound.IsValid(subHandle) ? "loaded" : "not loaded")}");
            }
            catch (Exception ex)
            {
                Log("loading the target drum kits failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Starts the note of every cue whose time the song has reached. The position is the song
        /// as heard; while it isn't heard (not in sync, paused) the watched player's position
        /// only moves the place in the chart along, without sound.
        /// </summary>
        private static void UpdateSchedule(bool has, double wantMs)
        {
            System.Collections.Generic.List<SongCues.Cue> cues = SpectatorSong.Cues;
            if (cues == null || cues.Count == 0)
            {
                scheduleCount = -1;
                return;
            }
            if (scheduleCount != cues.Count || scheduleCues == null)
            {
                scheduleCount = cues.Count;
                scheduleCues = cues.ToArray();
                scheduleMs = new double[scheduleCount];
                for (int i = 0; i < scheduleCount; i++)
                    scheduleMs[i] = scheduleCues[i] != null ? AudioDriver.TickSpanToMs(song, 0f, scheduleCues[i].tick) : double.MaxValue;
                Array.Sort(scheduleMs, scheduleCues);
                schedulePlaced = false;
            }

            bool heard = SpectatorSong.TryGetAudiblePositionMs(out double position);
            if (!heard)
            {
                if (!has) return;
                position = wantMs;
            }
            double reach = position + NoteLeadMs;

            if (!schedulePlaced || position < schedulePosition - JumpBackMs || position > schedulePosition + JumpAheadMs)
            {
                // Started, or the song moved: carry on from here without playing what was skipped.
                scheduleNext = 0;
                while (scheduleNext < scheduleCount && scheduleMs[scheduleNext] <= reach) scheduleNext++;
                schedulePlaced = true;
            }
            schedulePosition = position;

            while (scheduleNext < scheduleCount && scheduleMs[scheduleNext] <= reach)
            {
                SongCues.Cue cue = scheduleCues[scheduleNext];
                scheduleNext++;
                if (cue == null || cue.behavior == Target.TargetBehavior.Dodge) continue;
                long key = CueKey(cue);
                known.TryGetValue(key, out KnownHit hit);
                known.Remove(key);
                if (heard) StartNote(cue, key, hit);
            }

            // Hits whose cue was never reached (a seek): forgotten after a while.
            if (known.Count > 16)
            {
                knownOld.Clear();
                foreach (System.Collections.Generic.KeyValuePair<long, KnownHit> pair in known)
                    if (pair.Value.DueMs < position - 2000 || pair.Value.DueMs > position + 10000) knownOld.Add(pair.Key);
                for (int i = 0; i < knownOld.Count; i++) known.Remove(knownOld[i]);
            }
        }

        /// <summary>
        /// From SpectatorTargets when a hit result is received (before the window's song position
        /// reaches it): the cue's note is to be heard. Position: the target's place (world, in the
        /// watched player's scene) when known.
        /// </summary>
        internal static void HitKnown(SongCues.Cue cue, bool hasPosition, Vector3 worldPosition, bool simultaneous)
        {
            if (cue == null || song == null) return;
            try
            {
                long key = CueKey(cue);
                KnownHit hit = new KnownHit { DueMs = AudioDriver.TickSpanToMs(song, 0f, cue.tick), HasPosition = hasPosition, Position = worldPosition, Simultaneous = simultaneous };

                // Its note is already ringing, silent: heard from here on.
                for (int i = 0; i < SpatialVoices; i++)
                {
                    if (voiceKey[i] != key || voiceAudible[i] || Time.unscaledTime - voiceStart[i] > 2f) continue;
                    uint handle = spatialHandles[i];
                    if (handle == Hmx.Audio.Sound.kInvalidSoundHandle || !Hmx.Audio.Sound.IsValid(handle)) break;
                    if (hasPosition && spatialObjects[i] != null) spatialObjects[i].transform.position = AsHeardByMe(worldPosition);
                    Hmx.Audio.Sound.SetGain(handle, Level() * (simultaneous ? 0.707f : 1f));
                    voiceAudible[i] = true;
                    lateUnmutes++;
                    notesPlayed++;
                    return;
                }

                // Its note hasn't been started yet: it will be audible from its start.
                if (!schedulePlaced || hit.DueMs > schedulePosition + NoteLeadMs)
                {
                    known[key] = hit;
                    return;
                }

                // Its cue's time passed without a note (not in sync then, or the kit was taken).
                if (schedulePosition - hit.DueMs <= LateStartMs && SpectatorSong.TryGetAudiblePositionMs(out double heardMs)) StartNote(cue, key, hit);
                else hitsTooLate++;
            }
            catch (Exception ex)
            {
                if (ex.Message != lastError)
                {
                    lastError = ex.Message;
                    Log("a target sound failed: " + ex.Message);
                }
            }
        }

        /// <summary>The kit for the next note: one that isn't audibly ringing if there is one, the oldest of those.</summary>
        private static int NextVoice()
        {
            int best = -1;
            bool bestFree = false;
            float now = Time.unscaledTime;
            for (int i = 0; i < SpatialVoices; i++)
            {
                bool free = !voiceAudible[i] || now - voiceStart[i] > VoiceBusySeconds;
                if (best < 0 || (free && !bestFree) || (free == bestFree && voiceStart[i] < voiceStart[best]))
                {
                    best = i;
                    bestFree = free;
                }
            }
            return best;
        }

        /// <summary>Starts a cue's note: audible when its hit is known (hit != null), silent otherwise.</summary>
        private static void StartNote(SongCues.Cue cue, long key, KnownHit hit)
        {
            try
            {
                EnsureKits();
                float gain = hit != null ? Level() * (hit.Simultaneous ? 0.707f : 1f) : 0f;
                sbyte note = (sbyte)Mathf.Clamp(cue.pitch, 0, 127);
                sbyte velocity = (sbyte)Mathf.Clamp(cue.velocity, 1, 127);

                // The non-positional kit is one sound for every note: only for a known hit.
                if (hit != null && subHandle != Hmx.Audio.Sound.kInvalidSoundHandle && Hmx.Audio.Sound.IsValid(subHandle))
                {
                    Hmx.Audio.Sound.SetGain(subHandle, gain);
                    Hmx.Audio.FusionGenerator sub = Hmx.Audio.Sound.GetFusionGenerator(subHandle);
                    if (sub != null) sub.NoteOn(note, velocity);
                }

                int voice = NextVoice();
                uint handle = voice >= 0 ? spatialHandles[voice] : Hmx.Audio.Sound.kInvalidSoundHandle;
                if (handle != Hmx.Audio.Sound.kInvalidSoundHandle && Hmx.Audio.Sound.IsValid(handle))
                {
                    if (hit != null && hit.HasPosition && spatialObjects[voice] != null)
                        spatialObjects[voice].transform.position = AsHeardByMe(hit.Position);
                    Hmx.Audio.Sound.SetGain(handle, gain);
                    Hmx.Audio.FusionGenerator spatial = Hmx.Audio.Sound.GetFusionGenerator(handle);
                    if (spatial != null) spatial.NoteOn(note, velocity);
                    voiceKey[voice] = key;
                    voiceStart[voice] = Time.unscaledTime;
                    voiceAudible[voice] = hit != null;
                }
                notesStarted++;
                if (hit != null) notesPlayed++;
            }
            catch (Exception ex)
            {
                if (ex.Message != lastError)
                {
                    lastError = ex.Message;
                    Log("a target sound failed: " + ex.Message);
                }
            }
        }

        // ── Sustain tracks ───────────────────────────────────────────────────

        private static void UpdateTrack(Track track, string path, string fmodEvent, double wantMs, bool playing, float dt, int hand)
        {
            bool valid = track.Handle != Hmx.Audio.Sound.kInvalidSoundHandle;
            if (valid && !track.Paused && !IsAlive(track.Handle))
            {
                StopTrack(track);
                valid = false;
            }

            if (!valid && playing && Time.unscaledTime >= track.NextStart && track.Emitter != null)
            {
                Hmx.Audio.PlayArgs args = new Hmx.Audio.PlayArgs();
                args.volumeSettings.volume = -96f;
                args.volumeSettings.modifyAtStart = true;
                uint h = KataUtil.PlaySound(args, path, track.Emitter.Get(), fmodEvent);
                if (h == Hmx.Audio.Sound.kInvalidSoundHandle)
                {
                    track.NextStart = Time.unscaledTime + StartRetrySeconds;
                    Log($"PlaySound failed for the {(hand == 0 ? "left" : "right")} sustain track ({path})");
                    return;
                }
                Hmx.Audio.Sound.SetGain(h, 0f);
                track.Handle = h;
                track.SeekedOnce = false; track.SeekPending = false; track.Paused = false; track.InSync = false;
                track.HasGap = false; track.Speed = 1f; track.Fade = 0f;
                valid = true;
                Log($"{(hand == 0 ? "left" : "right")} sustain track started (handle {h})");
            }
            if (!valid) return;

            float length = Hmx.Audio.Sound.GetLengthMs(track.Handle);
            if (!playing || (length > 0f && wantMs >= length - 50))
            {
                if (!track.Paused)
                {
                    SetSpeed(track, 1f);
                    Hmx.Audio.Sound.Pause(track.Handle);
                    track.Paused = true;
                    track.InSync = false;
                }
                return;
            }
            if (track.Paused)
            {
                Hmx.Audio.Sound.Continue(track.Handle);
                track.Paused = false;
                track.HasGap = false;
            }

            double gap = wantMs - Hmx.Audio.Sound.GetTimelineMs(track.Handle);
            track.LastGap = gap;

            if (track.SeekPending)
            {
                if (Math.Abs(gap) < SeekSettledMs || Time.unscaledTime - track.SeekAt > SeekTimeoutSeconds)
                {
                    track.SeekPending = false;
                    track.HasGap = false;
                }
            }

            if (!track.SeekPending && (!track.SeekedOnce || Math.Abs(gap) > SeekThresholdMs))
            {
                SetSpeed(track, 1f);
                Hmx.Audio.Sound.SeekToMs(track.Handle, (float)Math.Max(0, wantMs + SeekLeadMs));
                track.SeekPending = true;
                track.SeekedOnce = true;
                track.SeekAt = Time.unscaledTime;
                track.InSync = false;
            }
            else if (!track.SeekPending)
            {
                track.Gap = track.HasGap ? track.Gap + (gap - track.Gap) * Math.Min(1.0, dt * GapSmoothing) : gap;
                track.HasGap = true;
                track.InSync = Math.Abs(track.Gap) <= (track.InSync ? InSyncLeaveMs : InSyncEnterMs);
                double limit = track.InSync ? FineSpeed : CatchUpSpeed;
                double wanted = Math.Abs(track.Gap) < SpeedDeadbandMs ? 1.0 : 1.0 + Math.Max(-limit, Math.Min(limit, track.Gap / SpeedGainMs));
                SetSpeed(track, (float)wanted);
            }
        }

        private static void SetSpeed(Track track, float value)
        {
            if (Mathf.Abs(value - track.Speed) < 0.001f) return;
            track.Speed = value;
            Hmx.Audio.Sound.SetSpeed(track.Handle, value);
        }

        private static bool IsAlive(uint h)
        {
            if (h == Hmx.Audio.Sound.kInvalidSoundHandle) return false;
            uint state = (uint)Hmx.Audio.Sound.GetTransportState(h);
            return (state & 0xFFFFFFFAu) != 0 || state == 1;
        }

        // ── Cleanup / debug ──────────────────────────────────────────────────

        private static void StopHandle(ref uint handle)
        {
            if (handle != Hmx.Audio.Sound.kInvalidSoundHandle && handle != 0)
            {
                try { Hmx.Audio.Sound.Stop(handle); }
                catch (Exception ex) { Log("stopping a sound failed: " + ex.Message); }
            }
            handle = Hmx.Audio.Sound.kInvalidSoundHandle;
        }

        private static void StopTrack(Track track)
        {
            StopHandle(ref track.Handle);
            track.Paused = false;
            track.InSync = false;
            track.SeekPending = false;
            track.Speed = 1f;
            track.Fade = 0f;
        }

        private static void StopAll(string reason)
        {
            for (int h = 0; h < 2; h++)
            {
                StopTrack(sustain[h]);
                if (sustain[h].EmitterObject != null) GameObject.Destroy(sustain[h].EmitterObject);
                sustain[h].EmitterObject = null;
                sustain[h].Emitter = null;
            }
            StopHandle(ref subHandle);
            for (int i = 0; i < SpatialVoices; i++)
            {
                StopHandle(ref spatialHandles[i]);
                if (spatialObjects[i] != null) GameObject.Destroy(spatialObjects[i]);
                spatialObjects[i] = null;
            }
            known.Clear();
            for (int i = 0; i < SpatialVoices; i++) { voiceKey[i] = 0; voiceStart[i] = 0f; voiceAudible[i] = false; }
            scheduleCues = null; scheduleMs = null; scheduleCount = -1; scheduleNext = 0; schedulePlaced = false;
            if (song != null) Log($"stopped ({reason}): {notesPlayed} target sounds heard of {notesStarted} notes started ({lateUnmutes} unmuted after their start, {hitsTooLate} hits too late)");
            notesStarted = 0; lateUnmutes = 0; hitsTooLate = 0;
            song = null;
            kitsTried = false;
            notesPlayed = 0;
        }

        public static void DebugDump()
        {
            MelonLogger.Log("[SpectatorSounds] ── dump ──");
            MelonLogger.Log($"  song={(song != null ? song.songID : "none")} kits tried={kitsTried} spatial={spatialPath ?? "-"} sub={subPath ?? "-"} heard={notesPlayed} started={notesStarted} unmutedLate={lateUnmutes} tooLate={hitsTooLate} known={known.Count} next={scheduleNext}/{scheduleCount} lead={NoteLeadMs:0} ms level={Level():0.00}");
            MelonLogger.Log($"  sub handle={subHandle} valid={Hmx.Audio.Sound.IsValid(subHandle)}");
            for (int i = 0; i < SpatialVoices; i++)
                MelonLogger.Log($"  spatial[{i}] handle={spatialHandles[i]} valid={Hmx.Audio.Sound.IsValid(spatialHandles[i])} audible={voiceAudible[i]} object={(spatialObjects[i] != null ? spatialObjects[i].transform.position.ToString() : "null")}");
            for (int h = 0; h < 2; h++)
            {
                Track t = sustain[h];
                MelonLogger.Log($"  sustain {(h == 0 ? "L" : "R")}: handle={t.Handle} paused={t.Paused} inSync={t.InSync} seekPending={t.SeekPending} gap={t.LastGap:0} ms speed={t.Speed:0.000} fade={t.Fade:0.00} holding={SpectatorTargets.IsHolding(h)} " +
                    $"emitter={(t.EmitterObject != null ? t.EmitterObject.transform.position.ToString() : "null")}");
            }
        }
    }
}