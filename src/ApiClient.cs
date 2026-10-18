using System;
using System.Collections;
using System.Collections.Generic;
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
                    platform = GetPlatformString(),
                    exCues = slimCues
                };

                string json = JsonConvert.SerializeObject(submitData, runDataSerializerSettings);
                byte[] gzipBody = GzipCompress(json);

                DebugLog.Log($"[ExScoring] Submitting run {submitData.clientRunId} ({submitData.songId}/{submitData.difficulty})");
                SetApiSubmitStatus(ApiSubmitStatus.Sending, "Sending score...", Color.white);
                MelonCoroutines.Start(SubmitRunCoroutine(submitData.songId, submitData.difficulty, gzipBody));
            }
            catch (Exception ex)
            {
                DebugLog.Log($"[ExScoring] Failed to prepare run submission: {ex}");
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
        private static IEnumerator SubmitRunCoroutine(string songId, string difficulty, byte[] gzipBody)
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
                    DebugLog.Log($"[ExScoring] Run submission failed ({request.responseCode}): {request.error} | Body: {errorBody}");
                    SetApiSubmitStatus(ApiSubmitStatus.Failed, "Send Failed", Color.red);
                    yield break;
                }

                try
                {
                    RunSubmitResponse response = JsonConvert.DeserializeObject<RunSubmitResponse>(request.downloadHandler.text);
                    DebugLog.Log($"[ExScoring] Run submitted: runId={response.runId}, rank={response.rank}, " +
                        $"grade={response.grade?.text}, judgementPercent={response.judgementPercent:N2}, personalBest={response.isPersonalBest}, " +
                        $"mapDataNeeded={response.mapDataNeeded}");

                    string statusText = response.isPersonalBest
                        ? $"Personal Best!\nRank #{response.rank}"
                        : $"Rank #{response.rank}";
                    Color statusColor = response.isPersonalBest ? Color.green : Color.white;
                    SetApiSubmitStatus(ApiSubmitStatus.Success, statusText, statusColor);

                    if (response.mapDataNeeded)
                    {
                        UploadMapDataIfNeeded(songId, difficulty);
                    }
                }
                catch (Exception ex)
                {
                    DebugLog.Log($"[ExScoring] Run submitted but failed to parse response ({request.downloadHandler.text}): {ex}");
                    SetApiSubmitStatus(ApiSubmitStatus.Failed, "Send Failed", Color.red);
                }
            }
            finally
            {
                request.Dispose();
            }
        }

        /// <summary>
        /// Fetches the AudicaEx online leaderboard for a song+difficulty (GET /api/leaderboard —
        /// see ApiContract.md section 4). `view` is "top" (public, default paging), "self"
        /// (requires an API key; server ignores offset and returns a window centered on the
        /// requester's rank) or "friends" (requires an API key; server ignores limit/offset and
        /// returns the requester plus every friend with a run, re-ranked within the group). An
        /// `Authorization` header is sent whenever an API key is configured, regardless of view,
        /// since the contract says a valid key also unlocks `requesterRank` on the Top view. Always
        /// calls onComplete exactly once: with the parsed response on success, or null on any
        /// failure (network error, HTTP error, an unparsable body, or the view=self/view=friends
        /// missing-key case below). Every failure path is logged first, so a null result can always
        /// be traced back to a specific cause via MelonLogger/UnityExplorer.
        /// </summary>
        public static void FetchLeaderboard(string songId, string difficulty, int limit, string view, Action<LeaderboardApiResponse> onComplete)
        {
            if (string.IsNullOrEmpty(songId))
            {
                DebugLog.Log("[ExScoring] FetchLeaderboard: songId is null/empty, aborting.");
                onComplete?.Invoke(null);
                return;
            }

            if ((view == "self" || view == "friends") && string.IsNullOrEmpty(Config.ApiKey))
            {
                // Shouldn't be reachable in practice — the Friends button is only shown with a key
                // and GetExLeaderboardView falls back to "top" without one (ExLeaderboardDisplay.cs)
                // — but guard here too rather than firing a request the server will just reject.
                DebugLog.Log($"[ExScoring] FetchLeaderboard: view={view} requested but no API key is set, aborting.");
                onComplete?.Invoke(null);
                return;
            }

            DebugLog.Log($"[ExScoring] FetchLeaderboard: starting songId={songId} difficulty={difficulty} limit={limit} view={view}");
            MelonCoroutines.Start(FetchLeaderboardCoroutine(songId, difficulty, limit, view, onComplete));
        }

        private static IEnumerator FetchLeaderboardCoroutine(string songId, string difficulty, int limit, string view, Action<LeaderboardApiResponse> onComplete)
        {
            string url = $"{ApiBaseUrl}/api/leaderboard?songId={Uri.EscapeDataString(songId)}&difficulty={Uri.EscapeDataString(difficulty)}&limit={limit}&offset=0&view={Uri.EscapeDataString(view)}";

            DebugLog.Log($"[ExScoring] FetchLeaderboard: GET {url}");

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
                    DebugLog.Log($"[ExScoring] FetchLeaderboard failed ({request.responseCode}): {request.error} | songId={songId} difficulty={difficulty} | Body: {errorBody}");
                    onComplete?.Invoke(null);
                    yield break;
                }

                try
                {
                    LeaderboardApiResponse response = JsonConvert.DeserializeObject<LeaderboardApiResponse>(request.downloadHandler.text);
                    int entryCount = response?.entries?.Length ?? 0;
                    DebugLog.Log($"[ExScoring] FetchLeaderboard succeeded: songId={songId} difficulty={difficulty} entries={entryCount} total={response?.total ?? -1}");
                    onComplete?.Invoke(response);
                }
                catch (Exception ex)
                {
                    DebugLog.Log($"[ExScoring] FetchLeaderboard: failed to parse response ({request.downloadHandler.text}): {ex}");
                    onComplete?.Invoke(null);
                }
            }
            finally
            {
                request.Dispose();
            }
        }

        // Session cache for GET /api/song-lists (ApiContract.md section 4b.1), sorted by sortOrder.
        // null until the first successful fetch; a failed fetch leaves it null so the next request
        // tries again. Lists change rarely (admin-only edits on the website), so once per launch is
        // enough — a restart picks up any change.
        private static List<SongListApiEntry> cachedSongLists;
        private static bool songListsFetchInFlight;
        private static readonly List<Action<List<SongListApiEntry>>> songListsWaiters = new List<Action<List<SongListApiEntry>>>();

        /// <summary>
        /// Gets every server-managed song list (GET /api/song-lists — ApiContract.md section 4b.1),
        /// sorted by sortOrder then name. Public endpoint, no auth. Served from the session cache
        /// after the first success; concurrent callers during a fetch all share that one request.
        /// Always calls onComplete exactly once: with the list (possibly empty) on success, or null
        /// on failure.
        /// </summary>
        public static void FetchSongLists(Action<List<SongListApiEntry>> onComplete)
        {
            if (cachedSongLists != null)
            {
                onComplete?.Invoke(cachedSongLists);
                return;
            }

            if (onComplete != null) songListsWaiters.Add(onComplete);
            if (songListsFetchInFlight) return;

            songListsFetchInFlight = true;
            DebugLog.Log("[ExScoring] FetchSongLists: starting");
            MelonCoroutines.Start(FetchSongListsCoroutine());
        }

        private static IEnumerator FetchSongListsCoroutine()
        {
            string url = $"{ApiBaseUrl}/api/song-lists";
            DebugLog.Log($"[ExScoring] FetchSongLists: GET {url}");

            List<SongListApiEntry> result = null;
            UnityWebRequest request = UnityWebRequest.Get(url);
            try
            {
                yield return request.SendWebRequest();

                if (request.isNetworkError || request.isHttpError)
                {
                    string errorBody = request.downloadHandler != null ? request.downloadHandler.text : "";
                    DebugLog.Log($"[ExScoring] FetchSongLists failed ({request.responseCode}): {request.error} | Body: {errorBody}");
                }
                else
                {
                    try
                    {
                        SongListsApiResponse response = JsonConvert.DeserializeObject<SongListsApiResponse>(request.downloadHandler.text);
                        result = (response?.lists ?? new SongListApiEntry[0])
                            .Where(l => l != null && !string.IsNullOrEmpty(l.listId))
                            .OrderBy(l => l.sortOrder)
                            .ThenBy(l => l.name ?? l.listId, StringComparer.OrdinalIgnoreCase)
                            .ToList();
                        cachedSongLists = result;
                        DebugLog.Log($"[ExScoring] FetchSongLists succeeded: {result.Count} list(s) [{string.Join(", ", result.Select(l => l.listId))}]");
                    }
                    catch (Exception ex)
                    {
                        DebugLog.Log($"[ExScoring] FetchSongLists: failed to parse response ({request.downloadHandler.text}): {ex}");
                        result = null;
                    }
                }
            }
            finally
            {
                request.Dispose();
                songListsFetchInFlight = false;
            }

            var waiters = songListsWaiters.ToArray();
            songListsWaiters.Clear();
            foreach (var waiter in waiters)
            {
                try { waiter(result); }
                catch (Exception ex) { DebugLog.Log($"[ExScoring] FetchSongLists: callback threw: {ex}"); }
            }
        }

        /// <summary>
        /// Fetches the AudicaEx Total leaderboard for a song-list+difficulty (GET /api/leaderboard/total
        /// — see ApiContract.md section 4c). Mirrors FetchLeaderboard above (same view/auth/staleness
        /// conventions), scoped to `listId` instead of a single `songId`. `view` is "top" (public,
        /// default paging), "self" (requires an API key, server ignores offset) or "friends"
        /// (requires an API key, server returns the whole friend group ranked by averagePercent).
        /// Always calls onComplete exactly once: with the parsed response on success, or null on any
        /// failure.
        /// </summary>
        public static void FetchTotalLeaderboard(string listId, string difficulty, int limit, string view, Action<TotalLeaderboardApiResponse> onComplete)
        {
            if (string.IsNullOrEmpty(listId))
            {
                DebugLog.Log("[ExScoring] FetchTotalLeaderboard: listId is null/empty, aborting.");
                onComplete?.Invoke(null);
                return;
            }

            if ((view == "self" || view == "friends") && string.IsNullOrEmpty(Config.ApiKey))
            {
                DebugLog.Log($"[ExScoring] FetchTotalLeaderboard: view={view} requested but no API key is set, aborting.");
                onComplete?.Invoke(null);
                return;
            }

            DebugLog.Log($"[ExScoring] FetchTotalLeaderboard: starting listId={listId} difficulty={difficulty} limit={limit} view={view}");
            MelonCoroutines.Start(FetchTotalLeaderboardCoroutine(listId, difficulty, limit, view, onComplete));
        }

        private static IEnumerator FetchTotalLeaderboardCoroutine(string listId, string difficulty, int limit, string view, Action<TotalLeaderboardApiResponse> onComplete)
        {
            string url = $"{ApiBaseUrl}/api/leaderboard/total?listId={Uri.EscapeDataString(listId)}&difficulty={Uri.EscapeDataString(difficulty)}&limit={limit}&offset=0&view={Uri.EscapeDataString(view)}";

            DebugLog.Log($"[ExScoring] FetchTotalLeaderboard: GET {url}");

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
                    DebugLog.Log($"[ExScoring] FetchTotalLeaderboard failed ({request.responseCode}): {request.error} | listId={listId} difficulty={difficulty} | Body: {errorBody}");
                    onComplete?.Invoke(null);
                    yield break;
                }

                try
                {
                    TotalLeaderboardApiResponse response = JsonConvert.DeserializeObject<TotalLeaderboardApiResponse>(request.downloadHandler.text);
                    int entryCount = response?.entries?.Length ?? 0;
                    DebugLog.Log($"[ExScoring] FetchTotalLeaderboard succeeded: listId={listId} difficulty={difficulty} entries={entryCount} total={response?.total ?? -1}");
                    onComplete?.Invoke(response);
                }
                catch (Exception ex)
                {
                    DebugLog.Log($"[ExScoring] FetchTotalLeaderboard: failed to parse response ({request.downloadHandler.text}): {ex}");
                    onComplete?.Invoke(null);
                }
            }
            finally
            {
                request.Dispose();
            }
        }

        /// <summary>
        /// Fetches full per-run detail for a single run by ID (GET /api/runs/:runId — see
        /// ApiContract.md Section 6). Public endpoint, no auth. Backs the leaderboard stats panel:
        /// a leaderboard row carries a runId (LeaderboardApiEntry.runId, v2.10), and shooting it
        /// calls this to get that run's full exCues for the graphs. Always calls onComplete exactly
        /// once: with the parsed response on success, or null on any failure (network error, HTTP
        /// error, or an unparsable body) — every failure path is logged first.
        /// </summary>
        public static void FetchRun(string runId, Action<RunDetailApiResponse> onComplete)
        {
            if (string.IsNullOrEmpty(runId))
            {
                DebugLog.Log("[ExScoring] FetchRun: runId is null/empty, aborting.");
                onComplete?.Invoke(null);
                return;
            }

            DebugLog.Log($"[ExScoring] FetchRun: starting runId={runId}");
            MelonCoroutines.Start(FetchRunCoroutine(runId, onComplete));
        }

        private static IEnumerator FetchRunCoroutine(string runId, Action<RunDetailApiResponse> onComplete)
        {
            string url = $"{ApiBaseUrl}/api/runs/{Uri.EscapeDataString(runId)}";

            DebugLog.Log($"[ExScoring] FetchRun: GET {url}");

            UnityWebRequest request = UnityWebRequest.Get(url);
            try
            {
                yield return request.SendWebRequest();

                if (request.isNetworkError || request.isHttpError)
                {
                    string errorBody = request.downloadHandler != null ? request.downloadHandler.text : "";
                    DebugLog.Log($"[ExScoring] FetchRun failed ({request.responseCode}): {request.error} | runId={runId} | Body: {errorBody}");
                    onComplete?.Invoke(null);
                    yield break;
                }

                try
                {
                    RunDetailApiResponse response = JsonConvert.DeserializeObject<RunDetailApiResponse>(request.downloadHandler.text);
                    int cueCount = response?.exCues?.Length ?? 0;
                    DebugLog.Log($"[ExScoring] FetchRun succeeded: runId={runId} songId={response?.songId} difficulty={response?.difficulty} cues={cueCount}");
                    onComplete?.Invoke(response);
                }
                catch (Exception ex)
                {
                    DebugLog.Log($"[ExScoring] FetchRun: failed to parse response ({request.downloadHandler.text}): {ex}");
                    onComplete?.Invoke(null);
                }
            }
            finally
            {
                request.Dispose();
            }
        }

        /// <summary>
        /// Fetches the requester's whole uploaded run history (GET /api/users/me/runs with no
        /// songId/difficulty filter — see ApiContract.md Section 5). Requires an API key. Returns
        /// every non-deleted run, not just bests, without exCues (no ?detail=true). Backs the
        /// once-per-launch "leaderboard-only best scores" merge in SongListHighScoreUI.cs, so a
        /// clean install with no local run files still shows its uploaded scores. Always calls
        /// onComplete exactly once: with the parsed response on success, or null on any failure
        /// (no API key, network error, HTTP error, or an unparsable body) — every failure path is
        /// logged first.
        /// </summary>
        public static void FetchMyRuns(Action<MyRunsApiResponse> onComplete)
        {
            if (string.IsNullOrEmpty(Config.ApiKey))
            {
                DebugLog.Log("[ExScoring] FetchMyRuns: no API key is set, aborting.");
                onComplete?.Invoke(null);
                return;
            }

            DebugLog.Log("[ExScoring] FetchMyRuns: starting (full history).");
            MelonCoroutines.Start(FetchMyRunsCoroutine(onComplete));
        }

        private static IEnumerator FetchMyRunsCoroutine(Action<MyRunsApiResponse> onComplete)
        {
            string url = $"{ApiBaseUrl}/api/users/me/runs";

            DebugLog.Log($"[ExScoring] FetchMyRuns: GET {url}");

            UnityWebRequest request = UnityWebRequest.Get(url);
            try
            {
                request.SetRequestHeader("Authorization", "ApiKey " + Config.ApiKey);

                yield return request.SendWebRequest();

                if (request.isNetworkError || request.isHttpError)
                {
                    string errorBody = request.downloadHandler != null ? request.downloadHandler.text : "";
                    DebugLog.Log($"[ExScoring] FetchMyRuns failed ({request.responseCode}): {request.error} | Body: {errorBody}");
                    onComplete?.Invoke(null);
                    yield break;
                }

                string body = request.downloadHandler.text;
                string preview = body != null && body.Length > 500 ? body.Substring(0, 500) + "..." : body;

                MyRunsApiResponse response = null;
                try
                {
                    response = JsonConvert.DeserializeObject<MyRunsApiResponse>(body);
                }
                catch (Exception ex)
                {
                    DebugLog.Log($"[ExScoring] FetchMyRuns: failed to parse response ({preview}): {ex}");
                    response = null;
                }

                if (response == null)
                {
                    onComplete?.Invoke(null);
                    yield break;
                }

                if (response.runs == null)
                {
                    // Most likely the server's array isn't named "runs" — log the start of the
                    // body so the actual shape is visible without dumping a whole history.
                    DebugLog.Log($"[ExScoring] FetchMyRuns: response has no runs array ({body?.Length ?? 0} chars): {preview}");
                    onComplete?.Invoke(null);
                    yield break;
                }

                DebugLog.Log($"[ExScoring] FetchMyRuns succeeded: runs={response.runs.Length} ({body.Length} chars)");
                onComplete?.Invoke(response);
            }
            finally
            {
                request.Dispose();
            }
        }

        /// <summary>
        /// Builds the static chart-shape payload for POST /api/songs/:songId/map (see ApiContract.md
        /// Section 9) and kicks off the upload. Called off RunSubmitResponse.mapDataNeeded after a
        /// successful run submission — the server has already told us it has no map data yet for
        /// this songId+difficulty, so no separate existence check is needed first.
        ///
        /// Looks the song up fresh via SongList.I.GetSong(songId)/Enum.TryParse(difficulty) rather
        /// than trusting selectedSongData/KataConfig.I.GetDifficulty() to still be current — this
        /// runs after an async HTTP round-trip, so the player could already be on another screen by
        /// the time it fires. Same defensive pattern as RunDataRecalculator.BuildChainTailLookup.
        /// </summary>
        private static void UploadMapDataIfNeeded(string songId, string difficultyStr)
        {
            try
            {
                if (!Enum.TryParse(difficultyStr, out KataConfig.Difficulty difficulty))
                {
                    DebugLog.Log($"[ExScoring] UploadMapData: couldn't parse difficulty '{difficultyStr}', aborting.");
                    return;
                }

                var songData = SongList.I.GetSong(songId);
                if (songData == null)
                {
                    DebugLog.Log($"[ExScoring] UploadMapData: song '{songId}' not found, aborting.");
                    return;
                }

                var cues = SongCues.GetCues(songData, difficulty);

                // Same cold-call caveat as RunDataRecalculator.BuildChainTailLookup: chainNext is
                // only populated as a side effect of HookUpChains, which normally only runs during
                // gameplay setup. Without it here, every Chain/ChainStart cue's chainNext reads null.
                SongCues.HookUpChains(cues);

                List<MapCueData> mapCues = new List<MapCueData>();
                foreach (SongCues.Cue cue in cues)
                {
                    bool isChainTail = (cue.behavior == Target.TargetBehavior.Chain || cue.behavior == Target.TargetBehavior.ChainStart)
                        && cue.chainNext == null;

                    mapCues.Add(new MapCueData
                    {
                        index = cue.index,
                        tick = cue.tick,
                        tickLength = cue.tickLength,
                        pitch = cue.pitch,
                        velocity = cue.velocity,
                        gridOffset = new Vector2Data(cue.gridOffset),
                        zOffset = cue.zOffset,
                        handType = cue.handType.ToString(),
                        behavior = cue.behavior.ToString(),
                        overdriveSectionIndex = cue.overdriveSectionIndex,
                        tickLookahead = cue.tickLookahead,
                        slopBeforeTicks = cue.slopBeforeTicks,
                        slopAfterTicks = cue.slopAfterTicks,
                        finaleSequenceFinalNote = cue.finaleSequenceFinalNote,
                        isChainTail = isChainTail
                    });
                }

                MapUploadRequest uploadData = new MapUploadRequest { cues = mapCues.ToArray() };
                string json = JsonConvert.SerializeObject(uploadData, runDataSerializerSettings);
                byte[] gzipBody = GzipCompress(json);

                DebugLog.Log($"[ExScoring] Uploading map data for {songId}/{difficultyStr} ({mapCues.Count} cues)");
                MelonCoroutines.Start(UploadMapDataCoroutine(songId, difficultyStr, gzipBody));
            }
            catch (Exception ex)
            {
                DebugLog.Log($"[ExScoring] Failed to prepare map upload for {songId}/{difficultyStr}: {ex}");
            }
        }

        private static IEnumerator UploadMapDataCoroutine(string songId, string difficultyStr, byte[] gzipBody)
        {
            string url = $"{ApiBaseUrl}/api/songs/{Uri.EscapeDataString(songId)}/map?difficulty={Uri.EscapeDataString(difficultyStr)}";

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
                    DebugLog.Log($"[ExScoring] Map upload failed ({request.responseCode}) for {songId}/{difficultyStr}: {request.error} | Body: {errorBody}");
                    yield break;
                }

                try
                {
                    MapUploadResponse response = JsonConvert.DeserializeObject<MapUploadResponse>(request.downloadHandler.text);
                    DebugLog.Log($"[ExScoring] Map upload for {songId}/{difficultyStr}: stored={response.stored}");
                }
                catch (Exception ex)
                {
                    DebugLog.Log($"[ExScoring] Map upload succeeded but failed to parse response ({request.downloadHandler.text}): {ex}");
                }
            }
            finally
            {
                request.Dispose();
            }
        }

        /// <summary>
        /// Outcome of FetchProfile. Distinguishes a genuine auth failure (401 — key is missing/revoked/
        /// wrong) from a connectivity problem (no network, DNS failure, server unreachable, unexpected
        /// non-2xx, or an unparsable body) so the caller can show a different message for each — see
        /// AudicaExStatus.cs.
        /// </summary>
        public enum ProfileFetchStatus
        {
            Success,
            InvalidKey,
            ConnectionError
        }

        public class ProfileFetchResult
        {
            public ProfileFetchStatus status;

            /// <summary>Only populated when status == Success.</summary>
            public ProfileApiResponse response;
        }

        /// <summary>
        /// Fetches the requester's own profile (GET /api/users/me — see ApiContract.md Section 7.1).
        /// Requires Config.ApiKey to be set; if it's not, this reports ConnectionError without making a
        /// request — callers that need to tell "no key configured" apart from "key was rejected" should
        /// check Config.ApiKey themselves first (see AudicaExStatus.cs) rather than relying on this result.
        /// Always calls onComplete exactly once, with every failure path logged first via
        /// MelonLogger/UnityExplorer.
        /// </summary>
        public static void FetchProfile(Action<ProfileFetchResult> onComplete)
        {
            if (string.IsNullOrEmpty(Config.ApiKey))
            {
                DebugLog.Log("[ExScoring] FetchProfile: no API key set, aborting.");
                onComplete?.Invoke(new ProfileFetchResult { status = ProfileFetchStatus.ConnectionError });
                return;
            }

            DebugLog.Log("[ExScoring] FetchProfile: starting");
            MelonCoroutines.Start(FetchProfileCoroutine(onComplete));
        }

        private static IEnumerator FetchProfileCoroutine(Action<ProfileFetchResult> onComplete)
        {
            string url = $"{ApiBaseUrl}/api/users/me";

            DebugLog.Log($"[ExScoring] FetchProfile: GET {url}");

            UnityWebRequest request = UnityWebRequest.Get(url);
            try
            {
                request.SetRequestHeader("Authorization", "ApiKey " + Config.ApiKey);

                yield return request.SendWebRequest();

                if (request.isNetworkError)
                {
                    DebugLog.Log($"[ExScoring] FetchProfile failed (network error): {request.error}");
                    onComplete?.Invoke(new ProfileFetchResult { status = ProfileFetchStatus.ConnectionError });
                    yield break;
                }

                if (request.isHttpError)
                {
                    string errorBody = request.downloadHandler != null ? request.downloadHandler.text : "";
                    DebugLog.Log($"[ExScoring] FetchProfile failed ({request.responseCode}): {request.error} | Body: {errorBody}");

                    // 401 per ApiContract.md Section 2 ("Missing or invalid key -> 401 Unauthorized") is the
                    // one case we know for certain means "the key itself is bad", not "something's down".
                    ProfileFetchStatus status = request.responseCode == 401
                        ? ProfileFetchStatus.InvalidKey
                        : ProfileFetchStatus.ConnectionError;
                    onComplete?.Invoke(new ProfileFetchResult { status = status });
                    yield break;
                }

                try
                {
                    ProfileApiResponse response = JsonConvert.DeserializeObject<ProfileApiResponse>(request.downloadHandler.text);
                    DebugLog.Log($"[ExScoring] FetchProfile succeeded: nickname={response?.nickname}");
                    onComplete?.Invoke(new ProfileFetchResult { status = ProfileFetchStatus.Success, response = response });
                }
                catch (Exception ex)
                {
                    DebugLog.Log($"[ExScoring] FetchProfile: failed to parse response ({request.downloadHandler.text}): {ex}");
                    onComplete?.Invoke(new ProfileFetchResult { status = ProfileFetchStatus.ConnectionError });
                }
            }
            finally
            {
                request.Dispose();
            }
        }

        /// <summary>
        /// Writes per-player settings to the server (PATCH /api/users/me/settings — see ApiContract.md
        /// Section 14.1). Plain JSON body, not gzip. Null fields of <paramref name="settings"/> are left
        /// out of the body. Requires Config.ApiKey. Always calls onComplete exactly once: with the
        /// server's echoed settings on success, or null on any failure (every failure path is logged
        /// first). Callers decide when to call this — see UserSettingsSync.cs.
        /// </summary>
        public static void UpdateUserSettings(UserSettingsData settings, Action<UserSettingsData> onComplete)
        {
            if (string.IsNullOrEmpty(Config.ApiKey))
            {
                DebugLog.Log("[ExScoring] UpdateUserSettings: no API key set, aborting.");
                onComplete?.Invoke(null);
                return;
            }

            if (settings == null)
            {
                DebugLog.Log("[ExScoring] UpdateUserSettings: nothing to send, aborting.");
                onComplete?.Invoke(null);
                return;
            }

            try
            {
                string json = JsonConvert.SerializeObject(settings, new JsonSerializerSettings
                {
                    NullValueHandling = NullValueHandling.Ignore
                });
                DebugLog.Log($"[ExScoring] UpdateUserSettings: starting, body={json}");
                MelonCoroutines.Start(UpdateUserSettingsCoroutine(Encoding.UTF8.GetBytes(json), onComplete));
            }
            catch (Exception ex)
            {
                DebugLog.Log($"[ExScoring] UpdateUserSettings: failed to build request: {ex}");
                onComplete?.Invoke(null);
            }
        }

        private static IEnumerator UpdateUserSettingsCoroutine(byte[] body, Action<UserSettingsData> onComplete)
        {
            string url = $"{ApiBaseUrl}/api/users/me/settings";

            UnityWebRequest request = new UnityWebRequest(url, "PATCH");
            try
            {
                request.uploadHandler = new UploadHandlerRaw(body);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Authorization", "ApiKey " + Config.ApiKey);

                yield return request.SendWebRequest();

                if (request.isNetworkError || request.isHttpError)
                {
                    string errorBody = request.downloadHandler != null ? request.downloadHandler.text : "";
                    DebugLog.Log($"[ExScoring] UpdateUserSettings failed ({request.responseCode}): {request.error} | Body: {errorBody}");
                    onComplete?.Invoke(null);
                    yield break;
                }

                try
                {
                    UserSettingsData response = JsonConvert.DeserializeObject<UserSettingsData>(request.downloadHandler.text);
                    DebugLog.Log($"[ExScoring] UpdateUserSettings succeeded: {request.downloadHandler.text}");
                    onComplete?.Invoke(response);
                }
                catch (Exception ex)
                {
                    DebugLog.Log($"[ExScoring] UpdateUserSettings: failed to parse response ({request.downloadHandler.text}): {ex}");
                    onComplete?.Invoke(null);
                }
            }
            finally
            {
                request.Dispose();
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  Account linking (ApiContract.md Section 16)
        // ══════════════════════════════════════════════════════════════════
        //
        // Neither call sends Authorization: ApiKey — the mod has no key yet. Plain JSON, no gzip.
        // SECURITY: these responses carry the pollToken (start) and the player's apiKey (approved
        // poll). Never log their bodies, and never log the pollToken or apiKey themselves — only
        // status codes and the server's `error` string.

        /// <summary>Response shape for POST /api/mod/link/start (ApiContract.md Section 16.2).</summary>
        public class LinkStartResponse
        {
            public string code;
            public string pollToken;
            public int expiresIn;
            public int pollInterval;
        }

        /// <summary>Response shape for POST /api/mod/link/poll (ApiContract.md Section 16.3), both 202 and 200.</summary>
        public class LinkPollResponse
        {
            public string status;
            public int? expiresIn;
            public string apiKey;
            public string nickname;
        }

        public enum LinkStartStatus
        {
            Success,
            RateLimited,
            Failed
        }

        public class LinkStartResult
        {
            public LinkStartStatus status;

            /// <summary>Only populated when status == Success.</summary>
            public LinkStartResponse response;
        }

        public enum LinkPollStatus
        {
            /// <summary>202 — not approved yet, keep polling.</summary>
            Pending,
            /// <summary>200 — approved; apiKey and nickname are set. The request is now used up.</summary>
            Approved,
            /// <summary>410 CODE_EXPIRED.</summary>
            Expired,
            /// <summary>403 ACCOUNT_DISABLED.</summary>
            AccountDisabled,
            /// <summary>404 INVALID_POLL_TOKEN (unknown or already collected), 400 (mod-side bug), or an unparsable/unexpected reply.</summary>
            Failed,
            /// <summary>Transport failure or a 5xx — worth retrying a couple of times before giving up.</summary>
            NetworkError
        }

        public class LinkPollResult
        {
            public LinkPollStatus status;

            /// <summary>Seconds left on the code, from a 202 reply. Null otherwise.</summary>
            public int? expiresIn;

            /// <summary>Only set when status == Approved. Never log this.</summary>
            public string apiKey;

            /// <summary>Only set when status == Approved.</summary>
            public string nickname;
        }

        /// <summary>
        /// Starts an account-link request (POST /api/mod/link/start — ApiContract.md Section 16.2).
        /// Always calls onComplete exactly once. A 404 (server without Section 16 deployed yet) and
        /// any other failure come back as Failed; 429 comes back as RateLimited.
        /// </summary>
        public static void StartAccountLink(Action<LinkStartResult> onComplete)
        {
            DebugLog.Log("[ExScoring] StartAccountLink: starting");
            MelonCoroutines.Start(StartAccountLinkCoroutine(onComplete));
        }

        private static IEnumerator StartAccountLinkCoroutine(Action<LinkStartResult> onComplete)
        {
            string url = $"{ApiBaseUrl}/api/mod/link/start";

            UnityWebRequest request = new UnityWebRequest(url, "POST");
            try
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes("{}"));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");

                yield return request.SendWebRequest();

                if (request.isNetworkError)
                {
                    DebugLog.Log($"[ExScoring] StartAccountLink failed (network error): {request.error}");
                    onComplete?.Invoke(new LinkStartResult { status = LinkStartStatus.Failed });
                    yield break;
                }

                if (request.responseCode != 200)
                {
                    // Error bodies are { "error": "..." } and carry no secrets, so they're safe to log.
                    DebugLog.Log($"[ExScoring] StartAccountLink failed ({request.responseCode}): {ExtractApiError(request)}");
                    LinkStartStatus status = request.responseCode == 429 ? LinkStartStatus.RateLimited : LinkStartStatus.Failed;
                    onComplete?.Invoke(new LinkStartResult { status = status });
                    yield break;
                }

                LinkStartResponse response = null;
                try
                {
                    response = JsonConvert.DeserializeObject<LinkStartResponse>(request.downloadHandler.text);
                }
                catch (Exception ex)
                {
                    // Deliberately not logging the body: it holds the pollToken.
                    DebugLog.Log($"[ExScoring] StartAccountLink: failed to parse response: {ex.GetType().Name}");
                }

                if (response == null || string.IsNullOrEmpty(response.code) || string.IsNullOrEmpty(response.pollToken))
                {
                    DebugLog.Log("[ExScoring] StartAccountLink: response missing code or pollToken");
                    onComplete?.Invoke(new LinkStartResult { status = LinkStartStatus.Failed });
                    yield break;
                }

                DebugLog.Log($"[ExScoring] StartAccountLink succeeded: expiresIn={response.expiresIn}s, pollInterval={response.pollInterval}s");
                onComplete?.Invoke(new LinkStartResult { status = LinkStartStatus.Success, response = response });
            }
            finally
            {
                request.Dispose();
            }
        }

        /// <summary>
        /// Polls an account-link request once (POST /api/mod/link/poll — ApiContract.md Section 16.3).
        /// Always calls onComplete exactly once. Branches on responseCode, not isHttpError: 202 (pending)
        /// is a success status, so UnityWebRequest reports it as neither a network nor an HTTP error.
        /// </summary>
        public static void PollAccountLink(string pollToken, Action<LinkPollResult> onComplete)
        {
            if (string.IsNullOrEmpty(pollToken))
            {
                DebugLog.Log("[ExScoring] PollAccountLink: no poll token, aborting.");
                onComplete?.Invoke(new LinkPollResult { status = LinkPollStatus.Failed });
                return;
            }

            byte[] body;
            try
            {
                body = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new Dictionary<string, string> { { "pollToken", pollToken } }));
            }
            catch (Exception ex)
            {
                DebugLog.Log($"[ExScoring] PollAccountLink: failed to build request: {ex.GetType().Name}");
                onComplete?.Invoke(new LinkPollResult { status = LinkPollStatus.Failed });
                return;
            }

            MelonCoroutines.Start(PollAccountLinkCoroutine(body, onComplete));
        }

        private static IEnumerator PollAccountLinkCoroutine(byte[] body, Action<LinkPollResult> onComplete)
        {
            string url = $"{ApiBaseUrl}/api/mod/link/poll";

            UnityWebRequest request = new UnityWebRequest(url, "POST");
            try
            {
                request.uploadHandler = new UploadHandlerRaw(body);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");

                yield return request.SendWebRequest();

                if (request.isNetworkError)
                {
                    DebugLog.Log($"[ExScoring] PollAccountLink failed (network error): {request.error}");
                    onComplete?.Invoke(new LinkPollResult { status = LinkPollStatus.NetworkError });
                    yield break;
                }

                long code = request.responseCode;

                if (code == 202 || code == 200)
                {
                    LinkPollResponse response = null;
                    try
                    {
                        response = JsonConvert.DeserializeObject<LinkPollResponse>(request.downloadHandler.text);
                    }
                    catch (Exception ex)
                    {
                        // Deliberately not logging the body: on 200 it holds the apiKey.
                        DebugLog.Log($"[ExScoring] PollAccountLink: failed to parse {code} response: {ex.GetType().Name}");
                    }

                    if (code == 202)
                    {
                        onComplete?.Invoke(new LinkPollResult
                        {
                            status = LinkPollStatus.Pending,
                            expiresIn = response?.expiresIn
                        });
                        yield break;
                    }

                    if (response == null || string.IsNullOrEmpty(response.apiKey))
                    {
                        DebugLog.Log("[ExScoring] PollAccountLink: approved response missing apiKey");
                        onComplete?.Invoke(new LinkPollResult { status = LinkPollStatus.Failed });
                        yield break;
                    }

                    DebugLog.Log("[ExScoring] PollAccountLink: approved");
                    onComplete?.Invoke(new LinkPollResult
                    {
                        status = LinkPollStatus.Approved,
                        apiKey = response.apiKey.Trim(),
                        nickname = response.nickname ?? ""
                    });
                    yield break;
                }

                // Error bodies are { "error": "..." } and carry no secrets, so they're safe to log.
                DebugLog.Log($"[ExScoring] PollAccountLink failed ({code}): {ExtractApiError(request)}");

                LinkPollStatus status;
                if (code == 410) status = LinkPollStatus.Expired;
                else if (code == 403) status = LinkPollStatus.AccountDisabled;
                else if (code >= 500) status = LinkPollStatus.NetworkError;
                else status = LinkPollStatus.Failed; // 404 (unknown/collected token), 400 (mod bug), anything else

                onComplete?.Invoke(new LinkPollResult { status = status });
            }
            finally
            {
                request.Dispose();
            }
        }

        /// <summary>
        /// Pulls the server's `error` string out of an error response body ({"error": "..."}), for
        /// showing on screen. Falls back to the HTTP status / transport error when the body isn't
        /// that shape (empty, HTML from a proxy, etc.). Never throws.
        /// </summary>
        private static string ExtractApiError(UnityWebRequest request)
        {
            string body = request.downloadHandler != null ? request.downloadHandler.text : null;
            if (!string.IsNullOrEmpty(body))
            {
                try
                {
                    Dictionary<string, object> parsed = JsonConvert.DeserializeObject<Dictionary<string, object>>(body);
                    if (parsed != null && parsed.TryGetValue("error", out object err) && err != null)
                    {
                        string text = err.ToString();
                        if (!string.IsNullOrEmpty(text)) return text;
                    }
                }
                catch
                {
                    // Not JSON — fall through to the status-based message.
                }
            }

            if (request.isNetworkError) return string.IsNullOrEmpty(request.error) ? "Network error" : request.error;
            return $"HTTP {request.responseCode}";
        }

        /// <summary>
        /// Fetches a score comparison between the requester and another player (GET
        /// /api/users/me/compare/:userId — ApiContract.md Section 15). `listId` and `difficulty`
        /// (v2.30 filters) narrow the rows to that song-list's songs at that difficulty — the mod
        /// passes whatever its Total leaderboard was fetched with, so the comparison covers exactly
        /// that Total's songs. Either can be null/empty to leave that filter off. Requires an API key.
        /// Always calls onComplete exactly once: (response, null) on success, or (null, errorMessage)
        /// on any failure — errorMessage is the server's `error` string when it sent one, otherwise a
        /// short status description, suitable for showing on the compare page. Every failure path is
        /// logged first.
        /// </summary>
        public static void FetchCompare(string userId, string listId, string difficulty, Action<CompareApiResponse, string> onComplete)
        {
            if (string.IsNullOrEmpty(Config.ApiKey))
            {
                DebugLog.Log("[ExScoring] FetchCompare: no API key set, aborting.");
                onComplete?.Invoke(null, "No API key");
                return;
            }

            if (string.IsNullOrEmpty(userId))
            {
                DebugLog.Log("[ExScoring] FetchCompare: userId is null/empty, aborting.");
                onComplete?.Invoke(null, "Unknown player");
                return;
            }

            DebugLog.Log($"[ExScoring] FetchCompare: starting userId={userId} listId={listId} difficulty={difficulty}");
            MelonCoroutines.Start(FetchCompareCoroutine(userId, listId, difficulty, onComplete));
        }

        private static IEnumerator FetchCompareCoroutine(string userId, string listId, string difficulty, Action<CompareApiResponse, string> onComplete)
        {
            List<string> query = new List<string>();
            if (!string.IsNullOrEmpty(listId)) query.Add("listId=" + Uri.EscapeDataString(listId));
            if (!string.IsNullOrEmpty(difficulty)) query.Add("difficulty=" + Uri.EscapeDataString(difficulty));

            string url = $"{ApiBaseUrl}/api/users/me/compare/{Uri.EscapeDataString(userId)}";
            if (query.Count > 0) url += "?" + string.Join("&", query);

            DebugLog.Log($"[ExScoring] FetchCompare: GET {url}");

            UnityWebRequest request = UnityWebRequest.Get(url);
            try
            {
                request.SetRequestHeader("Authorization", "ApiKey " + Config.ApiKey);

                yield return request.SendWebRequest();

                if (request.isNetworkError || request.isHttpError)
                {
                    string errorBody = request.downloadHandler != null ? request.downloadHandler.text : "";
                    DebugLog.Log($"[ExScoring] FetchCompare failed ({request.responseCode}): {request.error} | userId={userId} listId={listId} difficulty={difficulty} | Body: {errorBody}");
                    onComplete?.Invoke(null, ExtractApiError(request));
                    yield break;
                }

                try
                {
                    CompareApiResponse response = JsonConvert.DeserializeObject<CompareApiResponse>(request.downloadHandler.text);
                    DebugLog.Log($"[ExScoring] FetchCompare succeeded: userId={userId} rows={response?.rows?.Length ?? 0} them={response?.them?.nickname} isWatched={response?.them?.isWatched?.ToString() ?? "null"}");
                    onComplete?.Invoke(response, null);
                }
                catch (Exception ex)
                {
                    DebugLog.Log($"[ExScoring] FetchCompare: failed to parse response ({request.downloadHandler.text}): {ex}");
                    onComplete?.Invoke(null, "Invalid server response");
                }
            }
            finally
            {
                request.Dispose();
            }
        }

        /// <summary>
        /// Adds a player to the requester's friends/watch list by userId (POST /api/friends with
        /// { userId } — ApiContract.md Section 7.3, v2.25). Idempotent server-side: adding someone
        /// already on the list returns 200 with the existing entry. Requires an API key. Always calls
        /// onComplete exactly once: with the parsed response on success, or null on any failure
        /// (every failure path is logged first).
        /// </summary>
        public static void AddFriendByUserId(string userId, Action<FriendAddResponse> onComplete)
        {
            if (string.IsNullOrEmpty(Config.ApiKey))
            {
                DebugLog.Log("[ExScoring] AddFriendByUserId: no API key set, aborting.");
                onComplete?.Invoke(null);
                return;
            }

            if (string.IsNullOrEmpty(userId))
            {
                DebugLog.Log("[ExScoring] AddFriendByUserId: userId is null/empty, aborting.");
                onComplete?.Invoke(null);
                return;
            }

            try
            {
                string json = JsonConvert.SerializeObject(new FriendAddRequest { userId = userId });
                DebugLog.Log($"[ExScoring] AddFriendByUserId: starting, body={json}");
                MelonCoroutines.Start(AddFriendByUserIdCoroutine(Encoding.UTF8.GetBytes(json), userId, onComplete));
            }
            catch (Exception ex)
            {
                DebugLog.Log($"[ExScoring] AddFriendByUserId: failed to build request: {ex}");
                onComplete?.Invoke(null);
            }
        }

        private static IEnumerator AddFriendByUserIdCoroutine(byte[] body, string userId, Action<FriendAddResponse> onComplete)
        {
            string url = $"{ApiBaseUrl}/api/friends";

            UnityWebRequest request = new UnityWebRequest(url, "POST");
            try
            {
                request.uploadHandler = new UploadHandlerRaw(body);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Authorization", "ApiKey " + Config.ApiKey);

                yield return request.SendWebRequest();

                if (request.isNetworkError || request.isHttpError)
                {
                    string errorBody = request.downloadHandler != null ? request.downloadHandler.text : "";
                    DebugLog.Log($"[ExScoring] AddFriendByUserId failed ({request.responseCode}): {request.error} | userId={userId} | Body: {errorBody}");
                    onComplete?.Invoke(null);
                    yield break;
                }

                try
                {
                    FriendAddResponse response = JsonConvert.DeserializeObject<FriendAddResponse>(request.downloadHandler.text);
                    DebugLog.Log($"[ExScoring] AddFriendByUserId succeeded: userId={response?.userId} nickname={response?.nickname}");
                    onComplete?.Invoke(response ?? new FriendAddResponse { userId = userId });
                }
                catch (Exception ex)
                {
                    // The request itself succeeded (2xx), so the friend is on the list even if the
                    // body didn't parse — report success rather than a misleading failure.
                    DebugLog.Log($"[ExScoring] AddFriendByUserId: succeeded but failed to parse response ({request.downloadHandler.text}): {ex}");
                    onComplete?.Invoke(new FriendAddResponse { userId = userId });
                }
            }
            finally
            {
                request.Dispose();
            }
        }
    }
}