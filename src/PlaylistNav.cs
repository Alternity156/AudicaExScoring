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
    /// folder/virtual system keys on songID, so we resolve stem -> SongData via zipPath. Entries
    /// with no locally-installed match are omitted.
    /// </summary>
    internal static class PlaylistNav
    {
        // ── Palette (tunable) ─────────────────────────────────────────────────
        public static readonly Color PlaylistRowColor = new Color(0.30f, 0.30f, 0.30f, 1f); // lighter grey
        public static readonly Color BackColor = new Color(0.20f, 0.24f, 0.34f, 1f); // slate-blue
        public static readonly Color CreateColor = new Color(0.10f, 0.30f, 0.14f, 1f); // dark green
        public static readonly Color DeleteColor = new Color(0.34f, 0.10f, 0.10f, 1f); // dark red
        public static readonly Color ConfirmDeleteColor = new Color(0.65f, 0.10f, 0.10f, 1f); // brighter red
        public static readonly Color MarathonColor = new Color(0.28f, 0.20f, 0.40f, 1f); // dark indigo

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

        /// <summary>Level 2: Back, Marathon, Delete, then the playlist's resolved (installed) songs.</summary>
        public static List<ViewRow> BuildPlaylistContentsView(string playlistName)
        {
            var rows = new List<ViewRow>();

            rows.Add(ViewRow.ActionRow("Back", BackColor, FolderRowManager.NavBack));
            rows.Add(ViewRow.ActionRow("Marathon", MarathonColor,
                () => MarathonSetup.Begin(playlistName), null, MarathonSetup.MarathonActionId));

            bool armed = deleteArmedPlaylist == playlistName;
            rows.Add(ViewRow.ActionRow(
                armed ? "Confirm Delete?" : "Delete Playlist",
                armed ? ConfirmDeleteColor : DeleteColor,
                () => OnDelete(playlistName)));

            foreach (var sd in ResolvePlaylistSongs(playlistName))
                rows.Add(ViewRow.SongRow(sd.songID));

            return rows;
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

        /// <summary>filename stem (no ".audica") -> SongData, for installed, non-hidden songs.</summary>
        private static Dictionary<string, SongList.SongData> BuildStemToSongData()
        {
            var map = new Dictionary<string, SongList.SongData>();
            var songs = SongList.I != null ? SongList.I.songs : null;
            if (songs == null) return map;

            for (int i = 0; i < songs.Count; i++)
            {
                var sd = songs[i];
                if (sd == null || sd.hidden) continue;

                string fn = Path.GetFileName(sd.zipPath);
                if (string.IsNullOrEmpty(fn)) continue;
                if (fn.EndsWith(".audica")) fn = fn.Substring(0, fn.Length - ".audica".Length);

                if (!map.ContainsKey(fn)) map[fn] = sd;
            }
            return map;
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