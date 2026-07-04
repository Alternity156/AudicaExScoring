using System;
using System.Collections.Generic;
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

        private static readonly JsonSerializerSettings runDataSerializerSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.None,
            NullValueHandling = NullValueHandling.Ignore
        };

        // Behaviors that carry a meaningful spatial aim point (raw intersection/contact + aimAssist).
        private static readonly HashSet<Target.TargetBehavior> aimBehaviors = new HashSet<Target.TargetBehavior>
        {
            Target.TargetBehavior.Standard,
            Target.TargetBehavior.Vertical,
            Target.TargetBehavior.Horizontal,
            Target.TargetBehavior.ChainStart,
            Target.TargetBehavior.Hold
        };

        /// <summary>
        /// Saves the current run's raw ExCue data to its own JSON file.
        /// Safe to call multiple times per run; only writes once (see runSaved).
        /// </summary>
        public static void SaveRunData()
        {
            if (!Config.EnableRunDataSaving) return;
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

                ExCueSaveData[] slimCues = exCues
                    .Where(c => c.behavior != Target.TargetBehavior.Dodge)
                    .Select(BuildExCueSaveData)
                    .ToArray();

                ScoreSaveData saveData = new ScoreSaveData
                {
                    songId = selectedSongData.songID,
                    songTitle = selectedSongData.title,
                    songArtist = selectedSongData.artist,
                    songMapper = selectedSongData.author,
                    difficulty = KataConfig.I.GetDifficulty().ToString(),
                    scoringCalculation = Config.LinearCalculation ? "Linear" : "Audica",
                    unixTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    exCues = slimCues
                };

                string fileName = SanitizeFileName($"{saveData.songId}_{saveData.difficulty}_{saveData.unixTimestamp}") + ".json";
                string filePath = Path.Combine(runDataDirectory, fileName);

                string json = JsonConvert.SerializeObject(saveData, runDataSerializerSettings);
                File.WriteAllText(filePath, json);

                MelonLogger.Log($"[ExScoring] Saved run data to {filePath}");

                EnforceRunDataLimits(saveData.songId, saveData.difficulty);
            }
            catch (Exception ex)
            {
                MelonLogger.Log($"[ExScoring] Failed to save run data: {ex}");
            }
        }

        private struct ParsedRunFileName
        {
            public string songId;
            public string difficulty;
            public string timestamp;
        }

        /// <summary>
        /// Splits a run data filename (without extension) into songId/difficulty/timestamp.
        /// Parses from the right since songId may itself contain underscores - only the last
        /// two tokens (difficulty, timestamp) have a fixed, known shape.
        /// </summary>
        private static ParsedRunFileName ParseRunFileName(string fileName)
        {
            string nameNoExt = Path.GetFileNameWithoutExtension(fileName);
            string[] parts = nameNoExt.Split('_');
            if (parts.Length < 3) return new ParsedRunFileName();

            string timestamp = parts[parts.Length - 1];
            string difficulty = parts[parts.Length - 2];
            string songId = string.Join("_", parts.Take(parts.Length - 2));
            return new ParsedRunFileName { songId = songId, difficulty = difficulty, timestamp = timestamp };
        }

        /// <summary>
        /// Trims old run data files: first down to Config.MaxRunsPerSong for this
        /// song+difficulty, then (across all songs) down to Config.MaxRunDataSizeMB total.
        /// Oldest files (by last write time) are deleted first in both passes.
        /// </summary>
        private static void EnforceRunDataLimits(string songId, string difficulty)
        {
            try
            {
                string sanitizedSongId = SanitizeFileName(songId);
                string sanitizedDifficulty = SanitizeFileName(difficulty);

                List<FileInfo> songFiles = Directory.GetFiles(runDataDirectory, "*.json")
                    .Select(f => new FileInfo(f))
                    .Where(f =>
                    {
                        var parsed = ParseRunFileName(f.Name);
                        return parsed.songId == sanitizedSongId && parsed.difficulty == sanitizedDifficulty;
                    })
                    .OrderBy(f => f.LastWriteTimeUtc)
                    .ToList();

                int maxRuns = Config.MaxRunsPerSong;
                while (songFiles.Count > maxRuns)
                {
                    DeleteRunFile(songFiles[0]);
                    songFiles.RemoveAt(0);
                }

                long maxBytes = (long)(Config.MaxRunDataSizeMB * 1024 * 1024);
                List<FileInfo> allFiles = Directory.GetFiles(runDataDirectory, "*.json")
                    .Select(f => new FileInfo(f))
                    .OrderBy(f => f.LastWriteTimeUtc)
                    .ToList();

                long totalBytes = allFiles.Sum(f => f.Length);
                int i = 0;
                while (totalBytes > maxBytes && i < allFiles.Count)
                {
                    totalBytes -= allFiles[i].Length;
                    DeleteRunFile(allFiles[i]);
                    i++;
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Log($"[ExScoring] Failed to enforce run data limits: {ex}");
            }
        }

        private static void DeleteRunFile(FileInfo file)
        {
            try
            {
                file.Delete();
                MelonLogger.Log($"[ExScoring] Deleted old run data file: {file.Name}");
            }
            catch (Exception ex)
            {
                MelonLogger.Log($"[ExScoring] Failed to delete run data file {file.Name}: {ex}");
            }
        }

        /// <summary>
        /// Maps a live ExCue to the slim on-disk record, including only the fields
        /// relevant to its behavior. Assumes Dodge cues have already been filtered out.
        /// </summary>
        private static ExCueSaveData BuildExCueSaveData(ExCue cue)
        {
            ExCueSaveData data = new ExCueSaveData
            {
                behavior = cue.behavior,
                handType = cue.handType,
                tick = cue.tick,
                health = cue.health,
                miss = cue.miss
            };

            if (cue.miss)
            {
                return data;
            }

            if (aimBehaviors.Contains(cue.behavior))
            {
                data.timingMs = cue.timingMs;
                data.intersectionPoint = new Vector3Data(cue.intersectionPoint);
                data.contactPos = new Vector3Data(cue.contactPos);
                data.aimAssist = cue.aimAssist;
            }

            if (cue.behavior == Target.TargetBehavior.Hold)
            {
                data.sustainPercent = cue.sustainPercent;
            }

            if (cue.behavior == Target.TargetBehavior.Chain)
            {
                data.aim = cue.aim;
                data.isChainTail = cue.isChainTail;
                data.chainAverage = cue.chainAverage;
            }

            if (cue.behavior == Target.TargetBehavior.Melee)
            {
                data.velocity = cue.velocity;
            }

            return data;
        }

        private static string SanitizeFileName(string name)
        {
            char[] invalidChars = Path.GetInvalidFileNameChars();
            return new string(name.Where(c => !invalidChars.Contains(c)).ToArray());
        }
    }
}