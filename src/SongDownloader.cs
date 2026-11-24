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
        /// songID is Maudica's file name without ".audica": the map's real name, which its in-game
        /// ID is built from. A file on disk counts as this map when its real name is exactly songID
        /// (case included; RealFileName) and, when Maudica's catalog knows the map, its title and
        /// artist match. A file with the same name that is another song (NewJeans' and Joji's
        /// "Attention" by the same mapper) is never treated as this map: the download is saved as
        /// "songID~2.audica" with its real name stored inside, so it still loads as songID_hash.
        ///
        /// Without <paramref name="expectedSongId"/> (song search, playlists, requests), a file of
        /// this map already on disk counts as the map: nothing is downloaded, and that file is
        /// processed in place. A download that is byte for byte a file already installed under the
        /// real name isn't written again either.
        ///
        /// With <paramref name="expectedSongId"/> (online lobbies: the exact in-game version the
        /// lobby wants), a file of this map on disk while that version isn't installed is an
        /// older version of the map (a Maudica update keeps the file name). The map is downloaded
        /// anyway. If Maudica's file is the same as one on disk, nothing changes. Otherwise the
        /// old versions (only files of this map) are taken out of the song list and moved to the
        /// SongBackups folder (ExScoring.BackUpOtherVersions) before the new one is written.
        /// Success then means the expected version is installed afterwards.
        /// </summary>
        public static IEnumerator DownloadSong(string songID, string downloadUrl, Action<string, bool> onDownloadComplete = null,
                                               Action<string, float> onProgress = null, string expectedSongId = null)
        {
            string audicaName = songID + ".audica";
            bool hasExpected = !string.IsNullOrEmpty(expectedSongId);

            if (hasExpected && SongList.I != null && SongList.I.GetSong(expectedSongId) != null)
            {
                onProgress?.Invoke(songID, 1f);
                onDownloadComplete?.Invoke(songID, true); // that version is already installed
                yield break;
            }

            // Files on disk that are this map: the same real name, and the same title and artist
            // as Maudica's entry when both are known.
            RealFileName.MapMeta maudica = RealFileName.MetaOf(MaudicaCatalog.GetByDownloadUrl(downloadUrl));
            List<string> sameName = RealFileName.FindFilesWithRealName(songID);
            List<string> sameMap = new List<string>();
            foreach (string f in sameName)
            {
                if (RealFileName.SameMap(maudica, RealFileName.MetaOfFile(f)))
                    sameMap.Add(f);
                else
                    DebugLog.Log($"[SongDownloader] {f} has the file name of {audicaName} but is another song: not treated as this map");
            }

            bool onDisk = sameMap.Count > 0;
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

            string resultsMd5 = results != null ? RealFileName.Md5Hex(results) : null;

            if (update)
            {
                foreach (string f in sameMap)
                {
                    if (RealFileName.SameContent(results, resultsMd5, f))
                    {
                        DebugLog.Log($"[SongDownloader] Maudica's {audicaName} is the file already installed ({f}), not {expectedSongId}: nothing to update");
                        onDownloadComplete?.Invoke(songID, false);
                        yield break;
                    }
                }

                ExScoring.BackUpOtherVersions(sameMap, expectedSongId);
            }

            // A fresh download that is exactly a file already installed under this name (the map,
            // with title or artist edited on Maudica since): that file is used, not a second copy.
            string existing = null;
            if (results != null && !update)
            {
                foreach (string f in sameName)
                {
                    if (RealFileName.SameContent(results, resultsMd5, f))
                    {
                        DebugLog.Log($"[SongDownloader] Maudica's {audicaName} is already installed as {f}: using that file");
                        existing = f;
                        results = null;
                        break;
                    }
                }
            }

            SongList.SongSourceDir dir;
            string file;
            if (results != null)
            {
                // The real name when no file has it (any case), else name~2, ~3...
                string diskName = RealFileName.ChooseDiskName(songID);
                if (diskName == null)
                {
                    DebugLog.Log($"[SongDownloader] No free file name for {audicaName}: not saving it");
                    failedDownloads.Add(audicaName);
                    onDownloadComplete?.Invoke(songID, false);
                    yield break;
                }

                string target = Path.Combine(ExScoring.downloadsDirectory, diskName);
                bool tagged = !string.Equals(Path.GetFileNameWithoutExtension(diskName), songID, StringComparison.Ordinal);
                try
                {
                    byte[] bytes = tagged ? RealFileName.AddTag(results, songID, resultsMd5) : results;
                    // A path queued for deletion (an old file that was locked) must not take the new file with it.
                    ExScoring.ForgetPendingDelete(target);
                    File.WriteAllBytes(target, bytes);
                    if (tagged)
                        DebugLog.Log($"[SongDownloader] Another song already uses the name {audicaName}: saved as {diskName}, it still loads as {songID}");
                }
                catch (Exception ex)
                {
                    DebugLog.Log($"[SongDownloader] Couldn't write {target}: {ex.Message}");
                    failedDownloads.Add(audicaName);
                    onDownloadComplete?.Invoke(songID, false);
                    yield break;
                }
                dir = new SongList.SongSourceDir(Application.dataPath, ExScoring.downloadsDirectory);
                file = target;
            }
            else
            {
                // Already on disk (Downloads first, then the songs folder, then its subfolders).
                file = existing ?? sameMap[0];
                dir = RealFileName.SourceDirFor(file);
            }

            // Process the song into the song list
            bool processed = SongList.I.ProcessSingleSong(dir, file.Replace('\\', '/'), new Il2CppSystem.Collections.Generic.HashSet<string>());
            downloadedFileNames.Add(Path.GetFileName(file));

            if (processed)
            {
                needRefresh = true;
            }
            else
            {
                failedDownloads.Add(audicaName);
                // Only a file this call just wrote is removed; one that was already on disk stays.
                if (results != null && File.Exists(file))
                    File.Delete(file);
            }

            bool success = processed && (!hasExpected || SongList.I.GetSong(expectedSongId) != null);
            if (processed && !success)
                DebugLog.Log($"[SongDownloader] {audicaName} was added, but it isn't {expectedSongId} (Maudica has another version of the map)");

            onDownloadComplete?.Invoke(songID, success);
        }

        /// <summary>
        /// The installed song that is another version of <paramref name="songId"/> (same real
        /// file name, case included, different cue checksum), or null when songId itself is
        /// installed, isn't a custom song, or no other version is installed. A custom song's ID
        /// is its real file name (originalSongID, RealFileName) plus "_" and a 32-character
        /// checksum. Songs whose names only differ in case are different songs, not versions.
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
                string name = !string.IsNullOrEmpty(sd.originalSongID)
                    ? sd.originalSongID
                    : (!string.IsNullOrEmpty(sd.zipPath) ? Path.GetFileNameWithoutExtension(sd.zipPath) : null);
                if (string.Equals(name, baseName, StringComparison.Ordinal)) return sd;
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