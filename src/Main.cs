using System;
using System.Collections.Generic;
using MelonLoader;
using static ExScoringMod.ExScoring;
using static SongCues;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        public static MenuState.State menuState;
        public static bool gameHasLoaded = false;
        public static string selectedSong;
        public static SongList.SongData selectedSongData;

        public static float chainAimExDivision = 10;

        public static List<int> processedCuesIndexes = new List<int>();
        public static List<ExCue> exCues = new List<ExCue>();

        public static UInt32 nextMaxScore = 0;
        public static UInt32 nextMaxScoreSub = 0;

        public static bool nextPopupIsScore = false;
        public static string nextPopupText = "";
        public static float lastExScore = 0;

        public static float maxPossibleExScore;
        public static float exScore = 0;
        public static float currentMaxPossibleExScore;

        public static class BuildInfo
        {
            public const string Name = "ExScoring";  // Name of the Mod.  (MUST BE SET)
            public const string Author = "Alternity"; // Author of the Mod.  (Set as null if none)
            public const string Company = null; // Company that made the Mod.  (Set as null if none)
            public const string Version = "0.1.0"; // Version of the Mod.  (MUST BE SET)
            public const string DownloadLink = null; // Download Link for the Mod.  (Set as null if none)
        }

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
            public float velocity;
            public float sustainPercent;
            public float aimAssist;
            public bool miss = false;
        }

        public static string GetPopupText(ExCue exCue)
        {
            string text = "";

            switch(exCue.behavior)
            {
                case Target.TargetBehavior.Standard:
                case Target.TargetBehavior.Horizontal:
                case Target.TargetBehavior.Vertical:
                case Target.TargetBehavior.ChainStart:
                case Target.TargetBehavior.Hold:
                    text = $"T: {GetPercentFromRaw(exCue.timing)}%\nA: {GetPercentFromRaw(exCue.aim)}%";
                    break;
                case Target.TargetBehavior.Chain:
                    text = $"A: {GetPercentFromRaw(exCue.aim)}%";
                    break;
                case Target.TargetBehavior.Melee:
                    text = $"V: {GetPercentFromRaw(exCue.velocity)}%";
                    break;
            }

            if (exCue.behavior == Target.TargetBehavior.Hold)
            {
                text += $"\nS: {GetPercentFromRaw(exCue.sustainPercent)}%";
            }

            return text;
        }
    }
}



