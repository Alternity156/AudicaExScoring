using System;
using Newtonsoft.Json;

namespace ExScoringMod
{
    // Data shapes for online play (ApiContract.md Section 19). Server -> client messages are read
    // into these with Newtonsoft (unknown fields are ignored, so the server can add fields without
    // breaking older mod builds). Client -> server messages are built as JObjects in MpSession, so
    // optional fields can be left out and explicit nulls (setSongChoice) sent exactly.
    //
    // Field names match the contract's JSON exactly, hence the lowercase public fields.

    /// <summary>SongRef (19.3).</summary>
    public class MpSongRef
    {
        public string songId;
        public string difficulty;
        public string title;
        public string artist;
        public string mapper;
        public int? maudicaId;

        public override string ToString()
        {
            return $"{title ?? songId} [{difficulty}] (songId={songId}, maudicaId={(maudicaId.HasValue ? maudicaId.Value.ToString() : "null")})";
        }
    }

    /// <summary>PlayerState.browsingSong (19.3).</summary>
    public class MpBrowsingSong
    {
        public string songId;
        public string title;
    }

    /// <summary>PlayerState.songStatus (19.3). Always about the lobby's current selectedSong.</summary>
    public class MpSongStatus
    {
        /// <summary>"have" | "missing" | "downloading" | "unavailable".</summary>
        public string status;
        public float? progress;
        public string[] availableDifficulties;
    }

    /// <summary>PlayerState (19.3).</summary>
    public class MpPlayerState
    {
        public string userId;
        public string nickname;
        public bool guest;
        public bool bot;
        public bool isHost;
        /// <summary>False while inside the 30 s reconnect grace.</summary>
        public bool connected;
        public string location;
        public MpBrowsingSong browsingSong;
        /// <summary>"ex" | "classic".</summary>
        public string scoringMode;
        public MpSongStatus songStatus;
        /// <summary>Effective difficulty for the selected song.</summary>
        public string difficulty;
        public bool ready;
        public MpSongRef songChoice;
        /// <summary>"player" | "spectator" | null (only set during loading/playing).</summary>
        public string matchRole;
    }

    /// <summary>LobbyState (19.3). Always the full state; replace, never merge.</summary>
    public class MpLobbyState
    {
        public string lobbyId;
        public string code;
        public string name;
        /// <summary>"public" | "private".</summary>
        public string visibility;
        public int maxPlayers;
        public bool allowPlayerDifficulty;
        public string hostUserId;
        /// <summary>"lobby" | "loading" | "playing".</summary>
        public string phase;
        public MpSongRef selectedSong;
        public string matchId;
        /// <summary>Join order.</summary>
        public MpPlayerState[] players;

        public MpPlayerState FindPlayer(string userId)
        {
            if (players == null || userId == null) return null;
            foreach (MpPlayerState player in players)
            {
                if (player != null && player.userId == userId) return player;
            }
            return null;
        }
    }

    /// <summary>LobbySummary.selectedSong (19.2): a reduced SongRef.</summary>
    public class MpLobbySummarySong
    {
        public string songId;
        public string title;
        public string difficulty;
    }

    /// <summary>LobbySummary (19.2).</summary>
    public class MpLobbySummary
    {
        public string lobbyId;
        public string code;
        public string name;
        public string hostNickname;
        public int playerCount;
        public int maxPlayers;
        public string phase;
        public MpLobbySummarySong selectedSong;
    }

    /// <summary>welcome (19.1.1).</summary>
    public class MpWelcome
    {
        public int protocolVersion;
        public string userId;
        public string nickname;
        public bool guest;
        public bool bot;
        /// <summary>Seat secret. Memory only, never logged.</summary>
        public string resumeToken;
        public long serverTime;
        /// <summary>Non-null when the player was still seated inside the reconnect grace.</summary>
        public MpLobbyState lobby;
    }

    /// <summary>matchPrepare.participants entry (19.5).</summary>
    public class MpMatchParticipant
    {
        public string userId;
        public string difficulty;
    }

    /// <summary>matchPrepare (19.5 step 1).</summary>
    public class MpMatchPrepare
    {
        public string matchId;
        public MpSongRef song;
        public MpMatchParticipant[] participants;
        public int loadTimeoutMs;
    }

    /// <summary>participantDropped (19.5 step 2).</summary>
    public class MpParticipantDropped
    {
        public string matchId;
        public string userId;
        public string reason;
    }

