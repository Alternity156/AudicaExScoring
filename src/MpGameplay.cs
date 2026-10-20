using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Harmony;
using MelonLoader;
using UnityEngine;
using static ExScoringMod.ExScoring;

namespace ExScoringMod
{
    /// <summary>
    /// Connects online play (MpSession, ApiContract.md Section 19) to the game itself:
    ///  - location: the menu the player is in and the song they're browsing (setLocation);
    ///  - song status for the lobby's selected song, with an automatic Maudica download when it's
    ///    missing (setSongStatus);
    ///  - the scoring mode when the player switches EX/classic (setScoringMode);
    ///  - match launch: on matchPrepare a participant's song is launched the way marathon does it,
    ///    paused the moment playback starts, "loaded" is sent, and playback resumes at matchStart's
    ///    startAt on the server clock (19.5);
    ///  - live scores (scoreUpdate), no failing at zero health (AllowFailure refused), finished / quitSong;
    ///  - a run that reached zero health is played to the end, then ended as failed (SongEnd.OnSongOver).
    ///
    /// Main thread only. Init() from OnApplicationStart, Tick() from OnUpdate after MpSession.Tick().
    /// </summary>
    public static class MpGameplay
    {
        private const float LaunchPageTimeoutSeconds = 8f;
        private const float MaudicaSearchTimeoutSeconds = 20f;
        private const long ScoreSampleIntervalMs = 100;
        private const long SongStatusRecheckIntervalMs = 1000;

        private static readonly KataConfig.Difficulty[] AllDifficulties =
        {
            KataConfig.Difficulty.Easy, KataConfig.Difficulty.Normal, KataConfig.Difficulty.Hard, KataConfig.Difficulty.Expert
        };

        private static bool initialized;

        // ── Location ──
        private static string currentLocation;

        // ── Song status ──
        private static string statusSongId;                 // song the cached availability below is for
        private static string[] statusAvailableDifficulties; // null = not installed (or not checked yet)
        private static string downloadingSongId;
        private static readonly HashSet<string> failedDownloadSongIds = new HashSet<string>();
        private static long nextStatusCheckMs;

        // ── Match run ──
        private static string launchMatchId;   // match a launch was started for, until playback starts
        private static string runMatchId;      // match the current in-song run belongs to
        private static bool holding;           // paused at the start, waiting for matchStart
        private static bool allowResume;       // set only around our own AudioDriver.Resume call
        private static bool allowPause;        // set only around our own AudioDriver.Pause call (the hold)
        private static long releaseAtLocalMs;  // local unix ms to resume at; 0 = no matchStart yet
        private static bool released;          // playback running for real
        private static bool exitPending;       // leave the song as soon as it's safe (abort/drop during launch)
        private static bool matchFailed;       // health reached zero during the match
        private static bool finishedSent;
        private static long lastScoreSampleMs;
        private static int highestStreak;

        // clientRunId of a run that must not upload (failed during a match).
        private static string suppressUploadRunId;

        public static bool InMatchRun => runMatchId != null;

        /// <summary>True from a match launch until its song starts playing (LobbyLaunchButton lets that Play through).</summary>
        public static bool IsLaunchingMatch => launchMatchId != null;
        public static bool IsHolding => holding;

        public static void Init()
        {
            if (initialized) return;
            initialized = true;

            MpSession.OnConnected += _ => SendCurrentLocation();
            MpSession.OnLobbyState += OnLobbyState;
            MpSession.OnLeftLobby += OnLeftLobby;
            MpSession.OnDisconnected += OnDisconnected;
            MpSession.OnMatchPrepare += OnMatchPrepare;
            MpSession.OnParticipantDropped += OnParticipantDropped;
            MpSession.OnMatchStart += OnMatchStart;
            MpSession.OnMatchAborted += OnMatchAborted;
        }

