using System;
using System.Collections;
using System.Collections.Generic;
using MelonLoader;
using Newtonsoft.Json;
using UnityEngine.Networking;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        /// <summary>
        /// Countdown shown to spectators on the lobby panel while a match is playing: how long
        /// until the current song is done.
        ///
        /// Song length: for each participant difficulty, the end tick is the furthest
        /// tick + tickLength of its cues. Read locally with SongCues.GetCues when the song is
        /// installed (ticks to ms through the native AudioDriver.TickSpanToMs). Otherwise it's
        /// fetched once per match from GET /api/songs/:songId/map (ApiContract Section 9), with
        /// ticks converted through its bpmEvents at 480 ticks/beat, the same formula the server
        /// uses for session lengths.
        ///
        /// Remaining time: for every participant still playing, the time from their last
        /// reported matchScores tick to the end tick, minus the time since that snapshot's
        /// serverTime. The largest wins. That follows the real playback position, so preroll
        /// and clock drift don't matter. A participant with no score yet falls back to
        /// matchStart.startAt + full length.
        ///
        /// When the length can't be known (map not uploaded, or no bpmEvents), shows the
        /// elapsed time instead.
        /// </summary>
        internal static class MatchTimer
        {
            private const float TicksPerBeat = 480f;

            private sealed class BpmEvent
            {
                public float tick;
                public float bpm;
            }

            private sealed class MapCue
            {
                public float tick;
                public float tickLength;
            }

            private sealed class MapResponse
            {
                public MapCue[] cues;
                public BpmEvent[] bpmEvents;
            }

            /// <summary>One difficulty's end tick and a way to turn a tick span into ms.</summary>
            private sealed class SongTiming
            {
                public float EndTick;
                public SongList.SongData Song;   // local song: native conversion
                public BpmEvent[] Bpm;           // API map: our own conversion

                public double SpanMs(float fromTick, float toTick)
                {
                    if (toTick <= fromTick) return 0.0;
                    if (Song != null) return AudioDriver.TickSpanToMs(Song, fromTick, toTick);
                    return BpmSpanMs(Bpm, fromTick, toTick);
                }
            }

            private static string currentMatchId;
            private static string currentSongId;
            private static readonly Dictionary<string, SongTiming> timings = new Dictionary<string, SongTiming>();
            private static readonly HashSet<string> pending = new HashSet<string>();
            private static readonly HashSet<string> attempted = new HashSet<string>();

            /// <summary>
            /// The panel's timer text for the current match, or null when there's nothing to show
            /// (not spectating, no match, or not playing yet).
            /// </summary>
            public static string GetSpectatorText(MpLobbyState lobby)
            {
                MpMatchPrepare match = MpSession.CurrentMatch;
                MpMatchStart start = MpSession.CurrentMatchStart;
                if (lobby == null || match == null || start == null) return null;
                if (lobby.phase != "playing") return null;
                if (MpSession.IsMatchParticipant) return null;
                if (start.matchId != null && match.matchId != null && start.matchId != match.matchId) return null;

                EnsureMatch(match);

                long nowLocal = MpSession.LocalNowMs;
                long startLocal = MpSession.ServerToLocalMs(start.startAt);
                if (nowLocal < startLocal)
                {
                    return $"starting in {Math.Ceiling((startLocal - nowLocal) / 1000.0):F0}s";
                }
                double elapsedMs = nowLocal - startLocal;

                MpMatchScores scores = MpSession.LastMatchScores;
                if (scores != null && scores.matchId != null && match.matchId != null && scores.matchId != match.matchId)
                {
                    scores = null;
                }
                Dictionary<string, MpScoreEntry> entries = new Dictionary<string, MpScoreEntry>();
                if (scores != null && scores.scores != null)
                {
                    foreach (MpScoreEntry s in scores.scores)
                    {
                        if (s != null && s.userId != null) entries[s.userId] = s;
                    }
                }

                bool anyPlaying = false;
                double best = double.MinValue;
                bool anyKnown = false;

                if (match.participants != null)
                {
                    foreach (MpMatchParticipant participant in match.participants)
                    {
                        if (participant == null || participant.userId == null) continue;

                        // Dropped while loading: they spectate now, so they don't hold the match.
                        MpPlayerState player = lobby.FindPlayer(participant.userId);
                        if (player != null && player.matchRole == "spectator") continue;

                        entries.TryGetValue(participant.userId, out MpScoreEntry entry);
                        if (entry != null && entry.status != null && entry.status != "playing") continue;
                        anyPlaying = true;

                        string difficulty = DifficultyOf(participant, match);
                        if (difficulty == null || !timings.TryGetValue(difficulty, out SongTiming timing)) continue;

                        double remaining;
                        if (entry != null && entry.tick.HasValue && scores != null)
                        {
                            double sinceSnapshotMs = Math.Max(0.0, MpSession.ServerNowMs - scores.serverTime);
                            float tick = Math.Max(0f, entry.tick.Value);
                            remaining = timing.SpanMs(tick, timing.EndTick) - sinceSnapshotMs;
                        }
                        else
                        {
                            remaining = timing.SpanMs(0f, timing.EndTick) - elapsedMs;
                        }

                        anyKnown = true;
                        if (remaining > best) best = remaining;
                    }
                }

                if (!anyPlaying) return "wrapping up...";
                if (!anyKnown) return $"playing for {FormatMs(elapsedMs)}";
                if (best <= 0) return "wrapping up...";
                return $"{FormatMs(best)} left";
            }

            // ── Song length ──

            private static void EnsureMatch(MpMatchPrepare match)
            {
                string songId = match.song != null ? match.song.songId : null;
                if (match.matchId != currentMatchId || songId != currentSongId)
                {
                    currentMatchId = match.matchId;
                    currentSongId = songId;
                    timings.Clear();
                    pending.Clear();
                    attempted.Clear();
                }
                if (songId == null) return;

                HashSet<string> difficulties = new HashSet<string>();
                if (match.participants != null)
                {
                    foreach (MpMatchParticipant participant in match.participants)
                    {
                        string difficulty = participant != null ? DifficultyOf(participant, match) : null;
                        if (difficulty != null) difficulties.Add(difficulty);
                    }
                }

                // Checked every refresh: a catch-up match (MpSession.EnsureCatchUpMatch) can gain
                // participants or correct their difficulty once matchScores arrives.
                foreach (string difficulty in difficulties)
                {
                    if (!attempted.Add(difficulty)) continue;

                    SongTiming local = TryLocal(songId, difficulty);
                    if (local != null)
                    {
                        timings[difficulty] = local;
                        DebugLog.Log($"[MatchTimer] {songId}/{difficulty}: local length {local.SpanMs(0f, local.EndTick) / 1000.0:F1} s");
                        continue;
                    }

                    pending.Add(difficulty);
                    MelonCoroutines.Start(FetchMapCoroutine(match.matchId, songId, difficulty));
                }
            }

            private static string DifficultyOf(MpMatchParticipant participant, MpMatchPrepare match)
            {
                if (!string.IsNullOrEmpty(participant.difficulty)) return participant.difficulty;
                return match.song != null && !string.IsNullOrEmpty(match.song.difficulty) ? match.song.difficulty : null;
            }

            private static SongTiming TryLocal(string songId, string difficulty)
            {
                try
                {
                    if (SongList.I == null) return null;
                    if (!Enum.TryParse(difficulty, out KataConfig.Difficulty parsed)) return null;

                    SongList.SongData data = SongList.I.GetSong(songId);
                    if (data == null) return null;

                    var cues = SongCues.GetCues(data, parsed);
                    if (cues == null || cues.Length == 0) return null;

                    float endTick = 0f;
                    foreach (SongCues.Cue cue in cues)
                    {
                        float end = cue.tick + Math.Max(0, cue.tickLength);
                        if (end > endTick) endTick = end;
                    }
                    if (endTick <= 0f) return null;

                    return new SongTiming { EndTick = endTick, Song = data };
                }
                catch (Exception ex)
                {
                    DebugLog.Log($"[MatchTimer] local length failed for {songId}/{difficulty}: {ex.Message}");
                    return null;
                }
            }

            private static IEnumerator FetchMapCoroutine(string matchId, string songId, string difficulty)
            {
                string url = $"{ApiBaseUrl}/api/songs/{Uri.EscapeDataString(songId)}/map?difficulty={Uri.EscapeDataString(difficulty)}";
                DebugLog.Log($"[MatchTimer] GET {url}");

                UnityWebRequest request = UnityWebRequest.Get(url);
                try
                {
                    yield return request.SendWebRequest();

                    // The match moved on while we waited.
                    if (matchId != currentMatchId || songId != currentSongId) yield break;
                    pending.Remove(difficulty);

                    if (request.isNetworkError || request.isHttpError)
                    {
                        DebugLog.Log($"[MatchTimer] map fetch failed ({request.responseCode}) for {songId}/{difficulty}: {request.error}");
                        yield break;
                    }

                    SongTiming timing = null;
                    try
                    {
                        MapResponse map = JsonConvert.DeserializeObject<MapResponse>(request.downloadHandler.text);
                        timing = FromMap(map);
                    }
                    catch (Exception ex)
                    {
                        DebugLog.Log($"[MatchTimer] couldn't parse map for {songId}/{difficulty}: {ex.Message}");
                    }

                    if (timing == null)
                    {
                        DebugLog.Log($"[MatchTimer] {songId}/{difficulty}: no usable cues/bpmEvents, showing elapsed time.");
                        yield break;
                    }

                    timings[difficulty] = timing;
                    DebugLog.Log($"[MatchTimer] {songId}/{difficulty}: API length {timing.SpanMs(0f, timing.EndTick) / 1000.0:F1} s");
                }
                finally
                {
                    request.Dispose();
                }
            }

            private static SongTiming FromMap(MapResponse map)
            {
                if (map == null || map.cues == null || map.cues.Length == 0) return null;
                if (map.bpmEvents == null || map.bpmEvents.Length == 0) return null; // "length unknown" (Section 9)

                float endTick = 0f;
                foreach (MapCue cue in map.cues)
                {
                    if (cue == null) continue;
                    float end = cue.tick + Math.Max(0f, cue.tickLength);
                    if (end > endTick) endTick = end;
                }
                if (endTick <= 0f) return null;

                List<BpmEvent> bpm = new List<BpmEvent>();
                foreach (BpmEvent e in map.bpmEvents)
                {
                    if (e != null && e.bpm > 0f) bpm.Add(e);
                }
                if (bpm.Count == 0) return null;
                bpm.Sort((a, b) => a.tick.CompareTo(b.tick));

                return new SongTiming { EndTick = endTick, Bpm = bpm.ToArray() };
            }

            /// <summary>Tick span to ms over a tempo track. Ticks before the first event use its bpm.</summary>
            private static double BpmSpanMs(BpmEvent[] bpm, float fromTick, float toTick)
            {
                if (bpm == null || bpm.Length == 0) return 0.0;

                double ms = 0.0;
                for (int i = 0; i < bpm.Length; i++)
                {
                    float segStart = i == 0 ? float.MinValue : bpm[i].tick;
                    float segEnd = i + 1 < bpm.Length ? bpm[i + 1].tick : float.MaxValue;

                    float a = Math.Max(fromTick, segStart);
                    float b = Math.Min(toTick, segEnd);
                    if (b <= a) continue;

                    ms += (b - a) / TicksPerBeat * (60000.0 / bpm[i].bpm);
                }
                return ms;
            }

            private static string FormatMs(double ms)
            {
                int total = (int)Math.Ceiling(Math.Max(0.0, ms) / 1000.0);
                return $"{total / 60}:{total % 60:D2}";
            }
        }
    }
}