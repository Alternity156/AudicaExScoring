using System.Collections;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        private static object watchCoroutine;

        public static MenuState.State menuState;
        public static bool gameHasLoaded = false;
        public static string selectedSong;
        public static SongList.SongData selectedSongData;

        public static List<int> processedCuesIndexes = new List<int>();
        public static List<ExCue> exCues = new List<ExCue>();
        public static List<UnprocessedTargetHitPos> unprocessedTargetHitPoses = new List<UnprocessedTargetHitPos>();

        private static Dictionary<int, List<(float aimScore, Vector3 intersectionPoint)>> pendingAimResults = new Dictionary<int, List<(float, Vector3)>>();

        public static bool nextPopupIsScore = false;
        public static string nextPopupText = "";

        public static float maxPossibleExScore;
        public static float exScore = 0;
        public static float judgementScore = 0;
        public static float currentMaxPossibleExScore;
        public static float currentMaxPossibleJudgementScore;

        public static class BuildInfo
        {
            public const string Name = "ExScoring";  // Name of the Mod.  (MUST BE SET)
            public const string Author = "Alternity"; // Author of the Mod.  (Set as null if none)
            public const string Company = null; // Company that made the Mod.  (Set as null if none)
            public const string Version = "0.1.0"; // Version of the Mod.  (MUST BE SET)
            public const string DownloadLink = null; // Download Link for the Mod.  (Set as null if none)
        }

        public override void OnApplicationStart()
        {
            Config.RegisterConfig();
        }

        public override void OnModSettingsApplied()
        {
            Config.OnModSettingsApplied();
        }

        public static void StartWatching()
        {
            if (watchCoroutine == null)
                watchCoroutine = MelonCoroutines.Start(WatchPrefs());
        }

        public static void StopWatching()
        {
            if (watchCoroutine != null)
            {
                MelonCoroutines.Stop(watchCoroutine);
                watchCoroutine = null;
            }
        }

        private static IEnumerator WatchPrefs()
        {
            while (true)
            {
                yield return new WaitForSeconds(0.1f);

                bool audicaType = MelonPrefs.GetBool(Config.Category, nameof(Config.AudicaType));
                bool exType = MelonPrefs.GetBool(Config.Category, nameof(Config.ExType));

                if (audicaType && exType)
                {
                    if (!Config.AudicaType)
                        MelonPrefs.SetBool(Config.Category, nameof(Config.ExType), false);
                    else
                        MelonPrefs.SetBool(Config.Category, nameof(Config.AudicaType), false);
                }
                else if (!audicaType && !exType)
                {
                    if (Config.AudicaType)
                        MelonPrefs.SetBool(Config.Category, nameof(Config.ExType), true);
                    else
                        MelonPrefs.SetBool(Config.Category, nameof(Config.AudicaType), true);
                }

                bool audicaCalc = MelonPrefs.GetBool(Config.Category, nameof(Config.AudicaCalculation));
                bool linearCalc = MelonPrefs.GetBool(Config.Category, nameof(Config.LinearCalculation));

                if (audicaCalc && linearCalc)
                {
                    if (!Config.AudicaCalculation)
                        MelonPrefs.SetBool(Config.Category, nameof(Config.LinearCalculation), false);
                    else
                        MelonPrefs.SetBool(Config.Category, nameof(Config.AudicaCalculation), false);
                }
                else if (!audicaCalc && !linearCalc)
                {
                    if (Config.AudicaCalculation)
                        MelonPrefs.SetBool(Config.Category, nameof(Config.LinearCalculation), true);
                    else
                        MelonPrefs.SetBool(Config.Category, nameof(Config.AudicaCalculation), true);
                }

                Config.AudicaType = audicaType;
                Config.ExType = exType;
                Config.AudicaCalculation = audicaCalc;
                Config.LinearCalculation = linearCalc;
            }
        }
    }
}