        public static void Tick()
        {
            long now = MpSession.LocalNowMs;

            if (holding && releaseAtLocalMs > 0 && now >= releaseAtLocalMs)
            {
                Release(now);
            }

            if (InMatchRun && released && !finishedSent)
            {
                // Last line of defence against a pause path none of the patches catch (e.g. the
                // SteamVR plugin freezing time while its dashboard is open).
                if (Time.timeScale == 0f)
                {
                    Time.timeScale = 1f;
                    Log("Time was frozen during an online match: unfrozen.");
                }

                TrackHealthAndCombo();
                if (now - lastScoreSampleMs >= ScoreSampleIntervalMs)
                {
                    lastScoreSampleMs = now;
                    SendScoreSample();
                }
            }

            if (MpSession.IsConnected && MpSession.InLobby && now >= nextStatusCheckMs)
            {
                nextStatusCheckMs = now + SongStatusRecheckIntervalMs;
                UpdateSongStatus();
            }
        }

        // ── Hooks called from existing patches ──

        /// <summary>From SetStatePatch.Postfix, before menuState is updated.</summary>
        public static void OnMenuStateChanged(MenuState.State from, MenuState.State to)
        {
            bool wasInSong = from == MenuState.State.Launching || from == MenuState.State.Launched;
            bool nowInSong = to == MenuState.State.Launching || to == MenuState.State.Launched;

            currentLocation = nowInSong ? "InSong" : to.ToString();
            MpSession.SetLocation(currentLocation);

            if (wasInSong && !nowInSong)
            {
                if (InMatchRun && !finishedSent)
                {
                    Log($"Left the song during match {runMatchId}: quitSong");
                    MpSession.SendQuitSong();
                }
                EndRun();
                launchMatchId = null;
            }
        }

        /// <summary>From PatchSongOnSelect.Postfix: the song the player is looking at.</summary>
        public static void OnSongBrowsed(SongList.SongData song)
        {
            if (song == null) return;
            MpSession.SetLocation(currentLocation ?? menuState.ToString(), song.songID, song.title);
            LobbyLaunchButton.OnSongSelected(song);
        }

        /// <summary>From Config.SetScoringType.</summary>
        public static void OnScoringTypeChanged(bool ex)
        {
            if (MpSession.IsConnected && MpSession.InLobby) MpSession.SetScoringMode(ex);
        }

        /// <summary>From InGameUIRestartPatch.Prefix: no restarting a match song.</summary>
        public static bool ShouldBlockRestart()
        {
            if (!InMatchRun) return false;
            Log("Restart blocked during an online match.");
            return true;
        }

        /// <summary>From ApiClient: true when this run failed during a match and must not upload.</summary>
        public static bool ShouldSuppressUpload(string runId)
        {
            return !string.IsNullOrEmpty(runId) && runId == suppressUploadRunId;
        }

        // ── MpSession events ──

        private static void OnLobbyState(MpLobbyState lobby)
        {
            UpdateSongStatus();
        }

        private static void OnLeftLobby(string reason)
        {
            statusSongId = null;
            statusAvailableDifficulties = null;
            if (holding || launchMatchId != null) ExitMatchSong($"left the lobby ({reason})");
        }

        private static void OnDisconnected(MpDisconnectInfo info)
        {
            // A reconnect gets matchPrepare/matchStart again (19.5.1), so keep holding through it.
            if (!info.willReconnect && (holding || launchMatchId != null)) ExitMatchSong("disconnected");
        }

        private static void OnMatchPrepare(MpMatchPrepare prepare)
        {
            if (!MpSession.IsMatchParticipant)
            {
                Log($"matchPrepare {prepare.matchId}: spectating.");
                return;
            }

            // Catch-up after a reconnect: already launched or holding for this match.
            if (prepare.matchId == runMatchId || prepare.matchId == launchMatchId)
            {
                if (holding)
                {
                    Log($"matchPrepare {prepare.matchId} again (reconnect): re-sending loaded.");
                    MpSession.SendLoaded();
                }
                return;
            }

            MelonCoroutines.Start(LaunchForMatch(prepare));
        }

        private static void OnParticipantDropped(MpParticipantDropped dropped)
        {
            if (dropped == null || dropped.userId != MpSession.MyUserId) return;
            if (dropped.matchId != launchMatchId && dropped.matchId != runMatchId) return;

            // Dropped while loading (load timeout): we're a spectator now.
            if (!released) ExitMatchSong($"dropped from the match ({dropped.reason})");
        }

