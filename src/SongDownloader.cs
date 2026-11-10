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

        /// <summary>
        /// Coroutine that downloads a Maudica map into Downloads and adds it to the song list.
        ///
        /// Without <paramref name="expectedSongId"/> (song search, playlists, requests), a file of
        /// that name already on disk counts as the map: nothing is downloaded, and the file on
        /// disk is processed in place.
        ///
        /// With <paramref name="expectedSongId"/> (online lobbies: the exact in-game version the
        /// lobby wants), a file of that name on disk while that version isn't installed is an
        /// older version of the map (a Maudica update keeps the file name). The map is downloaded
        /// anyway. If Maudica's file is the same as the one on disk, nothing changes. Otherwise
        /// the old version is taken out of the song list and moved to the SongBackups folder
        /// (ExScoring.BackUpOtherVersions) before the new one is written. Success then means the
        /// expected version is installed afterwards.
        /// </summary>
        public static IEnumerator DownloadSong(string songID, string downloadUrl, Action<string, bool> onDownloadComplete = null,
                                               Action<string, float> onProgress = null, string expectedSongId = null)
        {
            string audicaName = songID + ".audica";
            string path = Path.Combine(ExScoring.mainSongDirectory, audicaName);
            string downloadPath = Path.Combine(ExScoring.downloadsDirectory, audicaName);
            bool hasExpected = !string.IsNullOrEmpty(expectedSongId);

            if (hasExpected && SongList.I != null && SongList.I.GetSong(expectedSongId) != null)
            {
                onProgress?.Invoke(songID, 1f);
                onDownloadComplete?.Invoke(songID, true); // that version is already installed
                yield break;
            }

            bool onDisk = File.Exists(path) || File.Exists(downloadPath);
            bool update = onDisk && hasExpected;

            byte[] results = null;
            if (!onDisk || update)
            {
                if (update)
                    DebugLog.Log($"[SongDownloader] {audicaName} is on disk but {expectedSongId} isn't installed: downloading the current version to update it");

                WWW www = new WWW(downloadUrl);
                while (!www.isDone)
                {
                    onProgress?.Invoke(songID, www.progress);
                    yield return null;
                }

                if (!string.IsNullOrEmpty(www.error) || www.bytes == null || www.bytes.Length == 0)
                {
                    DebugLog.Log($"[SongDownloader] Download of {audicaName} failed: {(string.IsNullOrEmpty(www.error) ? "empty response" : www.error)}");
                    failedDownloads.Add(audicaName);
                    onDownloadComplete?.Invoke(songID, false);
                    yield break;
                }

                onProgress?.Invoke(songID, 1f);
                results = www.bytes;
            }
            else
            {
                onProgress?.Invoke(songID, 1f); // already on disk
            }

            if (update)
            {
                if (SameAsFile(results, path) || SameAsFile(results, downloadPath))
                {
                    DebugLog.Log($"[SongDownloader] Maudica's {audicaName} is the file already installed, not {expectedSongId}: nothing to update");
                    onDownloadComplete?.Invoke(songID, false);
                    yield break;
                }

                ExScoring.BackUpOtherVersions(audicaName, expectedSongId);
                if (File.Exists(downloadPath))
                {
                    // The old version is in Downloads and couldn't be moved (in use): the new one
                    // has nowhere to go. It's moved to SongBackups on quit; the next try works.
                    DebugLog.Log($"[SongDownloader] Couldn't move the old {audicaName} out of Downloads: not updating it now");
                    onDownloadComplete?.Invoke(songID, false);
                    yield break;
                }
            }

            SongList.SongSourceDir dir;
            string file;
            if (results != null)
            {
                try
                {
                    // A path queued for deletion (an old file that was locked) must not take the new file with it.
                    ExScoring.ForgetPendingDelete(downloadPath);
                    File.WriteAllBytes(downloadPath, results);
                }
                catch (Exception ex)
                {
                    DebugLog.Log($"[SongDownloader] Couldn't write {downloadPath}: {ex.Message}");
                    failedDownloads.Add(audicaName);
                    onDownloadComplete?.Invoke(songID, false);
                    yield break;
                }
                dir = new SongList.SongSourceDir(Application.dataPath, ExScoring.downloadsDirectory);
                file = downloadPath;
            }
            else if (File.Exists(downloadPath))
            {
                dir = new SongList.SongSourceDir(Application.dataPath, ExScoring.downloadsDirectory);
                file = downloadPath;
            }
            else
            {
                // Already in the songs folder (this used to process the missing Downloads path and fail).
                dir = new SongList.SongSourceDir(Application.streamingAssetsPath, ExScoring.mainSongDirectory);
                file = path;
            }

            // Process the song into the song list
            bool processed = SongList.I.ProcessSingleSong(dir, file.Replace('\\', '/'), new Il2CppSystem.Collections.Generic.HashSet<string>());
            downloadedFileNames.Add(audicaName);

            if (processed)
            {
                needRefresh = true;
            }
            else
            {
                failedDownloads.Add(audicaName);
                // Only a file this call just wrote is removed; one that was already on disk stays.
                if (results != null && File.Exists(downloadPath))
                    File.Delete(downloadPath);
            }

            bool success = processed && (!hasExpected || SongList.I.GetSong(expectedSongId) != null);
            if (processed && !success)
                DebugLog.Log($"[SongDownloader] {audicaName} was added, but it isn't {expectedSongId} (Maudica has another version of the map)");

            onDownloadComplete?.Invoke(songID, success);
        }

        /// <summary>True when the file exists and holds exactly these bytes.</summary>
        private static bool SameAsFile(byte[] bytes, string filePath)
        {
            try
            {
                if (bytes == null || !File.Exists(filePath)) return false;
                if (new FileInfo(filePath).Length != bytes.Length) return false;
                byte[] onDisk = File.ReadAllBytes(filePath);
                for (int i = 0; i < bytes.Length; i++)
                    if (bytes[i] != onDisk[i]) return false;
                return true;
            }
            catch (Exception ex)
            {
                DebugLog.Log($"[SongDownloader] Couldn't compare with {filePath}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// The installed song that is another version of <paramref name="songId"/> (same file
        /// name, different cue checksum), or null when songId itself is installed, isn't a
        /// custom song, or no other version is installed. A custom song's ID is its file name
        /// plus "_" and a 32-character checksum.
        /// </summary>
        public static SongList.SongData FindOtherVersion(string songId)
        {
            if (SongList.I == null || string.IsNullOrEmpty(songId) || !SongSearch.IsCustomSong(songId)) return null;
            if (SongList.I.GetSong(songId) != null) return null;

            string baseName = songId.Substring(0, songId.LastIndexOf('_'));
            for (int i = 0; i < SongList.I.songs.Count; i++)
            {
                SongList.SongData sd = SongList.I.songs[i];
                if (sd == null || sd.songID == songId) continue;
                string name = !string.IsNullOrEmpty(sd.zipPath) ? Path.GetFileNameWithoutExtension(sd.zipPath) : sd.originalSongID;
                if (string.Equals(name, baseName, StringComparison.OrdinalIgnoreCase)) return sd;
            }
            return null;
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