using System.Collections;
using System.Collections.Generic;
using System.IO;
using MelonLoader;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// Tracks which songs are currently loaded in the song list.
    /// Used by SongDownloaderUI to show "Downloaded!" status,
    /// and by FilterPanel/SongSearch for filtering.
    /// </summary>
    internal static class SongDownloadTracker
    {
        public static HashSet<string> songFilenames = new HashSet<string>();
        public static HashSet<string> songIDs = new HashSet<string>();
        public static Dictionary<string, string> songDictionary = new Dictionary<string, string>();

        /// <summary>
        /// Rebuilds all tracking data from SongList.I.
        /// Call after the song list has finished loading/reloading.
        /// </summary>
        public static void UpdateSongData()
        {
            songFilenames.Clear();
            songIDs.Clear();
            songDictionary.Clear();

            for (int i = 0; i < SongList.I.songs.Count; i++)
            {
                string songID = SongList.I.songs[i].songID;
                string filename = Path.GetFileName(SongList.I.songs[i].zipPath);

                songIDs.Add(songID);

                if (!songFilenames.Contains(filename))
                    songFilenames.Add(filename);

                if (!songDictionary.ContainsKey(filename))
                    songDictionary.Add(filename, songID);
            }
        }

        /// <summary>
        /// Starts the song list update process. Call on initialization and after reloads.
        /// Uses SongList.OnSongListLoaded if processOnSongListLoaded is true (i.e. after a full reload).
        /// </summary>
        public static void StartSongListUpdate(bool processOnSongListLoaded = false)
        {
            if (processOnSongListLoaded)
            {
                SongList.OnSongListLoaded.On(new System.Action(() =>
                {
                    MelonCoroutines.Start(PostProcess());
                }));
            }
            else
            {
                MelonCoroutines.Start(PostProcess());
            }
        }

        private static IEnumerator PostProcess()
        {
            UpdateSongData();
            yield return null;

            SongSearch.Search(); // update search results with any new songs
            yield return null;

            KataConfig.I.CreateDebugText("Songs Loaded", new Vector3(0f, -1f, 5f), 5f, null, false, 0.2f);
        }
    }
}