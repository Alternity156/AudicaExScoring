using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// Online play menus:
    ///  - BuildSettings: the "Multiplayer" category of the song list's Options (GlobalOptions),
    ///    settings only (Enable Online Play, Show Other Players);
    ///  - Build: the Online Play screen on the Main page (OnlinePlayScreen), not reachable from the
    ///    song list. The settings at the top, then the lobby menu. Not in a lobby: create, join by code, the public lobby
    ///    list. In a lobby: ready, leave, suggest a song, and for the host the lobby settings,
    ///    song selection, start/abort, kick and make host.
    /// The Main page's Online Play button (OnlinePlayButton) opens that screen.
    ///
    /// Only the controls live here. Live per-player status (location, song status, scores) is on
    /// the LobbyPanel, because redrawing this menu wipes every row and resets its scroll: it is
    /// redrawn only when something that changes the controls themselves changes (see
    /// LobbySignature), never for location/download/score updates.
    /// </summary>
    internal static class OnlinePlayMenu
    {
        public const string SettingsCategoryId = "opt_multiplayer";

        private const long ListRefreshMinIntervalMs = 5000;
        private const int LobbyCodeLength = 4;
        private const int LobbyNameMaxLength = 40;

        private static bool initialized;

        /// <summary>
        /// Host setting: the launch panel's Play (and Start Match) start with whoever is ready
        /// instead of waiting for everyone. Not saved; off in every new session.
        /// </summary>
        public static bool ForceStart { get; private set; }
        private static string status = "";
        private static long lastListRequestMs;
        private static string lastLobbySignature;

        // Join-by-code keyboard entry (routed here by the KeyboardEntry patches in Hooks.cs).
        public static bool EnteringCode { get; private set; }
        private static string codeText = "";
        private static TextMeshPro codeLabel;

        public static void Init()
        {
            if (initialized) return;
            initialized = true;

            MpSession.OnConnectionStateChanged += _ => Redraw();
            MpSession.OnConnected += _ => { lastListRequestMs = 0; Redraw(); };
            MpSession.OnLobbyList += _ => Redraw();
            MpSession.OnLeftLobby += reason =>
            {
                lastLobbySignature = null;
                lastListRequestMs = 0;
                SetStatus(DescribeLeft(reason));
            };
            MpSession.OnLobbyState += lobby =>
            {
                string signature = LobbySignature(lobby);
                if (signature == lastLobbySignature) return;
                lastLobbySignature = signature;
                Redraw();
            };
            MpSession.OnDisconnected += info => SetStatus(info.reason + (info.willReconnect ? " Reconnecting..." : ""));
            MpSession.OnError += error => SetStatus(Describe(error));
        }

        // ── Multiplayer (settings) category ──

        public static void BuildSettings()
        {
            OptionsMenuClone.AddRow(OptionsMenuClone.CreateHeader(0, "Multiplayer"));

            var enableToggle = OptionsMenuClone.CreateToggle(0, "Enable Online Play",
                () => Config.EnableOnlinePlay,
                v => MpSession.SetEnabled(v),
                "Allows connecting to online play lobbies. Nothing connects while this is off");
            OptionsMenuClone.AddRow(enableToggle, CreateShowOtherPlayersToggle());
        }

        /// <summary>Show Other Players (MpPresence): the lobby's avatars in the menus.</summary>
        private static UnityEngine.GameObject CreateShowOtherPlayersToggle()
        {
            return OptionsMenuClone.CreateToggle(1, "Show Other Players",
                () => Config.ShowOtherPlayers,
                v => Config.UpdateShowOtherPlayers(v),
                "Shows the other players in your lobby in the menus. Your own pose is still shared while this is off");
        }

        // ── Online Play (lobby) category ──

        public static void Build()
        {
            codeLabel = null;

            // Settings first (the same toggles as Options > Multiplayer in the song list).
            OptionsMenuClone.AddRow(OptionsMenuClone.CreateToggle(0, "Enable Online Play",
                () => Config.EnableOnlinePlay,
                v =>
                {
                    MpSession.SetEnabled(v);
                    SetStatus("");
                    if (v) MpSession.Connect();
                    Redraw();
                },
                "Allows connecting to online play lobbies. Nothing connects while this is off"),
                CreateShowOtherPlayersToggle());

            if (!Config.EnableOnlinePlay)
            {
                OptionsMenuClone.AddRow(OptionsMenuClone.CreateHeader(0, "Online Play is turned off"));
                return;
            }

            OptionsMenuClone.AddRow(OptionsMenuClone.CreateHeader(0, StatusLine()));

            MpConnectionState state = MpSession.ConnectionState;
            if (state == MpConnectionState.Disconnected)
            {
                OptionsMenuClone.AddRow(OptionsMenuClone.CreateButton(0, "Connect", () =>
                {
                    SetStatus("");
                    MpSession.Connect();
                }, "Connects to the online play server"));
                return;
            }

            if (MpSession.InLobby)
            {
                BuildLobby();
            }
            else if (state == MpConnectionState.Connected)
            {
                BuildBrowser();
            }
        }

        private static void BuildBrowser()
        {
            long now = MpSession.LocalNowMs;
            if (now - lastListRequestMs > ListRefreshMinIntervalMs)
            {
                lastListRequestMs = now;
                MpSession.RefreshLobbyList(list => { if (list == null) SetStatus("Couldn't load the lobby list."); });
            }

            var create = OptionsMenuClone.CreateButton(0, "Create Lobby", OnCreateLobby,
                $"Creates a public lobby named \"{DefaultLobbyName()}\"; change its settings once you're in it");
            var join = OptionsMenuClone.CreateButton(1, "Join by Code", StartCodeEntry,
                "Type a lobby's 4-character code on the keyboard");
            OptionsMenuClone.AddRow(create, join);

            if (EnteringCode)
            {
                GameObject codeHeader = OptionsMenuClone.CreateHeader(0, CodeLabelText());
                codeLabel = codeHeader.GetComponentInChildren<TextMeshPro>(true);
                OptionsMenuClone.AddRow(codeHeader);
            }

            OptionsMenuClone.AddRow(OptionsMenuClone.CreateButton(0, "Refresh", () =>
            {
                lastListRequestMs = MpSession.LocalNowMs;
                MpSession.RefreshLobbyList(list => { if (list == null) SetStatus("Couldn't load the lobby list."); });
            }, "Reloads the list of public lobbies"));

            MpLobbySummary[] lobbies = MpSession.LastLobbyList;
            int count = lobbies != null ? lobbies.Length : 0;
            OptionsMenuClone.AddRow(OptionsMenuClone.CreateHeader(0, count == 0
                ? "No public lobbies. Create one, or join one by code."
                : $"Public lobbies ({count})"));

            if (lobbies == null) return;
            foreach (MpLobbySummary summary in lobbies)
            {
                if (summary == null) continue;
                string code = summary.code;
                string details = $"{summary.hostNickname} - {summary.playerCount}/{summary.maxPlayers} - " +
                                 (summary.phase == "lobby"
                                     ? (summary.selectedSong != null ? $"{summary.selectedSong.title} [{summary.selectedSong.difficulty}]" : "no song yet")
                                     : "playing");

                var joinButton = OptionsMenuClone.CreateButton(0, Trim(summary.name, 24), () => JoinLobby(code),
                    $"Join {summary.name} (code {code}): {details}");
                var info = OptionsMenuClone.CreateHeader(1, details);
                OptionsMenuClone.AddRow(joinButton, info);
            }
        }

        private static void BuildLobby()
        {
            MpLobbyState lobby = MpSession.Lobby;
            MpPlayerState me = MpSession.MyPlayer;
            bool isHost = MpSession.IsHost;
            bool inMatch = lobby.phase != "lobby";

            OptionsMenuClone.AddRow(OptionsMenuClone.CreateHeader(0, $"Lobby {lobby.code} - {lobby.name}"));
            OptionsMenuClone.AddRow(OptionsMenuClone.CreateHeader(0,
                (inMatch ? "Match in progress - " : "") +
                (lobby.selectedSong != null ? $"Song: {lobby.selectedSong.title} [{lobby.selectedSong.difficulty}]" : "No song selected yet")));

            var ready = OptionsMenuClone.CreateToggle(0, "Ready",
                () => MpSession.MyPlayer != null && MpSession.MyPlayer.ready,
                v => MpSession.SetReady(v, ReportError),
                "Ready to play the selected song (you need the song, on an available difficulty)");
            var leave = OptionsMenuClone.CreateButton(1, "Leave Lobby", () => MpSession.LeaveLobby(ReportError),
                "Leaves the lobby");
            OptionsMenuClone.AddRow(ready, leave);

            // The song currently selected in the song list, with the selected difficulty.
            MpSongRef current = CurrentSongRef();

            if (me != null && me.songChoice != null)
            {
                OptionsMenuClone.AddRow(OptionsMenuClone.CreateButton(0, "Withdraw Suggestion",
                    () => MpSession.SetSongChoice(null, ReportError),
                    $"Withdraws your suggestion ({me.songChoice.title} [{me.songChoice.difficulty}])"));
            }
            else if (current != null && !isHost)
            {
                OptionsMenuClone.AddRow(OptionsMenuClone.CreateButton(0, "Suggest: " + Trim(current.title, 18),
                    () => MpSession.SetSongChoice(current, ReportError),
                    $"Suggests {current.title} [{current.difficulty}] to the host (the song selected in the song list)"));
            }
            else if (!isHost)
            {
                OptionsMenuClone.AddRow(OptionsMenuClone.CreateHeader(0, "Pick a song in the song list to suggest it."));
            }

            if (isHost)
            {
                OptionsMenuClone.AddRow(OptionsMenuClone.CreateHeader(0, "Host"));

                GameObject select = current != null
                    ? OptionsMenuClone.CreateButton(0, "Select: " + Trim(current.title, 18),
                        () => MpSession.SelectSong(current, ReportError),
                        $"Makes {current.title} [{current.difficulty}] (selected in the song list) the lobby's song")
                    : OptionsMenuClone.CreateHeader(0, "Pick a song in the song list");
                var visibility = OptionsMenuClone.CreateToggle(1, "Public",
                    () => MpSession.Lobby != null && MpSession.Lobby.visibility == "public",
                    v => MpSession.UpdateLobbySettings(isPublic: v, onComplete: ReportError),
                    "Public lobbies are listed for everyone; private ones can only be joined by code");
                OptionsMenuClone.AddRow(select, visibility);

                string[] sizes = { "2", "3", "4", "5", "6", "7", "8" };
                var maxPlayers = OptionsMenuClone.CreateCycle(0, "Max Players", sizes,
                    () => Mathf.Clamp((MpSession.Lobby != null ? MpSession.Lobby.maxPlayers : 8) - 2, 0, sizes.Length - 1),
                    i => MpSession.UpdateLobbySettings(maxPlayers: i + 2, onComplete: ReportError),
                    6, "How many players the lobby holds (can't go below the current player count)");
                var pickDifficulty = OptionsMenuClone.CreateToggle(1, "Players Pick Difficulty",
                    () => MpSession.Lobby != null && MpSession.Lobby.allowPlayerDifficulty,
                    v => MpSession.UpdateLobbySettings(allowPlayerDifficulty: v, onComplete: ReportError),
                    "Lets each player choose their own difficulty for the selected song");
                OptionsMenuClone.AddRow(maxPlayers, pickDifficulty);

                if (!inMatch)
                {
                    var start = OptionsMenuClone.CreateButton(0, "Start Match", () => MpSession.StartMatch(ForceStart, ReportError),
                        "Starts the selected song (same as Play on the launch panel)");
                    var force = OptionsMenuClone.CreateToggle(1, "Force Start",
                        () => ForceStart,
                        v => ForceStart = v,
                        "On: start without waiting for everyone to be ready; players who aren't ready spectate");
                    OptionsMenuClone.AddRow(start, force);
                }
                else
                {
                    OptionsMenuClone.AddRow(OptionsMenuClone.CreateButton(0, "Abort Match", () => MpSession.AbortMatch(ReportError),
                        "Ends the match for everyone, with no results"));
                }
            }

            // Suggestions: the host can pick one; everyone sees them.
            List<MpPlayerState> suggesters = new List<MpPlayerState>();
            if (lobby.players != null)
            {
                foreach (MpPlayerState p in lobby.players)
                {
                    if (p != null && p.songChoice != null) suggesters.Add(p);
                }
            }

            if (suggesters.Count > 0)
            {
                OptionsMenuClone.AddRow(OptionsMenuClone.CreateHeader(0, "Suggestions"));
                foreach (MpPlayerState p in suggesters)
                {
                    string userId = p.userId;
                    string text = $"{p.nickname}: {p.songChoice.title} [{p.songChoice.difficulty}]";
                    OptionsMenuClone.AddRow(isHost
                        ? OptionsMenuClone.CreateButton(0, Trim(text, 26), () => MpSession.SelectSongFromChoiceOf(userId, ReportError),
                            "Selects " + text)
                        : OptionsMenuClone.CreateHeader(0, text));
                }
            }

            // Players, in join order. The host gets Kick / Make Host for everyone else.
            int playerCount = lobby.players != null ? lobby.players.Length : 0;
            OptionsMenuClone.AddRow(OptionsMenuClone.CreateHeader(0, $"Players ({playerCount}/{lobby.maxPlayers})"));
            if (lobby.players == null) return;

            foreach (MpPlayerState p in lobby.players)
            {
                if (p == null) continue;
                bool isMe = p.userId == MpSession.MyUserId;

                string label = (p.isHost ? "* " : "") + p.nickname +
                               (p.guest && !p.bot ? " (guest)" : "") +
                               (p.bot ? " (bot)" : "") +
                               (isMe ? " (you)" : "");
                OptionsMenuClone.AddRow(OptionsMenuClone.CreateHeader(0, label));

                if (isHost && !isMe)
                {
                    string userId = p.userId;
                    string nickname = p.nickname;
                    var kick = OptionsMenuClone.CreateButton(0, "Kick " + Trim(nickname, 14),
                        () => MpSession.KickPlayer(userId, ReportError), $"Removes {nickname} from the lobby");
                    var makeHost = OptionsMenuClone.CreateButton(1, "Make Host",
                        () => MpSession.TransferHost(userId, ReportError), $"Makes {nickname} the host");
                    OptionsMenuClone.AddRow(kick, makeHost);
                }
            }
        }

        // ── Actions ──

        private static void OnCreateLobby()
        {
            SetStatus("Creating a lobby...");
            MpSession.CreateLobby(DefaultLobbyName(), true, 8, false, error =>
            {
                SetStatus(error == null ? "" : Describe(error));
            });
        }

        private static void JoinLobby(string code)
        {
            SetStatus($"Joining {code}...");
            MpSession.JoinLobby(code, error =>
            {
                SetStatus(error == null ? "" : Describe(error));
            });
        }

        private static void ReportError(MpError error)
        {
            if (error != null) SetStatus(Describe(error));
        }

        // ── Join by code (keyboard) ──

        private static void StartCodeEntry()
        {
            if (EnteringCode) return;
            EnteringCode = true;
            codeText = "";
            SearchKeyboard.ShowGeneric();
            Redraw();
        }

        public static void AppendCode(string s)
        {
            if (!EnteringCode || s == null) return;

            foreach (char c in s.ToUpperInvariant())
            {
                if (codeText.Length >= LobbyCodeLength) break;
                if (char.IsLetterOrDigit(c)) codeText += c;
            }
            UpdateCodeLabel();

            if (codeText.Length == LobbyCodeLength) FinishCodeEntry();
        }

        public static void BackspaceCode()
        {
            if (!EnteringCode || codeText.Length == 0) return;
            codeText = codeText.Substring(0, codeText.Length - 1);
            UpdateCodeLabel();
        }

        public static void ClearCode()
        {
            if (!EnteringCode) return;
            codeText = "";
            UpdateCodeLabel();
        }

        /// <summary>Keyboard "done", or the 4th character typed.</summary>
        public static void FinishCodeEntry()
        {
            if (!EnteringCode) return;
            string code = codeText;
            CancelCodeEntry();

            if (code.Length != LobbyCodeLength)
            {
                SetStatus($"Lobby codes are {LobbyCodeLength} characters.");
                return;
            }
            JoinLobby(code);
        }

        /// <summary>Stops code entry and hides the keyboard. Also called when the panel closes.</summary>
        public static void CancelCodeEntry()
        {
            if (!EnteringCode) return;
            EnteringCode = false;
            codeText = "";
            codeLabel = null;
            SearchKeyboard.Hide();
            Redraw();
        }

        private static void UpdateCodeLabel()
        {
            if (codeLabel != null) codeLabel.text = CodeLabelText();
        }

        private static string CodeLabelText()
        {
            StringBuilder sb = new StringBuilder("Code: ");
            for (int i = 0; i < LobbyCodeLength; i++) sb.Append(i < codeText.Length ? codeText[i] : '_');
            return sb.ToString();
        }

        // ── Helpers ──

        private static void Redraw()
        {
            OnlinePlayScreen.RedrawIfShowing();
        }

        private static void SetStatus(string text)
        {
            status = text ?? "";
            Redraw();
        }

        private static string StatusLine()
        {
            string connection;
            switch (MpSession.ConnectionState)
            {
                case MpConnectionState.Connected:
                    connection = $"Playing as {MpSession.MyNickname}" +
                                 (MpSession.ApiKeyRejected ? " (guest: your API key wasn't accepted, scores won't upload)"
                                     : MpSession.IsGuest ? " (guest: scores won't upload)" : "");
                    break;
                case MpConnectionState.Connecting:
                case MpConnectionState.Handshaking:
                    connection = "Connecting...";
                    break;
                case MpConnectionState.Reconnecting:
                    connection = "Connection lost, reconnecting...";
                    break;
                default:
                    connection = "Not connected";
                    break;
            }
            return string.IsNullOrEmpty(status) ? connection : connection + " - " + status;
        }

        /// <summary>
        /// Everything about the lobby that changes this menu's controls. Locations, ready flags
        /// (the toggle reads them live), song status, downloads and scores are left out on purpose.
        /// </summary>
        private static string LobbySignature(MpLobbyState lobby)
        {
            if (lobby == null) return null;

            StringBuilder sb = new StringBuilder();
            sb.Append(lobby.lobbyId).Append('|').Append(lobby.name).Append('|').Append(lobby.hostUserId)
              .Append('|').Append(lobby.visibility).Append('|').Append(lobby.maxPlayers)
              .Append('|').Append(lobby.allowPlayerDifficulty).Append('|').Append(lobby.phase)
              .Append('|').Append(lobby.selectedSong?.songId).Append(':').Append(lobby.selectedSong?.difficulty);
            if (lobby.players != null)
            {
                foreach (MpPlayerState p in lobby.players)
                {
                    if (p == null) continue;
                    sb.Append('|').Append(p.userId).Append(':').Append(p.nickname)
                      .Append(':').Append(p.songChoice?.songId).Append(':').Append(p.songChoice?.difficulty);
                }
            }
            return sb.ToString();
        }

        private static MpSongRef CurrentSongRef()
        {
            SongList.SongData song = ExScoring.selectedSongData;
            if (song == null || KataConfig.I == null) return null;

            return new MpSongRef
            {
                songId = song.songID,
                difficulty = KataConfig.I.GetDifficulty().ToString(),
                title = song.title,
                artist = song.artist,
                mapper = song.author,
                maudicaId = null // the server looks it up from title/artist/mapper (Section 12)
            };
        }

        private static string DefaultLobbyName()
        {
            string nickname = string.IsNullOrEmpty(MpSession.MyNickname) ? "Online" : MpSession.MyNickname;
            return Trim(nickname + "'s Lobby", LobbyNameMaxLength);
        }

        private static string Trim(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max) return text ?? "";
            return text.Substring(0, max - 1) + "...";
        }

        private static string DescribeLeft(string reason)
        {
            switch (reason)
            {
                case "kicked": return "You were removed from the lobby.";
                case "seatExpired": return "Reconnected too late: your lobby seat expired.";
                case "serverRestarted": return "The server restarted and every lobby was closed.";
                case "disconnected": return "Disconnected from the lobby.";
                default: return "";
            }
        }

        public static string Describe(MpError error)
        {
            if (error == null) return "";
            switch (error.code)
            {
                case "LOBBY_NOT_FOUND": return "No lobby with that code.";
                case "LOBBY_FULL": return "That lobby is full.";
                case "ALREADY_IN_LOBBY": return "You're already in a lobby.";
                case "NOT_IN_LOBBY": return "You're not in a lobby.";
                case "NOT_HOST": return "Only the host can do that.";
                case "WRONG_PHASE": return "Not possible while a match is running.";
                case "NO_SONG_SELECTED": return "Select a song first.";
                case "NOT_ELIGIBLE": return "You need the selected song (on an available difficulty) to be ready.";
                case "NOT_ALL_READY": return "Not everyone is ready." + NotReadyNames(error.userIds);
                case "BAD_DIFFICULTY": return "That difficulty isn't available for this song.";
                case "UNKNOWN_PLAYER": return "That player isn't in the lobby anymore.";
                case "RATE_LIMITED": return "Too fast, try again in a moment.";
                case "TOO_MANY_GUESTS": return "Too many guest connections from this network.";
                case "SERVER_FULL": return "The server is full, try again later.";
                case "TIMEOUT": return "The server didn't answer.";
                case "NOT_CONNECTED":
                case "DISCONNECTED": return "Not connected.";
                default: return string.IsNullOrEmpty(error.message) ? error.code : error.message;
            }
        }

        private static string NotReadyNames(string[] userIds)
        {
            if (userIds == null || userIds.Length == 0 || MpSession.Lobby == null) return "";

            List<string> names = new List<string>();
            foreach (string id in userIds)
            {
                MpPlayerState p = MpSession.Lobby.FindPlayer(id);
                names.Add(p != null ? p.nickname : id);
            }
            return " Waiting for: " + string.Join(", ", names);
        }
    }
}