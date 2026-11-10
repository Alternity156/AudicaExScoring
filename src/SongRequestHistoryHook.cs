using System;
using System.Collections.Generic;
using System.Reflection;
using Harmony;
using MelonLoader;

namespace ExScoringMod
{
    /// <summary>
    /// Moves every request that leaves SongRequest's lists into <see cref="SongRequestHistoryStore"/>.
    ///
    /// Every real removal in SongRequest funnels through one of these:
    ///   • SongRequests.RemoveRequest(Request)       — auto-remove after Results/Failed, chat !remove /
    ///                                                  !wrongsong / mod remove, and ExScoring's Remove
    ///                                                  Request button and Clear List
    ///   • SongRequests.RemoveRequest(string)        — wrapper around the one above (patched too, in case
    ///                                                  the JIT inlines the inner call)
    ///   • SongRequests.RemoveMissing(MissingRequest)— chat removals / Skip / "unavailable" of missing songs
    ///
    /// SongRequests.ClearMissing() is deliberately NOT patched: its only caller re-queues every missing song
    /// for another lookup after a song-list refresh, so nothing actually leaves the list there.
    ///
    /// Rather than reading the patched method's argument (often a dummy Request carrying only a SongID), the
    /// prefix snapshots both lists and the postfix records whatever disappeared — full metadata, and immune
    /// to argument types we can't reference at compile time. Nested calls (string → Request overload) record
    /// the same song twice; the store de-duplicates.
    ///
    /// Legacy Harmony (0Harmony 1.x) API, same as SongRequestBlocker.
    /// </summary>
    internal static class SongRequestHistoryHook
    {
        private static bool applied;

        internal class Snapshot
        {
            public List<SongRequestIntegration.RequestInfo> Available;
            public List<SongRequestIntegration.RequestInfo> Missing;
        }

        public static void Apply()
        {
            if (applied) return;
            applied = true;

            Type t = SongRequestIntegration.SongRequestsType;
            if (t == null) return; // mod absent

            HarmonyInstance harmony = HarmonyInstance.Create("ExScoring.SongRequestHistoryHook");
            HarmonyMethod prefix = new HarmonyMethod(typeof(SongRequestHistoryHook)
                .GetMethod(nameof(SnapshotPrefix), BindingFlags.Static | BindingFlags.NonPublic));
            HarmonyMethod postfix = new HarmonyMethod(typeof(SongRequestHistoryHook)
                .GetMethod(nameof(RecordPostfix), BindingFlags.Static | BindingFlags.NonPublic));

            int patched = 0;
            foreach (MethodInfo mi in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (mi.Name != "RemoveRequest" && mi.Name != "RemoveMissing") continue;
                if (mi.GetParameters().Length != 1) continue;
                try
                {
                    harmony.Patch(mi, prefix, postfix);
                    patched++;
                    DebugLog.Log($"[SongRequestHistoryHook] Patched {t.Name}.{mi.Name}({mi.GetParameters()[0].ParameterType.Name})");
                }
                catch (Exception e)
                {
                    DebugLog.Log($"[SongRequestHistoryHook] Failed to patch {t.Name}.{mi.Name}: {e.Message}");
                }
            }

            if (patched == 0)
                DebugLog.Log("[SongRequestHistoryHook] No removal methods patched — request history will only see ExScoring's own store.");
        }

        private static void SnapshotPrefix(out Snapshot __state)
        {
            __state = null;
            try
            {
                __state = new Snapshot
                {
                    Available = SongRequestIntegration.GetAvailableRequests(),
                    Missing = SongRequestIntegration.GetMissingRequests(),
                };
            }
            catch (Exception e)
            {
                DebugLog.Log($"[SongRequestHistoryHook] snapshot failed: {e.Message}");
            }
        }

        private static void RecordPostfix(Snapshot __state)
        {
            if (__state == null) return;
            try
            {
                RecordRemoved(__state.Available, SongRequestIntegration.GetAvailableRequests());
                RecordRemoved(__state.Missing, SongRequestIntegration.GetMissingRequests());
            }
            catch (Exception e)
            {
                DebugLog.Log($"[SongRequestHistoryHook] record failed: {e.Message}");
            }
        }

        private static void RecordRemoved(List<SongRequestIntegration.RequestInfo> before,
                                          List<SongRequestIntegration.RequestInfo> after)
        {
            if (before == null || before.Count == 0) return;

            var remaining = new HashSet<string>();
            if (after != null)
                foreach (var r in after)
                    if (!string.IsNullOrEmpty(r.SongID)) remaining.Add(r.SongID);

            foreach (var r in before)
            {
                if (string.IsNullOrEmpty(r.SongID) || remaining.Contains(r.SongID)) continue;
                SongRequestHistoryStore.Add(r.SongID, r.Title, r.Artist, r.Mapper,
                                            r.DownloadURL, r.PreviewURL, r.RequestedBy);
            }
        }
    }
}