using Il2CppSystem;
using MelonLoader;
using System.Collections.Generic;
using UnityEngine;

namespace ExScoringMod
{
    public class DifficultyCalculator
    {
        public string songID;

        public static Dictionary<string, CachedCalculation> calculatorCache = new Dictionary<string, CachedCalculation>();

        public CalculatedDifficulty expert;
        public CalculatedDifficulty advanced;
        public CalculatedDifficulty standard;
        public CalculatedDifficulty beginner;

        public DifficultyCalculator(SongList.SongData songData)
        {
            if (songData == null) return;
            this.songID = songData.songID;
            EvaluateDifficulties(songData);
        }

        public struct CachedCalculation
        {
            public float value;
            public bool is360;
            public bool hasMines;

            public CachedCalculation(float valuef = 0, bool is360b = false, bool hasMinesb = false)
            {
                value = valuef;
                is360 = is360b;
                hasMines = hasMinesb;
            }
        }

        public static CachedCalculation GetRating(string songID, KataConfig.Difficulty difficulty)
        {
            var songData = SongList.I.GetSong(songID);
            string cacheKey = songID + difficulty.ToString();

            if (calculatorCache.ContainsKey(cacheKey)) return calculatorCache[cacheKey];

            if (songData == null)
            {
                calculatorCache.Add(cacheKey, new CachedCalculation());
                return new CachedCalculation();
            }

            var calc = new DifficultyCalculator(songData);

            CalculatedDifficulty result = null;
            switch (difficulty)
            {
                case KataConfig.Difficulty.Easy: result = calc.beginner; break;
                case KataConfig.Difficulty.Normal: result = calc.standard; break;
                case KataConfig.Difficulty.Hard: result = calc.advanced; break;
                case KataConfig.Difficulty.Expert: result = calc.expert; break;
            }

            CachedCalculation data = result != null
                ? new CachedCalculation(result.difficultyRating, result.is360, result.hasMines)
                : new CachedCalculation();

            calculatorCache.Add(cacheKey, data);
            return data;
        }

        public float GetRatingFromKataDifficulty(KataConfig.Difficulty difficulty)
        {
            switch (difficulty)
            {
                case KataConfig.Difficulty.Easy: return beginner != null ? beginner.difficultyRating : 0f;
                case KataConfig.Difficulty.Normal: return standard != null ? standard.difficultyRating : 0f;
                case KataConfig.Difficulty.Hard: return advanced != null ? advanced.difficultyRating : 0f;
                case KataConfig.Difficulty.Expert: return expert != null ? expert.difficultyRating : 0f;
                default: return 0f;
            }
        }

        private void EvaluateDifficulties(SongList.SongData songData)
        {
            var expertCues = SongCues.GetCues(songData, KataConfig.Difficulty.Expert);
            if (expertCues != null && expertCues.Length > 0)
                this.expert = new CalculatedDifficulty(expertCues, songData);

            var advancedCues = SongCues.GetCues(songData, KataConfig.Difficulty.Hard);
            if (advancedCues != null && advancedCues.Length > 0)
                this.advanced = new CalculatedDifficulty(advancedCues, songData);

            var standardCues = SongCues.GetCues(songData, KataConfig.Difficulty.Normal);
            if (standardCues != null && standardCues.Length > 0)
                this.standard = new CalculatedDifficulty(standardCues, songData);

            var beginnerCues = SongCues.GetCues(songData, KataConfig.Difficulty.Easy);
            if (beginnerCues != null && beginnerCues.Length > 0)
                this.beginner = new CalculatedDifficulty(beginnerCues, songData);
        }
    }

    public class CalculatedDifficulty
    {
        public static float spacingMultiplier = 1f;
        public static float lengthMultiplier = 0.7f;
        public static float densityMultiplier = 1f;
        public static float readabilityMultiplier = 1.2f;

        public float difficultyRating;
        public float spacing;
        public float density;
        public float readability;

        public (float lowest, float highest) cueExtremesX = (0, 0);
        public (float lowest, float highest) cueExtremesY = (0, 0);
        public bool is360 = false;
        public bool hasMines = false;

        float length;

        public static Dictionary<Target.TargetBehavior, float> objectDifficultyModifier = new Dictionary<Target.TargetBehavior, float>()
        {
            { Target.TargetBehavior.Standard,   1f  },
            { Target.TargetBehavior.Vertical,   1.2f },
            { Target.TargetBehavior.Horizontal, 1.3f },
            { Target.TargetBehavior.Hold,       1f  },
            { Target.TargetBehavior.ChainStart, 1.2f },
            { Target.TargetBehavior.Chain,      0.2f },
            { Target.TargetBehavior.Melee,      0.6f }
        };

        List<SongCues.Cue> leftHandCues = new List<SongCues.Cue>();
        List<SongCues.Cue> rightHandCues = new List<SongCues.Cue>();
        List<SongCues.Cue> eitherHandCues = new List<SongCues.Cue>();
        List<SongCues.Cue> allCues = new List<SongCues.Cue>();

        public CalculatedDifficulty(SongCues.Cue[] cues, SongList.SongData songData)
        {
            EvaluateCues(cues, songData);
            is360 = (Math.Abs(cueExtremesX.highest - cueExtremesX.lowest) >= 20);
        }

