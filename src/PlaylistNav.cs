using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// Builds the playlist-navigation views (Level 1: the list of playlists, Level 2: one
    /// playlist's songs) as ViewRow lists for VirtualSongList. Navigation level/scroll state and
    /// the drill-in/back transitions live in FolderRowManager; this class only produces rows.
    ///
    /// Playlist entries are stored as filename stems (e.g. "mysong" for "mysong.audica"); the
    /// folder/virtual system keys on songID, so we resolve stem -> SongData via zipPath
    /// (case-insensitive). Entries that aren't installed are shown as maudica lookups through
    /// PlaylistDownloadManager (searching / downloadable / not on Maudica), with a "Download all"
    /// row above the songs. Installed-but-hidden songs are left out, as before.
    /// </summary>
    internal static class PlaylistNav
    {
        // ── Palette (tunable) ─────────────────────────────────────────────────
        // Customisable on Options > Colors (ColorSettings); defaults are the previous hardcoded colours.
        public static Color PlaylistRowColor => ColorSettings.RowPlaylistEntry.Value;   // lighter grey
        public static Color PlaylistTitleColor => ColorSettings.RowPlaylistTitle.Value; // grey
        public static Color BackColor => ColorSettings.RowBack.Value;                  // slate-blue
        public static Color CreateColor => ColorSettings.RowCreate.Value;              // dark green
        public static Color DeleteColor => ColorSettings.RowDelete.Value;              // dark red
        public static Color ConfirmDeleteColor => ColorSettings.RowConfirmDelete.Value; // brighter red
        public static Color MarathonColor => ColorSettings.RowMarathon.Value;          // dark indigo

        // Two-tap delete: armed playlist shows "Confirm Delete?" until shot again or navigated away.
        private static string deleteArmedPlaylist = null;

        // ── Create-playlist (in-row name entry) ───────────────────────────────
        public const string CreateActionId = "create";
        public static bool Creating { get; private set; }
        private static string creatingName = "";

        /// <summary>Reset transient per-view state (delete arming, in-progress create). On nav.</summary>
        public static void ClearTransient()
        {
            deleteArmedPlaylist = null;
            CancelCreate();
        }

        /// <summary>Level 1: Back, Create, then one row per playlist (drills into its contents).</summary>
        public static List<ViewRow> BuildPlaylistListView()
        {
            var rows = new List<ViewRow>();

            rows.Add(ViewRow.ActionRow("Back", BackColor, FolderRowManager.NavBack));
            rows.Add(CreateRow());

            if (PlaylistManager.playlists != null)
            {
                foreach (var kv in PlaylistManager.playlists)
                {
                    string name = kv.Key;
                    int count = kv.Value.songs != null ? kv.Value.songs.Count : 0;
                    rows.Add(ViewRow.ActionRow(name, PlaylistRowColor,
                        () => FolderRowManager.EnterPlaylistContents(name),
                        $"{count} song{(count != 1 ? "s" : "")}"));
                }
            }

            return rows;
        }

        /// <summary>
        /// Level 2: the playlist's name (label row), Back, Marathon, Delete, "Download all" (when
        /// something missing is on Maudica), then the playlist's entries in order: installed songs
        /// as song rows, missing ones as maudica rows (searching / downloadable / not on Maudica).
        /// </summary>
        public static List<ViewRow> BuildPlaylistContentsView(string playlistName)
        {
            var rows = new List<ViewRow>();

            Playlist pl = GetPlaylist(playlistName);
            var visible = BuildStemToSongData();
            var installed = BuildInstalledStems();
            var missing = pl != null && pl.songs != null
                ? GetMissingStems(pl, visible, installed)
                : new List<string>();

            rows.Add(ViewRow.LabelRow(playlistName ?? "", PlaylistTitleColor, TitleSubLabel(pl, missing.Count)));
            rows.Add(ViewRow.ActionRow("Back", BackColor, FolderRowManager.NavBack));
            rows.Add(ViewRow.ActionRow("Marathon", MarathonColor,
                () => MarathonSetup.Begin(playlistName), null, MarathonSetup.MarathonActionId));

            bool armed = deleteArmedPlaylist == playlistName;
            rows.Add(ViewRow.ActionRow(
                armed ? "Confirm Delete?" : "Delete Playlist",
                armed ? ConfirmDeleteColor : DeleteColor,
                () => OnDelete(playlistName)));

            if (pl == null || pl.songs == null) return rows;

            PlaylistDownloadManager.EnsureLookups(missing);

            ViewRow? downloadAll = PlaylistDownloadManager.BuildDownloadAllRow(playlistName, missing);
            if (downloadAll.HasValue) rows.Add(downloadAll.Value);

            var shown = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (string stem in pl.songs)
            {
                if (string.IsNullOrEmpty(stem) || !shown.Add(stem)) continue;

                if (visible.TryGetValue(stem, out SongList.SongData sd))
                    rows.Add(ViewRow.SongRow(sd.songID));
                else if (!installed.Contains(stem))
                    rows.Add(PlaylistDownloadManager.BuildMissingRow(stem));
                // else: installed but hidden — omitted.
            }

            return rows;
        }

        /// <summary>Second line of the title row: "12 songs · 3 missing" (or just "12 songs").</summary>
        private static string TitleSubLabel(Playlist pl, int missingCount)
        {
            int count = pl != null && pl.songs != null ? pl.songs.Count : 0;
            string text = $"{count} song{(count != 1 ? "s" : "")}";
            if (missingCount > 0) text += $" \u00B7 {missingCount} missing";
            return text;
        }

        /// <summary>The playlist's entries that aren't installed at all, in playlist order, deduped.</summary>
        public static List<string> GetMissingStems(string playlistName)
        {
            Playlist pl = GetPlaylist(playlistName);
            if (pl == null || pl.songs == null) return new List<string>();
            return GetMissingStems(pl, BuildStemToSongData(), BuildInstalledStems());
        }

        private static List<string> GetMissingStems(Playlist pl, Dictionary<string, SongList.SongData> visible,
                                                    HashSet<string> installed)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            foreach (string stem in pl.songs)
            {
                if (string.IsNullOrEmpty(stem) || !seen.Add(stem)) continue;
                if (visible.ContainsKey(stem) || installed.Contains(stem)) continue;
                result.Add(stem);
            }
            return result;
        }

        private static Playlist GetPlaylist(string playlistName)
        {
            if (playlistName == null || PlaylistManager.playlists == null) return null;
            return PlaylistManager.playlists.TryGetValue(playlistName, out Playlist pl) ? pl : null;
        }

        /// <summary>The playlist's songs that are installed locally, in playlist order.</summary>
        public static List<SongList.SongData> ResolvePlaylistSongs(string playlistName)
        {
            var result = new List<SongList.SongData>();
            if (playlistName == null ||
                PlaylistManager.playlists == null ||
                !PlaylistManager.playlists.ContainsKey(playlistName))
                return result;

            Playlist pl = PlaylistManager.playlists[playlistName];
            if (pl.songs == null) return result;

            var map = BuildStemToSongData();
            foreach (string stem in pl.songs)
            {
                if (stem != null && map.TryGetValue(stem, out SongList.SongData sd))
                    result.Add(sd);
                // else: not installed locally — omitted.
            }
            return result;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// filename stem (no ".audica") -> SongData, for installed, non-hidden songs.
        /// Case-insensitive: maudica's filename casing can differ from the playlist's stem.
        /// </summary>
        private static Dictionary<string, SongList.SongData> BuildStemToSongData()
        {
            var map = new Dictionary<string, SongList.SongData>(System.StringComparer.OrdinalIgnoreCase);
            var songs = SongList.I != null ? SongList.I.songs : null;
            if (songs == null) return map;

            for (int i = 0; i < songs.Count; i++)
            {
                var sd = songs[i];
                if (sd == null || sd.hidden) continue;

                string fn = StemOf(sd);
                if (string.IsNullOrEmpty(fn)) continue;

                if (!map.ContainsKey(fn)) map[fn] = sd;
            }
            return map;
        }

        /// <summary>Stems of every installed song, hidden ones included (case-insensitive).</summary>
        private static HashSet<string> BuildInstalledStems()
        {
            var set = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            var songs = SongList.I != null ? SongList.I.songs : null;
            if (songs == null) return set;

            for (int i = 0; i < songs.Count; i++)
            {
                var sd = songs[i];
                if (sd == null) continue;
                string fn = StemOf(sd);
                if (!string.IsNullOrEmpty(fn)) set.Add(fn);
            }
            return set;
        }

        private static string StemOf(SongList.SongData sd)
        {
            string fn = Path.GetFileName(sd.zipPath);
            if (string.IsNullOrEmpty(fn)) return null;
            if (fn.EndsWith(".audica", System.StringComparison.OrdinalIgnoreCase))
                fn = fn.Substring(0, fn.Length - ".audica".Length);
            return fn;
        }

        private static void OnDelete(string playlistName)
        {
            // First shot arms the confirm; second shot (row now "Confirm Delete?") deletes.
            if (deleteArmedPlaylist != playlistName)
            {
                deleteArmedPlaylist = playlistName;
                PlaylistUtil.Popup("Shoot again to confirm delete");
                FolderRowManager.RefreshList(); // re-render the Delete row as "Confirm Delete?"
                return;
            }

            deleteArmedPlaylist = null;
            PlaylistManager.SetPlaylistToEdit(playlistName);
            PlaylistManager.DeletePlaylist();   // pops "<name> deleted" + removes from disk/dict
            FolderRowManager.NavBack();          // back to the (now shorter) playlist list
        }

        /// <summary>Transient picker: Back, Create (dummy), then one row per playlist (pick = add).</summary>
        public static List<ViewRow> BuildAddPickerView()
        {
            var rows = new List<ViewRow>();

            rows.Add(ViewRow.ActionRow("Back", BackColor, FolderRowManager.AddPickerCancel));
            rows.Add(CreateRow());

            if (PlaylistManager.playlists != null)
            {
                foreach (var kv in PlaylistManager.playlists)
                {
                    string name = kv.Key;
                    int count = kv.Value.songs != null ? kv.Value.songs.Count : 0;
                    rows.Add(ViewRow.ActionRow(name, PlaylistRowColor,
                        () => FolderRowManager.AddPickerPick(name),
                        $"{count} song{(count != 1 ? "s" : "")}"));
                }
            }

            return rows;
        }

        // ── Create playlist ───────────────────────────────────────────────────

        private static ViewRow CreateRow()
            => ViewRow.ActionRow(Creating ? CreateRowLabel() : "Create Playlist",
                                 CreateColor, OnCreateShot, null, CreateActionId);

        private static string CreateRowLabel()
            => "Create: " + (string.IsNullOrEmpty(creatingName) ? "_" : creatingName);

        private static void OnCreateShot()
        {
            if (Creating) return;   // already typing — the keyboard drives input
            Creating = true;
            creatingName = "";
            SearchKeyboard.ShowGeneric();
            VirtualSongList.UpdateActionRowText(CreateActionId, CreateRowLabel(), null);
        }

        public static void AppendCreateName(string s)
        {
            if (!Creating) return;
            creatingName += s;
            VirtualSongList.UpdateActionRowText(CreateActionId, CreateRowLabel(), null);
        }

        public static void SetCreateName(string s)
        {
            if (!Creating) return;
            creatingName = s ?? "";
            VirtualSongList.UpdateActionRowText(CreateActionId, CreateRowLabel(), null);
        }

        public static void BackspaceCreateName()
        {
            if (!Creating || creatingName.Length == 0) return;
            creatingName = creatingName.Substring(0, creatingName.Length - 1);
            VirtualSongList.UpdateActionRowText(CreateActionId, CreateRowLabel(), null);
        }

        /// <summary>Keyboard "done": validate + create, then hand off to the right post-create action.</summary>
        public static void FinishCreate()
        {
            if (!Creating) return;
            SearchKeyboard.Hide();
            Creating = false;

            string name = creatingName.Trim();
            creatingName = "";

            if (name.Length == 0)
            {
                PlaylistUtil.Popup("Playlist name can't be empty");
                FolderRowManager.RefreshList(); // reset the Create row label
                return;
            }
            if (PlaylistManager.playlists != null && PlaylistManager.playlists.ContainsKey(name))
            {
                PlaylistUtil.Popup(name + " already exists");
                FolderRowManager.RefreshList();
                return;
            }

            Playlist pl = new Playlist(name, new List<string>());
            pl.filename = name + ".playlist";
            PlaylistManager.AddNewPlaylist(pl, true);
            PlaylistManager.SavePlaylist(pl.name, false);
            PlaylistManager.SavePlaylistData();

            FolderRowManager.OnPlaylistCreated(name);
        }

        private static void CancelCreate()
        {
            if (!Creating) return;
            Creating = false;
            creatingName = "";
            SearchKeyboard.Hide();
        }
    }
}