using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Harmony;
using MelonLoader;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// TEMPORARY diagnostics for slow song list entry. Self-contained: delete this file (and its
    /// csproj line) to remove it — nothing else in the mod references it.
    ///
    /// Each song-page entry opens a 15 s "session". During it:
    ///  - every timed step below adds its wall time to a per-step total (calls are counted, and
    ///    nested steps are timed separately — e.g. Rebuild includes BuildView and SetView);
    ///  - every frame longer than HitchMs is recorded, so costs NOT covered by a timer (native
    ///    code, coroutines, binding) still show up.
    /// At the end a single [EntryPerf] block is written with MelonLogger.Log, so it appears even
    /// with debug logs off, along with the library / playlist / request sizes for context.
    ///
    /// Mod methods are timed with Harmony patches on our own managed methods, so none of the
    /// existing files had to change.
    /// </summary>
    internal static class EntryPerf
    {
        private const float SessionSeconds = 15f;
        private const float HitchMs = 50f;
        private const int MaxHitchLines = 25;

        private static bool sessionActive;
        private static float sessionStart;
        private static readonly Dictionary<string, double> totals = new Dictionary<string, double>();
        private static readonly Dictionary<string, int> calls = new Dictionary<string, int>();
        private static readonly List<string> order = new List<string>();
        private static readonly List<string> hitches = new List<string>();
        private static int hitchCount;
        private static double hitchTotalMs;
        private static double worstFrameMs;

        // ── Timer helpers (used by every patch below) ──────────────────────────

        internal static Stopwatch Begin()
        {
            EnsureSession();
            return Stopwatch.StartNew();
        }

        internal static void End(string name, Stopwatch sw)
        {
            if (sw == null) return;
            sw.Stop();
            if (!sessionActive) return;

            if (!totals.ContainsKey(name))
            {
                totals[name] = 0;
                calls[name] = 0;
                order.Add(name);
            }
            totals[name] += sw.Elapsed.TotalMilliseconds;
            calls[name]++;
        }

        private static void EnsureSession()
        {
            if (sessionActive) return;
            sessionActive = true;
            sessionStart = Time.realtimeSinceStartup;
            totals.Clear();
            calls.Clear();
            order.Clear();
            hitches.Clear();
            hitchCount = 0;
            hitchTotalMs = 0;
            worstFrameMs = 0;
            MelonCoroutines.Start(SessionCo());
        }

        private static IEnumerator SessionCo()
        {
            // The first delta measured covers the frame the session began in, which is where
            // the synchronous entry work lands.
            float lastFrame = Time.realtimeSinceStartup;
            while (Time.realtimeSinceStartup - sessionStart < SessionSeconds)
            {
                yield return null;
                float now = Time.realtimeSinceStartup;
                double frameMs = (now - lastFrame) * 1000.0;
                lastFrame = now;

                if (frameMs > worstFrameMs) worstFrameMs = frameMs;
                if (frameMs >= HitchMs)
                {
                    hitchCount++;
                    hitchTotalMs += frameMs;
                    if (hitches.Count < MaxHitchLines)
                        hitches.Add($"    +{(now - sessionStart):0.00}s  {frameMs:0} ms");
                }
            }

            try { WriteReport(); }
            catch (Exception e) { MelonLogger.Log("[EntryPerf] report failed: " + e); }
            sessionActive = false;
        }

        private static void WriteReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[EntryPerf] ===== Song list entry report =====");
            sb.AppendLine("  Context: " + Context());

            sb.AppendLine("  Timed steps (total ms / calls; nested steps are included in their parents):");
            if (order.Count == 0) sb.AppendLine("    (none)");
            foreach (string name in order)
                sb.AppendLine($"    {name,-44} {totals[name],9:0.0} ms  x{calls[name]}");

            sb.AppendLine($"  Frames >= {HitchMs:0} ms in first {SessionSeconds:0}s: {hitchCount} " +
                          $"(total {hitchTotalMs:0} ms, worst {worstFrameMs:0} ms)");
            foreach (string h in hitches) sb.AppendLine(h);
            if (hitchCount > hitches.Count) sb.AppendLine($"    ... {hitchCount - hitches.Count} more");
            sb.Append("[EntryPerf] ==================================");

            MelonLogger.Log(sb.ToString());
        }

        private static string Context()
        {
            var parts = new List<string>();
            try { parts.Add("songs=" + (SongList.I != null && SongList.I.songs != null ? SongList.I.songs.Count : -1)); }
            catch { parts.Add("songs=?"); }

            try
            {
                int lists = 0, entries = 0;
                if (PlaylistManager.playlists != null)
                {
                    foreach (var p in PlaylistManager.playlists.Values)
                    {
                        lists++;
                        if (p != null && p.songs != null) entries += p.songs.Count;
                    }
                }
                parts.Add($"playlists={lists} playlistEntries={entries}");
            }
            catch { parts.Add("playlists=?"); }

            try { parts.Add("requestHistory=" + SongRequestHistoryStore.Count); } catch { parts.Add("requestHistory=?"); }
            try { parts.Add("requests=" + SongRequestStore.GetAll().Count); } catch { parts.Add("requests=?"); }
            try { parts.Add("folders=" + (SongFolderManager.availableFolders != null ? SongFolderManager.availableFolders.Count : -1)); }
            catch { parts.Add("folders=?"); }

            return string.Join(", ", parts.ToArray());
        }

        // ══════════════════════════════════════════════════════════════════
        //  Game methods — whole call (Priority.First prefix / Priority.Last
        //  postfix, so every other patch on the method is included)
        // ══════════════════════════════════════════════════════════════════

        [HarmonyPatch(typeof(MenuState), "SetState", new Type[] { typeof(MenuState.State) })]
        private static class SetStateTimer
        {
            [HarmonyPriority(Priority.First)]
            private static void Prefix(MenuState.State state, out Stopwatch __state)
            {
                __state = state == MenuState.State.SongPage ? Begin() : null;
            }

            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Stopwatch __state) => End("MenuState.SetState(SongPage) [all patches]", __state);
        }

        [HarmonyPatch(typeof(SongSelect), "OnEnable", new Type[0])]
        private static class OnEnableTimer
        {
            [HarmonyPriority(Priority.First)]
            private static void Prefix(out Stopwatch __state) => __state = Begin();

            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Stopwatch __state) => End("SongSelect.OnEnable [all patches]", __state);
        }

        [HarmonyPatch(typeof(SongSelect), "ShowSongList", new Type[0])]
        private static class ShowSongListTimer
        {
            [HarmonyPriority(Priority.First)]
            private static void Prefix(out Stopwatch __state) => __state = Begin();

            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Stopwatch __state) => End("SongSelect.ShowSongList [all patches]", __state);
        }

        [HarmonyPatch(typeof(SongSelect), "GetSongIDs", new Type[] { typeof(bool) })]
        private static class GetSongIDsTimer
        {
            [HarmonyPriority(Priority.First)]
            private static void Prefix(out Stopwatch __state) => __state = Begin();

            [HarmonyPriority(Priority.Last)]
            private static void Postfix(Stopwatch __state) => End("SongSelect.GetSongIDs [all patches]", __state);
        }

        /// <summary>
        /// Native body only: Last prefix (just before the original) and First postfix (just
        /// after it), so the mod's own GetSongIDs postfix is not counted here.
        /// </summary>
        [HarmonyPatch(typeof(SongSelect), "GetSongIDs", new Type[] { typeof(bool) })]
        private static class GetSongIDsNativeTimer
        {
            [HarmonyPriority(Priority.Last)]
            private static void Prefix(out Stopwatch __state) => __state = Begin();

            [HarmonyPriority(Priority.First)]
            private static void Postfix(Stopwatch __state) => End("  SongSelect.GetSongIDs (native only)", __state);
        }

        // ══════════════════════════════════════════════════════════════════
        //  Mod methods
        // ══════════════════════════════════════════════════════════════════

        [HarmonyPatch(typeof(PlaylistManager), "PopulatePlaylistsSongNames")]
        private static class PopulatePlaylistsTimer
        {
            private static void Prefix(out Stopwatch __state) => __state = Begin();
            private static void Postfix(Stopwatch __state) => End("PlaylistManager.PopulatePlaylistsSongNames", __state);
        }

        [HarmonyPatch(typeof(ExScoring), "RunSongPageSetup")]
        private static class RunSongPageSetupTimer
        {
            private static void Prefix(out Stopwatch __state) => __state = Begin();
            private static void Postfix(Stopwatch __state) => End("ExScoring.RunSongPageSetup", __state);
        }

        [HarmonyPatch(typeof(ExScoring), "SongListUISetup")]
        private static class SongListUISetupTimer
        {
            private static void Prefix(out Stopwatch __state) => __state = Begin();
            private static void Postfix(Stopwatch __state) => End("  ExScoring.SongListUISetup", __state);
        }

        [HarmonyPatch(typeof(ExScoring), "AutoSelectSong")]
        private static class AutoSelectSongTimer
        {
            private static void Prefix(out Stopwatch __state) => __state = Begin();
            private static void Postfix(Stopwatch __state) => End("  ExScoring.AutoSelectSong (sync part)", __state);
        }

        [HarmonyPatch(typeof(FolderRowManager), "Rebuild")]
        private static class RebuildTimer
        {
            private static void Prefix(out Stopwatch __state) => __state = Begin();
            private static void Postfix(Stopwatch __state) => End("FolderRowManager.Rebuild", __state);
        }

        [HarmonyPatch(typeof(FolderRowManager), "BuildView")]
        private static class BuildViewTimer
        {
            private static void Prefix(out Stopwatch __state) => __state = Begin();
            private static void Postfix(Stopwatch __state) => End("  FolderRowManager.BuildView", __state);
        }

        [HarmonyPatch(typeof(FolderRowManager), "GetHistoryCounts")]
        private static class HistoryCountsTimer
        {
            private static void Prefix(out Stopwatch __state) => __state = Begin();
            private static void Postfix(Stopwatch __state) => End("    FolderRowManager.GetHistoryCounts", __state);
        }

        [HarmonyPatch(typeof(FolderRowManager), "GetSongRequestCounts")]
        private static class RequestCountsTimer
        {
            private static void Prefix(out Stopwatch __state) => __state = Begin();
            private static void Postfix(Stopwatch __state) => End("    FolderRowManager.GetSongRequestCounts", __state);
        }

        [HarmonyPatch(typeof(FolderRowManager), "EstimateMaxViewSize")]
        private static class EstimateMaxViewSizeTimer
        {
            private static void Prefix(out Stopwatch __state) => __state = Begin();
            private static void Postfix(Stopwatch __state) => End("  FolderRowManager.EstimateMaxViewSize", __state);
        }

        [HarmonyPatch(typeof(VirtualSongList), "SetView", new Type[] { typeof(List<ViewRow>), typeof(float?), typeof(bool) })]
        private static class SetViewTimer
        {
            private static void Prefix(out Stopwatch __state) => __state = Begin();
            private static void Postfix(Stopwatch __state) => End("  VirtualSongList.SetView", __state);
        }
    }
}