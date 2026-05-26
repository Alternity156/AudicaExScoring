using System.Collections;
using System.Collections.Generic;
using System.IO;
using MelonLoader;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// Tracks which song filenames are currently loaded in the song list.
    /// Used by SongDownloaderUI to show "Downloaded!" for songs that are already present.
    /// </summary>
    internal static class SongDownloadTracker
    {
        public static HashSet<string> songFilenames = new HashSet<string>();

        /// <summary>
        /// Rebuilds the set of loaded song filenames from SongList.I.
        /// Call after the song list has finished loading/reloading.
        /// </summary>
        public static void UpdateSongFilenames()
        {
            songFilenames.Clear();
            for (int i = 0; i < SongList.I.songs.Count; i++)
            {
                string path = Path.GetFileName(SongList.I.songs[i].zipPath);
                if (!songFilenames.Contains(path))
                    songFilenames.Add(path);
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
            UpdateSongFilenames();
            yield return null;
            KataConfig.I.CreateDebugText("Songs Loaded", new Vector3(0f, -1f, 5f), 5f, null, false, 0.2f);
        }
    }
}