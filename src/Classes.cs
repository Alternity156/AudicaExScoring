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
            public float velocity;
            public float sustainPercent;
            public float aimAssist;
            public bool miss = false;
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
            public ExCue[] exCues;
            public string songHash;
            public string songId;
            public long unixTimestamp;
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
    }
}
