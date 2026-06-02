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
            var view = VirtualSongList.CurrentViewSongIDs;
            if (view == null || view.Count == 0) return;

            // Prefer songs that haven't been picked recently.
            var candidates = new List<string>();
            for (int i = 0; i < view.Count; i++)
                if (!recentlySelected.Contains(view[i]))
                    candidates.Add(view[i]);

            // All visible songs were picked recently → reset history and use the whole view.
            if (candidates.Count == 0)
            {
                recentlySelected.Clear();
                for (int i = 0; i < view.Count; i++) candidates.Add(view[i]);
            }

            string songID = candidates[rand.Next(candidates.Count)];

            recentlySelected.Add(songID);
            if (recentlySelected.Count > historySize) recentlySelected.RemoveAt(0);

            ExScoring.selectedSong = songID;
            VirtualSongList.ScrollToAndSelect(songID);
        }

        /// <summary>
        /// Enable the Random Song button only when the open folder has songs in view.
        /// Called by FolderRowManager after every view change.
        /// </summary>
        public static void UpdateButtonState()
        {
            var view = VirtualSongList.CurrentViewSongIDs;
            if (view != null && view.Count > 0) RandomSongButton.Enable();
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