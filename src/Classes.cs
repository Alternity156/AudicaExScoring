using MelonLoader;
using UnityEngine;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        public class ExCue
        {
            public Target.TargetBehavior behavior;
            public Target.TargetHandType handType;
            public int index;
            public float tick;
            public float successTick;
            public float timing;
            public float timingMs;
            public float aim;
            public TargetHitPos targetHitPos;
            public Vector3 contactPos;
            public Quaternion contactRotation = Quaternion.identity;
            public Vector3 intersectionPoint;
            public float health;
            public float velocity;
            public float sustainPercent;
            public float aimAssist;
            public bool isChainTail = false;
            public float chainAverage;
            public bool miss = false;
            public bool hasMissAimData = false;
            public ScoringCalculation scoringCalculation;
            public Judgement timingJudgement;
            public Judgement aimJudgement;
            public Judgement chainJudgement;

            public enum ScoringCalculation
            {
                Audica,
                Linear
            }
        }

        public class SongInfo
        {
            public string songId;
            public string songHash;
            public string songTitle;
            public string songArtist;
            public string songMapper;
            public float songLowBpm;
            public float songHighBpm;
        }

        public class ScoreSaveData
        {
            public string songId;
            public string songTitle;
            public string songArtist;
            public string songMapper;
            public string difficulty;
            public string scoringCalculation;
            public string platform;
            public long unixTimestamp;
            public bool failed;
            public int pauseCount;
            public ExCueSaveData[] exCues;
        }

        /// <summary>
        /// Payload for POST /api/runs (see ApiContract.md). Deliberately separate from
        /// ScoreSaveData: no scoringCalculation (server always scores Linear), and it
        /// carries a clientRunId for server-side dedupe on retry.
        /// </summary>
        public class RunSubmitData
        {
            public string clientRunId;
            public string songId;
            public string songTitle;
            public string songArtist;
            public string songMapper;
            public string difficulty;
            public long unixTimestamp;
            public bool failed;
            public int pauseCount;
            public string platform;
            public ExCueSaveData[] exCues;
        }

        /// <summary>
        /// Response shape for POST /api/runs (see ApiContract.md).
        /// </summary>
        public class RunSubmitGrade
        {
            public string id;
            public string text;
        }

        public class RunSubmitResponse
        {
            public string runId;
            public float judgementScore;
            public float judgementPercent;
            public RunSubmitGrade grade;
            public int rank;
            public bool isPersonalBest;
            public bool mapDataNeeded;
        }

        public class LeaderboardApiEntry
        {
            public int rank;

            /// <summary>
            /// Server-assigned UUID of this entry's best run (ApiContract.md v2.10, Section 4).
            /// Lets a leaderboard row be traced to its full per-cue detail via GET /api/runs/:runId
            /// (Section 6) — see LeaderboardStatsButton.cs.
            /// </summary>
            public string runId;

            public string nickname;
            public float judgementPercent;
            public RunSubmitGrade grade;
            public long unixTimestamp;
            public bool fullCombo;
            public bool isRequester;

            /// <summary>
            /// Platform the entry's best run was played on (ApiContract.md v2.9, Section 4): one of
            /// "Steam"/"Oculus"/"Viveport"/"Playstation"/"Unknown". Since v2.24, when the best run's own
            /// platform is "Unknown" (submitted before the mod sent this field), the server returns
            /// the player's current platform instead, so this only reads "Unknown" when the player has
            /// no run with a known platform at all. See ApplyPlatformIcon in ExLeaderboardDisplay.cs
            /// for how this maps to a row icon.
            /// </summary>
            public string platform;
        }

        public class LeaderboardApiResponse
        {
            public string songId;
            public string difficulty;
            public LeaderboardApiEntry[] entries;
            public int total;
            public int? requesterRank;
        }

        /// <summary>
        /// Entry shape for GET /api/leaderboard/total (ApiContract.md section 4c, v2.23) — the
        /// main-menu Total leaderboard, scoped per song-list per difficulty. No grade/fullCombo/runId
        /// concept here (it aggregates many songs, not a single run), unlike LeaderboardApiEntry.
        /// platform is per player rather than per run (see the field below).
        ///
        /// averagePercent is the ranking value: the player's best judgementPercent averaged over
        /// every song in the list, unplayed songs counting as 0%. Nullable so a server that doesn't
        /// send it yet reads as "not available" instead of a real 0%. totalScore is deprecated by
        /// the API (v2.23) and no longer displayed — kept only so the shape still matches.
        /// </summary>
        public class TotalLeaderboardApiEntry
        {
            public int rank;
            public string nickname;
            public float? averagePercent;
            public float totalScore;
            public int songsPlayed;
            public bool isRequester;

            /// <summary>
            /// The player's current platform (ApiContract.md v2.24, Sections 4/4c): the platform of
            /// their most recent run with a known platform, across every song and difficulty — not
            /// tied to any run in this list. Same "Steam"/"Oculus"/"Viveport"/"Playstation"/"Unknown"
            /// values as LeaderboardApiEntry.platform. null when talking to a server older than
            /// v2.24, which ApplyPlatformIcon treats like Unknown (icon hidden).
            /// </summary>
            public string platform;

            /// <summary>
            /// The entry's player UUID (ApiContract.md v2.30, Section 4c). Used to open a Section 15
            /// comparison from this row's compare button (ExScoreComparison.cs). null when talking to
            /// a server older than v2.30 — the compare button then stays hidden.
            /// </summary>
            public string userId;
        }

        public class TotalLeaderboardApiResponse
        {
            public string listId;
            public string listName;
            public string difficulty;
            public TotalLeaderboardApiEntry[] entries;
            public int total;
            public int songsEligible;
            public int? requesterRank;
        }

        public class UnprocessedTargetHitPos
        {
            public int index;
            public Vector2 targetHitPos;
        }

        public class TargetHitPos
        {
            public float x;
            public float y;
        }

        /// <summary>
        /// Plain, non-Unity Vector3 for clean JSON serialization.
        /// </summary>
        public class Vector3Data
        {
            public float x;
            public float y;
            public float z;

            public Vector3Data() { }

            public Vector3Data(Vector3 v)
            {
                x = v.x;
                y = v.y;
                z = v.z;
            }
        }

        /// <summary>
        /// Plain, non-Unity Vector2 for clean JSON serialization (map cue gridOffset).
        /// </summary>
        public class Vector2Data
        {
            public float x;
            public float y;

            public Vector2Data() { }

            public Vector2Data(Vector2 v)
            {
                x = v.x;
                y = v.y;
            }
        }

        /// <summary>
        /// Plain, non-Unity Quaternion for clean JSON serialization.
        /// </summary>
        public class QuaternionData
        {
            public float x;
            public float y;
            public float z;
            public float w;

            public QuaternionData() { }

            public QuaternionData(Quaternion q)
            {
                x = q.x;
                y = q.y;
                z = q.z;
                w = q.w;
            }

            public Quaternion ToQuaternion() => new Quaternion(x, y, z, w);
        }

        /// <summary>
        /// Per-cue payload for POST/GET /api/songs/:songId/map (see ApiContract.md Section 9).
        /// Static chart-authoring fields only, mirroring the native Cue class minus its
        /// runtime-mutated fields (spawned, success, passed, target, goodForm, aim, sustainPercent,
        /// meleeVelocityAmount, successTick, wouldHaveMissedWithoutTemporalAssist, chainHitPoint) —
        /// none of those describe the map itself, only a specific play session against it.
        /// </summary>
        public class MapCueData
        {
            public int index;
            public int tick;
            public int tickLength;
            public int pitch;
            public int velocity;
            public Vector2Data gridOffset;
            public float zOffset;
            public string handType;
            public string behavior;
            public int overdriveSectionIndex;
            public float tickLookahead;
            public float slopBeforeTicks;
            public float slopAfterTicks;
            public bool finaleSequenceFinalNote;
            public bool isChainTail;
        }

        /// <summary>
        /// Request body for POST /api/songs/:songId/map.
        /// </summary>
        public class MapUploadRequest
        {
            public MapCueData[] cues;
        }

        /// <summary>
        /// Response shape for POST /api/songs/:songId/map.
        /// </summary>
        public class MapUploadResponse
        {
            public bool stored;
        }

        /// <summary>
        /// Response shape for GET /api/runs/:runId (ApiContract.md Section 6) — full detail for one
        /// run, including its decompressed exCues (same shape as Section 3's request body, i.e.
        /// ExCueSaveData[]). Public endpoint, no auth. Used by the leaderboard stats panel
        /// (LeaderboardStatsButton.cs) to pull up a specific leaderboard row's run — see
        /// RunDataRecalculator.RecalculateFromApiResponse for how this becomes a RecalculatedRun.
        /// </summary>
        public class RunDetailApiResponse
        {
            public string runId;
            public string userId;
            public string nickname;
            public string songId;
            public string songTitle;
            public string songArtist;
            public string songMapper;
            public string difficulty;
            public long unixTimestamp;
            public float judgementScore;
            public float maxJudgementScore;
            public float judgementPercent;
            public int missCount;
            public bool fullCombo;
            public bool failed;
            public int pauseCount;
            public string platform;
            public RunSubmitGrade grade;
            public ExCueSaveData[] exCues;

            /// <summary>
            /// The run owner's current gun colors (ApiContract.md Sections 6 / 14), or null if their
            /// game has never sent any. Used to color the leaderboard stats graphs as that player.
            /// </summary>
            public GunColorsData gunColors;
        }

        /// <summary>
        /// Slim per-target record written to disk. Non-universal fields are nullable
        /// and only populated when relevant to the cue's behavior (see BuildExCueSaveData),
        /// so JsonSerializerSettings.NullValueHandling.Ignore drops them from the output.
        /// Dodge cues are excluded entirely and never produce one of these.
        /// </summary>
        public class ExCueSaveData
        {
            // Universal - always present
            public Target.TargetBehavior behavior;
            public Target.TargetHandType handType;
            public float tick;
            public float health;
            public bool miss;
            public bool? hasMissAimData;

            // Standard / Vertical / Horizontal / ChainStart / Hold
            public float? timingMs;
            public Vector3Data intersectionPoint;
            public Vector3Data contactPos;
            public QuaternionData contactRotation;
            public float? aimAssist;

            // Hold only
            public float? sustainPercent;

            // Chain only
            public float? aim;
            public bool? isChainTail;
            public float? chainAverage;

            // Melee only
            public float? velocity;
        }

        /// <summary>
        /// Response shape for GET /api/users/me (ApiContract.md Section 7.1). Requires auth
        /// (Authorization: ApiKey) — lets the mod show the player's own nickname/friendCode without them
        /// needing to check the website. Currently only `nickname` is consumed (AudicaExStatus.cs, for the
        /// release-notes panel's connection line); `friendCode` is fetched now for a possible future
        /// in-game profile panel.
        /// </summary>
        public class ProfileApiResponse
        {
            public string userId;
            public string nickname;
            public string friendCode;

            /// <summary>
            /// The gun colors the server currently has stored for this player (ApiContract.md Section 14),
            /// or null if the mod has never sent any. Compared against the local colors by
            /// UserSettingsSync.SyncFromProfile to decide whether a PATCH is needed.
            /// </summary>
            public GunColorsData gunColors;
        }

        /// <summary>
        /// Left/right gun colors as "#RRGGBB" strings (no alpha) — ApiContract.md Section 14. The server
        /// lowercases what it stores, so compare case-insensitively.
        /// </summary>
        public class GunColorsData
        {
            public string left;
            public string right;
        }

        /// <summary>
        /// Request body and response shape for PATCH /api/users/me/settings (ApiContract.md Section 14.1).
        /// gunColors is the only setting so far; add further per-player settings here as new nullable
        /// fields — null fields are omitted from the request (see ApiClient.UpdateUserSettings), so each
        /// call only writes the keys it actually sets.
        /// </summary>
        public class UserSettingsData
        {
            public GunColorsData gunColors;
        }

        /// <summary>
        /// One side (me/them) of GET /api/users/me/compare/:userId (ApiContract.md Section 15).
        /// isWatched (v2.30) is only sent on `them`: true if that player is already on the requester's
        /// friends/watch list. Nullable so a pre-v2.30 server reads as "unknown" — treated as false
        /// (Add Friend shown; adding an existing friend is idempotent anyway).
        /// </summary>
        public class CompareUser
        {
            public string userId;
            public string nickname;
            public bool? isWatched;
        }

        /// <summary>
        /// A player's best run on one compare row (Section 15) — null on the row when that player has
        /// no run on that song+difficulty.
        /// </summary>
        public class CompareRunRef
        {
            public string runId;
            public float judgementPercent;
            public RunSubmitGrade grade;
        }

        /// <summary>
        /// One (songId, difficulty) row of a Section 15 comparison. difference is
        /// mine.judgementPercent - theirs.judgementPercent in percentage points (rounded to 2
        /// decimals by the server), null unless both sides exist.
        /// </summary>
        public class CompareRow
        {
            public string songId;
            public string songTitle;
            public string songArtist;
            public string difficulty;
            public CompareRunRef mine;
            public CompareRunRef theirs;
            public float? difference;
        }

        /// <summary>
        /// Response shape for GET /api/users/me/compare/:userId?listId=&amp;difficulty= (ApiContract.md
        /// Section 15, filters added v2.30). Rows come already ordered by the server (requester-only,
        /// ahead, ties, behind, other-only) and are shown in that order — see ExScoreComparison.cs.
        /// </summary>
        public class CompareApiResponse
        {
            public CompareUser me;
            public CompareUser them;
            public CompareRow[] rows;
        }

        /// <summary>
        /// Request body for POST /api/friends in its { userId } form (ApiContract.md Section 7.3,
        /// v2.25). Used by the compare page's Add Friend button.
        /// </summary>
        public class FriendAddRequest
        {
            public string userId;
        }

        /// <summary>
        /// Response shape for POST /api/friends (Section 7.3). Adding someone already on the list is
        /// idempotent and returns the existing entry, so a 200 always means "they're on the list now".
        /// </summary>
        public class FriendAddResponse
        {
            public string userId;
            public string nickname;
            public long addedAt;
        }

        /// <summary>
        /// One run from GET /api/users/me/runs (ApiContract.md Section 5, default non-detail
        /// fields). Only the fields the song list / grade folders / top-score rows need are read —
        /// Json.NET ignores the rest of the server's shape.
        /// </summary>
        public class MyRunsApiEntry
        {
            public string runId;
            public string songId;
            public string difficulty;
            public long unixTimestamp;
            public float judgementPercent;
            public bool failed;
        }

        /// <summary>
        /// Response shape for GET /api/users/me/runs with no filters (Section 5): the player's whole
        /// uploaded history, most recent first. Used once per launch to merge leaderboard-only best
        /// scores into the song list (see OnlineBestScores in SongListHighScoreUI.cs).
        /// </summary>
        public class MyRunsApiResponse
        {
            public string songId;
            public string difficulty;
            public MyRunsApiEntry[] runs;
        }
    }
}