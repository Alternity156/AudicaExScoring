using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MelonLoader;
using Newtonsoft.Json;
using UnityEngine;

namespace ExScoringMod
{
    internal class PlaylistIOHandler
    {
        public string playlistDirectory = Application.dataPath.Replace("Audica_Data", "Playlists");
        public string playlistDataFile;
        public List<PlaylistData> playlistData;

        public PlaylistIOHandler()
        {
            if (!Directory.Exists(playlistDirectory))
            {
                Directory.CreateDirectory(playlistDirectory);
            }
        }

        public SortedDictionary<string, Playlist> LoadPlaylists()
        {
            LoadPlaylistData();

            SortedDictionary<string, Playlist> playlists = new SortedDictionary<string, Playlist>();
            string[] files = Directory.GetFiles(playlistDirectory);
            for (int i = 0; i < files.Length; i++)
            {
                if (Path.GetExtension(files[i]) != ".playlist") continue;
                KeyValuePair<string, Playlist>? nullableEntry = DecodePlaylist(files[i]);
                if (nullableEntry is null) continue;
                KeyValuePair<string, Playlist> entry = nullableEntry.Value;
                CheckDuplicate(ref entry, ref playlists);
                playlists.Add(entry.Key, entry.Value);
                AddNewPlaylistData(entry.Value.name);
            }
            RemoveDeletedPlaylistData();
            return playlists;
        }

        private void CheckDuplicate(ref KeyValuePair<string, Playlist> entry, ref SortedDictionary<string, Playlist> playlists)
        {
            int i = 2;
            if (playlists.ContainsKey(entry.Key))
            {
                entry.Value.name += $" [{i}]";
                entry = new KeyValuePair<string, Playlist>(entry.Value.name, entry.Value);
            }
            while (playlists.ContainsKey(entry.Key))
            {
                i++;
                entry.Value.name = entry.Value.name.Substring(0, entry.Value.name.Length - 2);
                entry.Value.name += $"{i}]";
                entry = new KeyValuePair<string, Playlist>(entry.Value.name, entry.Value);
            }
        }

        private void LoadPlaylistData()
        {
            if (CreatePlaylistDataFile()) return;

            using (StreamReader reader = new StreamReader(playlistDataFile))
            {
                string json = reader.ReadToEnd();
                playlistData = JsonConvert.DeserializeObject<List<PlaylistData>>(json);
            }
            if (playlistData is null) CreatePlaylistDataFile(true);
        }

        private void RemoveDeletedPlaylistData()
        {
            for (int i = playlistData.Count - 1; i >= 0; i--)
            {
                if (!playlistData[i].loaded) playlistData.RemoveAt(i);
            }
        }

        private bool CreatePlaylistDataFile(bool recreate = false)
        {
            playlistDataFile = Path.Combine(playlistDirectory, "playlistData.dat");
            if (!File.Exists(playlistDataFile) || recreate)
            {
                var stream = new FileStream(playlistDataFile, FileMode.Create);
                stream.Dispose();
                playlistData = new List<PlaylistData>();
                return true;
            }
            return false;
        }

        public void UpdatePlaylistData(string playlistName)
        {
            PlaylistData playlist = playlistData.First(p => p.playlistName == playlistName);
            playlist.initialized = true;
        }

        public void AddNewPlaylistData(string playlistName, bool created = false)
        {
            if (!playlistData.Any(p => p.playlistName == playlistName))
            {
                PlaylistData data = new PlaylistData(playlistName, created);
                data.loaded = true;
                playlistData.Add(data);
            }
            else
            {
                playlistData.First(p => p.playlistName == playlistName).loaded = true;
            }
        }

        public void RemovePlaylistData(string playlistName)
        {
            if (playlistData.Any(p => p.playlistName == playlistName))
            {
                playlistData.Remove(playlistData.First(p => p.playlistName == playlistName));
            }
        }

        public void SavePlaylistData()
        {
            string json = JsonConvert.SerializeObject(playlistData, Formatting.Indented);
            File.WriteAllText(playlistDataFile, json);
        }

        private KeyValuePair<string, Playlist>? DecodePlaylist(string playlistJson)
        {
            try
            {
                using (StreamReader reader = new StreamReader(playlistJson))
                {
                    string json = reader.ReadToEnd();
                    Playlist playlist = JsonConvert.DeserializeObject<Playlist>(json);
                    playlist.filename = Path.GetFileName(playlistJson);
                    return new KeyValuePair<string, Playlist>(playlist.name, playlist);
                }
            }
            catch
            {
                MelonLogger.Log($"[WARNING] Encountered an error while loading {Path.GetFileName(playlistJson)} - please check if the file has valid json.");
                return null;
            }
        }

        public void SavePlaylist(Playlist playlist, bool update)
        {
            string fileName = GetPlaylistPath(playlist);
            if (!update)
            {
                if (File.Exists(fileName))
                {
                    int i = 1;
                    playlist.name += i.ToString();
                    fileName = GetPlaylistPath(playlist);
                    while (File.Exists(fileName))
                    {
                        playlist.name = playlist.name.Substring(0, playlist.name.Length - i.ToString().Length);
                        playlist.name += i.ToString();
                        fileName = GetPlaylistPath(playlist);
                    }
                }
            }
            string json = JsonConvert.SerializeObject(playlist, Formatting.Indented);
            File.WriteAllText(fileName, json);
        }

        public void DeletePlaylist(string name, string filename)
        {
            if (!playlistData.Any(p => p.playlistName == name)) return;
            PlaylistData data = playlistData.First(p => p.playlistName == name);
            playlistData.Remove(data);
            string filePath = Path.Combine(playlistDirectory, filename);
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }
            SavePlaylistData();
        }

        public bool IsPlaylistInitialized(string playlistName)
        {
            var data = playlistData.FirstOrDefault(p => p.playlistName == playlistName);
            return data != null && data.initialized;
        }

        private string GetPlaylistPath(Playlist playlist)
        {
            return GetPlaylistPath(playlist.filename);
        }

        private string GetPlaylistPath(string name)
        {
            return Path.Combine(playlistDirectory, name);
        }

        [Serializable]
        internal class PlaylistData
        {
            public string playlistName;
            public bool initialized = false;
            [NonSerialized] public bool loaded = false;

            public PlaylistData(string playlistName, bool initialized)
            {
                this.playlistName = playlistName;
                this.initialized = initialized;
            }
        }
    }
}