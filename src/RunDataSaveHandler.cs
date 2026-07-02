using System;
using System.IO;
using System.Linq;
using MelonLoader;
using Newtonsoft.Json;
using UnityEngine;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        private static readonly string runDataDirectory = Application.dataPath.Replace("Audica_Data", "ExScoringRunData");

        /// <summary>
        /// Saves the current run's raw ExCue data to its own JSON file.
        /// Safe to call multiple times per run; only writes once (see runSaved).
        /// </summary>
        public static void SaveRunData()
        {
            if (runSaved) return;
            if (exCues.Count == 0) return;
            if (selectedSongData == null) return;

            runSaved = true;

            try
            {
                if (!Directory.Exists(runDataDirectory))
                {
                    Directory.CreateDirectory(runDataDirectory);
                }

                ScoreSaveData saveData = new ScoreSaveData
                {
                    songId = selectedSongData.songID,
                    songTitle = selectedSongData.title,
                    songArtist = selectedSongData.artist,
                    songMapper = selectedSongData.author,
                    difficulty = KataConfig.I.GetDifficulty().ToString(),
                    scoringCalculation = Config.LinearCalculation ? "Linear" : "Audica",
                    unixTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    exCues = exCues.ToArray()
                };

                string fileName = SanitizeFileName($"{saveData.songId}_{saveData.difficulty}_{saveData.unixTimestamp}") + ".json";
                string filePath = Path.Combine(runDataDirectory, fileName);

                string json = JsonConvert.SerializeObject(saveData, Formatting.Indented);
                File.WriteAllText(filePath, json);

                MelonLogger.Log($"[ExScoring] Saved run data to {filePath}");
            }
            catch (Exception ex)
            {
                MelonLogger.Log($"[ExScoring] Failed to save run data: {ex}");
            }
        }

        private static string SanitizeFileName(string name)
        {
            char[] invalidChars = Path.GetInvalidFileNameChars();
            return new string(name.Where(c => !invalidChars.Contains(c)).ToArray());
        }
    }
}