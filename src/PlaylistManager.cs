using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader;
using UnityEngine;

namespace ExScoringMod
{
    public static class PlaylistManager
    {
        public static SortedDictionary<string, Playlist> playlists { get; private set; }
        public static Playlist selectedPlaylist { get; private set; }
        public static Playlist playlistToEdit { get; private set; }
        public static PlaylistState state { get; set; }
        public static List<SongList.SongData> internalSongList { get; private set; }
        public static Song[] apiSongList { get; set; }

        private static PlaylistIOHandler ioHandler;

        public static void OnApplicationStart()
        {
            ioHandler = new PlaylistIOHandler();

            LoadPlaylists();
        }

        private static void LoadPlaylists()
        {
            playlists = ioHandler.LoadPlaylists();
            SavePlaylistData();
        }

        public static void MoveSongUp(string song)
        {
            if (playlistToEdit is null) return;
            playlistToEdit.MoveSongUp(song);
        }

        public static void MoveSongDown(string song)
        {
            if (playlistToEdit is null) return;
            playlistToEdit.MoveSongDown(song);
        }

        public static void SavePlaylist(string playlistName, bool fromEdit)
        {
            if (fromEdit) playlistToEdit = null;
            if (!playlists.ContainsKey(playlistName))
            {
                return;
            }
            ioHandler.SavePlaylist(playlists[playlistName], true);
        }

        public static void PopulatePlaylistsSongNames()
        {
            RebuildSongIndex();

            foreach (Playlist playlist in playlists.Values)
            {
                playlist.PopulateSongNames();
            }
        }

        // ── Song lookup by .audica file name ──────────────────────────────────
        //
        // Playlists store songs by file name stem. Looking each one up used to scan the whole
        // library (twice, reading zipPath from Il2Cpp every time), which made song list entry
        // cost playlistEntries × songs: ~3.9 s for 723 entries × 1985 songs. One pass here builds
        // a file name → song map, and every lookup after that is a dictionary hit.

        private static Dictionary<string, SongList.SongData> songsByFileName;
        private static int indexedSongCount = -1;

        /// <summary>
        /// Snapshot the game's song list and index it by file name. Same snapshot point as the old
        /// internalSongList copy (every song select entry). When two songs share a file name, the
        /// first one wins, matching the old First() lookup.
        /// </summary>
        private static void RebuildSongIndex()
        {
            internalSongList = new List<SongList.SongData>();
            songsByFileName = new Dictionary<string, SongList.SongData>(StringComparer.Ordinal);
            indexedSongCount = -1;

            if (SongList.I == null || SongList.I.songs == null) return;

            var songs = SongList.I.songs;
            int count = songs.Count;
            for (int i = 0; i < count; i++)
            {
                SongList.SongData sd = songs[i];
                internalSongList.Add(sd);
                if (sd == null) continue;

                string zipPath = sd.zipPath;
                if (string.IsNullOrEmpty(zipPath)) continue;

                string fileName = Path.GetFileName(zipPath);
                if (!songsByFileName.ContainsKey(fileName))
                    songsByFileName.Add(fileName, sd);
            }
            indexedSongCount = count;
        }

        /// <summary>
        /// The song whose .audica file is <paramref name="fileName"/>, or null if it isn't in the
        /// library. On a miss, re-indexes once if the library has changed size since the last
        /// index (e.g. a song was downloaded since entering the song list).
        /// </summary>
        public static SongList.SongData FindSongByFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return null;

            if (songsByFileName == null) RebuildSongIndex();

            if (songsByFileName.TryGetValue(fileName, out SongList.SongData found))
                return found;

            int liveCount = SongList.I != null && SongList.I.songs != null ? SongList.I.songs.Count : -1;
            if (liveCount != indexedSongCount)
            {
                RebuildSongIndex();
                if (songsByFileName.TryGetValue(fileName, out found))
                    return found;
            }
            return null;
        }

        public static void SavePlaylistData()
        {
            ioHandler.SavePlaylistData();
        }

        public static void SetPlaylistInitialized(string playlistName)
        {
            ioHandler.UpdatePlaylistData(playlistName);
        }

        public static bool IsPlaylistInitialized(string playlistName)
        {
            return ioHandler.IsPlaylistInitialized(playlistName);
        }

        public static void AddNewPlaylist(Playlist playlist, bool created = false)
        {
            playlist.PopulateSongNames();
            playlists.Add(playlist.name, playlist);
            ioHandler.AddNewPlaylistData(playlist.name, created);
        }

        public static void SetPlaylistToEdit(string playlistName)
        {
            if (!playlists.ContainsKey(playlistName))
            {
                DebugLog.Log("Playlist " + playlistName + " couldn't be found.");
                return;
            }
            playlistToEdit = playlists[playlistName];
        }

        public static void SelectPlaylist(string playlistName)
        {
            if (!playlists.ContainsKey(playlistName))
            {
                DebugLog.Log("Playlist " + playlistName + " couldn't be found.");
                return;
            }
            selectedPlaylist = playlists[playlistName];
        }

        public static void AddSongToPlaylist(string playlistName, string songName)
        {
            if (!playlists.ContainsKey(playlistName))
            {
                DebugLog.Log("Playlist " + playlistName + " couldn't be found.");
                return;
            }
            PlaylistUtil.Popup(songName + " added to " + playlistName);
            playlists[playlistName].AddSong(songName);
            SavePlaylist(playlistName, false);
        }

        public static void RemoveSongFromPlaylist(string songName)
        {
            if (playlistToEdit is null)
            {
                DebugLog.Log("No playlist to edit selected");
                return;
            }
            playlists[playlistToEdit.name].RemoveSong(songName);
        }

        /// <summary>Remove a song (filename stem) from a named playlist and persist the change.</summary>
        public static void RemoveSongFromPlaylistByName(string playlistName, string songStem)
        {
            if (!playlists.ContainsKey(playlistName))
            {
                DebugLog.Log("Playlist " + playlistName + " couldn't be found.");
                return;
            }
            PlaylistUtil.Popup(songStem + " removed from " + playlistName);
            playlists[playlistName].RemoveSong(songStem);
            SavePlaylist(playlistName, false);
        }

        public static void DeletePlaylist()
        {
            if (playlistToEdit is null)
            {
                DebugLog.Log("No playlist to edit selected");
                return;
            }
            PlaylistUtil.Popup(playlistToEdit.name + " deleted");
            ioHandler.DeletePlaylist(playlistToEdit.name, playlistToEdit.filename);
            playlists.Remove(playlistToEdit.name);
            playlistToEdit = null;
        }

        public enum PlaylistState
        {
            None,
            Selecting,
            Creating,
            Adding,
            Editing,
            Endless
        }
    }
}