        private static void OnMatchStart(MpMatchStart start)
        {
            if (start == null || start.matchId != runMatchId || released) return;

            releaseAtLocalMs = MpSession.ServerToLocalMs(start.startAt);
            Log($"matchStart: resuming in {(releaseAtLocalMs - MpSession.LocalNowMs) / 1000.0:F2} s " +
                $"(offset {MpSession.ClockOffsetMs} ms, synced={MpSession.ClockSynced}, holding={holding})");
        }

        private static void OnMatchAborted(string matchId)
        {
            if (holding || launchMatchId != null || (InMatchRun && !finishedSent))
            {
                ExitMatchSong("the host aborted the match");
            }
        }

        // ── Song status (19.3 songStatus, 19.4.1 setSongStatus) ──

        private static void UpdateSongStatus()
        {
            MpLobbyState lobby = MpSession.Lobby;
            MpSongRef selected = lobby?.selectedSong;
            if (selected == null || string.IsNullOrEmpty(selected.songId)) return;

            string songId = selected.songId;
            if (songId == downloadingSongId) return; // the download reports its own progress

            if (songId != statusSongId)
            {
                statusSongId = songId;
                statusAvailableDifficulties = null;
            }

            if (SongList.I == null || SongList.OnSongListLoaded == null || !SongList.OnSongListLoaded.mDone)
            {
                return; // song list (re)loading: check again shortly
            }

            if (statusAvailableDifficulties == null)
            {
                statusAvailableDifficulties = GetAvailableDifficulties(songId);
            }

            if (statusAvailableDifficulties != null)
            {
                MpSession.SetSongStatus(songId, "have", null, statusAvailableDifficulties);
                return;
            }

            if (selected.maudicaId.HasValue && !failedDownloadSongIds.Contains(songId))
            {
                MelonCoroutines.Start(DownloadForLobby(songId, selected.maudicaId.Value));
                return;
            }

            // Not installed, and no Maudica map to fetch (or fetching it failed).
            MpSession.SetSongStatus(songId, "unavailable", null, new string[0]);
        }

        /// <summary>The song's difficulties with at least one cue, or null when it isn't installed.</summary>
        private static string[] GetAvailableDifficulties(string songId)
        {
            SongList.SongData data = SongList.I.GetSong(songId);
            if (data == null) return null;

            List<string> available = new List<string>();
            foreach (KataConfig.Difficulty difficulty in AllDifficulties)
            {
                try
                {
                    if (SongHasDifficulty(data, difficulty)) available.Add(difficulty.ToString());
                }
                catch (Exception ex)
                {
                    Log($"Couldn't read {songId} {difficulty}: {ex.Message}");
                }
            }
            return available.ToArray();
        }

        private static IEnumerator DownloadForLobby(string songId, int maudicaId)
        {
            downloadingSongId = songId;
            Log($"Selected song {songId} is missing: downloading Maudica map {maudicaId}");
            MpSession.SetSongStatus(songId, "downloading", 0f, new string[0]);

            APISongList found = null;
            bool searchDone = false;
            MelonCoroutines.Start(SongDownloader.DoMaudicaIDSearch(maudicaId.ToString(), (id, list) =>
            {
                found = list;
                searchDone = true;
            }));

            float deadline = Time.unscaledTime + MaudicaSearchTimeoutSeconds;
            while (!searchDone && Time.unscaledTime < deadline) yield return null;

            Song map = found != null && found.songs != null && found.songs.Length > 0 ? found.songs[0] : null;
            if (map == null || string.IsNullOrEmpty(map.download_url))
            {
                FinishDownload(songId, false, searchDone ? "Maudica has no such map" : "the Maudica lookup timed out");
                yield break;
            }

            bool downloadDone = false;
            bool success = false;
            MelonCoroutines.Start(SongDownloader.DownloadSong(map.song_id, map.download_url,
                (id, ok) =>
                {
                    success = ok;
                    downloadDone = true;
                },
                (id, progress) =>
                {
                    if (downloadingSongId == songId) MpSession.SetSongStatus(songId, "downloading", progress * 0.99f, new string[0]);
                }));

            while (!downloadDone) yield return null;

            FinishDownload(songId, success, success ? null : "the download or import failed");
        }

