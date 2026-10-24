using System;
using System.Collections.Generic;
using System.Text;
using MelonLoader;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// The "Suggested Songs" folder of the song list, shown at the top while the player is in an
    /// online lobby: one row per suggested song (PlayerState.songChoice), titled with everyone
    /// who suggested it, "Destiny (Suggested by Alternity, Bob)".
    ///  - Installed: a normal song row (with that title), so it selects like any song.
    ///  - Not installed but found on Maudica: a downloadable row, like Song Requests / search.
    ///  - Not found: a red row that can't be shot, "(Unavailable to download)", with whatever info
    ///    the suggestion carries.
    /// Rebuilt (FolderRowManager.RefreshList) when the suggestions change and when a Maudica lookup
    /// or download finishes.
    ///
    /// The lobby's selected song gets its own row too, "Title (Selected by host)", while it's
    /// missing and isn't also a suggestion, so it can be downloaded by hand (the host now picks
    /// straight from their song list, so the selection isn't always a suggestion).
    ///
    /// Maudica lookups go through MaudicaLookup (audica_id first). With Auto-Download Missing
    /// Songs on, Tick downloads missing suggestions on its own, one at a time, in join order, and
    /// only after the selected song (MpGameplay downloads that one). Not during songs.
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

        private const long AutoDownloadCheckIntervalMs = 1000;
        private static long nextAutoDownloadCheckMs;

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
            MpSongRef hostSelection = MissingHostSelection(suggestions);
            List<KeyValuePair<MpSongRef, string>> grouped = GroupBySong(suggestions);
            int count = grouped.Count + (hostSelection != null ? 1 : 0);

            int insertAt = 0;
            while (insertAt < rows.Count && rows[insertAt].kind == ViewRowKind.Action) insertAt++;

            List<ViewRow> mine = new List<ViewRow>();
            mine.Add(ViewRow.Header(FolderName, count,
                count == 0 ? "No suggestions yet" : null, FolderColor));

            if (SongFolderManager.openFolder == FolderName)
            {
                if (hostSelection != null)
                    mine.Add(BuildRow(hostSelection, $"{hostSelection.title ?? hostSelection.songId} (Selected by host)"));
                foreach (var entry in grouped)
                    mine.Add(BuildRow(entry.Key, $"{entry.Key.title ?? entry.Key.songId} (Suggested by {entry.Value})"));
            }

            rows.InsertRange(insertAt, mine);
        }

        /// <summary>
        /// One entry per suggested song, in the order it was first suggested (join order), with the
        /// first suggester's song ref and every suggester's nickname ("Alternity, Bob"). Several
        /// players may suggest the same song, possibly at different difficulties.
        /// </summary>
        private static List<KeyValuePair<MpSongRef, string>> GroupBySong(List<KeyValuePair<MpPlayerState, MpSongRef>> suggestions)
        {
            var order = new List<string>();
            var songs = new Dictionary<string, MpSongRef>();
            var names = new Dictionary<string, List<string>>();
            foreach (var entry in suggestions)
            {
                string songId = entry.Value.songId;
                if (!songs.ContainsKey(songId))
                {
                    order.Add(songId);
                    songs[songId] = entry.Value;
                    names[songId] = new List<string>();
                }
                names[songId].Add(entry.Key.nickname);
            }

            var result = new List<KeyValuePair<MpSongRef, string>>();
            foreach (string songId in order)
                result.Add(new KeyValuePair<MpSongRef, string>(songs[songId], string.Join(", ", names[songId].ToArray())));
            return result;
        }

        /// <summary>
        /// The lobby's selected song when this player is missing it and it isn't one of the
        /// suggestions (those already have a row), or null.
        /// </summary>
        private static MpSongRef MissingHostSelection(List<KeyValuePair<MpPlayerState, MpSongRef>> suggestions)
        {
            MpSongRef selected = MpSession.Lobby?.selectedSong;
            if (selected == null || string.IsNullOrEmpty(selected.songId) || IsInstalled(selected.songId)) return null;
            foreach (var entry in suggestions)
            {
                if (entry.Value.songId == selected.songId) return null;
            }
            return selected;
        }

        private static ViewRow BuildRow(MpSongRef song, string label)
        {
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
                    else if (MpGameplay.DownloadingSongId == song.songId) row.subLabel = "Downloading...";
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
            sb.Append('|').Append(MpSession.Lobby?.selectedSong?.songId ?? "-"); // the "Selected by host" row
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

            // audica_id first, then maudicaId, then a title search (MaudicaLookup). The callback can
            // run right away when the answer is already cached.
            bool answeredNow = true;
            MaudicaLookup.Find(song, match =>
            {
                lookup.state = match != null ? LookupState.Found : LookupState.NotFound;
                lookup.map = match;
                if (!answeredNow) Refresh();
            });
            answeredNow = false;
            return lookup;
        }

        // ── Auto-download (Config.AutoDownloadSongs) ──

        /// <summary>
        /// Once per frame from ExScoring.OnUpdate. About once a second, in a lobby with
        /// Auto-Download Missing Songs on and outside songs, starts the next missing suggestion's
        /// download: one at a time, the selected song first (MpGameplay), then suggestions in join
        /// order. A failed download isn't retried on its own (shooting its row retries it).
        /// </summary>
        public static void Tick()
        {
            if (!Config.AutoDownloadSongs || !MpSession.InLobby) return;

            MenuState.State state = ExScoring.menuState;
            if (state == MenuState.State.Launching || state == MenuState.State.Launched) return;

            long now = MpSession.LocalNowMs;
            if (now < nextAutoDownloadCheckMs) return;
            nextAutoDownloadCheckMs = now + AutoDownloadCheckIntervalMs;

            try
            {
                if (SongList.I == null || SongList.OnSongListLoaded == null || !SongList.OnSongListLoaded.mDone) return;
                if (MpGameplay.SelectedSongDownloadPending) return;
                foreach (Lookup existing in lookups.Values)
                {
                    if (existing.downloading) return;
                }

                string selectedId = MpSession.Lobby?.selectedSong?.songId;
                foreach (var entry in GetSuggestions())
                {
                    MpSongRef song = entry.Value;
                    if (song.songId == selectedId || IsInstalled(song.songId)) continue;

                    Lookup lookup = EnsureLookup(song);
                    if (lookup.state != LookupState.Found || lookup.downloading || lookup.failed) continue;

                    DebugLog.Log($"[LobbySongList] auto-downloading {song.songId}, suggested by {entry.Key.nickname}");
                    Download(song.songId);
                    return;
                }
            }
            catch (Exception ex)
            {
                DebugLog.Log("[LobbySongList] auto-download check failed: " + ex.Message);
            }
        }

        private static void Download(string songId)
        {
            if (!lookups.TryGetValue(songId, out Lookup lookup) || lookup.map == null || lookup.downloading) return;
            if (MpGameplay.DownloadingSongId == songId) return; // the lobby's own download has it

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