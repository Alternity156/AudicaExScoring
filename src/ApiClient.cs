using System;
using System.Collections;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using MelonLoader;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        // See ApiContract.md — kept as a plain constant for now since there's no UI/config
        // to edit it; update here if the server ever moves.
        private const string ApiBaseUrl = "https://audicaexscoringweb-production.up.railway.app";

        /// <summary>
        /// Submits the current run to the online leaderboard, if enabled and configured.
        /// Builds the same slim per-cue shape used by local run data saving (BuildExCueSaveData,
        /// in RunDataSaveHandler.cs), but as RunSubmitData (no scoringCalculation, has clientRunId)
        /// per ApiContract.md. Fire-and-forget for now: no retry on failure, errors are only logged.
        /// Only completed (non-failed) runs are submitted for now.
        /// </summary>
        public static void SubmitRun(bool failed)
        {
            if (!Config.EnableScoreUpload) return;
            if (failed) return;
            if (string.IsNullOrEmpty(Config.ApiKey)) return;
            if (exCues.Count == 0) return;
            if (selectedSongData == null) return;

            try
            {
                ExCueSaveData[] slimCues = exCues
                    .Where(c => c.behavior != Target.TargetBehavior.Dodge)
                    .Select(BuildExCueSaveData)
                    .ToArray();

                RunSubmitData submitData = new RunSubmitData
                {
                    clientRunId = currentRunId,
                    songId = selectedSongData.songID,
                    songTitle = selectedSongData.title,
                    songArtist = selectedSongData.artist,
                    songMapper = selectedSongData.author,
                    difficulty = KataConfig.I.GetDifficulty().ToString(),
                    unixTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    failed = failed,
                    pauseCount = pauseCount,
                    exCues = slimCues
                };

                string json = JsonConvert.SerializeObject(submitData, runDataSerializerSettings);
                byte[] gzipBody = GzipCompress(json);

                MelonLogger.Log($"[ExScoring] Submitting run {submitData.clientRunId} ({submitData.songId}/{submitData.difficulty})");
                MelonCoroutines.Start(SubmitRunCoroutine(gzipBody));
            }
            catch (Exception ex)
            {
                MelonLogger.Log($"[ExScoring] Failed to prepare run submission: {ex}");
            }
        }

        private static byte[] GzipCompress(string text)
        {
            byte[] rawBytes = Encoding.UTF8.GetBytes(text);
            using (MemoryStream output = new MemoryStream())
            {
                using (GZipStream gzipStream = new GZipStream(output, CompressionMode.Compress, true))
                {
                    gzipStream.Write(rawBytes, 0, rawBytes.Length);
                }
                return output.ToArray();
            }
        }

        /// <summary>
        /// POSTs the gzipped payload directly via UnityWebRequest. WWW (used elsewhere in this mod,
        /// e.g. SongDownloader.cs) only exposes a url-only constructor in this game's Il2Cpp interop
        /// (no way to set custom headers or a raw body), but WWW itself wraps UnityWebRequest
        /// internally, confirming the type is present here — so we use it directly instead.
        /// </summary>
        private static IEnumerator SubmitRunCoroutine(byte[] gzipBody)
        {
            string url = ApiBaseUrl + "/api/runs";

            UnityWebRequest request = new UnityWebRequest(url, "POST");
            try
            {
                request.uploadHandler = new UploadHandlerRaw(gzipBody);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Content-Encoding", "gzip");
                request.SetRequestHeader("Authorization", "ApiKey " + Config.ApiKey);

                yield return request.SendWebRequest();

                if (request.isNetworkError || request.isHttpError)
                {
                    string errorBody = request.downloadHandler != null ? request.downloadHandler.text : "";
                    MelonLogger.Log($"[ExScoring] Run submission failed ({request.responseCode}): {request.error} | Body: {errorBody}");
                    yield break;
                }

                try
                {
                    RunSubmitResponse response = JsonConvert.DeserializeObject<RunSubmitResponse>(request.downloadHandler.text);
                    MelonLogger.Log($"[ExScoring] Run submitted: runId={response.runId}, rank={response.rank}, " +
                        $"grade={response.grade?.text}, judgementPercent={response.judgementPercent:N2}, personalBest={response.isPersonalBest}");
                }
                catch (Exception ex)
                {
                    MelonLogger.Log($"[ExScoring] Run submitted but failed to parse response ({request.downloadHandler.text}): {ex}");
                }
            }
            finally
            {
                request.Dispose();
            }
        }
    }
}