        private static void FinishDownload(string songId, bool success, string failure)
        {
            if (downloadingSongId == songId) downloadingSongId = null;
            statusAvailableDifficulties = null;

            if (success && SongList.I != null && SongList.I.GetSong(songId) != null)
            {
                // DownloadSong already flagged the song list for a refresh; the lobby can use it now.
                Log($"Downloaded {songId}.");
            }
            else
            {
                failedDownloadSongIds.Add(songId);
                Log(success
                    ? $"Downloaded the Maudica map, but it didn't add a song with id {songId}."
                    : $"Couldn't download {songId}: {failure}.");
            }

            nextStatusCheckMs = 0;
            UpdateSongStatus();
        }

        // ── Match launch and synced start (19.5) ──

        private static IEnumerator LaunchForMatch(MpMatchPrepare prepare)
        {
            string matchId = prepare.matchId;
            string songId = prepare.song?.songId;

            string difficultyName = prepare.song?.difficulty;
            if (prepare.participants != null)
            {
                foreach (MpMatchParticipant participant in prepare.participants)
                {
                    if (participant != null && participant.userId == MpSession.MyUserId && !string.IsNullOrEmpty(participant.difficulty))
                    {
                        difficultyName = participant.difficulty;
                    }
                }
            }

            if (!Enum.TryParse(difficultyName, out KataConfig.Difficulty difficulty))
            {
                Log($"matchPrepare {matchId}: unknown difficulty \"{difficultyName}\", not launching.");
                yield break;
            }

            SongList.SongData data = SongList.I != null && songId != null ? SongList.I.GetSong(songId) : null;
            if (data == null)
            {
                Log($"matchPrepare {matchId}: {songId} isn't installed, not launching (the server will make us a spectator).");
                yield break;
            }

            if (menuState == MenuState.State.Launching || menuState == MenuState.State.Launched)
            {
                Log($"matchPrepare {matchId}: already in a song, not launching.");
                yield break;
            }

            launchMatchId = matchId;
            exitPending = false;
            Log($"matchPrepare {matchId}: launching {songId} on {difficulty}");

            SelectForLaunch(data, difficulty);

            if (menuState != MenuState.State.SongPage)
            {
                MenuState.I.GoToSongPage();
            }

            LaunchPanel panel = null;
            float deadline = Time.unscaledTime + LaunchPageTimeoutSeconds;
            while (Time.unscaledTime < deadline)
            {
                if (launchMatchId != matchId) yield break; // aborted/dropped meanwhile
                if (menuState == MenuState.State.SongPage)
                {
                    panel = UnityEngine.Object.FindObjectOfType<LaunchPanel>();
                    if (panel != null) break;
                }
                yield return new WaitForSecondsRealtime(0.1f);
            }

            if (panel == null)
            {
                Log($"matchPrepare {matchId}: the song page didn't come up, not launching.");
                if (launchMatchId == matchId) launchMatchId = null;
                yield break;
            }

            // Arriving on the song page can re-run the mod's own selection logic; settle, then
            // make sure the match song and difficulty are what Play will launch.
            yield return new WaitForSecondsRealtime(0.3f);
            if (launchMatchId != matchId) yield break;
            SelectForLaunch(data, difficulty);

            Log($"matchPrepare {matchId}: Play");
            panel.Play();
        }

        private static void SelectForLaunch(SongList.SongData data, KataConfig.Difficulty difficulty)
        {
            selectedSong = data.songID;
            selectedSongData = data;
            if (SongDataHolder.I != null) SongDataHolder.I.songData = data;
            // The field, not SetDifficulty(), which rebuilds the song list (see EnsureValidDifficultySelected).
            KataConfig.I.mDifficulty = difficulty;
            maxPossibleExScore = GetMaxPossibleExScore(data.songID, difficulty);
        }

        /// <summary>AudioDriver.StartPlaying postfix: hold the match song at its very start.</summary>
        private static void OnStartPlaying()
        {
            if (launchMatchId == null) return;

            string matchId = launchMatchId;
            launchMatchId = null;

            if (exitPending)
            {
                // Aborted or dropped while the song was loading: leave before anything plays.
                BeginRun(matchId);
                PauseForHold();
                holding = true;
                ExitMatchSong("aborted while loading");
                return;
            }

            string playingId = SongDataHolder.I?.songData?.songID;
            string matchSongId = MpSession.CurrentMatch?.song?.songId;
            if (MpSession.CurrentMatch == null || MpSession.CurrentMatch.matchId != matchId || playingId != matchSongId)
            {
                Log($"StartPlaying for {playingId}, but match {matchId} (song {matchSongId}) is no longer current: not holding.");
                return;
            }

            BeginRun(matchId);
            PauseForHold();
            holding = true;
            Log($"Holding {playingId} at its start for match {matchId}: loaded");
            MpSession.SendLoaded();
        }

