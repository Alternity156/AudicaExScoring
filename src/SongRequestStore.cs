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
                MelonLogger.Log($"[SongRequestStore] Load failed, starting empty: {e.Message}");
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
                MelonLogger.Log($"[SongRequestStore] Save failed: {e.Message}");
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

        public static void Remove(string songID)
        {
            EnsureLoaded();
            if (string.IsNullOrEmpty(songID)) return;
            int removed = store.Songs.RemoveAll(s => s.SongID == songID);
            if (removed > 0) Save();
        }

        public static void Clear()
        {
            EnsureLoaded();
            if (store.Songs.Count == 0) return;
            store.Songs.Clear();
            Save();
        }

        public static List<RequestedSong> GetAll()
        {
            EnsureLoaded();
            return new List<RequestedSong>(store.Songs);
        }
    }
}