        public void EvaluateCues(SongCues.Cue[] cues, SongList.SongData songData)
        {
            this.length = AudioDriver.TickSpanToMs(songData, cues[0].tick, cues[cues.Length - 1].tick);
            if (cues.Length >= 15 && this.length > 30000f)
            {
                SplitCues(cues);
                CalculateSpacing();
                CalculateDensity();
                CalculateReadability();
                difficultyRating = ((spacing + readability) / length) * 500f + (length / 100000f * lengthMultiplier);
            }
            else
            {
                difficultyRating = 0f;
            }
        }

        void CalculateReadability()
        {
            cueExtremesX.lowest = GetTrueCoordinates(allCues[0]).x;
            cueExtremesX.highest = GetTrueCoordinates(allCues[0]).x;
            cueExtremesY.lowest = GetTrueCoordinates(allCues[0]).y;
            cueExtremesY.highest = GetTrueCoordinates(allCues[0]).y;

            for (int i = 0; i < allCues.Count; i++)
            {
                float modifierValue = 0f;
                objectDifficultyModifier.TryGetValue(allCues[i].behavior, out modifierValue);
                readability += modifierValue * readabilityMultiplier;

                if (allCues[i].behavior == Target.TargetBehavior.Dodge)
                    hasMines = true;

                float truCoord = GetTrueCoordinates(allCues[i]).x;
                if (truCoord < cueExtremesX.lowest) cueExtremesX.lowest = truCoord;
                if (truCoord > cueExtremesX.highest) cueExtremesX.highest = truCoord;

                truCoord = GetTrueCoordinates(allCues[i]).y;
                if (truCoord < cueExtremesY.lowest) cueExtremesY.lowest = truCoord;
                if (truCoord > cueExtremesY.highest) cueExtremesY.highest = truCoord;
            }
        }

        void CalculateSpacing()
        {
            GetSpacingPerHand(leftHandCues);
            GetSpacingPerHand(rightHandCues);
        }

        void CalculateDensity()
        {
            density = (float)allCues.Count / length;
        }

        private void GetSpacingPerHand(List<SongCues.Cue> cues)
        {
            for (int i = 1; i < cues.Count; i++)
            {
                float dist = Vector2.Distance(GetTrueCoordinates(cues[i]), GetTrueCoordinates(cues[i - 1]));
                float distMultiplied = cues[i].behavior == Target.TargetBehavior.Melee
                    ? float.Epsilon
                    : dist * spacingMultiplier;
                spacing += distMultiplied;
            }
        }

        Vector2 GetTrueCoordinates(SongCues.Cue cue)
        {
            float x = cue.pitch % 12;
            float y = (int)(cue.pitch / 12);
            x += cue.gridOffset.x;
            y += cue.gridOffset.y;
            return new Vector2(x, y);
        }

        void SplitCues(SongCues.Cue[] cues)
        {
            for (int i = 0; i < cues.Length; i++)
            {
                allCues.Add(cues[i]);
                switch (cues[i].handType)
                {
                    case Target.TargetHandType.Left: leftHandCues.Add(cues[i]); break;
                    case Target.TargetHandType.Right: rightHandCues.Add(cues[i]); break;
                    case Target.TargetHandType.Either: eitherHandCues.Add(cues[i]); break;
                }
            }
        }
    }

    public static class DiffCalculatorUtil
    {
        /// <summary>
        /// Exports difficulty ratings for all songs to difficultyCalculatorOutput.txt
        /// in the game's root directory.
        /// </summary>
        public static void ExportDifficultyCalculation()
        {
            using (System.IO.StreamWriter file =
                new System.IO.StreamWriter(Application.dataPath + "/../difficultyCalculatorOutput.txt"))
            {
                file.WriteLine("Song Name, Difficulty, Difficulty Rating, BPM, Author");
                for (int i = 0; i < SongList.I.songs.Count; i++)
                {
                    var songData = SongList.I.songs[i];
                    var calc = new DifficultyCalculator(songData);
                    string title = songData.title ?? "";
                    string artist = songData.artist ?? "";
                    string author = songData.author ?? "";
                    string bpm = songData.tempos[0].tempo.ToString("n2");
                    string name = $"{artist} - {title}".Replace(",", "");

                    if (calc.expert != null) file.WriteLine($"{name},Expert,{calc.expert.difficultyRating.ToString("n2")},{bpm},{author}");
                    if (calc.advanced != null) file.WriteLine($"{name},Advanced,{calc.advanced.difficultyRating.ToString("n2")},{bpm},{author}");
                    if (calc.standard != null) file.WriteLine($"{name},Standard,{calc.standard.difficultyRating.ToString("n2")},{bpm},{author}");
                    if (calc.beginner != null) file.WriteLine($"{name},Beginner,{calc.beginner.difficultyRating.ToString("n2")},{bpm},{author}");
                }
            }

            MelonLogger.Log("Difficulty export complete: difficultyCalculatorOutput.txt");
        }

        /// <summary>
        /// Logs the difficulty ratings for the currently selected song to the MelonLoader console.
        /// </summary>
        public static void LogCurrentSongDifficulty()
        {
            var calc = new DifficultyCalculator(SongDataHolder.I.songData);
            MelonLogger.Log($"\n{calc.songID}");
            if (calc.expert != null) MelonLogger.Log($"Expert: {calc.expert.difficultyRating}");
            if (calc.advanced != null) MelonLogger.Log($"Advanced: {calc.advanced.difficultyRating}");
            if (calc.standard != null) MelonLogger.Log($"Standard: {calc.standard.difficultyRating}");
            if (calc.beginner != null) MelonLogger.Log($"Beginner: {calc.beginner.difficultyRating}");
        }
    }
}