        private static void PauseForHold()
        {
            allowPause = true;
            try
            {
                AudioDriver.I.Pause();
            }
            finally
            {
                allowPause = false;
            }
        }

        /// <summary>
        /// No pausing at all during a match run, from the hold until "finished": not from the menu
        /// button, the headset coming off, or the game losing focus. The others keep playing, so a
        /// pause could only ever cost the player. The run ends normally or by leaving the lobby.
        /// </summary>
        private static bool PauseBlocked => InMatchRun && !finishedSent;

        /// <summary>
        /// From InGameUISetStatePatch.Prefix: some pause paths (the SteamVR dashboard opening)
        /// set InGameUI's state to PausePage directly instead of going through GoToPausePage.
        /// </summary>
        public static bool ShouldBlockPausePage()
        {
            if (!PauseBlocked) return false;
            Log("Pause page blocked during an online match.");
            return true;
        }

        private static void Release(long now)
        {
            holding = false;
            released = true;
            long lateMs = now - releaseAtLocalMs;

            // The player opened the pause menu during the hold: their own Resume starts the song.
            if (InGameUI.I != null && InGameUI.I.GetState() == InGameUI.State.PausePage)
            {
                Log($"Start time reached with the pause menu open ({lateMs} ms): the player's Resume will start the song.");
                return;
            }

            allowResume = true;
            try
            {
                AudioDriver.I.Resume();
            }
            finally
            {
                allowResume = false;
            }
            Log($"Match {runMatchId} started ({lateMs} ms after the scheduled time).");
        }

        // ── Run tracking ──

        private static void BeginRun(string matchId)
        {
            runMatchId = matchId;
            released = false;
            releaseAtLocalMs = 0;
            matchFailed = false;
            finishedSent = false;
            highestStreak = 0;
            lastScoreSampleMs = 0;
        }

        private static void EndRun()
        {
            runMatchId = null;
            holding = false;
            released = false;
            releaseAtLocalMs = 0;
            exitPending = false;
        }

        private static void ExitMatchSong(string reason)
        {
            if (launchMatchId != null && runMatchId == null)
            {
                // Still launching: hold it at StartPlaying and leave from there.
                exitPending = true;
                if (menuState != MenuState.State.Launching && menuState != MenuState.State.Launched)
                {
                    // Play hasn't happened yet: just cancel.
                    launchMatchId = null;
                    exitPending = false;
                }
                Log($"Cancelling the match launch: {reason}");
                return;
            }

            if (!InMatchRun) return;

            Log($"Leaving the match song: {reason}");
            bool wasHolding = holding;
            EndRun();

            if (menuState == MenuState.State.Launched && InGameUI.I != null)
            {
                // Same call as the pause menu's song-list button (and DeleteButton).
                InGameUI.I.ReturnToSongList();
            }
            else
            {
                Log($"Not in a launched song (menuState={menuState}, wasHolding={wasHolding}): nothing to leave.");
            }
        }

        private static void TrackHealthAndCombo()
        {
            ScoreKeeper keeper = ScoreKeeper.I;
            if (keeper == null) return;

            int streak = keeper.GetStreak();
            if (streak > highestStreak) highestStreak = streak;

            if (!matchFailed && keeper.GetHealth() <= 0.0001f)
            {
                matchFailed = true;
                suppressUploadRunId = currentRunId;
                Log($"Health reached zero in match {runMatchId}: failed, playing on (failure refused), this run won't upload.");
            }
        }