    /// <summary>matchStart (19.5 step 3). startAt is SERVER ms; use MpSession.ServerToLocalMs.</summary>
    public class MpMatchStart
    {
        public string matchId;
        public long startAt;
        public MpSongRef song;
    }

    /// <summary>matchScores.scores entry (19.5 step 5).</summary>
    public class MpScoreEntry
    {
        public string userId;
        public string nickname;
        public string difficulty;
        /// <summary>"playing" | "finished" | "quit" | "disconnected".</summary>
        public string status;
        public long? tick;
        public float? exPercent;
        public long? judgementScore;
        public long? classicScore;
        public int? combo;
        public int? misses;
        public float? health;
        public bool? failed;
    }

    /// <summary>matchScores (19.5 step 5).</summary>
    public class MpMatchScores
    {
        public string matchId;
        public long serverTime;
        public MpScoreEntry[] scores;
    }

    /// <summary>matchResults.results entry (19.5 step 7).</summary>
    public class MpMatchResultEntry
    {
        public string userId;
        public string nickname;
        public string difficulty;
        public string scoringMode;
        public string status;
        public bool failed;
        public float? exPercent;
        public long? judgementScore;
        public long? classicScore;
        public int? misses;
        public int? maxCombo;
        public bool fullCombo;
        public bool guest;
        public bool bot;
        public string clientRunId;
        /// <summary>Null for failed, quit and disconnected players.</summary>
        public int? exRank;
        public int? classicRank;
    }

    /// <summary>matchResults (19.5 step 7).</summary>
    public class MpMatchResults
    {
        public string matchId;
        public MpSongRef song;
        public MpMatchResultEntry[] results;
    }

    /// <summary>
    /// error (19.7), or a client-side failure: code "TIMEOUT" (no reply within the request
    /// timeout) or "DISCONNECTED" (the socket closed with the request still pending).
    /// </summary>
    public class MpError
    {
        public int? reqId;
        public string code;
        public string message;
        /// <summary>Only on NOT_ALL_READY: who isn't ready.</summary>
        public string[] userIds;

        public override string ToString()
        {
            string who = userIds != null && userIds.Length > 0 ? $" userIds=[{string.Join(", ", userIds)}]" : "";
            return $"{code}: {message}{who}";
        }
    }

    /// <summary>
    /// The live values a participant reports during a song (19.5 step 4). Filled by the gameplay
    /// hooks (next phase) and handed to MpSession.SendScoreUpdate, which throttles it.
    /// </summary>
    public class MpScoreSnapshot
    {
        public long tick;
        public float exPercent;
        public long judgementScore;
        public long classicScore;
        public int combo;
        public int misses;
        public float health;
        public bool failed;
    }

    /// <summary>The final values sent with "finished" (19.5 step 6).</summary>
    public class MpFinishedReport
    {
        public bool failed;
        public float exPercent;
        public long judgementScore;
        public long classicScore;
        public int misses;
        public int maxCombo;
        public bool fullCombo;
        /// <summary>The Section 3 clientRunId the mod uploads with, or null when it won't upload.</summary>
        public string clientRunId;
    }

    /// <summary>Where the online-play connection stands.</summary>
    public enum MpConnectionState
    {
        /// <summary>No socket. Either never connected, turned off, or given up.</summary>
        Disconnected,
        /// <summary>Opening the socket (first connect or a reconnect attempt).</summary>
        Connecting,
        /// <summary>Socket open, hello sent, waiting for welcome.</summary>
        Handshaking,
        /// <summary>Welcome received; requests can be sent.</summary>
        Connected,
        /// <summary>Dropped unexpectedly; waiting before the next reconnect attempt.</summary>
        Reconnecting
    }

    /// <summary>Why the connection ended, passed with MpSession.OnDisconnected.</summary>
    public class MpDisconnectInfo
    {
        /// <summary>WebSocket close code, or null when the socket failed without one.</summary>
        public int? closeCode;
        /// <summary>Plain-language reason, safe to show to the player.</summary>
        public string reason;
        /// <summary>True when the client will try to reconnect on its own.</summary>
        public bool willReconnect;
    }

    internal static class MpJson
    {
        public static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            MissingMemberHandling = MissingMemberHandling.Ignore,
            NullValueHandling = NullValueHandling.Include
        };

        public static readonly JsonSerializer Serializer = JsonSerializer.Create(Settings);
    }
}