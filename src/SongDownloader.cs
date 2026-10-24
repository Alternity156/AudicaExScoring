using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Media;
using System.Net;
using MelonLoader;
using MelonLoader.TinyJSON;
using OggDecoder;
using UnityEngine;

namespace ExScoringMod
{
    public static class SongDownloader
    {
        internal static string apiUrl = "https://maudica.com/api/maps?per_page=14";
        internal static string apiBaseUrl = "https://maudica.com/api/maps?per_page=";
        internal static string downloadUrlFormat = "https://maudica.com/maps/{0}/download";
        internal static string previewUrlFormat = "https://maudica.com/maps/{0}/preview";
        internal static bool needRefresh = false;
        internal static HashSet<string> downloadedFileNames = new HashSet<string>();
        internal static HashSet<string> failedDownloads = new HashSet<string>();

        private static SoundPlayer player = new SoundPlayer();
        private static string lastPreview = "";

        /// <summary>
        /// Coroutine that searches for songs using the maudica.com web API.
        /// An empty search returns the whole catalog, newest upload first (maudica's default order).
        /// <paramref name="pageSize"/> sets per_page for the paged (non-total) path; 14 by default.
        /// </summary>
        public static IEnumerator DoSongWebSearch(string search, Action<string, APISongList> onSearchComplete,
                                                   DifficultyFilter difficulty, bool sortByDownloads = false,
                                                   int page = 1, bool total = false, int pageSize = 14)
        {
            if (total)
            {
                string webSearch = "https://maudica.com/api/maps?per_page=100&page={0}";

                WWW www = new WWW(string.Format(webSearch, 1));
                yield return www;
                NewAPISongList list = JSON.Load(www.text).Make<NewAPISongList>();

                APISongList result = new APISongList();
                result.song_count = list.count;
                result.page = 1;
                result.pagesize = list.count;
                result.total_pages = 1;
                result.songs = new Song[list.count];

                int numPages = (int)Math.Ceiling((double)list.count / 100);
                int currPage = 1;
                ConvertAPIList(list, result, 0);
                while (currPage <= numPages)
                {
                    currPage++;
                    www = new WWW(string.Format(webSearch, currPage));
                    yield return www;
                    list = JSON.Load(www.text).Make<NewAPISongList>();
                    ConvertAPIList(list, result, 100 * (currPage - 1));
                }
                onSearchComplete(search, result);
            }
            else
            {
                if (pageSize < 1) pageSize = 14;

                string webSearchParam = search == null || search == "" ? "" : "&search=" + WebUtility.UrlEncode(search);
                string webPage = page == 1 ? "" : "&page=" + page.ToString();
                string webDifficulty = difficulty == DifficultyFilter.All ? "" : "&difficulties%5B%5D=" + DifficultyToNewAPIValue(difficulty);
                string webDownloads = sortByDownloads ? "&sort=downloads" : "";
                string concatURL = apiBaseUrl + pageSize.ToString() + webSearchParam + webDifficulty + webPage + webDownloads;

                WWW www = new WWW(concatURL);
                yield return www;

                // A network error or unexpected response hands the caller null (instead of throwing
                // inside the coroutine and never calling back), so callers can show an error state.
                APISongList result = ParseSearchPage(www, page, pageSize);
                onSearchComplete(search, result);
            }
            yield return null;
        }

