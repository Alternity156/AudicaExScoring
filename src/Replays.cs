using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using Harmony;
using MelonLoader;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// Replays: this player's own songs, recorded the way a spectator receives them and watched
    /// later in the spectator window (window or diorama), online or offline. Replaces the debug
    /// recorder and fake player that SpectatorDebug used to have.
    ///
    /// Recording (Options > Replays > Record Gameplay, Config.RecordGameplay, off by default):
    /// during a song (end screen excluded), the pose this player would send to a watcher
    /// (MpPresence.BuildRecordingPose: head, hands, guns, triggers, shots, hand animation, songTick)
    /// is kept RecordHz times a second, stamped with the milliseconds since the recording began,
    /// with the spectateInfo a watcher would get (SpectatorSong.BuildLocalInfo) and the result
    /// batches SpectatorEvents would send (RecordEvents). Nothing is sent.
    ///   Kept only for a song that ends normally (SongEnd.OnSongOver, not failed). Thrown away:
    ///   a restart (the song position going back RestartTicks or more), leaving the song early,
    ///   a fail, Record Gameplay turned off mid-song, and practice mode (never recorded).
    ///   Saved on a worker thread as one gzipped file per run in UserData/ExScoring_Replays:
    ///   "songId_Difficulty_yyyyMMdd-HHmmss.jsonl.gz". Line 1 is the header ({"replay": {...}}:
    ///   song, difficulty, date, length, scores, gun colours), line 2 the spectateInfo, then one
    ///   pose or result batch per line, as the old debug recording.
    ///
    /// Watching (Options > Replays > a recording, or Play from the console): the file is read on a
    /// worker thread, then a replay player (UserId, in no lobby) is fed to MpPresence one pose at a
    /// time (InjectReplayPose), stamped as if the server had just relayed it and flagged inSong, and
    /// its results are handed to SpectatorEvents when their time comes. So the whole watching side
    /// runs as for a real watched player: interpolation, the song look on layer 31, the window's
    /// audio, chart, targets, results and popups. The spectator window opens on it (in a lobby too,
    /// where Next Player still reaches the others). Offline, MpPresence keeps only this player.
    ///   Plays once. Pause (TogglePause) and the end both hold the last pose: it is fed again with
    ///   fresh times, so the song position stops and SpectatorSong pauses the audio. Resuming
    ///   shifts the clock by the time spent paused. Restart removes the player and starts from 0
    ///   (the window's song, chart and targets start over cleanly). Stop removes it.
    ///   Starting a song stops the replay.
    ///
    /// Not handled: practice mode at a speed other than 1 (never recorded); cosmetics (the replay
    /// player shows the default models).
    ///
    /// From the UnityExplorer console: ExScoringMod.Replays.DebugDump();
    /// </summary>
    internal static class Replays
    {
        public const string UserId = "replay:local";

        private const int FormatVersion = 1;
        private const int RecordHz = 30;
        private const int MinPoses = 30;
        private const int MaxPoses = 60 * 60 * RecordHz;      // an hour
        private const double RestartTicks = 480;              // the song position going back this much = a restart
        private const long InfoBuildIntervalMs = 2000;
        private const long FrozenFeedIntervalMs = 1000 / RecordHz;
        private const float InfoRefreshSeconds = 1f;
        private const long RestartGapMs = 300;
        private const string FileExtension = ".jsonl.gz";
        private const string HeaderKey = "replay";
        private const string InfoLineKey = "spectateInfo";
        private const string EventsLineKey = "spectateEvents";

        private static void Log(string message) => MelonLogger.Log("[Replays] " + message);

        /// <summary>UserData/ExScoring_Replays (main thread only: Application.dataPath).</summary>
        internal static string Folder => Path.Combine(Application.dataPath, "..", "UserData", "ExScoring_Replays");
        private static string DeletedFolder => Path.Combine(Folder, "Deleted");

        /// <summary>This player is in practice mode right now (never recorded, never watched).</summary>
        internal static bool InPracticeMode
        {
            get
            {
                try
                {
                    KataConfig config = KataConfig.I;
                    return config != null && config.practiceMode;
                }
                catch
                {
                    return false;
                }
            }
        }

        // ── Recording state ──────────────────────────────────────────────────
        private static bool recording;
        private static bool songOver;            // OnSongOver came: nothing more is recorded until a restart or the next song
        private static long recordStartMs;
        private static long nextRecordAtMs;
        private static readonly List<string> recorded = new List<string>();
        private static JObject recordedInfo;
        private static long nextInfoBuildAtMs;
        private static double lastRecordTick = -1;
        private static int recordingsSaved;
        private static volatile bool saveFinished; // a worker finished writing: the list is out of date
        private static string lastSaveResult;

        /// <summary>A song is being recorded right now (SpectatorEvents captures results for it).</summary>
        internal static bool IsRecording => recording;

        // ── Playback state ───────────────────────────────────────────────────
        /// <summary>A replay is loaded or loading: its player exists for MpPresence and the window.</summary>
        internal static bool Active { get; private set; }
        internal static bool Loading { get; private set; }
        internal static bool Paused { get; private set; }
        internal static bool Finished { get; private set; }
        internal static Entry Current { get; private set; }

        private class LoadResult
        {
            public string Error;
            public MpSpectateInfo Info;
            public readonly List<MpPoseEntry> Poses = new List<MpPoseEntry>();
            public readonly List<long> PoseTimes = new List<long>();
            public readonly List<MpSpectateEvent[]> Events = new List<MpSpectateEvent[]>();
            public readonly List<long> EventTimes = new List<long>();
            public int Bad;
        }

        private static int loadGeneration;              // a newer Play makes an older load's result stale
        private static LoadResult pendingLoad;          // set by the worker, taken by Tick (lock)
        private static readonly object loadLock = new object();

        private static LoadResult playing;
        private static long baseMs;
        private static int poseIndex, eventIndex;
        private static long pauseStartMs;
        private static MpPoseEntry frozen;              // the held pose while paused or finished
        private static long lastFedT;
        private static long nextFrozenFeedMs;
        private static float nextInfoRefresh;

        // ── Library ──────────────────────────────────────────────────────────

        /// <summary>One saved recording, from its header line.</summary>
        internal class Entry
        {
            public string Path;
            public string FileName;
            public long FileSize;
            public string SongId;
            public string Title;
            public string Artist;
            public string Difficulty;
            public long RecordedAt;          // unix seconds, UTC
            public long DurationMs;
            public long Score;               // Audica score
            public float? ExPercent;         // EX score, %, when it could be read
            public string Scoring;           // "EX" or "Audica": the scoring type when recorded
            public float[] LeftColor, RightColor;

            public DateTime LocalTime => DateTimeOffset.FromUnixTimeSeconds(RecordedAt).LocalDateTime;

            /// <summary>The score shown in lists: EX % with EX scoring, else the Audica score.</summary>
            public string ScoreText
            {
                get
                {
                    if (Scoring == "EX" && ExPercent.HasValue) return ExPercent.Value.ToString("0.00", CultureInfo.InvariantCulture) + "%";
                    return Score.ToString("N0", CultureInfo.InvariantCulture);
                }
            }

            public string LengthText
            {
                get
                {
                    long s = Math.Max(0, DurationMs / 1000);
                    return (s / 60) + ":" + (s % 60).ToString("00");
                }
            }
        }

        private static readonly Dictionary<string, Entry> headerCache = new Dictionary<string, Entry>();
        private static List<Entry> library;

        /// <summary>Every saved recording, newest first. Headers are read once per file and kept.</summary>
        internal static List<Entry> List()
        {
            if (library != null) return library;
            library = new List<Entry>();
            try
            {
                string folder = Folder;
                if (!Directory.Exists(folder)) return library;
                HashSet<string> seen = new HashSet<string>();
                foreach (string path in Directory.GetFiles(folder, "*" + FileExtension))
                {
                    seen.Add(path);
                    long size = new FileInfo(path).Length;
                    if (!headerCache.TryGetValue(path, out Entry entry) || entry.FileSize != size)
                    {
                        entry = ReadHeader(path, size);
                        headerCache[path] = entry;
                    }
                    if (entry != null) library.Add(entry);
                }
                // Forget files that are gone.
                List<string> gone = null;
                foreach (string path in headerCache.Keys) if (!seen.Contains(path)) (gone ?? (gone = new List<string>())).Add(path);
                if (gone != null) foreach (string path in gone) headerCache.Remove(path);
            }
            catch (Exception ex)
            {
                Log("listing the replays failed: " + ex.Message);
            }
            library.Sort((a, b) => b.RecordedAt.CompareTo(a.RecordedAt));
            return library;
        }

        private static void InvalidateList() => library = null;

        private static Entry ReadHeader(string path, long size)
        {
            try
            {
                string first;
                using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (GZipStream gzip = new GZipStream(file, CompressionMode.Decompress))
                using (StreamReader reader = new StreamReader(gzip, Encoding.UTF8))
                {
                    first = reader.ReadLine();
                }
                if (string.IsNullOrEmpty(first)) return null;
                JObject h = JObject.Parse(first)[HeaderKey] as JObject;
                if (h == null) return null;
                return new Entry
                {
                    Path = path,
                    FileName = System.IO.Path.GetFileName(path),
                    FileSize = size,
                    SongId = (string)h["songId"],
                    Title = (string)h["title"] ?? (string)h["songId"] ?? "?",
                    Artist = (string)h["artist"],
                    Difficulty = (string)h["difficulty"] ?? "?",
                    RecordedAt = h.Value<long?>("recordedAt") ?? 0,
                    DurationMs = h.Value<long?>("durationMs") ?? 0,
                    Score = h.Value<long?>("score") ?? 0,
                    ExPercent = h.Value<float?>("exPercent"),
                    Scoring = (string)h["scoring"] ?? "Audica",
                    LeftColor = ReadColor(h["gunColors"]?["left"]),
                    RightColor = ReadColor(h["gunColors"]?["right"])
                };
            }
            catch (Exception ex)
            {
                Log($"couldn't read {System.IO.Path.GetFileName(path)}: {ex.Message}");
                return null;
            }
        }

        private static float[] ReadColor(JToken token)
        {
            if (!(token is JArray a) || a.Count != 3) return null;
            try
            {
                return new[] { a[0].Value<float>(), a[1].Value<float>(), a[2].Value<float>() };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Moves a recording to UserData/ExScoring_Replays/Deleted (never listed), so a wrong shot
        /// can be undone by moving it back. Stops it first if it is the one playing.
        /// </summary>
        internal static void Delete(Entry entry)
        {
            if (entry == null) return;
            try
            {
                if (Active && Current != null && Current.Path == entry.Path) Stop("its file was deleted");
                string folder = DeletedFolder;
                Directory.CreateDirectory(folder);
                string target = System.IO.Path.Combine(folder, entry.FileName);
                for (int i = 2; File.Exists(target); i++)
                    target = System.IO.Path.Combine(folder, entry.FileName.Replace(FileExtension, "") + " (" + i + ")" + FileExtension);
                File.Move(entry.Path, target);
                headerCache.Remove(entry.Path);
                Log($"{entry.FileName} moved to {folder}");
            }
            catch (Exception ex)
            {
                Log($"deleting {entry.FileName} failed: {ex.Message}");
            }
            InvalidateList();
        }

        /// <summary>Opens the replays folder on the desktop.</summary>
        internal static void OpenFolder()
        {
            try
            {
                string folder = System.IO.Path.GetFullPath(Folder);
                Directory.CreateDirectory(folder);
                Application.OpenURL("file:///" + folder.Replace('\\', '/'));
            }
            catch (Exception ex)
            {
                Log("opening the folder failed: " + ex.Message);
            }
        }

        // ── Tick ─────────────────────────────────────────────────────────────

        /// <summary>Once per frame from ExScoring.OnUpdate, after MpPresence.Tick and before SpectatorWindow.Tick.</summary>
        public static void Tick()
        {
            try
            {
                if (saveFinished)
                {
                    saveFinished = false;
                    InvalidateList();
                    if (lastSaveResult != null) Log(lastSaveResult);
                    GlobalOptions.RedrawIfShowing(ReplaysPage.CategoryId);
                }

                TickRecord(MpPresence.InSongForRecording);
                TickPlayback();
            }
            catch (Exception ex)
            {
                MelonLogger.Log("[Replays] tick failed: " + ex);
                Stop("error");
            }
        }

        // ── Record ───────────────────────────────────────────────────────────

        private static void TickRecord(bool inSong)
        {
            if (!inSong)
            {
                if (recording) Discard("left the song before its end");
                songOver = false;
                lastRecordTick = -1;
                return;
            }

            // A restart (or a playlist's next song) brings the song position back: a new run.
            double tick = CurrentTick();
            if (tick >= 0)
            {
                if (lastRecordTick >= 0 && tick < lastRecordTick - RestartTicks)
                {
                    if (recording) Discard("the song restarted");
                    songOver = false;
                }
                lastRecordTick = tick;
            }

            if (songOver) return;
            if (!Config.RecordGameplay)
            {
                if (recording) Discard("Record Gameplay was turned off");
                return;
            }
            if (InPracticeMode)
            {
                if (recording) Discard("practice mode");
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
            // per-song target speed is only in place once the song has started).
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

            JObject pose = MpPresence.BuildRecordingPose(now - recordStartMs);
            if (pose != null) recorded.Add(pose.ToString(Formatting.None));
        }

        private static double CurrentTick()
        {
            try
            {
                AudioDriver audio = AudioDriver.I;
                if (audio == null) return -1;
                double tick = audio.GetTick(AudioDriver.TickContext.Audio);
                return double.IsNaN(tick) || double.IsInfinity(tick) ? -1 : tick;
            }
            catch
            {
                return -1;
            }
        }

        /// <summary>From SpectatorEvents.FlushIfDue: a batch of results, kept with the recording's clock.</summary>
        internal static void RecordEvents(JArray events)
        {
            if (!recording || events == null || events.Count == 0 || recorded.Count >= MaxPoses) return;
            recorded.Add(new JObject
            {
                [EventsLineKey] = events,
                ["t"] = MpSession.LocalNowMs - recordStartMs
            }.ToString(Formatting.None));
        }

        private static void Discard(string reason)
        {
            recording = false;
            Log($"recording dropped ({reason}): {recorded.Count} lines");
            recorded.Clear();
        }

        /// <summary>The song ended (SongEnd.OnSongOver). A completed run is saved; a failed one is dropped.</summary>
        private static void OnSongOver(bool failed)
        {
            songOver = true;
            if (!recording) return;
            if (failed)
            {
                Discard("the song was failed");
                return;
            }
            // The last results of the song, before the scores are read.
            SpectatorEvents.FlushIfDue();
            Save();
        }

        private static void Save()
        {
            recording = false;
            int poses = 0;
            foreach (string line in recorded) if (!line.StartsWith("{\"" + EventsLineKey)) poses++;
            if (poses < MinPoses)
            {
                Log($"recording dropped: only {poses} poses");
                recorded.Clear();
                return;
            }

            JObject header;
            string fileName;
            try
            {
                header = BuildHeader(MpSession.LocalNowMs - recordStartMs);
                string songId = (string)header["songId"] ?? "song";
                string difficulty = (string)header["difficulty"] ?? "Unknown";
                fileName = SanitizeFileName($"{songId}_{difficulty}_{DateTime.Now:yyyyMMdd-HHmmss}") + FileExtension;
            }
            catch (Exception ex)
            {
                Log("recording dropped: couldn't build its header: " + ex.Message);
                recorded.Clear();
                return;
            }

            List<string> lines = new List<string>(recorded.Count + 2);
            lines.Add(new JObject { [HeaderKey] = header }.ToString(Formatting.None));
            if (recordedInfo != null) lines.Add(new JObject { [InfoLineKey] = recordedInfo }.ToString(Formatting.None));
            lines.AddRange(recorded);
            recorded.Clear();

            string folder = Folder;
            string path = System.IO.Path.Combine(folder, fileName);
            double seconds = (header.Value<long?>("durationMs") ?? 0) / 1000.0;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Directory.CreateDirectory(folder);
                    using (FileStream file = new FileStream(path, FileMode.Create))
                    using (GZipStream gzip = new GZipStream(file, System.IO.Compression.CompressionLevel.Optimal))
                    using (StreamWriter writer = new StreamWriter(gzip, new UTF8Encoding(false)))
                    {
                        foreach (string line in lines) writer.WriteLine(line);
                    }
                    Interlocked.Increment(ref recordingsSaved);
                    lastSaveResult = $"replay saved: {poses} poses, {seconds:0.0} s -> {path}";
                }
                catch (Exception ex)
                {
                    lastSaveResult = "saving the replay failed: " + ex.Message;
                }
                saveFinished = true;
            });
        }

        private static JObject BuildHeader(long durationMs)
        {
            JObject song = recordedInfo?["song"] as JObject;
            SongDataHolder holder = SongDataHolder.I;
            SongList.SongData data = holder != null ? holder.songData : null;
            KataConfig config = KataConfig.I;

            long score = 0;
            try
            {
                ScoreKeeper keeper = ScoreKeeper.I;
                if (keeper != null) score = (long)keeper.mScore;
            }
            catch { }

            float? exPercent = null;
            try
            {
                if (ExScoring.maxPossibleExScore > 0f)
                {
                    float p = ExScoring.GetCurrentExPercentage();
                    if (!float.IsNaN(p) && !float.IsInfinity(p)) exPercent = (float)Math.Round(p, 2);
                }
            }
            catch { }

            Color left = config != null ? config.leftHandColor : MpPresence.DefaultLeftColor;
            Color right = config != null ? config.rightHandColor : MpPresence.DefaultRightColor;

            JObject header = new JObject
            {
                ["v"] = FormatVersion,
                ["songId"] = (string)song?["songId"] ?? data?.songID,
                ["title"] = (string)song?["title"] ?? data?.title,
                ["artist"] = (string)song?["artist"] ?? data?.artist,
                ["mapper"] = (string)song?["mapper"] ?? data?.author,
                ["difficulty"] = (string)song?["difficulty"] ?? config?.GetDifficulty().ToString(),
                ["recordedAt"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                ["durationMs"] = durationMs,
                ["score"] = score,
                ["exPercent"] = exPercent.HasValue ? (JToken)exPercent.Value : JValue.CreateNull(),
                ["scoring"] = Config.ExType ? "EX" : "Audica",
                ["gunColors"] = new JObject
                {
                    ["left"] = new JArray(Math.Round(left.r, 3), Math.Round(left.g, 3), Math.Round(left.b, 3)),
                    ["right"] = new JArray(Math.Round(right.r, 3), Math.Round(right.g, 3), Math.Round(right.b, 3))
                }
            };
            return header;
        }

        private static string SanitizeFileName(string name)
        {
            StringBuilder sb = new StringBuilder(name.Length);
            char[] invalid = System.IO.Path.GetInvalidFileNameChars();
            foreach (char c in name) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            return sb.ToString();
        }

        [HarmonyPatch(typeof(SongEnd), "OnSongOver", new Type[] { typeof(bool) })]
        private static class SongEndOnSongOverPatch
        {
            private static void Postfix(bool failed)
            {
                try
                {
                    OnSongOver(failed);
                }
                catch (Exception ex)
                {
                    Log("song over: " + ex.Message);
                }
            }
        }

        // ── Playback ─────────────────────────────────────────────────────────

        /// <summary>Starts watching a recording (outside songs). The spectator window opens on it.</summary>
        internal static void Play(Entry entry)
        {
            if (entry == null) return;
            if (InSongScene())
            {
                Log("can't start a replay during a song");
                return;
            }
            Stop("another replay");

            Active = true;
            Loading = true;
            Paused = false;
            Finished = false;
            Current = entry;
            int generation = ++loadGeneration;
            string path = entry.Path;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                LoadResult result = Load(path);
                lock (loadLock)
                {
                    if (generation == loadGeneration) pendingLoad = result;
                }
            });
            SpectatorWindow.WatchReplay();
            GlobalOptions.RedrawIfShowing(ReplaysPage.CategoryId);
            Log($"loading {entry.FileName}");
        }

        /// <summary>Pause or resume (not while loading or finished).</summary>
        internal static void TogglePause()
        {
            if (!Active || Loading || Finished) return;
            long now = MpSession.ServerNowMs;
            if (!Paused)
            {
                Paused = true;
                pauseStartMs = now;
                nextFrozenFeedMs = now;
                Log("paused");
            }
            else
            {
                Paused = false;
                baseMs += now - pauseStartMs;
                frozen = null;
                Log("resumed");
            }
        }

        /// <summary>From the start. The player is removed first, so the window's song, chart and targets start over.</summary>
        internal static void Restart()
        {
            if (!Active || Loading || playing == null) return;
            RemovePlayer();
            StartFeeding();
            // A short gap with no player, so the window ends its song session and targets before
            // the first pose comes back (else the same frame's pose would keep them going).
            baseMs += RestartGapMs;
            Log("restarted");
        }

        /// <summary>Ends the replay and removes its player.</summary>
        internal static void Stop(string reason)
        {
            lock (loadLock)
            {
                loadGeneration++;
                pendingLoad = null;
            }
            if (!Active) return;
            RemovePlayer();
            SpectatorSong.ClearInfo(UserId);
            Active = false;
            Loading = false;
            Paused = false;
            Finished = false;
            playing = null;
            frozen = null;
            Current = null;
            Log($"replay stopped ({reason})");
            GlobalOptions.RedrawIfShowing(ReplaysPage.CategoryId);
        }

        private static void RemovePlayer()
        {
            MpPresence.RemoveReplayPlayer(UserId);
            SpectatorEvents.ClearFor(UserId);
        }

        private static bool InSongScene()
        {
            MenuState.State state = ExScoring.menuState;
            return state == MenuState.State.Launching || state == MenuState.State.Launched;
        }

        private static void TickPlayback()
        {
            if (!Active) return;
            if (InSongScene())
            {
                Stop("a song started");
                return;
            }

            if (Loading)
            {
                LoadResult result;
                lock (loadLock)
                {
                    result = pendingLoad;
                    pendingLoad = null;
                }
                if (result == null) return;
                Loading = false;
                if (result.Error != null)
                {
                    Log("can't play " + (Current != null ? Current.FileName : "the replay") + ": " + result.Error);
                    Stop("unreadable");
                    return;
                }
                if (result.Bad > 0) Log($"{result.Bad} unreadable or out-of-order lines skipped");
                playing = result;
                if (playing.Info == null) Log("this recording has no song info: no audio, chart or targets");
                StartFeeding();
                Log($"replay started: {playing.Poses.Count} poses, {playing.Events.Count} result batches, " +
                    $"{playing.PoseTimes[playing.PoseTimes.Count - 1] / 1000.0:0.0} s");
            }

            if (playing == null) return;

            // The song info is sticky, but leaving a lobby clears every player's: given again now and then.
            if (playing.Info != null && Time.unscaledTime >= nextInfoRefresh)
            {
                nextInfoRefresh = Time.unscaledTime + InfoRefreshSeconds;
                SpectatorSong.SetInfo(playing.Info, true);
            }

            long now = MpSession.ServerNowMs;
            if (Paused || Finished)
            {
                FeedFrozen(now);
                return;
            }

            // Every pose whose time has come, as if the server had just relayed it.
            while (poseIndex < playing.Poses.Count && baseMs + playing.PoseTimes[poseIndex] <= now)
            {
                MpPoseEntry entry = playing.Poses[poseIndex];
                long t = baseMs + playing.PoseTimes[poseIndex];
                poseIndex++;
                if (t <= lastFedT) continue;
                entry.userId = UserId;
                entry.t = t;
                entry.inSong = true;
                MpPresence.InjectReplayPose(entry);
                lastFedT = t;
                frozen = entry;
            }

            // Results whose time has come.
            while (eventIndex < playing.Events.Count && baseMs + playing.EventTimes[eventIndex] <= now)
            {
                SpectatorEvents.Receive(UserId, playing.Events[eventIndex]);
                eventIndex++;
            }

            if (poseIndex >= playing.Poses.Count)
            {
                Finished = true;
                frozen = frozen != null ? Clone(frozen) : null;
                nextFrozenFeedMs = now;
                Log("replay finished: holding the last pose (Restart or Stop)");
                GlobalOptions.RedrawIfShowing(ReplaysPage.CategoryId);
            }
        }

        private static void StartFeeding()
        {
            baseMs = MpSession.ServerNowMs;
            poseIndex = 0;
            eventIndex = 0;
            lastFedT = 0;
            frozen = null;
            Paused = false;
            Finished = false;
            nextInfoRefresh = 0f;
            if (playing.Info != null) SpectatorSong.SetInfo(playing.Info, true);
        }

        /// <summary>Paused or finished: the last pose again, with a fresh time, so the player stays and their song position stops.</summary>
        private static void FeedFrozen(long now)
        {
            if (frozen == null || now < nextFrozenFeedMs || now <= lastFedT) return;
            nextFrozenFeedMs = now + FrozenFeedIntervalMs;
            MpPoseEntry held = Clone(frozen);
            held.t = now;
            MpPresence.InjectReplayPose(held);
            lastFedT = now;
        }

        private static MpPoseEntry Clone(MpPoseEntry e)
        {
            return new MpPoseEntry
            {
                userId = e.userId,
                t = e.t,
                head = e.head,
                lHand = e.lHand,
                rHand = e.rHand,
                lGun = e.lGun,
                rGun = e.rGun,
                lTrigger = e.lTrigger,
                rTrigger = e.rTrigger,
                lPull = e.lPull,
                rPull = e.rPull,
                lShots = e.lShots,
                rShots = e.rShots,
                lHandAnim = e.lHandAnim,
                rHandAnim = e.rHandAnim,
                lFingers = e.lFingers,
                rFingers = e.rFingers,
                inSong = e.inSong,
                songTick = e.songTick
            };
        }

        /// <summary>Worker thread: the whole file, parsed. No Unity calls in here.</summary>
        private static LoadResult Load(string path)
        {
            LoadResult result = new LoadResult();
            try
            {
                long last = -1;
                using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (GZipStream gzip = new GZipStream(file, CompressionMode.Decompress))
                using (StreamReader reader = new StreamReader(gzip, Encoding.UTF8))
                {
                    string text;
                    while ((text = reader.ReadLine()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(text)) continue;
                        try
                        {
                            JObject line = JObject.Parse(text);
                            if (line[HeaderKey] != null) continue;
                            if (line[InfoLineKey] != null)
                            {
                                MpSpectateInfo info = line[InfoLineKey].ToObject<MpSpectateInfo>(MpJson.Serializer);
                                if (info != null && info.song != null && !string.IsNullOrEmpty(info.song.songId))
                                {
                                    info.userId = UserId;
                                    result.Info = info;
                                }
                                continue;
                            }
                            if (line[EventsLineKey] != null)
                            {
                                MpSpectateEvent[] batch = line[EventsLineKey].ToObject<MpSpectateEvent[]>(MpJson.Serializer);
                                if (batch != null && batch.Length > 0)
                                {
                                    result.Events.Add(batch);
                                    result.EventTimes.Add(line.Value<long>("t"));
                                }
                                continue;
                            }
                            MpPoseEntry entry = line.ToObject<MpPoseEntry>(MpJson.Serializer);
                            if (entry == null || entry.t <= last) { result.Bad++; continue; }
                            last = entry.t;
                            result.PoseTimes.Add(entry.t);
                            result.Poses.Add(entry);
                        }
                        catch
                        {
                            result.Bad++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                result.Error = ex.Message;
                return result;
            }
            if (result.Poses.Count < 2) result.Error = "the recording is empty";
            return result;
        }

        // ── For the window and MpPresence ────────────────────────────────────

        /// <summary>The replay player's gun colours, from the recording's header. False without a replay.</summary>
        internal static bool TryGetGunColors(out Color left, out Color right)
        {
            left = MpPresence.DefaultLeftColor;
            right = MpPresence.DefaultRightColor;
            Entry entry = Current;
            if (!Active || entry == null) return false;
            if (entry.LeftColor != null) left = new Color(entry.LeftColor[0], entry.LeftColor[1], entry.LeftColor[2], 1f);
            if (entry.RightColor != null) right = new Color(entry.RightColor[0], entry.RightColor[1], entry.RightColor[2], 1f);
            return true;
        }

        /// <summary>The window title's first line for the replay (rich text).</summary>
        internal static string TitleText
        {
            get
            {
                Entry entry = Current;
                string text = "Replay";
                if (entry != null) text += ": " + entry.Difficulty + " · " + entry.ScoreText;
                if (Loading) text += " <color=#c0c0c0>(loading...)</color>";
                else if (Finished) text += " <color=#c0c0c0>(finished)</color>";
                else if (Paused) text += " <color=#c0c0c0>(paused)</color>";
                return text;
            }
        }

        public static void DebugDump()
        {
            MelonLogger.Log("[Replays] ── dump ──");
            MelonLogger.Log($"  record={Config.RecordGameplay} recording={recording} lines={recorded.Count} songOver={songOver} practice={InPracticeMode} saved={recordingsSaved}");
            MelonLogger.Log($"  active={Active} loading={Loading} paused={Paused} finished={Finished} file={(Current != null ? Current.FileName : "none")}");
            if (playing != null)
                MelonLogger.Log($"  poses={playing.Poses.Count} next={poseIndex} results={playing.Events.Count} next={eventIndex} song={(playing.Info != null ? playing.Info.song.songId + " [" + playing.Info.song.difficulty + "]" : "none")}");
            MelonLogger.Log($"  folder={Folder} listed={(library != null ? library.Count.ToString() : "not read")}");
        }
    }
}