        private static void SendScoreSample()
        {
            ScoreKeeper keeper = ScoreKeeper.I;
            if (keeper == null) return;

            float tick = 0f;
            try
            {
                if (AudioDriver.I != null) tick = AudioDriver.I.GetTick(AudioDriver.TickContext.Audio);
            }
            catch
            {
                // Tick is informational only.
            }

            MpSession.SendScoreUpdate(new MpScoreSnapshot
            {
                tick = (long)tick,
                exPercent = SafePercent(currentMaxPossibleJudgementScore > 0f ? GetCurrentMaxPossibleJudgementPercentage() : 0f),
                judgementScore = (long)Math.Round(judgementScore),
                classicScore = keeper.GetScore(),
                combo = keeper.GetStreak(),
                misses = CountMisses(),
                health = Mathf.Clamp01(keeper.GetHealth()),
                failed = matchFailed
            });
        }

        /// <summary>SongEnd.OnSongOver postfix: report the result (19.5 step 6).</summary>
        private static void OnSongOver(bool failed)
        {
            if (!InMatchRun || finishedSent) return;

            ScoreKeeper keeper = ScoreKeeper.I;
            bool runFailed = failed || matchFailed;
            if (runFailed && suppressUploadRunId != currentRunId) suppressUploadRunId = currentRunId;

            int misses = CountMisses();
            int maxCombo = keeper != null ? Math.Max(keeper.GetMaxStreak(), highestStreak) : highestStreak;
            string clientRunId = !runFailed && WillSubmitRun(false) ? currentRunId : null;

            MpFinishedReport report = new MpFinishedReport
            {
                failed = runFailed,
                exPercent = SafePercent(GetFinalJudgementPercentage()),
                judgementScore = (long)Math.Round(judgementScore),
                classicScore = keeper != null ? keeper.GetScore() : 0,
                misses = misses,
                maxCombo = maxCombo,
                fullCombo = !runFailed && misses == 0 && keeper != null && keeper.GetIsFullComboSoFar(),
                clientRunId = clientRunId
            };

            finishedSent = true;
            Log($"Song over in match {runMatchId}: failed={report.failed} ex={report.exPercent:F2} classic={report.classicScore} " +
                $"misses={report.misses} maxCombo={report.maxCombo} fc={report.fullCombo} upload={(clientRunId != null ? "yes" : "no")}");
            MpSession.SendFinished(report);
        }

        private static int CountMisses()
        {
            int misses = 0;
            List<ExCue> cues = exCues;
            for (int i = 0; i < cues.Count; i++)
            {
                if (cues[i] != null && cues[i].miss) misses++;
            }
            return misses;
        }

