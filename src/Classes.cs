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
            public Vector3 intersectionPoint;
            public float health;
            public float velocity;
            public float sustainPercent;
            public float aimAssist;
            public bool isChainTail = false;
            public float chainAverage;
            public bool miss = false;
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
            public long unixTimestamp;
            public ExCueSaveData[] exCues;
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

            // Standard / Vertical / Horizontal / ChainStart / Hold
            public float? timingMs;
            public Vector3Data intersectionPoint;
            public Vector3Data contactPos;
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