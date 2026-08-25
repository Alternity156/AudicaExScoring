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

        /// <summary>
        /// Fetches the AudicaEx online leaderboard for a song+difficulty (GET /api/leaderboard —
        /// see ApiContract.md section 4). `view` is "top" (public, default paging) or "self"
        /// (requires an API key; server ignores offset and returns a window centered on the
        /// requester's rank). An `Authorization` header is sent whenever an API key is configured,
        /// regardless of view, since the contract says a valid key also unlocks `requesterRank` on
        /// the Top view. Always calls onComplete exactly once: with the parsed response on success,
        /// or null on any failure (network error, HTTP error, an unparsable body, or the view=self/
        /// missing-key case below). Every failure path is logged first, so a null result can always
        /// be traced back to a specific cause via MelonLogger/UnityExplorer.
        /// </summary>
        public static void FetchLeaderboard(string songId, string difficulty, int limit, string view, Action<LeaderboardApiResponse> onComplete)
        {
            if (string.IsNullOrEmpty(songId))
            {
                MelonLogger.Log("[ExScoring] FetchLeaderboard: songId is null/empty, aborting.");
                onComplete?.Invoke(null);
                return;
            }

            if (view == "self" && string.IsNullOrEmpty(Config.ApiKey))
            {
                // Shouldn't be reachable in practice — the Self button is gated on having a key
                // (see UpdateSelfButtonAvailability in ExLeaderboardDisplay.cs) — but guard here too
                // rather than firing a request the server will just reject.
                MelonLogger.Log("[ExScoring] FetchLeaderboard: view=self requested but no API key is set, aborting.");
                onComplete?.Invoke(null);
                return;
            }

            MelonLogger.Log($"[ExScoring] FetchLeaderboard: starting songId={songId} difficulty={difficulty} limit={limit} view={view}");
            MelonCoroutines.Start(FetchLeaderboardCoroutine(songId, difficulty, limit, view, onComplete));
        }

        private static IEnumerator FetchLeaderboardCoroutine(string songId, string difficulty, int limit, string view, Action<LeaderboardApiResponse> onComplete)
        {
            string url = $"{ApiBaseUrl}/api/leaderboard?songId={Uri.EscapeDataString(songId)}&difficulty={Uri.EscapeDataString(difficulty)}&limit={limit}&offset=0&view={Uri.EscapeDataString(view)}";

            MelonLogger.Log($"[ExScoring] FetchLeaderboard: GET {url}");

            UnityWebRequest request = UnityWebRequest.Get(url);
            try
            {
                if (!string.IsNullOrEmpty(Config.ApiKey))
                {
                    request.SetRequestHeader("Authorization", "ApiKey " + Config.ApiKey);
                }

                yield return request.SendWebRequest();

                if (request.isNetworkError || request.isHttpError)
                {
                    string errorBody = request.downloadHandler != null ? request.downloadHandler.text : "";
                    MelonLogger.Log($"[ExScoring] FetchLeaderboard failed ({request.responseCode}): {request.error} | songId={songId} difficulty={difficulty} | Body: {errorBody}");
                    onComplete?.Invoke(null);
                    yield break;
                }

                try
                {
                    LeaderboardApiResponse response = JsonConvert.DeserializeObject<LeaderboardApiResponse>(request.downloadHandler.text);
                    int entryCount = response?.entries?.Length ?? 0;
                    MelonLogger.Log($"[ExScoring] FetchLeaderboard succeeded: songId={songId} difficulty={difficulty} entries={entryCount} total={response?.total ?? -1}");
                    onComplete?.Invoke(response);
                }
                catch (Exception ex)
                {
                    MelonLogger.Log($"[ExScoring] FetchLeaderboard: failed to parse response ({request.downloadHandler.text}): {ex}");
                    onComplete?.Invoke(null);
                }
            }
            finally
            {
                request.Dispose();
            }
        }

        /// <summary>
        /// Fetches the AudicaEx Total leaderboard for a song-list+difficulty (GET /api/leaderboard/total
        /// — see ApiContract.md section 4c). Mirrors FetchLeaderboard above (same view/auth/staleness
        /// conventions), scoped to `listId` instead of a single `songId`. `view` is "top" (public,
        /// default paging) or "self" (requires an API key, server ignores offset). Always calls
        /// onComplete exactly once: with the parsed response on success, or null on any failure.
        /// </summary>
        public static void FetchTotalLeaderboard(string listId, string difficulty, int limit, string view, Action<TotalLeaderboardApiResponse> onComplete)
        {
            if (string.IsNullOrEmpty(listId))
            {
                MelonLogger.Log("[ExScoring] FetchTotalLeaderboard: listId is null/empty, aborting.");
                onComplete?.Invoke(null);
                return;
            }

            if (view == "self" && string.IsNullOrEmpty(Config.ApiKey))
            {
                MelonLogger.Log("[ExScoring] FetchTotalLeaderboard: view=self requested but no API key is set, aborting.");
                onComplete?.Invoke(null);
                return;
            }

            MelonLogger.Log($"[ExScoring] FetchTotalLeaderboard: starting listId={listId} difficulty={difficulty} limit={limit} view={view}");
            MelonCoroutines.Start(FetchTotalLeaderboardCoroutine(listId, difficulty, limit, view, onComplete));
        }

        private static IEnumerator FetchTotalLeaderboardCoroutine(string listId, string difficulty, int limit, string view, Action<TotalLeaderboardApiResponse> onComplete)
        {
            string url = $"{ApiBaseUrl}/api/leaderboard/total?listId={Uri.EscapeDataString(listId)}&difficulty={Uri.EscapeDataString(difficulty)}&limit={limit}&offset=0&view={Uri.EscapeDataString(view)}";

            MelonLogger.Log($"[ExScoring] FetchTotalLeaderboard: GET {url}");

            UnityWebRequest request = UnityWebRequest.Get(url);
            try
            {
                if (!string.IsNullOrEmpty(Config.ApiKey))
                {
                    request.SetRequestHeader("Authorization", "ApiKey " + Config.ApiKey);
                }

                yield return request.SendWebRequest();

                if (request.isNetworkError || request.isHttpError)
                {
                    string errorBody = request.downloadHandler != null ? request.downloadHandler.text : "";
                    MelonLogger.Log($"[ExScoring] FetchTotalLeaderboard failed ({request.responseCode}): {request.error} | listId={listId} difficulty={difficulty} | Body: {errorBody}");
                    onComplete?.Invoke(null);
                    yield break;
                }

                try
                {
                    TotalLeaderboardApiResponse response = JsonConvert.DeserializeObject<TotalLeaderboardApiResponse>(request.downloadHandler.text);
                    int entryCount = response?.entries?.Length ?? 0;
                    MelonLogger.Log($"[ExScoring] FetchTotalLeaderboard succeeded: listId={listId} difficulty={difficulty} entries={entryCount} total={response?.total ?? -1}");
                    onComplete?.Invoke(response);
                }
                catch (Exception ex)
                {
                    MelonLogger.Log($"[ExScoring] FetchTotalLeaderboard: failed to parse response ({request.downloadHandler.text}): {ex}");
                    onComplete?.Invoke(null);
                }
            }
            finally
            {
                request.Dispose();
            }
        }
    }
}