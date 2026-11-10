using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader;
using MelonLoader.TinyJSON;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// ExScoring's own persisted list of requested songs that were found via web search but are not
    /// downloaded locally yet. SongRequest can't hold these for us in a no-SongBrowser setup (its
    /// AddMissing takes a SongBrowser-typed Song we can't construct), so we own this list. It feeds the
    /// downloadable rows in the Song Requests folder (phase 3) and is reconciled into SongRequest's
    /// available queue on download (phase 4).
    /// </summary>
    internal static class SongRequestStore
    {
        [Serializable]
        public class RequestedSong
        {
            public string SongID;
            public string Title;
            public string Artist;
            public string Mapper;
            public string DownloadURL;
            public string PreviewURL;
            public string RequestedBy;
            public long RequestedAtUtcTicks;
        }

        [Serializable]
        public class Store
        {
            public List<RequestedSong> Songs = new List<RequestedSong>();
        }

        private static Store store;
        private static readonly string path =
            Path.Combine(Application.dataPath, "..", "UserData", "ExScoring_RequestedSongs.json");

        private static void EnsureLoaded()
        {
            if (store != null) return;
            store = new Store();
            try
            {
                if (File.Exists(path))
                    store = JSON.Load(File.ReadAllText(path)).Make<Store>() ?? new Store();
            }
            catch (Exception e)
            {
                DebugLog.Log($"[SongRequestStore] Load failed, starting empty: {e.Message}");
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
                DebugLog.Log($"[SongRequestStore] Save failed: {e.Message}");
            }
        }

        public static bool Contains(string songID)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(songID)) return false;
            return store.Songs.Exists(s => s.SongID == songID);
        }

        /// <summary>Adds an entry if not already present. Returns true if it was newly added.</summary>
        public static bool Add(RequestedSong entry)
        {
            EnsureLoaded();
            if (entry == null || string.IsNullOrEmpty(entry.SongID)) return false;
            if (store.Songs.Exists(s => s.SongID == entry.SongID)) return false;
            store.Songs.Add(entry);
            Save();
            return true;
        }

        /// <summary>
        /// Removes an entry. By default the removed entry is recorded in the Song Request History; pass
        /// toHistory=false when the request isn't really leaving the list (e.g. it was just downloaded and
        /// is being moved into SongRequest's available queue).
        /// </summary>
        public static void Remove(string songID, bool toHistory = true)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(songID)) return;
            if (toHistory)
                foreach (var s in store.Songs)
                    if (s.SongID == songID) RecordHistory(s);
            int removed = store.Songs.RemoveAll(s => s.SongID == songID);
            if (removed > 0) Save();
        }

        /// <summary>Clears every entry, recording each one in the Song Request History.</summary>
        public static void Clear()
        {
            EnsureLoaded();
            if (store.Songs.Count == 0) return;
            // Oldest first, so the history ends up in the same order the list was in.
            for (int i = store.Songs.Count - 1; i >= 0; i--)
                RecordHistory(store.Songs[i]);
            store.Songs.Clear();
            Save();
        }

        private static void RecordHistory(RequestedSong s)
        {
            SongRequestHistoryStore.Add(s.SongID, s.Title, s.Artist, s.Mapper, s.DownloadURL, s.PreviewURL, s.RequestedBy);
        }

        public static List<RequestedSong> GetAll()
        {
            EnsureLoaded();
            return new List<RequestedSong>(store.Songs);
        }
    }
}