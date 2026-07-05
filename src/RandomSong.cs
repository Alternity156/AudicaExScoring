using System.Collections.Generic;

namespace ExScoringMod
{
    /// <summary>
    /// PHASE 3 — Random Song now operates on the songs currently in view (the open folder),
    /// reading them from VirtualSongList and selecting via VirtualSongList.ScrollToAndSelect.
    /// The old mSongButtons scan + absolute SnapTo path is gone.
    /// </summary>
    internal static class RandomSong
    {
        private const int historySize = 10;
        private static readonly List<string> recentlySelected = new List<string>();
        private static readonly System.Random rand = new System.Random();

        public static void SelectRandomSong()
        {
            bool allSongs = Config.RandomSongScope == 1;
            var pool = allSongs ? GetAllSongIDs() : VirtualSongList.CurrentViewSongIDs;
            if (pool == null || pool.Count == 0) return;

            // Prefer songs that haven't been picked recently.
            var candidates = new List<string>();
            for (int i = 0; i < pool.Count; i++)
                if (!recentlySelected.Contains(pool[i]))
                    candidates.Add(pool[i]);

            // All eligible songs were picked recently → reset history and use the whole pool.
            if (candidates.Count == 0)
            {
                recentlySelected.Clear();
                for (int i = 0; i < pool.Count; i++) candidates.Add(pool[i]);
            }

            string songID = candidates[rand.Next(candidates.Count)];

            recentlySelected.Add(songID);
            if (recentlySelected.Count > historySize) recentlySelected.RemoveAt(0);

            ExScoring.selectedSong = songID;

            if (allSongs)
                // Song may live in a folder that isn't currently open — open it and scroll to
                // the song, same as every other cross-folder selection (favorites, search, etc.).
                FolderRowManager.RevealAndSelect(songID);
            else
                VirtualSongList.ScrollToAndSelect(songID);
        }

        /// <summary>
        /// Every loaded songID, unfiltered. Used as the Random Song pool in "All Songs" mode.
        /// </summary>
        private static List<string> GetAllSongIDs()
        {
            var ids = new List<string>();
            if (SongList.I == null || SongList.I.songs == null) return ids;

            for (int i = 0; i < SongList.I.songs.Count; i++)
            {
                var song = SongList.I.songs[i];
                if (song == null) continue;
                ids.Add(song.songID);
            }
            return ids;
        }

        /// <summary>
        /// Enable the Random Song button when there are songs to pick from: any loaded song in
        /// "All Songs" mode, or the open folder's visible songs in "Folder Songs" mode.
        /// Called by FolderRowManager after every view change.
        /// </summary>
        public static void UpdateButtonState()
        {
            bool hasSongs;
            if (Config.RandomSongScope == 1)
                hasSongs = SongList.I != null && SongList.I.songs != null && SongList.I.songs.Count > 0;
            else
            {
                var view = VirtualSongList.CurrentViewSongIDs;
                hasSongs = view != null && view.Count > 0;
            }

            if (hasSongs) RandomSongButton.Enable();
            else RandomSongButton.Disable();
        }

        /// <summary>
        /// Vestigial. In folder mode the game's full id list is suppressed, so this no longer
        /// feeds Random Song (selection comes from the in-view songs). Kept so the existing
        /// GetSongIDs hook call still compiles; button state is owned by UpdateButtonState.
        /// </summary>
        public static void UpdateAvailableSongs(Il2CppSystem.Collections.Generic.List<string> songs, bool extras) { }
    }
}