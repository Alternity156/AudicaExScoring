using System;
using System.Collections.Generic;
using System.Globalization;

namespace ExScoringMod
{
    /// <summary>
    /// Options > Replays (a category of the song list's Options, GlobalOptions): the Record Gameplay
    /// toggle, then every saved recording (Replays.List), newest first, under one header per day.
    /// Each row: the recording (song, difficulty, score, time; shoot it to watch it in the spectator
    /// window) and Delete (moves the file to the replays folder's Deleted subfolder, no prompt).
    /// While a replay plays, Stop Replay replaces Open Folder. The page is redrawn when a recording
    /// is saved and when a replay starts, stops or finishes (GlobalOptions.RedrawIfShowing).
    /// Playback controls (pause, restart, stop) are on the spectator window itself.
    /// </summary>
    internal static class ReplaysPage
    {
        public const string CategoryId = "opt_replays";

        public static void Build()
        {
            OptionsMenuClone.AddRow(OptionsMenuClone.CreateHeader(0, "Replays"));

            var record = OptionsMenuClone.CreateToggle(0, "Record Gameplay",
                () => Config.RecordGameplay,
                v => Config.UpdateRecordGameplay(v),
                "Records every song you finish (not failed runs, restarts or practice mode), to watch it again here in the spectator window. About 1 MB per minute of song");

            var side = Replays.Active
                ? OptionsMenuClone.CreateButton(1, "Stop Replay",
                    () => Replays.Stop("stopped from Options"),
                    "Stops the replay that is playing and closes it")
                : OptionsMenuClone.CreateButton(1, "Open Folder",
                    () => Replays.OpenFolder(),
                    "Opens the folder the replays are saved in (UserData/ExScoring_Replays) on the desktop");
            OptionsMenuClone.AddRow(record, side);

            List<Replays.Entry> entries = Replays.List();
            if (entries.Count == 0)
            {
                OptionsMenuClone.AddRow(OptionsMenuClone.CreateHeader(0, Config.RecordGameplay
                    ? "No replays yet: finish a song to record one"
                    : "No replays yet: turn on Record Gameplay and finish a song"));
                return;
            }

            DateTime today = DateTime.Now.Date;
            DateTime? lastDay = null;
            foreach (Replays.Entry entry in entries)
            {
                DateTime when = entry.LocalTime;
                if (lastDay != when.Date)
                {
                    lastDay = when.Date;
                    string day = when.Date == today ? "Today"
                        : when.Date == today.AddDays(-1) ? "Yesterday"
                        : when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    OptionsMenuClone.AddRow(OptionsMenuClone.CreateHeader(0, day));
                }

                Replays.Entry e = entry; // captured per row
                bool playing = Replays.Active && Replays.Current != null && Replays.Current.Path == e.Path;
                string label = Clean(e.Title) + " - " + e.Difficulty + "  " + e.ScoreText + "  " + when.ToString("HH:mm", CultureInfo.InvariantCulture);
                if (playing) label = "> " + label;

                string hover = Clean(e.Title) + (string.IsNullOrEmpty(e.Artist) ? "" : " by " + Clean(e.Artist))
                    + ", " + e.Difficulty + ", " + (e.Scoring == "EX" ? "EX " : "") + e.ScoreText
                    + ", " + e.LengthText + ", " + when.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                    + (playing ? ". Playing now" : ". Shoot to watch it in the spectator window");

                var watch = OptionsMenuClone.CreateButton(0, label, () => Replays.Play(e), hover);
                var delete = OptionsMenuClone.CreateButton(1, "Delete",
                    () =>
                    {
                        Replays.Delete(e);
                        GlobalOptions.RedrawIfShowing(CategoryId);
                    },
                    "Removes this replay from the list (the file goes to UserData/ExScoring_Replays/Deleted)");
                OptionsMenuClone.AddRow(watch, delete);
            }
        }

        /// <summary>No rich-text tags from a song's title in a label.</summary>
        private static string Clean(string text)
        {
            return (text ?? "").Replace("<", "‹").Replace(">", "›");
        }
    }
}