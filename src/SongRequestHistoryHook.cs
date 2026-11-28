using System;
using System.Collections;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// Moves every request that leaves SongRequest's lists into <see cref="SongRequestHistoryStore"/>.
    ///
    /// WHY POLLING, NOT HARMONY: patching any method of AudicaModding.SongRequests fails with
    /// "IL Compile Error ... Could not load file or assembly 'SongBrowser'" whenever SongBrowser (an
    /// optional dependency of SongRequest) isn't installed — MonoMod walks the class's methods while
    /// resolving the patched body, and some of them reference SongBrowser types. So instead of hooking
    /// SongRequests.RemoveRequest / RemoveMissing, a coroutine diffs SongRequest's two lists (via
    /// SongRequestIntegration's reflection reads, which work fine) every <see cref="PollInterval"/>.
    ///
    /// That catches every removal path regardless of who triggers it: auto-remove after Results/Failed,
    /// chat !remove / !wrongsong / mod remove, Skip / unavailable, ExScoring's Remove Request button and
    /// Clear List.
    ///
    /// False-positive guards:
    ///   • Available (downloaded) requests: a song must be absent on TWO consecutive polls before it's
    ///     recorded. A read that raced a chat-thread modification can come back partial; the next poll
    ///     would show the song again and the candidate is dropped.
    ///   • Missing requests: SongRequest's ProcessQueueAfterRefresh (SongBrowser installs only) clears the
    ///     whole missing list and re-queues every entry for another lookup, and a missing song that gets
    ///     downloaded moves to the available list. So a vanished missing song waits
    ///     <see cref="MissingGrace"/> seconds and is dropped if it comes back as missing, or if a song with
    ///     the same title/artist/mapper shows up in the available list.
    ///   • The first poll only records a baseline — whatever is already queued at startup isn't history.
    ///
    /// ExScoring's own missing-request store (SongRequestStore) still records its removals directly.
    /// Main-thread only (coroutine).
    /// </summary>
    internal static class SongRequestHistoryHook
    {
        private const float PollInterval = 0.5f;
        private const float MissingGrace = 10f;
        private const int MaxErrorLogs = 5;

        private static bool applied;
        private static bool hasBaseline;
        private static int errorLogs;

        // Last poll's contents, by SongID (keeps full metadata for the moment a song disappears).
        private static Dictionary<string, SongRequestIntegration.RequestInfo> lastAvailable =
            new Dictionary<string, SongRequestIntegration.RequestInfo>();
        private static Dictionary<string, SongRequestIntegration.RequestInfo> lastMissing =
            new Dictionary<string, SongRequestIntegration.RequestInfo>();

        // Available songs seen absent once; confirmed (recorded) if still absent on the next poll.
        private static readonly Dictionary<string, SongRequestIntegration.RequestInfo> pendingAvailable =
            new Dictionary<string, SongRequestIntegration.RequestInfo>();

        // Missing songs seen absent; recorded once absent for MissingGrace seconds.
        private class PendingMissing
        {
            public SongRequestIntegration.RequestInfo Info;
            public float Since;
        }
        private static readonly Dictionary<string, PendingMissing> pendingMissing =
            new Dictionary<string, PendingMissing>();

        public static void Apply()
        {
            if (applied) return;
            applied = true;

            if (SongRequestIntegration.SongRequestsType == null) return; // mod absent

            MelonCoroutines.Start(PollLoop());
            DebugLog.Log("[SongRequestHistoryHook] Watching SongRequest's lists for removals (polling).");
        }

        private static IEnumerator PollLoop()
        {
            float next = 0f;
            while (true)
            {
                float now = Time.realtimeSinceStartup;
                if (now >= next)
                {
                    next = now + PollInterval;
                    try
                    {
                        Poll(now);
                    }
                    catch (Exception e)
                    {
                        if (errorLogs < MaxErrorLogs)
                        {
                            errorLogs++;
                            DebugLog.Log($"[SongRequestHistoryHook] poll failed: {e}");
                        }
                    }
                }
                yield return null;
            }
        }

        private static void Poll(float now)
        {
            var available = ToMap(SongRequestIntegration.GetAvailableRequests());
            var missing = ToMap(SongRequestIntegration.GetMissingRequests());

            if (!hasBaseline)
            {
                lastAvailable = available;
                lastMissing = missing;
                hasBaseline = true;
                DebugLog.Log($"[SongRequestHistoryHook] Baseline: {available.Count} available, {missing.Count} missing.");
                return;
            }

            // ── Available: confirm last poll's candidates, then collect new ones ──
            if (pendingAvailable.Count > 0)
            {
                foreach (var kv in new List<KeyValuePair<string, SongRequestIntegration.RequestInfo>>(pendingAvailable))
                {
                    if (!available.ContainsKey(kv.Key))
                        Record(kv.Value);
                }
                pendingAvailable.Clear();
            }
            foreach (var kv in lastAvailable)
                if (!available.ContainsKey(kv.Key))
                    pendingAvailable[kv.Key] = kv.Value;

            // ── Missing: wait out the grace period, drop re-queued / downloaded ones ──
            if (pendingMissing.Count > 0)
            {
                foreach (var kv in new List<KeyValuePair<string, PendingMissing>>(pendingMissing))
                {
                    if (missing.ContainsKey(kv.Key) || IsNowAvailable(kv.Value.Info, available))
                        pendingMissing.Remove(kv.Key);
                    else if (now - kv.Value.Since >= MissingGrace)
                    {
                        Record(kv.Value.Info);
                        pendingMissing.Remove(kv.Key);
                    }
                }
            }
            foreach (var kv in lastMissing)
            {
                if (missing.ContainsKey(kv.Key) || pendingMissing.ContainsKey(kv.Key)) continue;
                if (IsNowAvailable(kv.Value, available)) continue; // downloaded, not removed
                pendingMissing[kv.Key] = new PendingMissing { Info = kv.Value, Since = now };
            }

            lastAvailable = available;
            lastMissing = missing;
        }

        private static Dictionary<string, SongRequestIntegration.RequestInfo> ToMap(
            List<SongRequestIntegration.RequestInfo> list)
        {
            var map = new Dictionary<string, SongRequestIntegration.RequestInfo>();
            if (list == null) return map;
            foreach (var r in list)
                if (!string.IsNullOrEmpty(r.SongID))
                    map[r.SongID] = r;
            return map;
        }

        private static string Norm(string s) => (s ?? "").ToLowerInvariant().Replace(" ", "");

        /// <summary>A missing request whose song now sits in the available list (same title/artist/mapper)
        /// was downloaded, not removed. The IDs differ (maudica ID vs local ID), so match on metadata.</summary>
        private static bool IsNowAvailable(SongRequestIntegration.RequestInfo m,
                                           Dictionary<string, SongRequestIntegration.RequestInfo> available)
        {
            if (available.ContainsKey(m.SongID)) return true;
            string t = Norm(m.Title);
            if (t.Length == 0) return false;
            foreach (var a in available.Values)
                if (Norm(a.Title) == t && Norm(a.Artist) == Norm(m.Artist) && Norm(a.Mapper) == Norm(m.Mapper))
                    return true;
            return false;
        }

        private static void Record(SongRequestIntegration.RequestInfo r)
        {
            SongRequestHistoryStore.Add(r.SongID, r.Title, r.Artist, r.Mapper,
                                        r.DownloadURL, r.PreviewURL, r.RequestedBy);
        }
    }
}