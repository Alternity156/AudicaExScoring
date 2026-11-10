using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader;
using MelonLoader.TinyJSON;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// Persisted history of song requests that left the request list — played (SongRequest's auto-remove
    /// on the Results/Failed screen), removed (Remove Request button, chat !remove / !wrongsong / mod
    /// remove, Skip / unavailable), or cleared (Clear List). Feeds the "Song Request History" folder.
    ///
    /// Newest first, one entry per song (a song that leaves the list again moves back to the top), capped
    /// at <see cref="MaxEntries"/>. Entries keep the download URL when the song wasn't downloaded yet, so
    /// the history folder can still offer it as a downloadable row.
    ///
    /// Thread-safe: SongRequest's chat removals may arrive off the main thread, so every access is locked
    /// and the list refresh is deferred to the main thread via <see cref="ConsumeDirty"/> (polled in OnUpdate).
    /// </summary>
    internal static class SongRequestHistoryStore
    {
        public const int MaxEntries = 100;

        [Serializable]
        public class HistoryEntry
        {
            public string SongID;
            public string Title;
            public string Artist;
            public string Mapper;
            public string DownloadURL;
            public string PreviewURL;
            public string RequestedBy;
            public long RemovedAtUtcTicks;
        }

        [Serializable]
        public class Store
        {
            public List<HistoryEntry> Songs = new List<HistoryEntry>();
        }

        private static readonly object sync = new object();
        private static Store store;
        private static volatile bool dirty;

        private static readonly string path =
            Path.Combine(Application.dataPath, "..", "UserData", "ExScoring_RequestHistory.json");

        private static void EnsureLoaded()
        {
            if (store != null) return;
            store = new Store();
            try
            {
                if (File.Exists(path))
                    store = JSON.Load(File.ReadAllText(path)).Make<Store>() ?? new Store();
                if (store.Songs == null) store.Songs = new List<HistoryEntry>();
            }
            catch (Exception e)
            {
                DebugLog.Log($"[SongRequestHistoryStore] Load failed, starting empty: {e.Message}");
                store = new Store();
            }
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JSON.Dump(store));
            }
            catch (Exception e)
            {
                DebugLog.Log($"[SongRequestHistoryStore] Save failed: {e.Message}");
            }
        }

        private static string Norm(string s) => (s ?? "").ToLowerInvariant().Replace(" ", "");

        /// <summary>Same song? SongID match, or same title+artist+mapper (a downloaded web request can carry
        /// its maudica ID in one list and its local ID in the other).</summary>
        private static bool SameSong(HistoryEntry a, HistoryEntry b)
        {
            if (!string.IsNullOrEmpty(a.SongID) && a.SongID == b.SongID) return true;
            string ta = Norm(a.Title);
            return ta.Length > 0 && ta == Norm(b.Title)
                && Norm(a.Artist) == Norm(b.Artist)
                && Norm(a.Mapper) == Norm(b.Mapper);
        }

        /// <summary>Record a request that just left the request list. Moves an existing entry for the same
        /// song back to the top (keeping any download URL / requester it already had).</summary>
        public static void Add(string songID, string title, string artist, string mapper,
                               string downloadURL, string previewURL, string requestedBy)
        {
            if (string.IsNullOrEmpty(songID) && string.IsNullOrEmpty(title)) return;

            var entry = new HistoryEntry
            {
                SongID = songID,
                Title = title,
                Artist = artist,
                Mapper = mapper,
                DownloadURL = downloadURL,
                PreviewURL = previewURL,
                RequestedBy = requestedBy,
                RemovedAtUtcTicks = DateTime.UtcNow.Ticks,
            };

            lock (sync)
            {
                EnsureLoaded();
                for (int i = store.Songs.Count - 1; i >= 0; i--)
                {
                    var old = store.Songs[i];
                    if (!SameSong(old, entry)) continue;
                    // Don't lose info the newer removal didn't carry (e.g. the available-queue copy of a
                    // song has no download URL, the stored web copy does).
                    if (string.IsNullOrEmpty(entry.DownloadURL)) entry.DownloadURL = old.DownloadURL;
                    if (string.IsNullOrEmpty(entry.PreviewURL)) entry.PreviewURL = old.PreviewURL;
                    if (string.IsNullOrEmpty(entry.RequestedBy)) entry.RequestedBy = old.RequestedBy;
                    store.Songs.RemoveAt(i);
                }

                store.Songs.Insert(0, entry);
                if (store.Songs.Count > MaxEntries)
                    store.Songs.RemoveRange(MaxEntries, store.Songs.Count - MaxEntries);
                Save();
                dirty = true;
            }
            DebugLog.Log($"[SongRequestHistoryStore] + {title} ({songID})");
        }

        public static void Clear()
        {
            lock (sync)
            {
                EnsureLoaded();
                if (store.Songs.Count == 0) return;
                store.Songs.Clear();
                Save();
                dirty = true;
            }
        }

        public static int Count
        {
            get { lock (sync) { EnsureLoaded(); return store.Songs.Count; } }
        }

        /// <summary>A copy of the history, newest first.</summary>
        public static List<HistoryEntry> GetAll()
        {
            lock (sync)
            {
                EnsureLoaded();
                return new List<HistoryEntry>(store.Songs);
            }
        }

        /// <summary>True once after the history changed. Polled on the main thread to refresh the song list.</summary>
        public static bool ConsumeDirty()
        {
            if (!dirty) return false;
            dirty = false;
            return true;
        }
    }
}