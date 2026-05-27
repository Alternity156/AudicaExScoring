using System.Collections.Generic;
using MelonLoader;
using TMPro;
using UnityEngine;

namespace ExScoringMod
{
    internal class PlaylistDownloadManager
    {
        public Song[] songList { get; private set; }
        public static bool IsDownloadingMissing { get; set; } = false;
        public static int ActiveDownloads = 0;
        private bool missingSongsFound = false;
        private GunButton backButton = null;
        private TextMeshPro backButtonLabel = null;
        private bool playlistsPopulated = false;
        public static List<string> campaignSongs = new List<string>();
        public static int ActiveSearchers = 0;

        public void DownloadSingleSong(string filename, bool showPopup, GunButton button, TextMeshPro label)
        {
            if (backButton is null && button != null)
            {
                backButton = button;
                backButtonLabel = label;
                backButton.SetInteractable(false);
                backButtonLabel.text = "Loading..";
                backButtonLabel.alpha = .25f;
            }
            if (showPopup) PlaylistUtil.Popup("Downloading..");
            MelonLogger.Log("Downloading " + filename);
            MelonCoroutines.Start(SongDownloader.DoSongWebSearch(filename, OnWebSearchDone, DifficultyFilter.All, false, 1, false));
        }

        public void DownloadSongs(List<string> filenames, bool showPopup, GunButton button, TextMeshPro label)
        {
            if (backButton is null && button != null)
            {
                backButton = button;
                backButtonLabel = label;
                backButton.SetInteractable(false);
                backButtonLabel.text = "Loading..";
                backButtonLabel.alpha = .25f;
            }
            if (showPopup) PlaylistUtil.Popup("Downloading..");
            ActiveSearchers = filenames.Count;
            MelonLogger.Log("Files: " + filenames.Count);
            foreach (string filename in filenames)
            {
                MelonCoroutines.Start(SongDownloader.DoSongWebSearch(filename, OnWebSearchDone, DifficultyFilter.All, false, 1, false));
            }
        }

        public void DownloadMissingSongs()
        {
            if (missingSongsFound) return;
            missingSongsFound = true;
            IsDownloadingMissing = true;
            List<string> songs = new List<string>();
            if (PlaylistManager.playlists != null && PlaylistManager.playlists.Values.Count > 0)
            {
                foreach (Playlist playlist in PlaylistManager.playlists.Values)
                {
                    if (PlaylistManager.IsPlaylistInitialized(playlist.name)) continue;
                    PlaylistManager.SetPlaylistInitialized(playlist.name);
                    foreach (string song in playlist.songs)
                    {
                        if (!SongDownloadTracker.songDictionary.ContainsKey(song + ".audica"))
                        {
                            songs.Add(song + ".audica");
                        }
                    }
                }
            }
            AddMissingCampaignSongs(ref songs);
            if (songs.Count > 0)
            {
                DownloadSongs(songs, false, null, null);
            }
            else
            {
                IsDownloadingMissing = false;
                PopulatePlaylists();
            }
        }

        private void AddMissingCampaignSongs(ref List<string> songs)
        {
            foreach (string s in campaignSongs)
            {
                if (!SongDownloadTracker.songDictionary.ContainsKey(s))
                {
                    if (!songs.Contains(s))
                    {
                        songs.Add(s);
                    }
                }
            }
        }

        public void OnWebSearchDone(string search, APISongList result)
        {
            ActiveSearchers--;
            if (result is null)
            {
                if (ActiveDownloads <= 0 && ActiveSearchers <= 0)
                {
                    CompleteDownloadMissing();
                }
                MelonLogger.Log("search returned no matches.");
                return;
            }
            if (result.song_count == 1)
            {
                Song song = result.songs[0];
                ActiveDownloads++;
                MelonCoroutines.Start(SongDownloader.DownloadSong(song.song_id, song.download_url, OnDownloadComplete));
            }
            else
            {
                MelonLogger.Log("Multiple or no matches found.");
                if (ActiveDownloads <= 0 && ActiveSearchers <= 0)
                {
                    CompleteDownloadMissing();
                }
            }
        }

        private void OnDownloadComplete(string search, bool success)
        {
            ActiveDownloads -= 1;
            if (!success)
            {
                MelonLogger.Log("[WARNING] Download of " + search + " failed");
                if (ActiveDownloads > 0) return;
                if (IsDownloadingMissing)
                {
                    IsDownloadingMissing = false;
                    PlaylistUtil.Popup("Missing playlist songs downloaded.");
                    PopulatePlaylists();
                    ExScoring.ReloadSongList();
                }
            }
            if (ActiveDownloads > 0) return;
            if (!IsDownloadingMissing)
            {
                ExScoring.ReloadSongList();
                return;
            }
            PlaylistManager.SavePlaylistData();
            CompleteDownloadMissing();
        }

        private void CompleteDownloadMissing()
        {
            if (IsDownloadingMissing)
            {
                IsDownloadingMissing = false;
                PlaylistUtil.Popup("Missing playlist songs downloaded.");
                PopulatePlaylists();
                ExScoring.ReloadSongList();
            }
        }

        public void EnableBackButton()
        {
            if (backButton != null)
            {
                backButton.SetInteractable(true);
                backButtonLabel.text = "Back";
                backButtonLabel.alpha = 1f;
                backButton = null;
                backButtonLabel = null;
            }
        }

        private void PopulatePlaylists()
        {
            if (!playlistsPopulated)
            {
                playlistsPopulated = true;
                PlaylistManager.PopulatePlaylistsSongNames();
            }
        }
    }
}