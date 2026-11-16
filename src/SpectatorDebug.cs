using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// Spectator window test without a server or a second player (debug only, both switches reset
    /// when the game closes; Options > Debug, or from the UnityExplorer console).
    ///
    /// Record (SpectatorDebug.Record): during a song (end screen excluded), the pose this player
    /// would send to a watcher is built RecordHz times a second (MpPresence.DebugBuildPose: head,
    /// hands, guns, triggers, shots, hand animation, songTick) and kept, stamped with the
    /// milliseconds since the recording began. Nothing is sent. When the song ends, or the switch
    /// goes off, the recording is written to UserData/ExScoring_SpectatorRecording.jsonl (one pose
    /// per line), replacing the last one. A song with fewer than MinPoses poses isn't saved. Works
    /// offline, autoplayed or played by hand.
    ///
    /// Replay (SpectatorDebug.Replay): in a lobby with presence, outside songs, the file is read
    /// and a fake player (FakeUserId, in no lobby list) is fed to MpPresence one pose at a time
    /// (MpPresence.DebugInjectPose), each stamped as if the server had just relayed it and flagged
    /// inSong. So the whole watching side runs as it would for a real player in a song: the
    /// samples are buffered and interpolated, the avatar gets the song look on layer 31, and the
    /// spectator window lists the fake player ("Replay (debug)") and follows their head. The
    /// recording loops. The fake player stands on the spot after this player's. Turning the switch
    /// off, leaving the lobby or entering a song removes them; the file is read again each time
    /// the replay starts.
    ///
    /// The song: a recording starts with one line holding the spectateInfo this player would send
    /// (SpectatorSong.BuildLocalInfo: song, difficulty, target speed), and the replay hands it to
    /// SpectatorSong as the fake player's, so the window plays the recorded song's audio in time
    /// with the replay and loads its chart. A recording made before this (no such line) replays
    /// without audio.
    ///
    /// Results: while recording, SpectatorEvents hands over each batch of hits, misses and sustain
    /// results it would send (RecordEvents), saved as their own lines with the same clock as the
    /// poses; the replay gives them back to SpectatorEvents as the fake player's when their time
    /// comes. A recording made before this replays without results (targets go at their cue's time).
    ///
    /// What this doesn't test: the server (watch, watchers, the in-song relays) and sending.
    ///
    /// From the UnityExplorer console: ExScoringMod.SpectatorDebug.DebugDump();
    /// </summary>
    internal static class SpectatorDebug
    {
        public const string FakeUserId = "debug:replay";
        public const string FakeName = "Replay (debug)";

        private const int RecordHz = 30;
        private const int MinPoses = 30;
        private const int MaxPoses = 60 * 60 * RecordHz;   // an hour, so a forgotten switch can't grow without end

        /// <summary>Session only: record the next song.</summary>
        public static bool Record;
        /// <summary>Session only: show the fake player replaying the last recording.</summary>
        public static bool Replay;

        /// <summary>A song is being recorded right now.</summary>
        internal static bool IsRecording => recording;

        /// <summary>The fake player is being fed right now.</summary>
        internal static bool ReplayActive { get; private set; }

        private static string FilePath => Path.Combine(Application.dataPath, "..", "UserData", "ExScoring_SpectatorRecording.jsonl");

        // Recording
        private static bool recording;
        private static long recordStartMs;
        private static long nextRecordAtMs;
        private static readonly List<string> recorded = new List<string>();
        private static int recordingsSaved;
        private const string InfoLineKey = "spectateInfo";
        private const string EventsLineKey = "spectateEvents";
        private const long InfoBuildIntervalMs = 2000;
        private static JObject recordedInfo;
        private static long nextInfoBuildAtMs;

        // Replay
        private static readonly List<MpPoseEntry> replayEntries = new List<MpPoseEntry>();
        private static readonly List<long> replayTimes = new List<long>();
        private static MpSpectateInfo replayInfo;   // the recording's song, or null (an older recording)
        private static readonly List<MpSpectateEvent[]> replayEvents = new List<MpSpectateEvent[]>();
        private static readonly List<long> replayEventTimes = new List<long>();
        private static int replayEventIndex;
        private static long replayBaseMs;
        private static int replayIndex;
        private static int replayLoops;

        private static void Log(string message) => MelonLogger.Log("[SpectatorDebug] " + message);

        /// <summary>Once per frame from ExScoring.OnUpdate, after MpPresence.Tick and before SpectatorWindow.Tick.</summary>
        public static void Tick()
        {
            try
            {
                bool inSong = MpPresence.DebugInSong;
                TickRecord(inSong);
                TickReplay(inSong);
            }
            catch (Exception ex)
            {
                MelonLogger.Log("[SpectatorDebug] tick failed: " + ex);
                StopReplay("error");
                Replay = false;
            }
        }

        // ── Record ──

        private static void TickRecord(bool inSong)
        {
            if (!Record || !inSong)
            {
                if (recording) StopRecording();
                return;
            }

            long now = MpSession.LocalNowMs;
            if (!recording)
            {
                recording = true;
                recorded.Clear();
                recordStartMs = now;
                nextRecordAtMs = now;
                recordedInfo = null;
                nextInfoBuildAtMs = now;
                Log("recording started");
            }

            // What a watcher would be told about the song, refreshed like the real thing (the
            // per-song target speed is only in place once the song has started). Saved as line 1.
            if (now >= nextInfoBuildAtMs)
            {
                nextInfoBuildAtMs = now + InfoBuildIntervalMs;
                JObject info = SpectatorSong.BuildLocalInfo();
                if (info != null) recordedInfo = info;
            }

            SpectatorEvents.FlushIfDue(); // results reach the recording even when nobody is watching

            if (now < nextRecordAtMs || recorded.Count >= MaxPoses) return;
            long interval = 1000 / RecordHz;
            nextRecordAtMs = now - nextRecordAtMs >= interval ? now + interval : nextRecordAtMs + interval;

            JObject pose = MpPresence.DebugBuildPose(now - recordStartMs);
            if (pose != null) recorded.Add(pose.ToString(Formatting.None));
        }

        /// <summary>From SpectatorEvents.FlushIfDue: a batch of results this player would send, kept with the recording's clock.</summary>
        internal static void RecordEvents(JArray events)
        {
            if (!recording || events == null || events.Count == 0 || recorded.Count >= MaxPoses) return;
            recorded.Add(new JObject
            {
                [EventsLineKey] = events,
                ["t"] = MpSession.LocalNowMs - recordStartMs
            }.ToString(Formatting.None));
        }

        private static void StopRecording()
        {
            recording = false;
            if (recorded.Count < MinPoses)
            {
                Log($"recording dropped: only {recorded.Count} poses");
                recorded.Clear();
                return;
            }

            try
            {
                string path = FilePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                List<string> lines = new List<string>(recorded.Count + 1);
                if (recordedInfo != null) lines.Add(new JObject { [InfoLineKey] = recordedInfo }.ToString(Formatting.None));
                lines.AddRange(recorded);
                File.WriteAllLines(path, lines.ToArray());
                recordingsSaved++;
                Log($"recording saved: {recorded.Count} poses, {(MpSession.LocalNowMs - recordStartMs) / 1000.0:0.0} s, " +
                    $"{(recordedInfo != null ? "song " + recordedInfo["song"]?["songId"] : "no song info (the replay will have no audio)")} -> {path}");
            }
            catch (Exception ex)
            {
                Log("saving the recording failed: " + ex.Message);
            }
            recorded.Clear();
        }

        // ── Replay ──

        private static void TickReplay(bool inSong)
        {
            bool want = Replay && MpSession.InLobby && MpSession.PresenceEnabled && !inSong;
            if (!want)
            {
                if (ReplayActive) StopReplay(Replay ? "not in a lobby's menus" : "switched off");
                return;
            }

            long now = MpSession.ServerNowMs;
            if (!ReplayActive)
            {
                if (!LoadRecording())
                {
                    Replay = false; // nothing to replay: the toggle goes back off
                    return;
                }
                ReplayActive = true;
                if (replayInfo != null) SpectatorSong.SetInfo(replayInfo, true);
                else Log("this recording has no song info (made before the audio step): no audio. Record again to get it");
                replayBaseMs = now;
                replayIndex = 0;
                replayEventIndex = 0;
                replayLoops = 0;
                Log($"replay started: {replayEntries.Count} poses, {replayEvents.Count} result batches, {replayTimes[replayTimes.Count - 1] / 1000.0:0.0} s, looping");
            }

            // Every pose whose time has come, as if the server had just relayed it.
            while (replayIndex < replayEntries.Count && replayBaseMs + replayTimes[replayIndex] <= now)
            {
                MpPoseEntry entry = replayEntries[replayIndex];
                entry.userId = FakeUserId;
                entry.t = replayBaseMs + replayTimes[replayIndex];
                entry.inSong = true;
                MpPresence.DebugInjectPose(entry);
                replayIndex++;
            }

            // Results whose time has come.
            while (replayEventIndex < replayEvents.Count && replayBaseMs + replayEventTimes[replayEventIndex] <= now)
            {
                SpectatorEvents.Receive(FakeUserId, replayEvents[replayEventIndex]);
                replayEventIndex++;
            }

            if (replayIndex >= replayEntries.Count)
            {
                replayBaseMs = now;
                replayIndex = 0;
                replayEventIndex = 0;
                replayLoops++;
            }
        }

        private static void StopReplay(string reason)
        {
            if (!ReplayActive) return;
            ReplayActive = false;
            MpPresence.DebugRemoveRemote(FakeUserId);
            SpectatorSong.ClearInfo(FakeUserId);
            SpectatorEvents.ClearFor(FakeUserId);
            Log($"replay stopped ({reason})");
        }

        private static bool LoadRecording()
        {
            replayEntries.Clear();
            replayTimes.Clear();
            replayInfo = null;
            replayEvents.Clear();
            replayEventTimes.Clear();

            string path = FilePath;
            if (!File.Exists(path))
            {
                Log($"no recording yet ({path}): turn on Record Spectator Poses and play a song first");
                return false;
            }

            try
            {
                string[] lines = File.ReadAllLines(path);
                int bad = 0;
                long last = -1;
                for (int i = 0; i < lines.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(lines[i])) continue;
                    MpPoseEntry entry;
                    try
                    {
                        JObject line = JObject.Parse(lines[i]);
                        if (line[InfoLineKey] != null)
                        {
                            // The song line, not a pose.
                            MpSpectateInfo info = line[InfoLineKey].ToObject<MpSpectateInfo>(MpJson.Serializer);
                            if (info != null && info.song != null && !string.IsNullOrEmpty(info.song.songId))
                            {
                                info.userId = FakeUserId;
                                replayInfo = info;
                            }
                            continue;
                        }
                        if (line[EventsLineKey] != null)
                        {
                            // A batch of results, not a pose.
                            MpSpectateEvent[] batch = line[EventsLineKey].ToObject<MpSpectateEvent[]>(MpJson.Serializer);
                            if (batch != null && batch.Length > 0)
                            {
                                replayEvents.Add(batch);
                                replayEventTimes.Add(line.Value<long>("t"));
                            }
                            continue;
                        }
                        entry = line.ToObject<MpPoseEntry>(MpJson.Serializer);
                    }
                    catch
                    {
                        bad++;
                        continue;
                    }
                    if (entry == null || entry.t <= last) { bad++; continue; }
                    last = entry.t;
                    replayTimes.Add(entry.t);
                    replayEntries.Add(entry);
                }
                if (bad > 0) Log($"{bad} unreadable or out-of-order lines skipped");
            }
            catch (Exception ex)
            {
                Log("reading the recording failed: " + ex.Message);
                return false;
            }

            if (replayEntries.Count < 2)
            {
                Log("the recording is empty");
                return false;
            }
            return true;
        }

        public static void DebugDump()
        {
            MelonLogger.Log("[SpectatorDebug] ── dump ──");
            MelonLogger.Log($"  record={Record} recording={recording} poses={recorded.Count} saved={recordingsSaved}");
            MelonLogger.Log($"  replay={Replay} active={ReplayActive} poses={replayEntries.Count} index={replayIndex} loops={replayLoops} inLobby={MpSession.InLobby} presence={MpSession.PresenceEnabled}");
            MelonLogger.Log($"  file={FilePath} exists={File.Exists(FilePath)}");
            MelonLogger.Log($"  result batches={replayEvents.Count} next={replayEventIndex}");
            MelonLogger.Log($"  recorded song={(replayInfo != null && replayInfo.song != null ? replayInfo.song.songId + " [" + replayInfo.song.difficulty + "]" : "none")}");
        }
    }
}