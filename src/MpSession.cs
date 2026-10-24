using System;
using System.Collections.Generic;
using System.Text;
using MelonLoader;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExScoringMod
{
    /// <summary>
    /// Online play (ApiContract.md Section 19): the state of the player's connection, lobby and
    /// match, typed calls for every client message, and events for the UI. MpClient underneath it
    /// handles only the socket.
    ///
    /// Everything here runs on Unity's main thread: calls, state, events and reply callbacks.
    /// Everything is public and static, so it can be driven from the UnityExplorer C# console and
    /// tested against the website's Multiplayer Tester before any UI exists, e.g.
    ///     ExScoringMod.MpSession.SetEnabled(true);
    ///     ExScoringMod.MpSession.Connect();
    ///     ExScoringMod.MpSession.CreateLobby("Test lobby");
    ///     ExScoringMod.MpSession.DebugDump();
    ///
    /// Reply callbacks: every request takes an optional Action&lt;MpError&gt; that is called exactly once,
    /// with null on success, a server error (19.7), or a client-side TIMEOUT / DISCONNECTED /
    /// NOT_CONNECTED / NO_MATCH. Fire-and-forget messages (setLocation, scoreUpdate, download
    /// progress, ping) take none, and are throttled here so the server never has to drop them (19.8).
    ///
    /// The resumeToken and the API key are never logged.
    /// </summary>
    public static class MpSession
    {
        private const int ProtocolVersion = 1;
        private const long RequestTimeoutMs = 10000;
        private const long PingIntervalMs = 5000;
        private const int ClockSampleCount = 8;

        // Client-side throttles, a little slower than the server's limits (19.8) so a burst at
        // the edge of the server's one-second window is never dropped.
        private const long ScoreUpdateIntervalMs = 110;     // server: 10/s
        private const long LocationIntervalMs = 260;        // server: 4/s
        private const long DownloadProgressIntervalMs = 550; // server: 2/s
        private const long GunColorsIntervalMs = 600;       // server: 2/s (v2.39)

        private const int GuestNameMaxLength = 24;

        /// <summary>The v2.39 feature name asked for in hello.</summary>
        private const string PresenceFeature = "presence";

        // ── Public state ──

        public static MpConnectionState ConnectionState => MpClient.State;
        public static bool IsConnected => MpClient.State == MpConnectionState.Connected;

        /// <summary>From the last welcome. Null before the first one.</summary>
        public static string MyUserId { get; private set; }
        public static string MyNickname { get; private set; }
        /// <summary>True when playing as a guest (no key, or the key wasn't accepted): no uploads.</summary>
        public static bool IsGuest { get; private set; }
        /// <summary>True when an API key was sent but the server still answered guest: the key isn't valid.</summary>
        public static bool ApiKeyRejected { get; private set; }

        /// <summary>The lobby the player is in, or null. Always the server's full latest copy.</summary>
        public static MpLobbyState Lobby { get; private set; }
        public static bool InLobby => Lobby != null;
        public static MpPlayerState MyPlayer => Lobby?.FindPlayer(MyUserId);
        public static bool IsHost => Lobby != null && MyUserId != null && Lobby.hostUserId == MyUserId;

        public static MpLobbySummary[] LastLobbyList { get; private set; }

        /// <summary>
        /// True when the last welcome listed "presence" (v2.39): pose and setGunColors may be sent,
        /// and poses will arrive. False before the first welcome, after a disconnect, and against a
        /// server that doesn't know the feature.
        /// </summary>
        public static bool PresenceEnabled { get; private set; }

        /// <summary>This player's seat in the current lobby (v2.39), or -1 outside a lobby.</summary>
        public static int MySeat => MyPlayer != null ? MyPlayer.seat : -1;

        /// <summary>The current match, from matchPrepare until results or abort. Null otherwise.</summary>
        public static MpMatchPrepare CurrentMatch { get; private set; }
        /// <summary>The current match's start, once matchStart arrived. Null otherwise.</summary>
        public static MpMatchStart CurrentMatchStart { get; private set; }
        public static MpMatchScores LastMatchScores { get; private set; }
        /// <summary>The last finished match's results. Kept until the next match starts.</summary>
        public static MpMatchResults LastMatchResults { get; private set; }

        /// <summary>True when this player plays the current match (false while spectating).</summary>
        public static bool IsMatchParticipant
        {
            get
            {
                if (CurrentMatch == null || CurrentMatch.participants == null || MyUserId == null) return false;
                MpPlayerState me = MyPlayer;
                if (me != null && me.matchRole == "spectator") return false;
                foreach (MpMatchParticipant participant in CurrentMatch.participants)
                {
                    if (participant != null && participant.userId == MyUserId) return true;
                }
                return false;
            }
        }

        public static MpDisconnectInfo LastDisconnect { get; private set; }

        // ── Clock (19.1.3) ──

        /// <summary>Server clock minus local clock, in ms, from the lowest-RTT recent ping.</summary>
        public static long ClockOffsetMs { get; private set; }
        public static long LastRttMs { get; private set; } = -1;
        public static long BestRttMs { get; private set; } = -1;
        /// <summary>True once at least one pong has been measured (welcome alone gives only a rough offset).</summary>
        public static bool ClockSynced => clockSamples.Count > 0;

        public static long LocalNowMs => MpClient.NowMs();
        public static long ServerNowMs => MpClient.NowMs() + ClockOffsetMs;

        /// <summary>Converts a server timestamp (e.g. matchStart.startAt) to this machine's unix ms.</summary>
        public static long ServerToLocalMs(long serverMs) => serverMs - ClockOffsetMs;

        /// <summary>Seconds from now until a server timestamp (negative once it has passed).</summary>
        public static double SecondsUntilServerTime(long serverMs) => (ServerToLocalMs(serverMs) - LocalNowMs) / 1000.0;

        // ── Events (main thread) ──

        /// <summary>Every connection state change, including reconnect attempts.</summary>
        public static event Action<MpConnectionState> OnConnectionStateChanged;
        /// <summary>A welcome arrived (first connect or a successful reconnect).</summary>
        public static event Action<MpWelcome> OnConnected;
        /// <summary>The connection ended. willReconnect tells whether a retry is coming.</summary>
        public static event Action<MpDisconnectInfo> OnDisconnected;

        /// <summary>The lobby changed (joined, created, or any update from the server).</summary>
        public static event Action<MpLobbyState> OnLobbyState;
        /// <summary>The player is no longer in a lobby: "left", "kicked", "seatExpired", "serverRestarted" or "disconnected".</summary>
        public static event Action<string> OnLeftLobby;
        public static event Action<MpLobbySummary[]> OnLobbyList;

        public static event Action<MpMatchPrepare> OnMatchPrepare;
        public static event Action<MpParticipantDropped> OnParticipantDropped;
        public static event Action<MpMatchStart> OnMatchStart;
        public static event Action<MpMatchScores> OnMatchScores;
        public static event Action<MpMatchResults> OnMatchResults;
        /// <summary>The host aborted the match (argument: matchId, may be null). No results follow.</summary>
        public static event Action<string> OnMatchAborted;

        /// <summary>
        /// Every poses message (v2.39), ~15 Hz while not in a song. Not sent at all while this
        /// player is in a song, so listeners must hide avatars on their own when a song starts.
        /// </summary>
        public static event Action<MpPoses> OnPoses;

        /// <summary>Server errors that no request callback was waiting for.</summary>
        public static event Action<MpError> OnError;

        // ── Private state ──

        private class PendingRequest
        {
            public string Type;
            public long SentAtMs;
            public Action<MpError> OnComplete;
        }

        private struct ClockSample
        {
            public long Rtt;
            public long Offset;
        }

        private static readonly Dictionary<int, PendingRequest> pendingRequests = new Dictionary<int, PendingRequest>();

        // Requests made while the connection is still opening (or reconnecting): sent in order
        // as soon as welcome arrives, so Connect() and CreateLobby() can be called back to back.
        private class QueuedRequest
        {
            public string Type;
            public JObject Fields;
            public Action<MpError> OnComplete;
            public long QueuedAtMs;
        }

        private const long QueuedRequestTimeoutMs = 20000;
        private static readonly List<QueuedRequest> queuedRequests = new List<QueuedRequest>();
        private static int nextReqId = 1;

        private static readonly List<ClockSample> clockSamples = new List<ClockSample>();
        private static long nextPingAtMs;

        // Identity used for hello, captured on Connect() (main thread, Il2Cpp reads) and reused
        // for every reconnect.
        private static string helloPlatform;
        private static string helloGuestName;
        private static bool helloSentApiKey;
        /// <summary>Seat secret from the last welcome. Memory only, never logged.</summary>
        private static string resumeToken;

        private static MpConnectionState lastReportedState = MpConnectionState.Disconnected;

        // Lobby the scoring mode was last sent for, so joining a lobby sends it once.
        private static string scoringModeSentForLobbyId;

        // Throttled sends.
        private static JObject pendingScoreUpdate;
        private static long lastScoreUpdateSentMs;

        private static string currentLocation;
        private static string currentBrowsingSongId;
        private static string currentBrowsingSongTitle;
        private static string lastSentLocationKey;
        private static bool locationDirty;
        private static long lastLocationSentMs;

        private static JObject pendingDownloadProgress;
        private static string lastSentSongStatusKey;
        private static long lastDownloadProgressSentMs;

        // setGunColors (v2.39): the latest colours asked for, and what was last sent. The colours
        // belong to the connection server-side, but the contract asks for them after every join,
        // so the sent key is forgotten on welcome and on joining a lobby.
        private static float[] wantedGunLeft;
        private static float[] wantedGunRight;
        private static string lastSentGunColorsKey;
        private static long lastGunColorsSentMs;

        // ── Connection ──

        /// <summary>
        /// Opens the connection (19.1). Does nothing while already connected or connecting.
        /// Returns false if Enable Online Play is off. Main thread.
        /// </summary>
        public static bool Connect()
        {
            if (!Config.EnableOnlinePlay)
            {
                Log("Connect ignored: Enable Online Play is off.");
                return false;
            }

            if (MpClient.State != MpConnectionState.Disconnected)
            {
                Log($"Connect ignored: already {MpClient.State}.");
                return true;
            }

            helloPlatform = ExScoring.GetPlatformString();
            helloGuestName = ReadGuestName(helloPlatform);
            helloSentApiKey = !string.IsNullOrEmpty(Config.ApiKey);
            resumeToken = null;
            clockSamples.Clear();
            ClockOffsetMs = 0;
            LastRttMs = -1;
            BestRttMs = -1;

            MpClient.Open();
            ReportStateIfChanged();
            return true;
        }

        /// <summary>Closes the connection on purpose and forgets the lobby seat. Main thread.</summary>
        public static void Disconnect()
        {
            Disconnect("Disconnected.");
        }

        /// <summary>
        /// Turns Enable Online Play on or off (saved to the config). Turning it off disconnects.
        /// </summary>
        public static void SetEnabled(bool enabled)
        {
            Config.UpdateEnableOnlinePlay(enabled);
            if (!enabled) Disconnect("Online Play was turned off.");
        }

        /// <summary>Once per frame from ExScoring.OnUpdate.</summary>
        public static void Tick()
        {
            if (!Config.EnableOnlinePlay && MpClient.State != MpConnectionState.Disconnected)
            {
                Disconnect("Online Play was turned off.");
            }

            MpClient.Tick();
            ReportStateIfChanged();
            ExpireQueuedRequests(MpClient.NowMs());

            if (!IsConnected) return;

            long now = MpClient.NowMs();

            if (now >= nextPingAtMs)
            {
                nextPingAtMs = now + PingIntervalMs;
                SendFireAndForget("ping", new JObject { ["t"] = now });
            }

            if (pendingScoreUpdate != null && now - lastScoreUpdateSentMs >= ScoreUpdateIntervalMs)
            {
                SendFireAndForget("scoreUpdate", pendingScoreUpdate);
                pendingScoreUpdate = null;
                lastScoreUpdateSentMs = now;
            }

            if (locationDirty && InLobby && now - lastLocationSentMs >= LocationIntervalMs)
            {
                SendLocationNow(now);
            }

            if (pendingDownloadProgress != null && now - lastDownloadProgressSentMs >= DownloadProgressIntervalMs)
            {
                SendFireAndForget("setSongStatus", pendingDownloadProgress);
                pendingDownloadProgress = null;
                lastDownloadProgressSentMs = now;
            }

            SendGunColorsIfDue(now);

            ExpireRequests(now);
        }

        /// <summary>Called every frame (also while not connected) to time out queued requests.</summary>
        private static void ExpireQueuedRequests(long now)
        {
            if (queuedRequests.Count == 0) return;

            for (int i = queuedRequests.Count - 1; i >= 0; i--)
            {
                QueuedRequest queued = queuedRequests[i];
                if (now - queued.QueuedAtMs <= QueuedRequestTimeoutMs) continue;

                queuedRequests.RemoveAt(i);
                Log($"{queued.Type} dropped: still not connected after {QueuedRequestTimeoutMs / 1000} s");
                if (queued.OnComplete != null)
                {
                    SafeInvoke(queued.OnComplete, new MpError { code = "NOT_CONNECTED", message = "Couldn't connect to online play in time." }, $"{queued.Type} callback");
                }
            }
        }

        private static void FlushQueuedRequests()
        {
            if (queuedRequests.Count == 0) return;

            List<QueuedRequest> toSend = new List<QueuedRequest>(queuedRequests);
            queuedRequests.Clear();
            Log($"Connected: sending {toSend.Count} queued request(s)");
            foreach (QueuedRequest queued in toSend) Request(queued.Type, queued.Fields, queued.OnComplete);
        }

        private static void FailQueuedRequests()
        {
            if (queuedRequests.Count == 0) return;

            List<QueuedRequest> toFail = new List<QueuedRequest>(queuedRequests);
            queuedRequests.Clear();
            foreach (QueuedRequest queued in toFail)
            {
                if (queued.OnComplete != null)
                {
                    SafeInvoke(queued.OnComplete, new MpError { code = "NOT_CONNECTED", message = "Not connected to online play." }, $"{queued.Type} callback");
                }
            }
        }

        /// <summary>From ExScoring.OnApplicationQuit: a clean close so the server frees the seat now.</summary>
        public static void Shutdown()
        {
            MpClient.Shutdown();
        }

        // ── Lobby list (19.2) ──

        /// <summary>Asks for the public lobbies. onDone gets the list, or null on failure.</summary>
        public static void RefreshLobbyList(Action<MpLobbySummary[]> onDone = null)
        {
            Request("listLobbies", null, error =>
            {
                if (onDone != null) SafeInvoke(onDone, error == null ? LastLobbyList : null, "RefreshLobbyList callback");
            });
        }

        // ── Anyone (19.4.1) ──

        public static void CreateLobby(string name, bool isPublic = true, int maxPlayers = 8, bool allowPlayerDifficulty = false, Action<MpError> onComplete = null)
        {
            Request("createLobby", new JObject
            {
                ["name"] = name,
                ["visibility"] = isPublic ? "public" : "private",
                ["maxPlayers"] = maxPlayers,
                ["allowPlayerDifficulty"] = allowPlayerDifficulty
            }, onComplete);
        }

        public static void JoinLobby(string code, Action<MpError> onComplete = null)
        {
            Request("joinLobby", new JObject { ["code"] = (code ?? "").Trim().ToUpperInvariant() }, onComplete);
        }

        public static void LeaveLobby(Action<MpError> onComplete = null)
        {
            Request("leaveLobby", null, onComplete);
        }

        /// <summary>
        /// Where the player is in the menus (the MenuState name, or "InSong"), plus the song they're
        /// looking at, if any. Remembered always; sent only while in a lobby, only when it changed,
        /// and at most ~4 times a second. Re-sent automatically on joining a lobby.
        /// </summary>
        public static void SetLocation(string location, string browsingSongId = null, string browsingSongTitle = null)
        {
            currentLocation = location;
            currentBrowsingSongId = browsingSongId;
            currentBrowsingSongTitle = browsingSongTitle;

            if (LocationKey() != lastSentLocationKey) locationDirty = true;

            if (locationDirty && IsConnected && InLobby && MpClient.NowMs() - lastLocationSentMs >= LocationIntervalMs)
            {
                SendLocationNow(MpClient.NowMs());
            }
        }

        /// <summary>EX (true) or classic (false). Sent automatically on joining a lobby from Config.ExType.</summary>
        public static void SetScoringMode(bool ex, Action<MpError> onComplete = null)
        {
            Request("setScoringMode", new JObject { ["mode"] = ex ? "ex" : "classic" }, onComplete);
        }

        /// <summary>
        /// The player's status for the lobby's selected song: "have", "missing", "downloading" or
        /// "unavailable". "downloading" with a progress (0..1) is throttled to ~2/s and the latest
        /// value always gets through; any other status goes out at once. Repeats are skipped.
        /// </summary>
        public static void SetSongStatus(string songId, string status, float? progress, string[] availableDifficulties, Action<MpError> onComplete = null)
        {
            JObject fields = new JObject
            {
                ["songId"] = songId,
                ["status"] = status,
                ["availableDifficulties"] = new JArray(availableDifficulties ?? new string[0])
            };
            if (progress.HasValue) fields["progress"] = Math.Max(0f, Math.Min(1f, progress.Value));

            string key = fields.ToString(Formatting.None);
            if (key == lastSentSongStatusKey)
            {
                onComplete?.Invoke(null);
                return;
            }

            if (status == "downloading" && progress.HasValue)
            {
                // Progress ticks: fire-and-forget through the throttle.
                lastSentSongStatusKey = key;
                pendingDownloadProgress = fields;
                onComplete?.Invoke(null);
                return;
            }

            pendingDownloadProgress = null;
            lastSentSongStatusKey = key;
            Request("setSongStatus", fields, onComplete);
        }

        /// <summary>Only when the lobby allows player difficulty. Clears this player's ready flag.</summary>
        public static void SetDifficulty(string difficulty, Action<MpError> onComplete = null)
        {
            Request("setDifficulty", new JObject { ["difficulty"] = difficulty }, onComplete);
        }

        public static void SetReady(bool ready, Action<MpError> onComplete = null)
        {
            Request("setReady", new JObject { ["ready"] = ready }, onComplete);
        }

        /// <summary>This player's one song pick for the host. Null withdraws it.</summary>
        public static void SetSongChoice(MpSongRef song, Action<MpError> onComplete = null)
        {
            Request("setSongChoice", new JObject { ["song"] = song != null ? (JToken)SongRefToJson(song) : JValue.CreateNull() }, onComplete);
        }

        // ── Host only (19.4.2) ──

        /// <summary>Only the arguments given are changed.</summary>
        public static void UpdateLobbySettings(string name = null, bool? isPublic = null, int? maxPlayers = null, bool? allowPlayerDifficulty = null, Action<MpError> onComplete = null)
        {
            JObject fields = new JObject();
            if (name != null) fields["name"] = name;
            if (isPublic.HasValue) fields["visibility"] = isPublic.Value ? "public" : "private";
            if (maxPlayers.HasValue) fields["maxPlayers"] = maxPlayers.Value;
            if (allowPlayerDifficulty.HasValue) fields["allowPlayerDifficulty"] = allowPlayerDifficulty.Value;
            Request("updateLobbySettings", fields, onComplete);
        }

        public static void SelectSong(MpSongRef song, Action<MpError> onComplete = null)
        {
            Request("selectSong", new JObject { ["song"] = SongRefToJson(song) }, onComplete);
        }

        /// <summary>Selects the song a player picked with SetSongChoice.</summary>
        public static void SelectSongFromChoiceOf(string userId, Action<MpError> onComplete = null)
        {
            Request("selectSong", new JObject { ["fromChoiceOf"] = userId }, onComplete);
        }

        public static void KickPlayer(string userId, Action<MpError> onComplete = null)
        {
            Request("kickPlayer", new JObject { ["userId"] = userId }, onComplete);
        }

        public static void TransferHost(string userId, Action<MpError> onComplete = null)
        {
            Request("transferHost", new JObject { ["userId"] = userId }, onComplete);
        }

        /// <summary>force: start with only the ready players; everyone else spectates.</summary>
        public static void StartMatch(bool force = false, Action<MpError> onComplete = null)
        {
            Request("startMatch", new JObject { ["force"] = force }, onComplete);
        }

        public static void AbortMatch(Action<MpError> onComplete = null)
        {
            Request("abortMatch", null, onComplete);
        }

        /// <summary>
        /// Moves a player to a seat (0..7, v2.39). If someone else sits there, the two swap. Any
        /// phase. Errors: NOT_HOST, NOT_IN_LOBBY, UNKNOWN_PLAYER, BAD_SEAT.
        /// </summary>
        public static void SetSeat(string userId, int seat, Action<MpError> onComplete = null)
        {
            Request("setSeat", new JObject { ["userId"] = userId, ["seat"] = seat }, onComplete);
        }

        // ── Presence (v2.39) ──

        /// <summary>
        /// The player's gun colours ([r, g, b], 0..1). Call as often as convenient: they go out only
        /// while in a lobby with presence on, only when they changed (or after a join), and at most
        /// ~1.7 times a second with the latest value winning.
        /// </summary>
        public static void SetGunColors(float[] left, float[] right)
        {
            if (left == null || right == null || left.Length < 3 || right.Length < 3) return;
            wantedGunLeft = new[] { Clamp01(left[0]), Clamp01(left[1]), Clamp01(left[2]) };
            wantedGunRight = new[] { Clamp01(right[0]), Clamp01(right[1]), Clamp01(right[2]) };
        }

        /// <summary>
        /// One pose (v2.39). Fire-and-forget, no reqId, never queued: dropped unless connected, in a
        /// lobby and with presence on. The caller keeps it to ~15 a second and stops in songs.
        /// </summary>
        public static void SendPose(JObject pose)
        {
            if (pose == null || !IsConnected || !PresenceEnabled || !InLobby) return;
            SendFireAndForget("pose", pose);
        }

        // ── Match (19.5) ──

        /// <summary>The song is loaded and holding before playback.</summary>
        public static void SendLoaded(Action<MpError> onComplete = null)
        {
            string matchId = CurrentMatchId();
            if (matchId == null)
            {
                FailNoMatch("loaded", onComplete);
                return;
            }
            Request("loaded", new JObject { ["matchId"] = matchId }, onComplete);
        }

        /// <summary>
        /// Live values during the song. Call as often as convenient (e.g. every frame): only the
        /// latest goes out, at most ~9 times a second.
        /// </summary>
        public static void SendScoreUpdate(MpScoreSnapshot snapshot)
        {
            string matchId = CurrentMatchId();
            if (matchId == null || snapshot == null) return;

            pendingScoreUpdate = new JObject
            {
                ["matchId"] = matchId,
                ["tick"] = snapshot.tick,
                ["exPercent"] = snapshot.exPercent,
                ["judgementScore"] = snapshot.judgementScore,
                ["classicScore"] = snapshot.classicScore,
                ["combo"] = snapshot.combo,
                ["misses"] = snapshot.misses,
                ["health"] = snapshot.health,
                ["failed"] = snapshot.failed
            };
        }

        /// <summary>The song ended for this player. Drops any score update still waiting.</summary>
        public static void SendFinished(MpFinishedReport report, Action<MpError> onComplete = null)
        {
            string matchId = CurrentMatchId();
            if (matchId == null || report == null)
            {
                FailNoMatch("finished", onComplete);
                return;
            }

            pendingScoreUpdate = null;
            Request("finished", new JObject
            {
                ["matchId"] = matchId,
                ["failed"] = report.failed,
                ["exPercent"] = report.exPercent,
                ["judgementScore"] = report.judgementScore,
                ["classicScore"] = report.classicScore,
                ["misses"] = report.misses,
                ["maxCombo"] = report.maxCombo,
                ["fullCombo"] = report.fullCombo,
                ["clientRunId"] = report.clientRunId != null ? (JToken)report.clientRunId : JValue.CreateNull()
            }, onComplete);
        }

        /// <summary>The player quit the song.</summary>
        public static void SendQuitSong(Action<MpError> onComplete = null)
        {
            string matchId = CurrentMatchId();
            if (matchId == null)
            {
                FailNoMatch("quitSong", onComplete);
                return;
            }

            pendingScoreUpdate = null;
            Request("quitSong", new JObject { ["matchId"] = matchId }, onComplete);
        }

        // ── Debug ──

        /// <summary>Logs the whole state through MelonLogger (shown even with Enable Logs off).</summary>
        public static void DebugDump()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[Mp] ===== MpSession =====");
            sb.AppendLine($"[Mp] enabled={Config.EnableOnlinePlay} state={MpClient.State} me={MyUserId ?? "none"} \"{MyNickname}\" guest={IsGuest} apiKeyRejected={ApiKeyRejected} resumeToken={(resumeToken != null ? "held" : "none")} presence={PresenceEnabled} seat={MySeat}");
            sb.AppendLine($"[Mp] clock: offset={ClockOffsetMs} ms, lastRtt={LastRttMs} ms, bestRtt={BestRttMs} ms, samples={clockSamples.Count}, serverNow={ServerNowMs}");
            sb.AppendLine($"[Mp] pending requests={pendingRequests.Count}, queued={queuedRequests.Count}, location=\"{currentLocation}\" browsing={currentBrowsingSongId ?? "none"}");

            if (LastDisconnect != null)
            {
                sb.AppendLine($"[Mp] last disconnect: code={(LastDisconnect.closeCode.HasValue ? LastDisconnect.closeCode.Value.ToString() : "none")} \"{LastDisconnect.reason}\" willReconnect={LastDisconnect.willReconnect}");
            }

            if (Lobby == null)
            {
                sb.AppendLine("[Mp] lobby: none");
            }
            else
            {
                sb.AppendLine($"[Mp] lobby {Lobby.code} \"{Lobby.name}\" ({Lobby.visibility}, {Lobby.players?.Length ?? 0}/{Lobby.maxPlayers}, playerDifficulty={Lobby.allowPlayerDifficulty}) phase={Lobby.phase} matchId={Lobby.matchId ?? "none"} isHost={IsHost}");
                sb.AppendLine($"[Mp]   selected: {(Lobby.selectedSong != null ? Lobby.selectedSong.ToString() : "none")}");
                if (Lobby.players != null)
                {
                    foreach (MpPlayerState p in Lobby.players)
                    {
                        if (p == null) continue;
                        string status = p.songStatus == null
                            ? "-"
                            : p.songStatus.status + (p.songStatus.progress.HasValue ? $" {p.songStatus.progress.Value:P0}" : "");
                        sb.AppendLine($"[Mp]   {(p.isHost ? "*" : " ")} seat {p.seat} \"{p.nickname}\" {p.userId}{(p.guest ? " guest" : "")}{(p.bot ? " bot" : "")}{(p.connected ? "" : " DISCONNECTED")}{(p.presence ? " presence" : "")}{(p.gunColors != null ? " colors" : "")} " +
                                      $"loc={p.location} mode={p.scoringMode} song={status} diff={p.difficulty ?? "-"} ready={p.ready} " +
                                      $"choice={(p.songChoice != null ? p.songChoice.title ?? p.songChoice.songId : "-")} role={p.matchRole ?? "-"}");
                    }
                }
            }

            if (CurrentMatch != null)
            {
                sb.AppendLine($"[Mp] match {CurrentMatch.matchId}: {CurrentMatch.song}, participants={CurrentMatch.participants?.Length ?? 0}, me participating={IsMatchParticipant}");
                if (CurrentMatchStart != null)
                {
                    sb.AppendLine($"[Mp]   startAt(server)={CurrentMatchStart.startAt} local={ServerToLocalMs(CurrentMatchStart.startAt)} in {SecondsUntilServerTime(CurrentMatchStart.startAt):F2} s");
                }
            }

            if (LastMatchScores != null && LastMatchScores.scores != null)
            {
                sb.AppendLine($"[Mp] last scores ({LastMatchScores.matchId}):");
                foreach (MpScoreEntry s in LastMatchScores.scores)
                {
                    if (s == null) continue;
                    sb.AppendLine($"[Mp]   \"{s.nickname}\" {s.status} ex={s.exPercent:F2} classic={s.classicScore} combo={s.combo} misses={s.misses} health={s.health:F2} failed={s.failed}");
                }
            }

            if (LastMatchResults != null && LastMatchResults.results != null)
            {
                sb.AppendLine($"[Mp] last results ({LastMatchResults.matchId}, {LastMatchResults.song}):");
                foreach (MpMatchResultEntry r in LastMatchResults.results)
                {
                    if (r == null) continue;
                    sb.AppendLine($"[Mp]   exRank={(r.exRank.HasValue ? r.exRank.Value.ToString() : "-")} classicRank={(r.classicRank.HasValue ? r.classicRank.Value.ToString() : "-")} " +
                                  $"\"{r.nickname}\" {r.status} failed={r.failed} ex={r.exPercent:F2} classic={r.classicScore} misses={r.misses} maxCombo={r.maxCombo} fc={r.fullCombo}");
                }
            }

            if (LastLobbyList != null)
            {
                sb.AppendLine($"[Mp] last lobby list: {LastLobbyList.Length}");
                foreach (MpLobbySummary l in LastLobbyList)
                {
                    if (l == null) continue;
                    sb.AppendLine($"[Mp]   {l.code} \"{l.name}\" host={l.hostNickname} {l.playerCount}/{l.maxPlayers} {l.phase} song={(l.selectedSong != null ? l.selectedSong.title : "-")}");
                }
            }

            foreach (string line in sb.ToString().TrimEnd().Split('\n'))
            {
                MelonLogger.Log(line.TrimEnd('\r'));
            }
        }

        // ── Called by MpClient (main thread) ──

        internal static void HandleSocketOpened()
        {
            JObject hello = new JObject
            {
                ["type"] = "hello",
                ["protocolVersion"] = ProtocolVersion,
                ["modVersion"] = ExScoring.BuildInfo.Version,
                ["platform"] = helloPlatform,
                ["guestName"] = helloGuestName,
                ["features"] = new JArray(PresenceFeature)
            };
            if (!string.IsNullOrEmpty(resumeToken)) hello["resumeToken"] = resumeToken;

            Log($"-> hello (platform={helloPlatform}, guestName=\"{helloGuestName}\", resume={(resumeToken != null ? "yes" : "no")}, features=[{PresenceFeature}])");
            MpClient.Send(hello.ToString(Formatting.None));
        }

        internal static void HandleSocketMessage(JObject message)
        {
            string type = message.Value<string>("type");
            int? reqId = message.Value<int?>("reqId");

            if (type != "pong" && type != "matchScores" && type != "poses")
            {
                Log($"<- {type}{(reqId.HasValue ? $" (reqId {reqId})" : "")}");
            }

            MpError error = null;

            switch (type)
            {
                case "welcome":
                    HandleWelcome(message.ToObject<MpWelcome>(MpJson.Serializer));
                    break;

                case "pong":
                    HandlePong(message);
                    break;

                case "lobbyState":
                case "lobbyJoined":
                    if (message["lobby"] != null && message["lobby"].Type == JTokenType.Object)
                    {
                        SetLobby(message["lobby"].ToObject<MpLobbyState>(MpJson.Serializer));
                    }
                    break;

                case "leftLobby":
                    ClearLobby("left");
                    break;

                case "kicked":
                    ClearLobby("kicked");
                    break;

                case "lobbyList":
                    LastLobbyList = message["lobbies"] != null && message["lobbies"].Type == JTokenType.Array
                        ? message["lobbies"].ToObject<MpLobbySummary[]>(MpJson.Serializer)
                        : new MpLobbySummary[0];
                    SafeInvoke(OnLobbyList, LastLobbyList, "OnLobbyList");
                    break;

                case "matchPrepare":
                    CurrentMatch = message.ToObject<MpMatchPrepare>(MpJson.Serializer);
                    CurrentMatchStart = null;
                    LastMatchScores = null;
                    LastMatchResults = null;
                    pendingScoreUpdate = null;
                    Log($"   match {CurrentMatch.matchId}: {CurrentMatch.song}, {CurrentMatch.participants?.Length ?? 0} participant(s), me participating={IsMatchParticipant}, load timeout {CurrentMatch.loadTimeoutMs} ms");
                    SafeInvoke(OnMatchPrepare, CurrentMatch, "OnMatchPrepare");
                    break;

                case "participantDropped":
                    MpParticipantDropped dropped = message.ToObject<MpParticipantDropped>(MpJson.Serializer);
                    Log($"   {dropped.userId} dropped from {dropped.matchId}: {dropped.reason}");
                    SafeInvoke(OnParticipantDropped, dropped, "OnParticipantDropped");
                    break;

                case "matchStart":
                    CurrentMatchStart = message.ToObject<MpMatchStart>(MpJson.Serializer);
                    Log($"   startAt(server)={CurrentMatchStart.startAt}, starts in {SecondsUntilServerTime(CurrentMatchStart.startAt):F2} s locally (offset {ClockOffsetMs} ms, synced={ClockSynced})");
                    SafeInvoke(OnMatchStart, CurrentMatchStart, "OnMatchStart");
                    break;

                case "matchScores":
                    LastMatchScores = message.ToObject<MpMatchScores>(MpJson.Serializer);
                    SafeInvoke(OnMatchScores, LastMatchScores, "OnMatchScores");
                    break;

                case "matchResults":
                    LastMatchResults = message.ToObject<MpMatchResults>(MpJson.Serializer);
                    ClearMatch();
                    SafeInvoke(OnMatchResults, LastMatchResults, "OnMatchResults");
                    break;

                case "matchAborted":
                    string abortedId = message.Value<string>("matchId");
                    ClearMatch();
                    SafeInvoke(OnMatchAborted, abortedId, "OnMatchAborted");
                    break;

                case "poses":
                    if (OnPoses != null)
                    {
                        MpPoses poses;
                        try
                        {
                            poses = message.ToObject<MpPoses>(MpJson.Serializer);
                        }
                        catch (Exception ex)
                        {
                            Log($"   poses unreadable, ignored: {ex.Message}");
                            break;
                        }
                        SafeInvoke(OnPoses, poses, "OnPoses");
                    }
                    break;

                case "ok":
                    break;

                case "error":
                    error = message.ToObject<MpError>(MpJson.Serializer);
                    Log($"   error {error}");
                    break;

                default:
                    Log($"   unknown message type \"{type}\", ignored");
                    break;
            }

            // Resolve the waiting request, after the state above is updated so the callback sees it.
            if (reqId.HasValue && pendingRequests.TryGetValue(reqId.Value, out PendingRequest pending))
            {
                pendingRequests.Remove(reqId.Value);
                if (pending.OnComplete != null) SafeInvoke(pending.OnComplete, error, $"{pending.Type} callback");
            }
            else if (error != null)
            {
                SafeInvoke(OnError, error, "OnError");
            }
        }

        internal static void HandleSocketClosed(MpDisconnectInfo info, bool forgetSeat)
        {
            FailAllRequests("DISCONNECTED", "The connection closed before the server replied.");
            pendingScoreUpdate = null;
            pendingDownloadProgress = null;

            if (forgetSeat) resumeToken = null;

            if (!info.willReconnect || forgetSeat)
            {
                if (Lobby != null) ClearLobby(info.closeCode == 4010 ? "serverRestarted" : "disconnected");
                ClearMatch();
            }

            if (!info.willReconnect)
            {
                resumeToken = null;
                MyUserId = null;
                MyNickname = null;
                FailQueuedRequests();
            }

            // Features belong to one connection; the next welcome says what the new one has.
            PresenceEnabled = false;
            lastSentGunColorsKey = null;

            LastDisconnect = info;
            Log($"Disconnected: {info.reason} (willReconnect={info.willReconnect})");
            ReportStateIfChanged();
            SafeInvoke(OnDisconnected, info, "OnDisconnected");
        }

        // ── Message handling ──

        private static void HandleWelcome(MpWelcome welcome)
        {
            if (welcome == null) return;

            MyUserId = welcome.userId;
            MyNickname = welcome.nickname;
            IsGuest = welcome.guest;
            ApiKeyRejected = helloSentApiKey && welcome.guest;
            resumeToken = welcome.resumeToken;
            PresenceEnabled = welcome.features != null && Array.IndexOf(welcome.features, PresenceFeature) >= 0;
            lastSentGunColorsKey = null; // a new connection holds no colours yet

            // Rough offset until the first pong (no RTT correction).
            if (clockSamples.Count == 0 && welcome.serverTime > 0)
            {
                ClockOffsetMs = welcome.serverTime - MpClient.NowMs();
            }

            MpClient.MarkWelcomed();
            nextPingAtMs = 0; // ping right away for a real clock sample

            Log($"   welcome: userId={MyUserId} nickname=\"{MyNickname}\" guest={IsGuest}{(ApiKeyRejected ? " (API key not accepted)" : "")} lobby={(welcome.lobby != null ? welcome.lobby.code : "none")} presence={PresenceEnabled}");

            if (welcome.lobby != null)
            {
                SetLobby(welcome.lobby);
                locationDirty = true; // the server may have missed changes while we were away
            }
            else if (Lobby != null)
            {
                // Reconnected too late: the 30 s grace ran out and the seat is gone.
                ClearLobby("seatExpired");
                ClearMatch();
            }

            ReportStateIfChanged();
            SafeInvoke(OnConnected, welcome, "OnConnected");
            FlushQueuedRequests();
        }

        private static void HandlePong(JObject message)
        {
            long now = MpClient.NowMs();
            long t = message.Value<long?>("t") ?? 0;
            long serverTime = message.Value<long?>("serverTime") ?? 0;
            if (t <= 0 || serverTime <= 0) return;

            long rtt = now - t;
            if (rtt < 0) return;

            clockSamples.Add(new ClockSample { Rtt = rtt, Offset = serverTime - (t + rtt / 2) });
            if (clockSamples.Count > ClockSampleCount) clockSamples.RemoveAt(0);

            ClockSample best = clockSamples[0];
            foreach (ClockSample sample in clockSamples)
            {
                if (sample.Rtt < best.Rtt) best = sample;
            }

            LastRttMs = rtt;
            BestRttMs = best.Rtt;
            ClockOffsetMs = best.Offset;
        }

        private static void SetLobby(MpLobbyState lobby)
        {
            if (lobby == null) return;

            bool newLobby = Lobby == null || Lobby.lobbyId != lobby.lobbyId;
            Lobby = lobby;

            // The server resets songStatus to null whenever the selection changes (19.3), even
            // when the same song is picked again; forget what we last sent so it goes out again.
            MpPlayerState me = lobby.FindPlayer(MyUserId);
            if (me == null || me.songStatus == null) lastSentSongStatusKey = null;

            if (lobby.phase == "lobby" && CurrentMatch != null)
            {
                // Back on the lobby screen without results/abort reaching us (e.g. a reconnect).
                ClearMatch();
            }

            if (newLobby)
            {
                Log($"   in lobby {lobby.code} \"{lobby.name}\" as {(IsHost ? "host" : "player")}");
                lastSentSongStatusKey = null;
                lastSentLocationKey = null;
                locationDirty = currentLocation != null;
                lastSentGunColorsKey = null; // v2.39: send the colours once after every join
            }

            if (lobby.lobbyId != scoringModeSentForLobbyId)
            {
                scoringModeSentForLobbyId = lobby.lobbyId;
                SetScoringMode(Config.ExType);
            }

            SafeInvoke(OnLobbyState, Lobby, "OnLobbyState");
        }

        private static void ClearLobby(string reason)
        {
            if (Lobby == null) return;

            Log($"   left lobby {Lobby.code} ({reason})");
            Lobby = null;
            scoringModeSentForLobbyId = null;
            lastSentSongStatusKey = null;
            lastSentLocationKey = null;
            pendingDownloadProgress = null;
            ClearMatch();
            SafeInvoke(OnLeftLobby, reason, "OnLeftLobby");
        }

        private static void ClearMatch()
        {
            CurrentMatch = null;
            CurrentMatchStart = null;
            pendingScoreUpdate = null;
        }

        private static void Disconnect(string reason)
        {
            bool wasActive = MpClient.State != MpConnectionState.Disconnected;
            MpClient.Close(reason);

            if (!wasActive) return;

            HandleSocketClosed(new MpDisconnectInfo { closeCode = null, reason = reason, willReconnect = false }, true);
        }

        // ── Sending ──

        private static void Request(string type, JObject fields, Action<MpError> onComplete)
        {
            if (!IsConnected)
            {
                MpConnectionState state = MpClient.State;
                if (state == MpConnectionState.Connecting || state == MpConnectionState.Handshaking || state == MpConnectionState.Reconnecting)
                {
                    queuedRequests.Add(new QueuedRequest
                    {
                        Type = type,
                        Fields = fields != null ? (JObject)fields.DeepClone() : null,
                        OnComplete = onComplete,
                        QueuedAtMs = MpClient.NowMs()
                    });
                    Log($"{type} queued until connected ({state}).");
                    return;
                }

                Log($"{type} not sent: not connected ({state}).");
                if (onComplete != null)
                {
                    SafeInvoke(onComplete, new MpError { code = "NOT_CONNECTED", message = "Not connected to online play." }, $"{type} callback");
                }
                return;
            }

            int reqId = nextReqId++;
            JObject message = new JObject { ["type"] = type };
            if (fields != null)
            {
                foreach (JProperty property in fields.Properties()) message[property.Name] = property.Value.DeepClone();
            }
            message["reqId"] = reqId;

            if (!MpClient.Send(message.ToString(Formatting.None)))
            {
                if (onComplete != null)
                {
                    SafeInvoke(onComplete, new MpError { reqId = reqId, code = "NOT_CONNECTED", message = "The socket isn't open." }, $"{type} callback");
                }
                return;
            }

            pendingRequests[reqId] = new PendingRequest { Type = type, SentAtMs = MpClient.NowMs(), OnComplete = onComplete };
            Log($"-> {type} (reqId {reqId})");
        }

        private static void SendFireAndForget(string type, JObject fields)
        {
            JObject message = new JObject { ["type"] = type };
            if (fields != null)
            {
                foreach (JProperty property in fields.Properties()) message[property.Name] = property.Value.DeepClone();
            }
            MpClient.Send(message.ToString(Formatting.None));
        }

        private static void SendLocationNow(long now)
        {
            JObject fields = new JObject { ["location"] = currentLocation ?? "" };
            fields["browsingSong"] = currentBrowsingSongId != null
                ? (JToken)new JObject { ["songId"] = currentBrowsingSongId, ["title"] = currentBrowsingSongTitle }
                : JValue.CreateNull();

            SendFireAndForget("setLocation", fields);
            lastSentLocationKey = LocationKey();
            lastLocationSentMs = now;
            locationDirty = false;
        }

        private static string LocationKey()
        {
            return (currentLocation ?? "") + "|" + (currentBrowsingSongId ?? "");
        }

        private static void SendGunColorsIfDue(long now)
        {
            if (wantedGunLeft == null || wantedGunRight == null) return;
            if (!PresenceEnabled || !InLobby) return;
            if (now - lastGunColorsSentMs < GunColorsIntervalMs) return;

            string key = ColorKey(wantedGunLeft) + "|" + ColorKey(wantedGunRight);
            if (key == lastSentGunColorsKey) return;

            lastSentGunColorsKey = key;
            lastGunColorsSentMs = now;
            Request("setGunColors", new JObject
            {
                ["left"] = new JArray(Round3(wantedGunLeft[0]), Round3(wantedGunLeft[1]), Round3(wantedGunLeft[2])),
                ["right"] = new JArray(Round3(wantedGunRight[0]), Round3(wantedGunRight[1]), Round3(wantedGunRight[2]))
            }, error =>
            {
                // Let the next tick try again (e.g. BAD_MESSAGE can't happen with clamped values,
                // but a TIMEOUT or a drop can).
                if (error != null && lastSentGunColorsKey == key) lastSentGunColorsKey = null;
            });
        }

        private static string ColorKey(float[] c)
        {
            return $"{Round3(c[0])},{Round3(c[1])},{Round3(c[2])}";
        }

        private static double Round3(float v)
        {
            return Math.Round(v, 3);
        }

        private static float Clamp01(float v)
        {
            if (float.IsNaN(v)) return 0f;
            return v < 0f ? 0f : (v > 1f ? 1f : v);
        }

        private static void ExpireRequests(long now)
        {
            if (pendingRequests.Count == 0) return;

            List<int> expired = null;
            foreach (KeyValuePair<int, PendingRequest> entry in pendingRequests)
            {
                if (now - entry.Value.SentAtMs > RequestTimeoutMs)
                {
                    if (expired == null) expired = new List<int>();
                    expired.Add(entry.Key);
                }
            }
            if (expired == null) return;

            foreach (int reqId in expired)
            {
                PendingRequest pending = pendingRequests[reqId];
                pendingRequests.Remove(reqId);
                Log($"{pending.Type} (reqId {reqId}) timed out");
                if (pending.OnComplete != null)
                {
                    SafeInvoke(pending.OnComplete, new MpError { reqId = reqId, code = "TIMEOUT", message = "The server didn't reply in time." }, $"{pending.Type} callback");
                }
            }
        }

        private static void FailAllRequests(string code, string message)
        {
            if (pendingRequests.Count == 0) return;

            List<KeyValuePair<int, PendingRequest>> all = new List<KeyValuePair<int, PendingRequest>>(pendingRequests);
            pendingRequests.Clear();
            foreach (KeyValuePair<int, PendingRequest> entry in all)
            {
                if (entry.Value.OnComplete != null)
                {
                    SafeInvoke(entry.Value.OnComplete, new MpError { reqId = entry.Key, code = code, message = message }, $"{entry.Value.Type} callback");
                }
            }
        }

        private static void FailNoMatch(string type, Action<MpError> onComplete)
        {
            Log($"{type} not sent: no current match.");
            if (onComplete != null)
            {
                SafeInvoke(onComplete, new MpError { code = "NO_MATCH", message = "There is no match in progress." }, $"{type} callback");
            }
        }

        private static string CurrentMatchId()
        {
            if (CurrentMatch != null && CurrentMatch.matchId != null) return CurrentMatch.matchId;
            return Lobby?.matchId;
        }

        private static JObject SongRefToJson(MpSongRef song)
        {
            if (song == null) return null;
            return new JObject
            {
                ["songId"] = song.songId,
                ["difficulty"] = song.difficulty,
                ["title"] = song.title,
                ["artist"] = song.artist,
                ["mapper"] = song.mapper,
                ["maudicaId"] = song.maudicaId.HasValue ? (JToken)song.maudicaId.Value : JValue.CreateNull()
            };
        }

        // ── Helpers ──

        private static void ReportStateIfChanged()
        {
            MpConnectionState state = MpClient.State;
            if (state == lastReportedState) return;

            Log($"State {lastReportedState} -> {state}");
            lastReportedState = state;
            SafeInvoke(OnConnectionStateChanged, state, "OnConnectionStateChanged");
        }

        /// <summary>
        /// Platform username (ApiContract.md 19.11), falling back to the platform name. Control
        /// characters dropped, cut to the server's 24-character limit. Main thread (Il2Cpp).
        /// </summary>
        private static string ReadGuestName(string platform)
        {
            string name = null;
            try
            {
                if (PlatformChooser.I != null) name = PlatformChooser.I.GetUserName();
            }
            catch (Exception ex)
            {
                Log($"GetUserName failed: {ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(name)) name = platform;

            StringBuilder clean = new StringBuilder();
            foreach (char c in name ?? "")
            {
                if (!char.IsControl(c)) clean.Append(c);
            }
            name = clean.ToString().Trim();
            if (name.Length > GuestNameMaxLength) name = name.Substring(0, GuestNameMaxLength).Trim();
            return name.Length > 0 ? name : "Guest";
        }

        private static void SafeInvoke<T>(Action<T> handler, T argument, string what)
        {
            if (handler == null) return;

            // One subscriber throwing must not stop the others, or break the message loop.
            foreach (Delegate subscriber in handler.GetInvocationList())
            {
                try
                {
                    ((Action<T>)subscriber)(argument);
                }
                catch (Exception ex)
                {
                    MelonLogger.Log($"[Mp] {what} threw: {ex}");
                }
            }
        }

        private static void Log(string message)
        {
            DebugLog.Log("[Mp] " + message);
        }
    }
}