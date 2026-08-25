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
        }

        public class LeaderboardApiEntry
        {
            public int rank;
            public string nickname;
            public float judgementPercent;
            public RunSubmitGrade grade;
            public long unixTimestamp;
            public bool fullCombo;
            public bool isRequester;
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
        /// Entry shape for GET /api/leaderboard/total (ApiContract.md section 4c) — the main-menu
        /// Total leaderboard, scoped per song-list per difficulty. No grade/fullCombo concept here
        /// (totalScore is a sum across many songs, not a single run), unlike LeaderboardApiEntry.
        /// </summary>
        public class TotalLeaderboardApiEntry
        {
            public int rank;
            public string nickname;
            public float totalScore;
            public int songsPlayed;
            public bool isRequester;
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
    }
}