        private static float SafePercent(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value)) return 0f;
            return Mathf.Clamp(value, 0f, 100f);
        }

        private static void SendCurrentLocation()
        {
            MenuState.State state = menuState;
            bool inSong = state == MenuState.State.Launching || state == MenuState.State.Launched;
            currentLocation = inSong ? "InSong" : state.ToString();
            MpSession.SetLocation(currentLocation);
        }

        private static void Log(string message)
        {
            DebugLog.Log("[MpGame] " + message);
        }

        // ── Patches ──

        [HarmonyPatch(typeof(AudioDriver), "StartPlaying", new Type[0])]
        private static class AudioDriverStartPlayingPatch
        {
            private static void Postfix()
            {
                try
                {
                    OnStartPlaying();
                }
                catch (Exception ex)
                {
                    MelonLogger.Log($"[MpGame] StartPlaying hook failed: {ex}");
                }
            }
        }

        /// <summary>Nothing but our own Release may resume a held match song.</summary>
        [HarmonyPatch(typeof(AudioDriver), "Resume", new Type[0])]
        private static class AudioDriverResumePatch
        {
            private static bool Prefix()
            {
                if (holding && !allowResume)
                {
                    Log("Resume blocked: waiting for the match start.");
                    return false;
                }
                return true;
            }
        }

        /// <summary>
        /// Headset off, lost VR/input/app focus, and the held pause button all go through
        /// CheckForPause (it reads PauseHandler.hardwareState). Skipped during a match run.
        /// </summary>
        [HarmonyPatch(typeof(PauseHandler), "CheckForPause", new Type[0])]
        private static class PauseHandlerCheckForPausePatch
        {
            private static bool Prefix()
            {
                return !PauseBlocked;
            }
        }

        /// <summary>Every pause opens the pause page; refused during a match run.</summary>
        [HarmonyPatch(typeof(InGameUI), "GoToPausePage", new Type[] { typeof(bool) })]
        private static class InGameUIGoToPausePagePatch
        {
            private static bool Prefix()
            {
                if (!PauseBlocked) return true;
                Log("Pause blocked during an online match.");
                return false;
            }
        }

        /// <summary>
        /// The game's own focus handlers (SteamVR dashboard / input focus, application pause):
        /// skipped during a match run so they can't pause anything.
        /// </summary>
        [HarmonyPatch(typeof(KataConfig), "OnSteamVRInputFocus", new Type[] { typeof(bool) })]
        private static class KataConfigOnSteamVRInputFocusPatch
        {
            private static bool Prefix(bool hasFocus)
            {
                if (!PauseBlocked) return true;
                Log($"SteamVR input focus {(hasFocus ? "back" : "lost")}: ignored during an online match.");
                return false;
            }
        }

        [HarmonyPatch(typeof(KataConfig), "OnApplicationPause", new Type[] { typeof(bool) })]
        private static class KataConfigOnApplicationPausePatch
        {
            private static bool Prefix(bool pause)
            {
                if (!PauseBlocked) return true;
                Log($"Application pause={pause}: ignored during an online match.");
                return false;
            }
        }

        /// <summary>
        /// Safety net for any pause path that skips the two patches above (e.g. an application
        /// pause/focus callback pausing the audio directly). The hold's own pause is allowed.
        /// </summary>
        [HarmonyPatch(typeof(AudioDriver), "Pause", new Type[0])]
        private static class AudioDriverPausePatch
        {
            private static bool Prefix()
            {
                if (!PauseBlocked || allowPause) return true;
                Log("Audio pause blocked during an online match.");
                return false;
            }
        }

        /// <summary>
        /// ScoreKeeper.OnFailure asks KataConfig.AllowFailure() exactly when health reaches 0, and
        /// only fails the song (SongFailed, which destroys every target) when it says yes. During a
        /// match run the answer is no: the player keeps going at 0 health like with no-fail, without
        /// touching the player's No Fail setting. The run is still marked failed (TrackHealthAndCombo),
        /// so it reports failed and never uploads.
        /// </summary>
        [HarmonyPatch(typeof(KataConfig), "AllowFailure", new Type[0])]
        private static class KataConfigAllowFailurePatch
        {
            private static bool Prefix(ref bool __result)
            {
                if (!InMatchRun || !released || finishedSent) return true;

                TrackHealthAndCombo();
                __result = false;
                return false;
            }
        }

        /// <summary>Keeps the failed flag and highest streak current after every miss.</summary>
        [HarmonyPatch(typeof(ScoreKeeper), "OnFailure", new Type[] { typeof(SongCues.Cue), typeof(bool), typeof(bool) })]
        private static class ScoreKeeperOnFailureTrackPatch
        {
            private static void Postfix()
            {
                if (InMatchRun && released && !finishedSent) TrackHealthAndCombo();
            }
        }

        [HarmonyPatch(typeof(SongEnd), "OnSongOver", new Type[] { typeof(bool) })]
        private static class SongEndOnSongOverMatchPatch
        {
            /// <summary>
            /// A run that reached zero health during the match was played to the end (failure was
            /// refused at AllowFailure), but the game must still treat it as failed: the end screen,
            /// native high score / play history, the mod's run data saving and the upload all key
            /// off this argument. Harmony passes the changed value on to the original method and to
            /// every Postfix (including ExScoring's own SongEndOnSongOverPatch).
            /// </summary>
            private static void Prefix(ref bool failed)
            {
                if (!InMatchRun || finishedSent || !matchFailed || failed) return;

                failed = true;
                try
                {
                    if (ScoreKeeper.I != null) ScoreKeeper.I.mFailed = true;
                }
                catch (Exception ex)
                {
                    MelonLogger.Log($"[MpGame] couldn't set mFailed: {ex}");
                }
                Log("Song over after reaching zero health in the match: ending it as failed.");
            }

            private static void Postfix(bool failed)
            {
                try
                {
                    OnSongOver(failed);
                }
                catch (Exception ex)
                {
                    MelonLogger.Log($"[MpGame] OnSongOver hook failed: {ex}");
                }
            }
        }
    }
}