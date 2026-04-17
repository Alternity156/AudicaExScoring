using System.Collections.Generic;
using MelonLoader;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        public static MenuState.State menuState;
        public static bool gameHasLoaded = false;
        public static string selectedSong;
        public static SongList.SongData selectedSongData;

        public static float perfectTimingSlopMs = 7;
        public static float chainAimExDivision = 10;

        public static List<int> processedCuesIndexes = new List<int>();
        public static List<ExCue> exCues = new List<ExCue>();
        public static List<UnprocessedTargetHitPos> unprocessedTargetHitPoses = new List<UnprocessedTargetHitPos>();

        public static bool nextPopupIsScore = false;
        public static string nextPopupText = "";

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
    }
}