        /// <summary>
        /// Turns one page of the maudica map listing into an APISongList, or null if the request
        /// failed or the response couldn't be read.
        /// </summary>
        private static APISongList ParseSearchPage(WWW www, int page, int pageSize)
        {
            try
            {
                if (!string.IsNullOrEmpty(www.error))
                {
                    MelonLogger.Log($"[SongDownloader] maudica search failed: {www.error}");
                    return null;
                }

                NewAPISongList list = JSON.Load(www.text).Make<NewAPISongList>();
                if (list == null || list.maps == null)
                    return null;

                APISongList result = new APISongList();
                result.song_count = list.count;
                result.page = page;
                result.pagesize = pageSize;
                result.total_pages = (int)Math.Ceiling(result.song_count / (double)result.pagesize);
                result.songs = new Song[list.maps.Length];
                ConvertAPIList(list, result, 0);
                return result;
            }
            catch (Exception e)
            {
                MelonLogger.Log($"[SongDownloader] maudica search response could not be read: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Look up maudica.com maps by the in-game song id (the .desc songID, e.g.
        /// "weaponizedcelldwellershark_a0508d763c057c198291149233c4d150"), which identifies the exact
        /// version of a map. Hands the caller an APISongList (normally 0 or 1 songs), or null when the
        /// request failed or the response couldn't be read. Note that Song.song_id comes from the
        /// download filename, so it can differ from the in-game id (no hash suffix): a hit here is
        /// the right map whatever its song_id says.
        /// </summary>
        public static IEnumerator DoAudicaIdSearch(string audicaId, Action<string, APISongList> onSearchComplete)
        {
            string concatURL = apiUrl + "&audica_id=" + WebUtility.UrlEncode(audicaId ?? "");

            WWW www = new WWW(concatURL);
            yield return www;

            APISongList result = ParseSearchPage(www, 1, 14);
            onSearchComplete(audicaId, result);
        }

        /// <summary>
        /// Look up a single maudica.com map by its numeric site ID (the "-id N" form of !asr).
        /// Returns an APISongList (0 or 1 songs) through the same callback shape as DoSongWebSearch,
        /// keyed by the id string so the caller can correlate the result.
        /// </summary>
        public static IEnumerator DoMaudicaIDSearch(string id, Action<string, APISongList> onSearchComplete)
        {
            string concatURL = apiUrl + "&id=" + WebUtility.UrlEncode(id);

            WWW www = new WWW(concatURL);
            yield return www;
            NewAPISongList list = JSON.Load(www.text).Make<NewAPISongList>();

            APISongList result = new APISongList();
            int mapCount = (list != null && list.maps != null) ? list.maps.Length : 0; // invalid id → no maps
            result.song_count = mapCount;
            result.page = 1;
            result.pagesize = 14;
            result.total_pages = 1;
            result.songs = new Song[mapCount];
            if (mapCount > 0)
                ConvertAPIList(list, result, 0);

            onSearchComplete(id, result);

            yield return null;
        }
        /// </summary>
        public static IEnumerator DownloadSong(string songID, string downloadUrl, Action<string, bool> onDownloadComplete = null,
                                               Action<string, float> onProgress = null)
        {
            string audicaName = songID + ".audica";
            string path = Path.Combine(ExScoring.mainSongDirectory, audicaName);
            string downloadPath = Path.Combine(ExScoring.downloadsDirectory, audicaName);

            byte[] results = null;
            if (!File.Exists(path) && !File.Exists(downloadPath))
            {
                WWW www = new WWW(downloadUrl);
                while (!www.isDone)
                {
                    onProgress?.Invoke(songID, www.progress);
                    yield return null;
                }
                onProgress?.Invoke(songID, 1f);
                results = www.bytes;
            }
            else
            {
                onProgress?.Invoke(songID, 1f); // already on disk
            }

            if (results != null)
            {
                File.WriteAllBytes(downloadPath, results);
            }

            // Process the downloaded song into the song list
            SongList.SongSourceDir dir = new SongList.SongSourceDir(Application.dataPath, ExScoring.downloadsDirectory);
            string file = downloadPath.Replace('\\', '/');
            bool success = SongList.I.ProcessSingleSong(dir, file, new Il2CppSystem.Collections.Generic.HashSet<string>());
            downloadedFileNames.Add(audicaName);

            if (success)
            {
                needRefresh = true;
            }
            else
            {
                failedDownloads.Add(audicaName);
                if (File.Exists(downloadPath))
                    File.Delete(downloadPath);
            }

            onDownloadComplete?.Invoke(songID, success);
        }

        /// <summary>
        /// Coroutine that plays a song preview for the given preview URL.
        /// If called with the URL of a preview that is already playing, the preview will be stopped.
        /// </summary>
        public static IEnumerator StreamPreviewSong(string url)
        {
            if (lastPreview == url)
            {
                lastPreview = "";
                player.Stop();
            }
            else
            {
                lastPreview = url;

                WWW www = new WWW(url);
                yield return www;
                byte[] results = www.bytes;
                Stream stream = new MemoryStream(results);
                player.Stream = new OggDecodeStream(stream);
                yield return new WaitForSeconds(0.2f);
                player.Play();
                yield return new WaitForSeconds(15f);
            }

            yield return null;
        }

        internal static string DifficultyToNewAPIValue(DifficultyFilter diff)
        {
            switch (diff)
            {
                case DifficultyFilter.Beginner: return "beginner";
                case DifficultyFilter.Standard: return "moderate";
                case DifficultyFilter.Advanced: return "advanced";
                case DifficultyFilter.Expert: return "expert";
                default: return "";
            }
        }

        internal static void ConvertAPIList(NewAPISongList from, APISongList to, int startIdx)
        {
            for (int idx = 0; idx < from.maps.Length; idx++)
            {
                Song newSong = new Song();
                SongV2 song = from.maps[idx];
                newSong.title = song.title;
                newSong.artist = song.artist;
                newSong.author = song.author;

                for (int diffIdx = 0; diffIdx < song.difficulties.Length; diffIdx++)
                {
                    if (song.difficulties[diffIdx] == "beginner")
                        newSong.beginner = true;
                    else if (song.difficulties[diffIdx] == "moderate")
                        newSong.standard = true;
                    else if (song.difficulties[diffIdx] == "advanced")
                        newSong.advanced = true;
                    else if (song.difficulties[diffIdx] == "expert")
                        newSong.expert = true;
                }

                newSong.download_url = string.Format(downloadUrlFormat, song.id);
                newSong.upload_time = song.created_at;
                newSong.update_time = song.updated_at;
                newSong.video_url = song.embed_url;
                newSong.filename = song.filename;
                newSong.song_id = song.filename.Remove(song.filename.Length - 7);
                newSong.preview_url = string.Format(previewUrlFormat, song.id);

                to.songs[startIdx + idx] = newSong;
            }
        }
    }

    // ── Data models for the maudica.com API ──

    [Serializable]
    public class APISongList
    {
        public int total_pages;
        public int song_count;
        public Song[] songs;
        public int pagesize;
        public int page;
    }

    [Serializable]
    internal class NewAPISongList
    {
        public SongV2[] maps = null;
        public bool has_more = false;
        public int count = 0;
    }

    [Serializable]
    internal class SongV2
    {
        public int id = 0;
        public string created_at = null;
        public string updated_at = null;
        public string title = null;
        public string artist = null;
        public string author = null;
        public string[] difficulties = null;
        public string description = null;
        public string embed_url = null;
        public string filename = null;
    }

    [Serializable]
    public class Song
    {
        public string song_id;
        public string author;
        public string title;
        public string artist;
        public bool beginner;
        public bool standard;
        public bool advanced;
        public bool expert;
        public string download_url;
        public string preview_url;
        public string upload_time;
        public string update_time;
        public string video_url;
        public string filename;
    }

    public enum DifficultyFilter
    {
        All,
        Expert,
        Advanced,
        Standard,
        Beginner,
    }
}