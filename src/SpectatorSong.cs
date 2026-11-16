using System;
using System.Collections;
using System.Collections.Generic;
using MelonLoader;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// The song behind the spectator window: while the window shows a player who is in a song, this
    /// makes sure the watcher has that song, plays its audio in time with the watched player, and
    /// loads its chart (for the targets, next step). Step 1 of the target work; findings in
    /// claude/Spectator-Window-Target-Findings.md, protocol in claude/ApiContract-v2.53-addendum.md.
    ///
    /// The watched player (SendInfoIfDue, from MpPresence's in-song branch, so only while watched
    /// and sharing): every InfoIntervalMs, and at once when their watchers change, sends
    /// spectateInfo = the song (id, difficulty, title, artist, mapper) and the settings a watcher
    /// needs (target speed, melee speed, player height). Nothing about the song position: that is songTick, already
    /// in every pose.
    ///
    /// The watcher (Tick, once a frame after SpectatorWindow.Tick), for the player the window shows
    /// while MpPresence lists them as in a song:
    ///  1. Song info: the latest spectateInfo from that player (kept InfoStaleMs, dropped when they
    ///     leave the song). None after InfoWaitSeconds = the view is shown without audio.
    ///  2. The song itself: installed = ready. Missing = looked up on Maudica (MaudicaLookup: exact
    ///     version by song id, then title + artist + mapper) and downloaded with
    ///     SongDownloader.DownloadSong, the same way the lobby fetches its selected song. Not when
    ///     Auto Download Songs is off or the song was deleted this session. A song that can't be
    ///     had can't be spectated: the window shows why and no view (ViewAllowed). A failure is
    ///     tried again after RetrySeconds.
    ///  3. Chart: SongCues.GetCues + HookUpChains, once per song and difficulty, kept in Cues for
    ///     the target step (nothing is drawn from it yet).
    ///  4. Audio: the song's mogg is played as its own sound, set up like a song preview (the way
    ///     MenuMusic's menu track is), at the menu preview gain times Spectator Volume
    ///     (Config.SpectatorVolume, 0..100 %). Every frame the watched player's
    ///     song position at the moment their avatar is drawn (MpPresence.TryGetSongTick: their
    ///     songTick, RenderDelayMs behind like their pose) is turned into milliseconds with
    ///     AudioDriver.TickSpanToMs on this client's copy of the song, and compared with the
    ///     sound's own position (the gap). Seeking this sound is slow and asynchronous (log
    ///     2026-11-14: a seek a second piles up and the sound never settles; native AudioDriver
    ///     tracks a pending seek the same way), so:
    ///      - one seek when the sound starts or the gap is over SeekThresholdMs, aimed SeekLeadMs
    ///        ahead, and no other until it has settled (gap under SeekSettledMs); a seek that hasn't
    ///        settled after SeekTimeoutSeconds, or the watched player going back more than
    ///        RestartBackMs (their song restarted, the debug replay looped), stops the sound and a
    ///        new one is started. Seeking the old one back from its end never settled (log
    ///        2026-11-16: about 2.7 s off after every seek until the game closed); a fresh start's
    ///        first seek does;
    ///      - the rest of the gap is closed with the playback speed (Sound.SetSpeed, what practice
    ///        mode calls every frame on the song): 1 + gap / SpeedGainMs, within CatchUpSpeed while
    ///        out of sync and FineSpeed once in sync, exactly 1 inside SpeedDeadbandMs;
    ///      - while the watched player's position isn't advancing (paused) or there is none, the
    ///        sound is paused (Sound.Pause / Continue).
    ///     It is heard only while in sync (gap within InSyncEnterMs, until it passes InSyncLeaveMs).
    ///
    /// Song previews (Config.SpectatorAudioOverPreviews, default on): while a sound exists,
    /// PreviewMute rises to 1 with MenuDuck and the preview's gain is scaled by 1 - PreviewMute
    /// (AudioNormalizer's Sound.SetGain prefix, which native SongPreview.Update goes through every
    /// frame), so only the watched player's song is heard. Off: a preview lowers this sound, like
    /// the menu track.
    ///
    /// While a sound exists, MenuDuck rises to 1 and MenuMusic lowers all menu music by it.
    ///
    /// Not handled: a watched player whose song speed isn't 1 (practice mode): the audio would
    /// lag behind and re-seek. OffsetMs (UnityExplorer) shifts the audio if it is constantly early or late.
    ///
    /// From the UnityExplorer console: ExScoringMod.SpectatorSong.DebugDump();
    /// </summary>
    internal static class SpectatorSong
    {
        public enum Stage
        {
            /// <summary>Not watching anyone who is in a song.</summary>
            None,
            /// <summary>In a song, their spectateInfo hasn't arrived yet.</summary>
            WaitingInfo,
            /// <summary>In a song, no spectateInfo came: view without audio.</summary>
            NoInfo,
            /// <summary>The song is missing: being looked up on Maudica.</summary>
            Searching,
            /// <summary>The song is missing: being downloaded.</summary>
            Downloading,
            /// <summary>The song can't be had: no spectating, UnavailableReason says why.</summary>
            Unavailable,
            /// <summary>The song is installed: audio and chart.</summary>
            Ready
        }

        // ── Tunables ─────────────────────────────────────────────────────────
        private const long InfoIntervalMs = 2000;        // the watched player repeats spectateInfo this often
        private const long InfoStaleMs = 6500;           // three missed repeats: the info no longer counts
        private const float InfoWaitSeconds = 5f;        // without info this long: view only, no audio
        private const float RetrySeconds = 60f;          // a failed lookup or download is tried again after this
        private const float MaudicaSearchTimeoutSeconds = 80f;

        private const string PreviewEvent = "event:/shell/song_preview"; // as SongPreview and MenuMusic's track
        private const float InitialVolumeDb = -96f;
        private const float FallbackGain = 0.4f;         // SongPreview.gain when there's no SongPreview
        private const float FadeSeconds = 0.25f;
        private const float DuckFadeSeconds = 0.7f;
        private const double SeekThresholdMs = 1000;     // a gap this big is seeked, not caught up with speed
        private const double SeekLeadMs = 300;           // a seek lands about this late (log 2026-11-14)
        private const double SeekSettledMs = 700;        // a pending seek is done once the gap is under this
        private const float SeekTimeoutSeconds = 5f;     // a seek that never settles: the sound is started again
        private const double RestartBackMs = 5000;       // the watched player this far behind the sound (song restarted, replay looped): started again
        private const double SpeedGainMs = 1000;         // speed = 1 + gap / this: a 30 ms gap plays 3% faster
        private const double CatchUpSpeed = 0.15;        // most the speed differs from 1 while out of sync (silent)
        private const double FineSpeed = 0.03;           // and while in sync (heard)
        private const double SpeedDeadbandMs = 8;        // closer than this: speed exactly 1
        private const double InSyncEnterMs = 100;        // gap within this: in sync, heard
        private const double InSyncLeaveMs = 200;        // gap past this: out of sync, silent
        private const float GapSmoothing = 8f;           // per second: how fast the gap used for the speed follows the measured one
        private const float StatusLogSeconds = 5f;
        private const float StartRetrySeconds = 2f;
        private const double EndMarginMs = 50;

        /// <summary>Added to the audio position (ms), from the UnityExplorer console, if the audio is constantly early (raise it) or late (lower it).</summary>
        public static double OffsetMs = 0;

        // ── State: watcher ───────────────────────────────────────────────────
        private class Info
        {
            public MpSpectateInfo Data;
            public float ReceivedAt;      // Time.unscaledTime
            public bool Sticky;           // the debug replay's: never stale
        }

        private static readonly Dictionary<string, Info> infos = new Dictionary<string, Info>();
        private static readonly Dictionary<string, KeyValuePair<float, string>> failures = new Dictionary<string, KeyValuePair<float, string>>();

        private static bool initialized;
        private static string sessionUser;       // the in-song player being followed
        private static float sessionStart;
        private static string sessionKey;        // songId|difficulty the rest of the state is for
        private static Stage stage = Stage.None;
        private static string unavailableReason;
        private static string songTitle;

        private static SongList.SongData song;
        private static KataConfig.Difficulty difficulty;
        private static bool chartTried;
        private static readonly List<SongCues.Cue> cues = new List<SongCues.Cue>();

        private static string downloadingSongId;
        private static bool downloadSearching;
        private static float downloadProgress;

        private static uint handle = Hmx.Audio.Sound.kInvalidSoundHandle;
        private static float fade;
        private static float duck;
        private static bool seekedOnce;          // this sound has been seeked at least once
        private static bool seekPending;         // a seek was issued and hasn't settled yet
        private static float seekIssuedAt;
        private static bool paused;              // the sound is paused because the watched player is
        private static bool inSync;
        private static float speed = 1f;         // the speed last set on the sound
        private static double smoothedGap;
        private static bool hasSmoothedGap;
        private static float nextStatusLog;
        private static float nextStartAttempt;
        private static int seekCount;
        private static double lastGap;
        private static double lastWantMs;
        private static bool lastMoving;
        private static bool lastHasTick;
        private static string lastError;

        // ── State: watched player ────────────────────────────────────────────
        private static long nextInfoAtMs;

        private static void Log(string message) => DebugLog.Log("[SpectatorSong] " + message);

        // ── What the others read ─────────────────────────────────────────────

        /// <summary>0..1: how much the menu music is lowered because a watched player's song is playing here.</summary>
        internal static float MenuDuck => duck;

        /// <summary>0..1: how much song previews are silenced because a watched player's song is playing here (Spectator Audio Over Previews).</summary>
        internal static float PreviewMute => Config.SpectatorAudioOverPreviews ? duck : 0f;

        /// <summary>The spectator window is looking up or downloading this song right now.</summary>
        internal static bool IsDownloading(string songId)
        {
            return songId != null && songId == downloadingSongId;
        }

        /// <summary>Where things stand for the player the window shows (None when they aren't in a song).</summary>
        internal static Stage CurrentStage => stage;

        /// <summary>The watched player's song once it's installed here, else null.</summary>
        internal static SongList.SongData Song => stage == Stage.Ready ? song : null;
        internal static KataConfig.Difficulty Difficulty => difficulty;

        /// <summary>The watched player's chart (chains hooked up), empty until loaded. For the target step.</summary>
        internal static List<SongCues.Cue> Cues => cues;

        /// <summary>The in-song player being followed, or null.</summary>
        internal static string WatchedUserId => sessionUser;

        /// <summary>
        /// The watched player's song position at the moment their avatar is drawn, in milliseconds
        /// and ticks on this client's copy of the song. False while the song isn't ready or there
        /// is no position. OffsetMs (audio only) isn't included.
        /// </summary>
        internal static bool TryGetPosition(out double ms, out double tick)
        {
            ms = 0;
            tick = 0;
            if (stage != Stage.Ready || song == null || sessionUser == null) return false;
            if (!MpPresence.TryGetSongTick(sessionUser, out tick, out bool moving)) return false;
            ms = AudioDriver.TickSpanToMs(song, 0f, (float)tick);
            return true;
        }

        /// <summary>
        /// Where the watched player's audio should be right now (ms, OffsetMs included) and whether
        /// it is running (false while they are paused). False when there is no song position.
        /// For SpectatorSounds' sustain tracks, which follow the same position as the song.
        /// </summary>
        internal static bool TryGetAudioTarget(out double wantMs, out bool playing)
        {
            wantMs = lastWantMs;
            playing = lastHasTick && lastMoving && lastWantMs >= 0;
            return stage == Stage.Ready && song != null && lastHasTick;
        }

        /// <summary>
        /// Where the watched player's song audibly is right now on this client (the sound's own
        /// position, in chart milliseconds: OffsetMs taken out). False while it isn't playing in
        /// sync. For SpectatorSounds, which plays target sounds on the beat of what is heard.
        /// </summary>
        internal static bool TryGetAudiblePositionMs(out double ms)
        {
            ms = 0;
            if (stage != Stage.Ready || handle == Hmx.Audio.Sound.kInvalidSoundHandle || paused || !inSync) return false;
            ms = Hmx.Audio.Sound.GetTimelineMs(handle) - OffsetMs;
            return true;
        }

        /// <summary>The watched player's settings for this song, or null without info.</summary>
        internal static MpSpectateSettings Settings
        {
            get
            {
                Info info = sessionUser != null ? ValidInfo(sessionUser) : null;
                return info != null ? info.Data.settings : null;
            }
        }

        /// <summary>
        /// False while the window must not show userId's view: they are in a song that isn't
        /// installed here (yet), or it isn't known yet what they are playing.
        /// </summary>
        internal static bool ViewAllowed(string userId)
        {
            if (userId == null || userId != sessionUser) return true;
            return stage == Stage.None || stage == Stage.NoInfo || stage == Stage.Ready;
        }

        /// <summary>A second title line for the window about userId's song (rich text), or null for none.</summary>
        internal static string StatusText(string userId)
        {
            if (userId == null || userId != sessionUser) return null;
            string title = "<noparse>" + (songTitle ?? "the song").Replace("</noparse>", "") + "</noparse>";
            switch (stage)
            {
                case Stage.WaitingInfo:
                    return "<color=#c0c0c0>In a song...</color>";
                case Stage.NoInfo:
                    return "<color=#c0c0c0>In a song (no song info: no audio)</color>";
                case Stage.Searching:
                    return "<color=#c0c0c0>Looking up " + title + " on Maudica...</color>";
                case Stage.Downloading:
                    return "<color=#c0c0c0>Downloading " + title + " " + Mathf.RoundToInt(Mathf.Clamp01(downloadProgress) * 100f) + "%</color>";
                case Stage.Unavailable:
                    return "<color=#ff8080>Can't spectate " + title + ": " + (unavailableReason ?? "the song isn't available") + "</color>";
                case Stage.Ready:
                    return "<color=#c0c0c0>" + title + "</color>";
                default:
                    return null;
            }
        }

        // ── The watched player's side ────────────────────────────────────────

        /// <summary>
        /// From MpPresence's in-song branch, only while this player is watched and sharing: sends
        /// spectateInfo every InfoIntervalMs, and at once when the watchers changed.
        /// </summary>
        internal static void SendInfoIfDue()
        {
            EnsureInit();
            long now = MpSession.LocalNowMs;
            if (now < nextInfoAtMs) return;
            nextInfoAtMs = now + InfoIntervalMs;

            JObject info = BuildLocalInfo();
            if (info != null) MpSession.SendSpectateInfo(info);
        }

        /// <summary>This player's spectateInfo for the song they are in right now, or null when it can't be read.</summary>
        internal static JObject BuildLocalInfo()
        {
            try
            {
                SongDataHolder holder = SongDataHolder.I;
                SongList.SongData data = holder != null ? holder.songData : null;
                KataConfig config = KataConfig.I;
                if (data == null || config == null || string.IsNullOrEmpty(data.songID)) return null;

                JObject settings = new JObject();
                PlayerPreferences prefs = PlayerPreferences.I;
                if (prefs != null)
                {
                    settings["targetSpeed"] = Math.Round((double)prefs.TargetSpeedMultiplier.mVal, 3);
                    settings["meleeSpeed"] = Math.Round((double)prefs.MeleeSpeedMultiplier.mVal, 3);
                    settings["playerHeight"] = Math.Round((double)prefs.PlayerHeightInches.mVal, 2);
                }

                return new JObject
                {
                    ["song"] = new JObject
                    {
                        ["songId"] = data.songID,
                        ["difficulty"] = config.GetDifficulty().ToString(),
                        ["title"] = data.title,
                        ["artist"] = data.artist,
                        ["mapper"] = data.author
                    },
                    ["settings"] = settings
                };
            }
            catch (Exception ex)
            {
                Log("couldn't build this player's song info: " + ex.Message);
                return null;
            }
        }

        // ── Song info coming in ──────────────────────────────────────────────

        private static void EnsureInit()
        {
            if (initialized) return;
            initialized = true;
            MpSession.OnSpectateInfo += info => SetInfo(info, false);
            MpSession.OnWatchers += count => nextInfoAtMs = 0; // a new watcher gets the info right away
            MpSession.OnLeftLobby += reason => infos.Clear();
        }

        /// <summary>A watched player's spectateInfo (from the server, or from the debug replay with sticky).</summary>
        internal static void SetInfo(MpSpectateInfo info, bool sticky)
        {
            if (info == null || string.IsNullOrEmpty(info.userId) || info.song == null || string.IsNullOrEmpty(info.song.songId)) return;

            bool changed = !infos.TryGetValue(info.userId, out Info old) || KeyOf(old.Data) != KeyOf(info);
            infos[info.userId] = new Info { Data = info, ReceivedAt = Time.unscaledTime, Sticky = sticky };
            if (changed)
            {
                MpSpectateSettings s = info.settings;
                Log($"{info.userId} is playing {info.song.songId} [{info.song.difficulty}] \"{info.song.title}\"" +
                    $" target speed {(s != null && s.targetSpeed.HasValue ? s.targetSpeed.Value.ToString("0.##") : "?")}" +
                    $" melee speed {(s != null && s.meleeSpeed.HasValue ? s.meleeSpeed.Value.ToString("0.##") : "?")}");
            }
        }

        internal static void ClearInfo(string userId)
        {
            if (userId != null) infos.Remove(userId);
        }

        private static string KeyOf(MpSpectateInfo info)
        {
            return info.song.songId + "|" + (info.song.difficulty ?? "");
        }

        private static Info ValidInfo(string userId)
        {
            if (!infos.TryGetValue(userId, out Info info)) return null;
            if (!info.Sticky && (Time.unscaledTime - info.ReceivedAt) * 1000f > InfoStaleMs) return null;
            return info;
        }

        // ── Tick ─────────────────────────────────────────────────────────────

        /// <summary>Once per frame from ExScoring.OnUpdate, after SpectatorWindow.Tick.</summary>
        public static void Tick()
        {
            float dt = Time.unscaledDeltaTime;
            try
            {
                EnsureInit();

                string userId = SpectatorWindow.ShownWatchedId;
                bool inSong = userId != null && MpPresence.IsRemoteInSong(userId);
                if (!inSong)
                {
                    if (sessionUser != null)
                    {
                        // They left the song (or the window moved on): what they played no longer counts.
                        if (!MpPresence.IsRemoteInSong(sessionUser) && infos.TryGetValue(sessionUser, out Info old) && !old.Sticky)
                            infos.Remove(sessionUser);
                        EndSession("not watching a player in a song");
                    }
                    UpdateDuck(dt);
                    return;
                }

                if (userId != sessionUser)
                {
                    EndSession("watching someone else");
                    sessionUser = userId;
                    sessionStart = Time.unscaledTime;
                    stage = Stage.WaitingInfo;
                }

                Info info = ValidInfo(userId);
                if (info == null)
                {
                    if (handle != Hmx.Audio.Sound.kInvalidSoundHandle) StopSound("no song info any more");
                    ResetSong();
                    stage = Time.unscaledTime - sessionStart < InfoWaitSeconds ? Stage.WaitingInfo : Stage.NoInfo;
                    UpdateDuck(dt);
                    return;
                }

                string key = KeyOf(info.Data);
                if (key != sessionKey)
                {
                    StopSound("another song");
                    ResetSong();
                    sessionKey = key;
                    songTitle = info.Data.song.title;
                }

                if (song == null) ResolveSong(info.Data.song);
                if (song == null)
                {
                    UpdateDuck(dt);
                    return;
                }

                stage = Stage.Ready;
                if (!chartTried) LoadChart();
                UpdateAudio(userId, dt);
                UpdateDuck(dt);
            }
            catch (Exception ex)
            {
                // Logged once per distinct error: this runs every frame.
                if (ex.Message != lastError)
                {
                    lastError = ex.Message;
                    MelonLogger.Log("[SpectatorSong] tick failed: " + ex);
                }
                StopSound("error");
                UpdateDuck(dt);
            }
        }

        private static void EndSession(string reason)
        {
            if (sessionUser == null) return;
            StopSound(reason);
            ResetSong();
            sessionUser = null;
            stage = Stage.None;
        }

        private static void ResetSong()
        {
            sessionKey = null;
            song = null;
            songTitle = null;
            chartTried = false;
            cues.Clear();
            unavailableReason = null;
        }

        // ── Having the song ──────────────────────────────────────────────────

        private static void ResolveSong(MpSongRef wanted)
        {
            string songId = wanted.songId;

            if (!Enum.TryParse(wanted.difficulty, out KataConfig.Difficulty parsed))
            {
                SetUnavailable("unknown difficulty \"" + wanted.difficulty + "\"");
                return;
            }
            difficulty = parsed;

            if (SongList.I == null || SongList.OnSongListLoaded == null || !SongList.OnSongListLoaded.mDone)
            {
                stage = Stage.Searching; // the song list is (re)loading: checked again next frame
                return;
            }

            SongList.SongData data = SongList.I.GetSong(songId);
            if (data != null)
            {
                if (string.IsNullOrEmpty(data.moggSong))
                {
                    SetUnavailable("it has no audio here");
                    return;
                }
                song = data;
                Log($"{songId} is installed: ready");
                return;
            }

            if (downloadingSongId == songId)
            {
                stage = downloadSearching ? Stage.Searching : Stage.Downloading;
                return;
            }
            if (MpGameplay.DownloadingSongId == songId)
            {
                stage = Stage.Downloading; // the lobby's own auto-download is fetching it
                downloadProgress = 0f;
                return;
            }

            if (failures.TryGetValue(songId, out KeyValuePair<float, string> failure))
            {
                if (Time.unscaledTime - failure.Key < RetrySeconds)
                {
                    SetUnavailable(failure.Value);
                    return;
                }
                failures.Remove(songId);
            }

            if (!Config.AutoDownloadSongs)
            {
                SetUnavailable("you don't have it and Auto Download Songs is off");
                return;
            }
            if (ExScoring.IsDeletedThisSession(songId))
            {
                SetUnavailable("you deleted it this session");
                return;
            }
            if (downloadingSongId != null)
            {
                stage = Stage.Searching; // one download at a time: this one starts when the other is done
                return;
            }

            // Copy the fields: the info object is replaced every couple of seconds.
            MpSongRef copy = new MpSongRef
            {
                songId = wanted.songId,
                difficulty = wanted.difficulty,
                title = wanted.title,
                artist = wanted.artist,
                mapper = wanted.mapper,
                maudicaId = wanted.maudicaId
            };
            downloadingSongId = songId;
            downloadSearching = true;
            downloadProgress = 0f;
            stage = Stage.Searching;
            MelonCoroutines.Start(Download(copy));
        }

        private static void SetUnavailable(string reason)
        {
            if (stage != Stage.Unavailable || unavailableReason != reason)
                Log($"can't spectate {sessionKey}: {reason}");
            stage = Stage.Unavailable;
            unavailableReason = reason;
        }

        /// <summary>Looks the song up on Maudica and downloads it, like MpGameplay.DownloadForLobby.</summary>
        private static IEnumerator Download(MpSongRef wanted)
        {
            string songId = wanted.songId;
            Log($"{songId} \"{wanted.title}\" is missing: looking it up on Maudica");

            Song map = null;
            bool searchDone = false;
            try
            {
                MaudicaLookup.Find(wanted, found =>
                {
                    map = found;
                    searchDone = true;
                });
            }
            catch (Exception ex)
            {
                Log("the Maudica lookup failed: " + ex.Message);
                searchDone = true;
            }

            // MaudicaLookup times each step out itself; this only guards against it never answering.
            float deadline = Time.unscaledTime + MaudicaSearchTimeoutSeconds;
            while (!searchDone && Time.unscaledTime < deadline) yield return null;

            if (map == null || string.IsNullOrEmpty(map.download_url))
            {
                FinishDownload(songId, false, searchDone ? "it isn't on Maudica" : "the Maudica lookup timed out");
                yield break;
            }

            Log($"downloading {songId} from {map.download_url}");
            downloadSearching = false;

            bool downloadDone = false;
            bool success = false;
            MelonCoroutines.Start(SongDownloader.DownloadSong(map.song_id, map.download_url,
                (id, ok) =>
                {
                    success = ok;
                    downloadDone = true;
                },
                (id, progress) => downloadProgress = progress,
                songId)); // the exact version: an older one with the same file name gets updated

            while (!downloadDone) yield return null;

            FinishDownload(songId, success, "the download failed, or Maudica has another version of the map");
        }

        private static void FinishDownload(string songId, bool success, string failure)
        {
            if (downloadingSongId == songId) downloadingSongId = null;
            downloadSearching = false;

            if (success && SongList.I != null && SongList.I.GetSong(songId) != null)
            {
                Log($"downloaded {songId}");
                return; // ResolveSong finds it on the next frame
            }

            failures[songId] = new KeyValuePair<float, string>(Time.unscaledTime, failure);
            Log($"couldn't get {songId}: {failure}");
        }

        // ── Chart ────────────────────────────────────────────────────────────

        private static void LoadChart()
        {
            chartTried = true;
            cues.Clear();
            try
            {
                var loaded = SongCues.GetCues(song, difficulty);
                if (loaded == null)
                {
                    Log($"{song.songID} has no chart on {difficulty}");
                    return;
                }
                // A cold GetCues leaves chainNext empty; HookUpChains fills it (see RunDataRecalculator).
                SongCues.HookUpChains(loaded);
                foreach (SongCues.Cue cue in loaded)
                {
                    if (cue != null) cues.Add(cue);
                }

                MpSpectateSettings s = Settings;
                Log($"chart loaded: {song.songID} [{difficulty}] {cues.Count} cues" +
                    (cues.Count > 0 ? $", ticks {cues[0].tick}..{cues[cues.Count - 1].tick}" : "") +
                    $", their target speed {(s != null && s.targetSpeed.HasValue ? s.targetSpeed.Value.ToString("0.##") : "?")}");
            }
            catch (Exception ex)
            {
                cues.Clear();
                Log($"loading the chart of {song.songID} [{difficulty}] failed: {ex}");
            }
        }

        // ── Audio ────────────────────────────────────────────────────────────

        private static void UpdateAudio(string userId, float dt)
        {
            bool audible = false;
            lastHasTick = MpPresence.TryGetSongTick(userId, out double tick, out bool moving);
            lastMoving = moving;

            double wantMs = 0;
            if (lastHasTick)
            {
                wantMs = AudioDriver.TickSpanToMs(song, 0f, (float)tick) + OffsetMs;
                lastWantMs = wantMs;
            }
            bool playing = lastHasTick && moving && wantMs >= 0;

            // A paused sound doesn't count as ended.
            if (handle != Hmx.Audio.Sound.kInvalidSoundHandle && !paused && !IsAlive(handle))
                StopSound("the sound ended");

            if (handle == Hmx.Audio.Sound.kInvalidSoundHandle && playing && Time.unscaledTime >= nextStartAttempt)
            {
                if (!StartSound()) nextStartAttempt = Time.unscaledTime + StartRetrySeconds;
            }

            if (handle == Hmx.Audio.Sound.kInvalidSoundHandle)
            {
                fade = 0f;
                return;
            }

            float length = Hmx.Audio.Sound.GetLengthMs(handle);
            if (!playing || (length > 0f && wantMs >= length - EndMarginMs))
            {
                // The watched player is paused, between poses, or past the end of the audio.
                if (!paused)
                {
                    SetSpeed(1f);
                    Hmx.Audio.Sound.Pause(handle);
                    paused = true;
                    inSync = false;
                    Log("audio paused (their song position isn't advancing)");
                }
            }
            else
            {
                if (paused)
                {
                    Hmx.Audio.Sound.Continue(handle);
                    paused = false;
                    hasSmoothedGap = false;
                    Log("audio resumed");
                }

                double gap = wantMs - Hmx.Audio.Sound.GetTimelineMs(handle);
                lastGap = gap;

                if (gap < -RestartBackMs)
                {
                    StopSound($"the watched player went back {-gap / 1000:0.0} s: starting it again");
                    return; // a new sound starts next frame, fade and gain with it
                }

                if (seekPending)
                {
                    if (Math.Abs(gap) < SeekSettledMs)
                    {
                        seekPending = false;
                        hasSmoothedGap = false;
                        Log($"seek settled after {Time.unscaledTime - seekIssuedAt:0.00} s, {gap:0} ms off");
                    }
                    else if (Time.unscaledTime - seekIssuedAt > SeekTimeoutSeconds)
                    {
                        seekPending = false;
                        Log($"seek didn't settle in {SeekTimeoutSeconds:0} s (still {gap:0} ms off)");
                        StopSound("a seek didn't settle: starting it again");
                        return;
                    }
                }

                if (!seekPending && (!seekedOnce || Math.Abs(gap) > SeekThresholdMs))
                {
                    double target = Math.Max(0, wantMs + SeekLeadMs);
                    SetSpeed(1f);
                    Hmx.Audio.Sound.SeekToMs(handle, (float)target);
                    seekPending = true;
                    seekedOnce = true;
                    seekIssuedAt = Time.unscaledTime;
                    inSync = false;
                    seekCount++;
                    Log($"seek to {target:0} ms (was {gap:0} ms off, seek #{seekCount})");
                }
                else if (!seekPending)
                {
                    // The position can read in small steps: the speed follows a smoothed gap.
                    smoothedGap = hasSmoothedGap ? smoothedGap + (gap - smoothedGap) * Math.Min(1.0, dt * GapSmoothing) : gap;
                    hasSmoothedGap = true;

                    inSync = Math.Abs(smoothedGap) <= (inSync ? InSyncLeaveMs : InSyncEnterMs);
                    double limit = inSync ? FineSpeed : CatchUpSpeed;
                    double wanted = Math.Abs(smoothedGap) < SpeedDeadbandMs
                        ? 1.0
                        : 1.0 + Math.Max(-limit, Math.Min(limit, smoothedGap / SpeedGainMs));
                    SetSpeed((float)wanted);
                    audible = inSync;
                }

                if (Time.unscaledTime >= nextStatusLog)
                {
                    nextStatusLog = Time.unscaledTime + StatusLogSeconds;
                    Log($"at {wantMs:0} ms: {gap:0} ms off, speed {speed:0.000}, {(seekPending ? "seeking" : inSync ? "in sync" : "catching up")}");
                }
            }

            fade = Mathf.MoveTowards(fade, audible ? 1f : 0f, dt / FadeSeconds);
            SongPreview preview = SongPreview.I;
            float baseGain = preview != null ? preview.gain : FallbackGain;
            // Spectator Audio Over Previews: the preview is the one silenced (PreviewMute), not this.
            float previewLevel = Config.SpectatorAudioOverPreviews ? 0f : PreviewLevel(preview);
            float volume = Mathf.Clamp01(Config.SpectatorVolume / 100f); // Spectator Volume
            Hmx.Audio.Sound.SetGain(handle, baseGain * volume * fade * (1f - previewLevel));
        }

        /// <summary>Sets the sound's playback speed, only when it changes.</summary>
        private static void SetSpeed(float value)
        {
            if (Mathf.Abs(value - speed) < 0.001f) return;
            speed = value;
            Hmx.Audio.Sound.SetSpeed(handle, value);
        }

        /// <summary>How much of SongPreview is audible right now (0 = nothing, 1 = full), as MenuMusic reads it.</summary>
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

        /// <summary>Same test SongPreview.Update uses to decide its sound is still going.</summary>
        private static bool IsAlive(uint h)
        {
            if (h == Hmx.Audio.Sound.kInvalidSoundHandle) return false;
            uint state = (uint)Hmx.Audio.Sound.GetTransportState(h);
            return (state & 0xFFFFFFFAu) != 0 || state == 1;
        }

        private static bool StartSound()
        {
            KataConfig config = KataConfig.I;
            HmxAudioEmitter emitterComponent = config != null ? config.GetComponent<HmxAudioEmitter>() : null;
            Hmx.Audio.UAudioEmitterCom emitter = emitterComponent != null ? emitterComponent.Get() : null;

            Hmx.Audio.PlayArgs args = new Hmx.Audio.PlayArgs();
            args.volumeSettings.volume = InitialVolumeDb;
            args.volumeSettings.modifyAtStart = true;

            uint h = KataUtil.PlaySound(args, song.moggSong, emitter, PreviewEvent);
            if (h == Hmx.Audio.Sound.kInvalidSoundHandle)
            {
                Log($"PlaySound failed for {song.songID} ({song.moggSong})");
                return false;
            }

            Hmx.Audio.Sound.SetGain(h, 0f);
            handle = h;
            fade = 0f;
            seekedOnce = false;
            seekPending = false;
            paused = false;
            inSync = false;
            speed = 1f;
            hasSmoothedGap = false;
            nextStatusLog = Time.unscaledTime + StatusLogSeconds;
            Log($"audio started: {song.songID} (handle {h})");
            return true;
        }

        private static void StopSound(string reason)
        {
            if (handle == Hmx.Audio.Sound.kInvalidSoundHandle) return;
            if (handle != 0)
            {
                try { Hmx.Audio.Sound.Stop(handle); }
                catch (Exception ex) { Log("stopping the audio failed: " + ex.Message); }
            }
            handle = Hmx.Audio.Sound.kInvalidSoundHandle;
            fade = 0f;
            seekPending = false;
            paused = false;
            inSync = false;
            speed = 1f;
            Log($"audio stopped ({reason})");
        }

        private static void UpdateDuck(float dt)
        {
            bool playing = handle != Hmx.Audio.Sound.kInvalidSoundHandle;
            duck = Mathf.MoveTowards(duck, playing ? 1f : 0f, dt / DuckFadeSeconds);
        }

        // ── Debug ────────────────────────────────────────────────────────────

        public static void DebugDump()
        {
            MelonLogger.Log("[SpectatorSong] ── dump ──");
            MelonLogger.Log($"  watching={sessionUser ?? "nobody in a song"} stage={stage} key={sessionKey ?? "-"} reason={unavailableReason ?? "-"}");
            MelonLogger.Log($"  infos={infos.Count} song={(song != null ? song.songID : "null")} difficulty={difficulty} cues={cues.Count} chartTried={chartTried}");
            MelonLogger.Log($"  downloading={downloadingSongId ?? "-"} searching={downloadSearching} progress={downloadProgress:0.00} failures={failures.Count}");
            MelonLogger.Log($"  handle={handle} fade={fade:0.00} duck={duck:0.00} hasTick={lastHasTick} moving={lastMoving} wantMs={lastWantMs:0} gap={lastGap:0} ms");
            MelonLogger.Log($"  seeks={seekCount} seekPending={seekPending} paused={paused} inSync={inSync} speed={speed:0.000} offset={OffsetMs:0} ms");
            if (handle != Hmx.Audio.Sound.kInvalidSoundHandle)
            {
                try
                {
                    MelonLogger.Log($"  sound: timeline={Hmx.Audio.Sound.GetTimelineMs(handle):0} ms length={Hmx.Audio.Sound.GetLengthMs(handle):0} ms alive={IsAlive(handle)}");
                }
                catch (Exception ex)
                {
                    MelonLogger.Log("  sound: unreadable: " + ex.Message);
                }
            }
        }
    }
}