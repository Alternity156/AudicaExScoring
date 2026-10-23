using System;
using System.Collections.Generic;
using System.Text;
using MelonLoader;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// The "Suggested Songs" folder of the song list, shown at the top while the player is in an
    /// online lobby: one row per player's suggestion (PlayerState.songChoice), titled
    /// "Destiny (Suggested by Alternity)".
    ///  - Installed: a normal song row (with that title), so it selects like any song.
    ///  - Not installed but found on Maudica: a downloadable row, like Song Requests / search.
    ///  - Not found: a red row that can't be shot, "(Unavailable to download)", with whatever info
    ///    the suggestion carries.
    /// Rebuilt (FolderRowManager.RefreshList) when the suggestions change and when a Maudica lookup
    /// or download finishes.
    /// </summary>
    internal static class LobbySongList
    {
        public const string FolderName = "Suggested Songs";

        // Customisable on Options > Colors (ColorSettings).
        private static Color FolderColor => ColorSettings.LobbyFolder.Value;
        private static Color DownloadableColor => ColorSettings.LobbyDownloadable.Value;
        private static Color UnavailableColor => ColorSettings.LobbyUnavailable.Value;
        private static Color LookingColor => ColorSettings.LobbyLooking.Value;

        private enum LookupState { Searching, Found, NotFound }

        private class Lookup
        {
            public LookupState state;
            public Song map;            // when Found
            public bool downloading;
            public int percent = -1;
            public bool failed;
            public string label;        // the row's title, for live progress updates
        }

        // Keyed by the suggestion's songId.
        private static readonly Dictionary<string, Lookup> lookups = new Dictionary<string, Lookup>();

        private static bool initialized;
        private static string lastSignature;

        public static void Init()
        {
            if (initialized) return;
            initialized = true;

            MpSession.OnLobbyState += _ => RefreshIfChanged();
            MpSession.OnLeftLobby += _ => RefreshIfChanged();
            MpSession.OnDisconnected += _ => RefreshIfChanged();
        }

        // ── Song list integration (FolderRowManager.BuildRootView) ──

        /// <summary>
        /// Adds the folder (and its rows when open) to a root view, after the pinned action rows
        /// at the top (Options, Playlists). Does nothing outside a lobby.
        /// </summary>
        public static void InsertInto(List<ViewRow> rows)
        {
            if (!MpSession.InLobby || rows == null) return;

            List<KeyValuePair<MpPlayerState, MpSongRef>> suggestions = GetSuggestions();

            int insertAt = 0;
            while (insertAt < rows.Count && rows[insertAt].kind == ViewRowKind.Action) insertAt++;

            List<ViewRow> mine = new List<ViewRow>();
            mine.Add(ViewRow.Header(FolderName, suggestions.Count,
                suggestions.Count == 0 ? "No suggestions yet" : null, FolderColor));

            if (SongFolderManager.openFolder == FolderName)
            {
                foreach (var entry in suggestions) mine.Add(BuildRow(entry.Key, entry.Value));
            }

            rows.InsertRange(insertAt, mine);
        }

        private static ViewRow BuildRow(MpPlayerState player, MpSongRef song)
        {
            string label = $"{song.title ?? song.songId} (Suggested by {player.nickname})";
            string info = string.IsNullOrEmpty(song.mapper) ? (song.artist ?? "") : $"{song.artist} ({song.mapper})";

            if (IsInstalled(song.songId)) return ViewRow.SongRow(song.songId, label);

            Lookup lookup = EnsureLookup(song);
            switch (lookup.state)
            {
                case LookupState.Found:
                    lookup.label = label;
                    var row = ViewRow.DownloadableSongRow(lookup.map.song_id, label, song.artist, song.mapper,
                        lookup.map.download_url, DownloadableColor, () => Download(song.songId));
                    if (lookup.downloading) row.subLabel = $"Downloading... {Math.Max(0, lookup.percent)}%";
                    else if (lookup.failed) row.subLabel = "Download failed - shoot to retry";
                    return row;

                case LookupState.NotFound:
                    return ViewRow.DisabledRow(label + " (Unavailable to download)", UnavailableColor,
                        $"{info} [{song.difficulty}]".Trim());

                default:
                    return ViewRow.DisabledRow(label, LookingColor, "Looking for it on Maudica...");
            }
        }

        // ── Suggestions ──

        /// <summary>Every player's suggestion, in join order.</summary>
        public static List<KeyValuePair<MpPlayerState, MpSongRef>> GetSuggestions()
        {
            var result = new List<KeyValuePair<MpPlayerState, MpSongRef>>();
            MpLobbyState lobby = MpSession.Lobby;
            if (lobby == null || lobby.players == null) return result;

            foreach (MpPlayerState p in lobby.players)
            {
                if (p != null && p.songChoice != null && !string.IsNullOrEmpty(p.songChoice.songId))
                    result.Add(new KeyValuePair<MpPlayerState, MpSongRef>(p, p.songChoice));
            }
            return result;
        }

        /// <summary>The first player who suggested this song (any difficulty), or null.</summary>
        public static MpPlayerState FindSuggester(string songId, bool excludeMe = false)
        {
            foreach (var entry in GetSuggestions())
            {
                if (entry.Value.songId != songId) continue;
                if (excludeMe && entry.Key.userId == MpSession.MyUserId) continue;
                return entry.Key;
            }
            return null;
        }

        private static void RefreshIfChanged()
        {
            string signature = Signature();
            if (signature == lastSignature) return;
            lastSignature = signature;
            Refresh();
        }

        private static string Signature()
        {
            if (!MpSession.InLobby) return "";
            StringBuilder sb = new StringBuilder("lobby");
            foreach (var entry in GetSuggestions())
            {
                sb.Append('|').Append(entry.Key.userId).Append(':').Append(entry.Key.nickname)
                  .Append(':').Append(entry.Value.songId).Append(':').Append(entry.Value.difficulty);
            }
            return sb.ToString();
        }

        private static void Refresh()
        {
            if (ExScoring.menuState != MenuState.State.SongPage) return;
            try
            {
                FolderRowManager.RefreshList();
            }
            catch (Exception ex)
            {
                DebugLog.Log("[LobbySongList] refresh failed: " + ex.Message);
            }
        }

        private static bool IsInstalled(string songId)
        {
            return SongList.I != null && !string.IsNullOrEmpty(songId) && SongList.I.GetSong(songId) != null;
        }

        // ── Maudica lookup and download ──

        private static Lookup EnsureLookup(MpSongRef song)
        {
            if (lookups.TryGetValue(song.songId, out Lookup existing)) return existing;

            Lookup lookup = new Lookup { state = LookupState.Searching };
            lookups[song.songId] = lookup;

            string songId = song.songId;
            Action<string, APISongList> onDone = (query, list) =>
            {
                Song match = PickMatch(list, song);
                lookup.state = match != null ? LookupState.Found : LookupState.NotFound;
                lookup.map = match;
                DebugLog.Log($"[LobbySongList] Maudica lookup for {songId}: {(match != null ? "found " + match.song_id : "not found")}");
                Refresh();
            };

            try
            {
                if (song.maudicaId.HasValue)
                    MelonCoroutines.Start(SongDownloader.DoMaudicaIDSearch(song.maudicaId.Value.ToString(), onDone));
                else
                    MelonCoroutines.Start(SongDownloader.DoSongWebSearch(song.title ?? songId, onDone, DifficultyFilter.All, false, 1, false, 30));
            }
            catch (Exception ex)
            {
                DebugLog.Log($"[LobbySongList] Maudica lookup for {songId} failed: {ex.Message}");
                lookup.state = LookupState.NotFound;
            }
            return lookup;
        }

        /// <summary>The Maudica map that is this song: same song id, or same title + artist + mapper.</summary>
        private static Song PickMatch(APISongList list, MpSongRef song)
        {
            if (list == null || list.songs == null) return null;

            foreach (Song s in list.songs)
            {
                if (s != null && !string.IsNullOrEmpty(s.song_id) && s.song_id == song.songId) return s;
            }

            if (string.IsNullOrEmpty(song.title)) return null;
            foreach (Song s in list.songs)
            {
                if (s == null) continue;
                if (Same(s.title, song.title) && Same(s.artist, song.artist) && Same(s.author, song.mapper)) return s;
            }
            return null;
        }

        private static bool Same(string a, string b)
        {
            return string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static void Download(string songId)
        {
            if (!lookups.TryGetValue(songId, out Lookup lookup) || lookup.map == null || lookup.downloading) return;

            Song map = lookup.map;
            lookup.downloading = true;
            lookup.failed = false;
            lookup.percent = 0;
            VirtualSongList.UpdateActionRowText(map.song_id, lookup.label, "Downloading... 0%");

            MelonCoroutines.Start(SongDownloader.DownloadSong(map.song_id, map.download_url,
                (id, ok) =>
                {
                    lookup.downloading = false;
                    lookup.failed = !ok;
                    DebugLog.Log($"[LobbySongList] download of {map.song_id} {(ok ? "done" : "failed")}");
                    Refresh();
                },
                (id, progress) =>
                {
                    int percent = Mathf.Clamp((int)(progress * 100f), 0, 100);
                    if (percent == lookup.percent) return;
                    lookup.percent = percent;
                    VirtualSongList.UpdateActionRowText(map.song_id, lookup.label, $"Downloading... {percent}%");
                }));
        }
    }
}