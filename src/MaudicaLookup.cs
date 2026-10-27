using System;
using System.Collections;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// Finds the maudica.com map for an online-play song (the lobby's selected song or a player's
    /// suggestion), shared by MpGameplay (selected-song status and download) and LobbySongList
    /// (the Suggested Songs folder), so a song is only searched once.
    ///
    /// Search order, first hit wins:
    ///  1. ?audica_id= the in-game song id: the exact version of the map, trusted as is. This one
    ///     stays a network request: the maudica listing (and so the catalog) has no audica_id.
    ///  2. The song ref's maudicaId, when the server filled one in.
    ///  3. The title, keeping only a map with the same song id, or the same title + artist
    ///     + mapper (case-insensitive, trimmed).
    /// Steps 2 and 3 are answered from the local maudica catalog (MaudicaCatalog) when it's ready,
    /// and only go to maudica's ?id= / ?search= when there's no catalog yet.
    /// Each network step gives up after StepTimeoutSeconds (a failed request never calls back).
    ///
    /// Results are cached per song id for the session. "Not found" is only kept for
    /// NotFoundRetrySeconds, so a network hiccup doesn't make a song unavailable for good.
    /// Callbacks run on the main thread (from the search coroutines), with null for "not found".
    /// </summary>
    internal static class MaudicaLookup
    {
        private const float StepTimeoutSeconds = 20f;
        private const float NotFoundRetrySeconds = 60f;
        private const int TitleSearchPageSize = 30;

        public enum State { Searching, Found, NotFound }

        private class Entry
        {
            public State state;
            public Song map;
            public float notFoundAt;
            public readonly List<Action<Song>> waiting = new List<Action<Song>>();
        }

        private static readonly Dictionary<string, Entry> cache = new Dictionary<string, Entry>();

        /// <summary>The cached state for a song id, without starting a search.</summary>
        public static State? Peek(string songId, out Song map)
        {
            map = null;
            if (string.IsNullOrEmpty(songId) || !cache.TryGetValue(songId, out Entry entry)) return null;
            if (IsExpired(entry)) return null;
            map = entry.map;
            return entry.state;
        }

        /// <summary>
        /// Finds the map for a song, searching if it isn't cached yet. onDone gets the map, or null
        /// when none was found; it is called right away when the answer is already known.
        /// </summary>
        public static void Find(MpSongRef song, Action<Song> onDone)
        {
            if (song == null || string.IsNullOrEmpty(song.songId))
            {
                onDone?.Invoke(null);
                return;
            }

            if (cache.TryGetValue(song.songId, out Entry entry) && !IsExpired(entry))
            {
                if (entry.state == State.Searching)
                {
                    if (onDone != null) entry.waiting.Add(onDone);
                }
                else
                {
                    onDone?.Invoke(entry.map);
                }
                return;
            }

            entry = new Entry { state = State.Searching };
            if (onDone != null) entry.waiting.Add(onDone);
            cache[song.songId] = entry;

            // Copy the fields: the lobby state object may be replaced while the search runs.
            MpSongRef copy = new MpSongRef
            {
                songId = song.songId,
                difficulty = song.difficulty,
                title = song.title,
                artist = song.artist,
                mapper = song.mapper,
                maudicaId = song.maudicaId
            };
            MelonCoroutines.Start(Search(copy, entry));
        }

        private static bool IsExpired(Entry entry)
        {
            return entry.state == State.NotFound && Time.unscaledTime - entry.notFoundAt > NotFoundRetrySeconds;
        }

        private static IEnumerator Search(MpSongRef song, Entry entry)
        {
            Song found = null;
            string how = null;

            // 1. Exact version by in-game song id.
            APISongList list = null;
            bool done = false;
            StartSafely(() => SongDownloader.DoAudicaIdSearch(song.songId, (q, result) => { list = result; done = true; }), () => done = true);
            float deadline = Time.unscaledTime + StepTimeoutSeconds;
            while (!done && Time.unscaledTime < deadline) yield return null;
            found = First(list);
            if (found != null) how = "audica_id";

            // 2. The server's Maudica id: catalog first.
            if (found == null && song.maudicaId.HasValue && MaudicaCatalog.IsReady)
            {
                CatalogMap map = MaudicaCatalog.GetById(song.maudicaId.Value.ToString());
                if (map != null)
                {
                    found = map.ToSong();
                    how = "maudicaId " + map.id + " (catalog)";
                }
            }

            // 3. Title match from the catalog.
            if (found == null && !string.IsNullOrEmpty(song.title) && MaudicaCatalog.IsReady)
            {
                found = MatchByMetadata(MaudicaCatalog.SearchSongs(song.title), song);
                if (found != null) how = "title (catalog)";
            }

            // No catalog yet: the same two steps through maudica's own lookups.
            // 2. The server's Maudica id.
            if (found == null && song.maudicaId.HasValue && !MaudicaCatalog.IsReady)
            {
                list = null;
                done = false;
                string id = song.maudicaId.Value.ToString();
                StartSafely(() => SongDownloader.DoMaudicaIDSearch(id, (q, result) => { list = result; done = true; }), () => done = true);
                deadline = Time.unscaledTime + StepTimeoutSeconds;
                while (!done && Time.unscaledTime < deadline) yield return null;
                found = First(list);
                if (found != null) how = "maudicaId " + id;
            }

            // 3. Title search, matched on song id or title + artist + mapper.
            if (found == null && !string.IsNullOrEmpty(song.title) && !MaudicaCatalog.IsReady)
            {
                list = null;
                done = false;
                StartSafely(() => SongDownloader.DoSongWebSearch(song.title, (q, result) => { list = result; done = true; },
                    DifficultyFilter.All, false, 1, false, TitleSearchPageSize), () => done = true);
                deadline = Time.unscaledTime + StepTimeoutSeconds;
                while (!done && Time.unscaledTime < deadline) yield return null;
                found = MatchByMetadata(list, song);
                if (found != null) how = "title search";
            }

            entry.map = found;
            entry.state = found != null ? State.Found : State.NotFound;
            if (found == null) entry.notFoundAt = Time.unscaledTime;
            DebugLog.Log($"[MaudicaLookup] {song.songId}: {(found != null ? $"found {found.song_id} by {how}" : "not found")}");

            List<Action<Song>> callbacks = new List<Action<Song>>(entry.waiting);
            entry.waiting.Clear();
            foreach (Action<Song> callback in callbacks)
            {
                try
                {
                    callback(found);
                }
                catch (Exception ex)
                {
                    MelonLogger.Log($"[MaudicaLookup] callback for {song.songId} threw: {ex}");
                }
            }
        }

        private static void StartSafely(Func<IEnumerator> coroutine, Action onFailure)
        {
            try
            {
                MelonCoroutines.Start(coroutine());
            }
            catch (Exception ex)
            {
                DebugLog.Log($"[MaudicaLookup] search failed to start: {ex.Message}");
                onFailure();
            }
        }

        private static Song First(APISongList list)
        {
            if (list == null || list.songs == null) return null;
            foreach (Song s in list.songs)
            {
                if (s != null && !string.IsNullOrEmpty(s.download_url)) return s;
            }
            return null;
        }

        private static Song MatchByMetadata(APISongList list, MpSongRef song)
        {
            if (list == null || list.songs == null) return null;
            return MatchByMetadata(list.songs, song);
        }

        private static Song MatchByMetadata(IEnumerable<Song> songs, MpSongRef song)
        {
            if (songs == null) return null;

            foreach (Song s in songs)
            {
                if (s != null && !string.IsNullOrEmpty(s.song_id) && s.song_id == song.songId) return s;
            }

            foreach (Song s in songs)
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
    }
}