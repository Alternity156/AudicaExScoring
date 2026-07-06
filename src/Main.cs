using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MelonLoader;
using UnityEngine;

[assembly: MelonOptionalDependencies("AuthorableModifiers", "SongDataLoader")]

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        private static object watchCoroutine;

        public static MenuState.State menuState;
        public static bool gameHasLoaded = false;
        public static bool authorableInstalled = false;
        public static bool modSettingsInstalled = false;
        public static bool songDataLoaderInstalled = false;
        public static bool songRequestInstalled = false;
        public static bool suppressShellPageAnimations = false;
        public static bool songPageSetupQueued = false;
        public static string selectedSong;
        public static SongList.SongData selectedSongData;

        public static List<int> processedCuesIndexes = new List<int>();
        public static List<ExCue> exCues = new List<ExCue>();

        /// <summary>
        /// Whether ExType gameplay should currently show the "purple" peak visual state.
        /// True on hit/song start, false on miss. Independent of the real ScoreKeeper multiplier.
        /// </summary>
        public static bool exTypePurple = true;
        public static List<UnprocessedTargetHitPos> unprocessedTargetHitPoses = new List<UnprocessedTargetHitPos>();
        public static bool runSaved = false;

        private static Dictionary<int, List<(float aimScore, Vector3 intersectionPoint)>> pendingAimResults = new Dictionary<int, List<(float, Vector3)>>();

        public static bool nextPopupIsScore = false;
        public static string nextPopupText = "";

        public static float maxPossibleExScore;
        public static float exScore = 0;
        public static float judgementScore = 0;
        public static float currentMaxPossibleExScore;
        public static float currentMaxPossibleJudgementScore;

        public static Dictionary<string, KataConfig.Difficulty> difficultyButtonMap = new Dictionary<string, KataConfig.Difficulty>();
        public static GameObject difficultyIndicatorSource;
        public static bool difficultyUISetup = false;
        public static bool songListUISetup = false;
        public static bool launchPanelUISetup = false;
        public static bool isAutoSelecting = false;

        public static string laurelStart = "laurel_start";
        public static string laurelEnd = "laurel_end";

        // ── Song downloading fields ──
        public static bool shouldShowKeyboard = false;
        public static string downloadsDirectory;
        public static string mainSongDirectory;

        // ── Song deletion fields ──
        public static string deletedDownloadsListPath;
        public static List<string> deletedSongs = new List<string>();
        public static List<string> deletedSongPaths = new List<string>();

        public static class BuildInfo
        {
            public const string Name = "ExScoring";  // Name of the Mod.  (MUST BE SET)
            public const string Author = "Alternity"; // Author of the Mod.  (Set as null if none)
            public const string Company = null; // Company that made the Mod.  (Set as null if none)
            public const string Version = "0.1.0"; // Version of the Mod.  (MUST BE SET)
            public const string DownloadLink = null; // Download Link for the Mod.  (Set as null if none)
        }

        public override void OnApplicationStart()
        {
            Config.RegisterConfig();
            PlaylistConfig.RegisterConfig();

            // Check if AuthorableModifiers is installed
            if (MelonHandler.Mods.Any(it => it.Assembly.GetName().Name == "AuthorableModifiers"))
            {
                authorableInstalled = true;
                MelonLogger.Log("AuthorableModifiers detected");
            }

            // Check if ModSettings is installed
            if (MelonHandler.Mods.Any(it => it.Assembly.GetName().Name == "ModSettings"))
            {
                modSettingsInstalled = true;
                MelonLogger.Log("ModSettings detected");
            }

            // Check if SongDataLoader is installed (optional — enables album art)
            if (MelonHandler.Mods.Any(it => it.Info.SystemType.Name == "SongDataLoader"))
            {
                songDataLoaderInstalled = true;
                MelonLogger.Log("SongDataLoader detected — album art enabled");
            }

            // Check if SongRequest (Silzoid's optional mod) is installed.
            // When present: enable the Song Requests folder + relocated controls (later phases),
            // and suppress SongRequest's own song-list UI now.
            if (SongRequestIntegration.IsPresent)
            {
                songRequestInstalled = true;
                MelonLogger.Log("SongRequest detected — request folder integration enabled");
                SongRequestBlocker.Apply();
                SongRequestInterceptor.Apply();
            }

            // Set up download directories
            mainSongDirectory = Path.Combine(Application.streamingAssetsPath, "HmxAudioAssets", "songs");
            downloadsDirectory = Application.dataPath.Replace("Audica_Data", "Downloads");
            deletedDownloadsListPath = Path.Combine(downloadsDirectory, "ExScoring_DeletedFiles");
            CheckFolderDirectories();

            // Clean up any songs marked for deletion from previous session
            CleanMarkedForDeletion();

            // Move any previously downloaded songs into the main songs folder
            // so the game picks them up naturally on this launch
            MoveDownloadedSongs();

            // Fallback: also register Downloads as a song source directory
            // in case any files couldn't be moved (e.g. locked files)
            RegisterDownloadsDirectory();

            RegisterSongSubfolders();
        }

        private void CheckFolderDirectories()
        {
            if (!Directory.Exists(mainSongDirectory))
            {
                Directory.CreateDirectory(mainSongDirectory);
            }
            if (!Directory.Exists(downloadsDirectory))
            {
                Directory.CreateDirectory(downloadsDirectory);
            }
        }

        /// <summary>
        /// Moves all .audica files from the Downloads folder into the main songs
        /// folder. This ensures the game loads them on startup without needing
        /// a custom song source directory. If a file already exists in the
        /// destination it is deleted from Downloads to avoid duplicates.
        /// </summary>
        private static void MoveDownloadedSongs()
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(downloadsDirectory, "*.audica", SearchOption.TopDirectoryOnly);
            }
            catch (Exception e)
            {
                MelonLogger.Log($"[WARNING] Could not read Downloads folder: {e.Message}");
                return;
            }

            if (files.Length == 0) return;

            MelonLogger.Log($"Moving {files.Length} downloaded song(s) to songs folder...");

            foreach (string filePath in files)
            {
                string fileName = Path.GetFileName(filePath);
                string destination = Path.Combine(mainSongDirectory, fileName);

                try
                {
                    if (File.Exists(destination))
                    {
                        // Already in songs folder — just clean up the download copy
                        File.Delete(filePath);
                        MelonLogger.Log($"  Deleted duplicate: {fileName}");
                    }
                    else
                    {
                        File.Move(filePath, destination);
                        MelonLogger.Log($"  Moved: {fileName}");
                    }
                }
                catch (Exception e)
                {
                    MelonLogger.Log($"[WARNING] Could not move {fileName}: {e.Message}");
                    // File stays in Downloads — the fallback directory registration
                    // will ensure it still gets loaded
                }
            }
        }

        /// <summary>
        /// Registers the Downloads folder as an additional song source directory
        /// so any .audica files that couldn't be moved are still loaded by the game.
        /// </summary>
        private static void RegisterDownloadsDirectory()
        {
            try
            {
                SongList.AddSongSearchDir(Application.dataPath, downloadsDirectory);
                MelonLogger.Log("Registered Downloads as additional song source directory");
            }
            catch (Exception e)
            {
                MelonLogger.Log($"[WARNING] Could not register Downloads directory: {e.Message}");
            }
        }

        /// <summary>
        /// Registers every immediate subdirectory of the main songs folder as an
        /// additional song source directory so the game loads .audica files placed
        /// inside those subfolders on startup.
        /// </summary>
        private static void RegisterSongSubfolders()
        {
            if (!Directory.Exists(mainSongDirectory)) return;

            string[] subdirs;
            try
            {
                subdirs = Directory.GetDirectories(mainSongDirectory, "*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception e)
            {
                MelonLogger.Log($"[WARNING] Could not enumerate song subfolders: {e.Message}");
                return;
            }

            foreach (string subdir in subdirs)
            {
                try
                {
                    SongList.AddSongSearchDir(Application.streamingAssetsPath, subdir);
                    MelonLogger.Log($"Registered song subfolder: {Path.GetFileName(subdir)}");
                }
                catch (Exception e)
                {
                    MelonLogger.Log($"[WARNING] Could not register subfolder {Path.GetFileName(subdir)}: {e.Message}");
                }
            }
        }

        // ── Song deletion methods ──

        public static void RemoveSong(string songID)
        {
            if (deletedSongs.Contains(songID))
                return;
            var song = SongList.I.GetSong(songID);
            deletedSongPaths.Add(song.searchRoot + "/" + song.zipPath);
            deletedSongs.Add(song.songID);
        }

        public static void RestoreDeletedSongs()
        {
            deletedSongPaths = new List<string>();
            deletedSongs = new List<string>();
            KataConfig.I.CreateDebugText("Restored songs", new Vector3(0f, -1f, 5f), 5f, null, false, 0.2f);
        }

        // ── Song count notification ──

        public static IEnumerator UpdateLastSongCount()
        {
            yield return new WaitForSeconds(0.5f);
            WWW www = new WWW(SongDownloader.apiUrl);
            yield return www;
            try
            {
                MelonLogger.Log($"[SongCount] API response length: {www.text?.Length}");
                int newSongCount = MelonLoader.TinyJSON.JSON.Load(www.text).Make<NewAPISongList>().count;
                MelonLogger.Log($"[SongCount] API count: {newSongCount}, LastSongCount: {Config.LastSongCount}");
                MelonLogger.Log($"[SongCount] notificationPanel null? {SongListNotification.notificationPanel == null}");
                if (SongListNotification.notificationPanel != null)
                {
                    if (Config.LastSongCount == 0)
                    {
                        Config.UpdateSongCount(newSongCount);
                        MelonLogger.Log("[SongCount] First run, storing count");
                    }
                    else if (Config.LastSongCount == newSongCount)
                    {
                        SongListNotification.SetText("There are no new songs available");
                        MelonLogger.Log("[SongCount] No new songs");
                    }
                    else
                    {
                        int diff = newSongCount - Config.LastSongCount;
                        bool isSingular = diff == 1;
                        string pre = isSingular ? "is " : "are ";
                        string noun = isSingular ? "song" : "songs";
                        SongListNotification.SetText("There " + pre + diff.ToString() + " new " + noun + " available");
                        MelonLogger.Log($"[SongCount] {diff} new songs");
                    }
                    Config.UpdateSongCount(newSongCount);
                }
            }
            catch (Exception e)
            {
                MelonLogger.Log($"[WARNING] Could not fetch song count: {e.Message}");
            }
        }

        public override void OnApplicationQuit()
        {
            CleanDeletedSongs();
        }

        private static void CleanDeletedSongs()
        {
            List<string> downloadsMarkedForDeletion = new List<string>();
            foreach (var songPath in deletedSongPaths)
            {
                if (File.Exists(songPath))
                {
                    try
                    {
                        File.Delete(songPath);
                    }
                    catch
                    {
                        downloadsMarkedForDeletion.Add(Path.GetFileName(songPath));
                    }
                }
                else
                {
                    string fileName = Path.GetFileName(songPath);
                    string downloadPath = Path.Combine(downloadsDirectory, fileName);
                    if (File.Exists(downloadPath))
                    {
                        downloadsMarkedForDeletion.Add(fileName);
                    }
                }
            }
            if (downloadsMarkedForDeletion.Count > 0)
            {
                string text = MelonLoader.TinyJSON.JSON.Dump(downloadsMarkedForDeletion);
                File.WriteAllText(deletedDownloadsListPath, text);
            }
        }

        private static void CleanMarkedForDeletion()
        {
            if (!File.Exists(deletedDownloadsListPath)) return;

            try
            {
                string text = File.ReadAllText(deletedDownloadsListPath);
                List<string> deleted = MelonLoader.TinyJSON.JSON.Load(text).Make<List<string>>();

                foreach (string fileName in deleted)
                {
                    string path = Path.Combine(downloadsDirectory, fileName);
                    try
                    {
                        if (File.Exists(path))
                        {
                            File.Delete(path);
                        }
                        else
                        {
                            path = Path.Combine(mainSongDirectory, fileName);
                            if (File.Exists(path))
                            {
                                File.Delete(path);
                            }
                        }
                    }
                    catch
                    {
                        MelonLogger.Log($"[WARNING] Unable to delete {fileName}");
                    }
                }
                File.Delete(deletedDownloadsListPath);
            }
            catch (Exception e)
            {
                MelonLogger.Log($"[WARNING] Could not process deletion list: {e.Message}");
            }
        }

        public override void OnModSettingsApplied()
        {
            Config.OnModSettingsApplied();
        }

        public static void StartWatching()
        {
            if (watchCoroutine == null)
                watchCoroutine = MelonCoroutines.Start(WatchPrefs());
        }

        public static void StopWatching()
        {
            if (watchCoroutine != null)
            {
                MelonCoroutines.Stop(watchCoroutine);
                watchCoroutine = null;
            }
        }

        private static IEnumerator WatchPrefs()
        {
            while (true)
            {
                yield return new WaitForSeconds(0.1f);

                bool audicaType = MelonPrefs.GetBool(Config.Category, nameof(Config.AudicaType));
                bool exType = MelonPrefs.GetBool(Config.Category, nameof(Config.ExType));

                if (audicaType && exType)
                {
                    if (!Config.AudicaType)
                        MelonPrefs.SetBool(Config.Category, nameof(Config.ExType), false);
                    else
                        MelonPrefs.SetBool(Config.Category, nameof(Config.AudicaType), false);
                }
                else if (!audicaType && !exType)
                {
                    if (Config.AudicaType)
                        MelonPrefs.SetBool(Config.Category, nameof(Config.ExType), true);
                    else
                        MelonPrefs.SetBool(Config.Category, nameof(Config.AudicaType), true);
                }

                bool audicaCalc = MelonPrefs.GetBool(Config.Category, nameof(Config.AudicaCalculation));
                bool linearCalc = MelonPrefs.GetBool(Config.Category, nameof(Config.LinearCalculation));

                if (audicaCalc && linearCalc)
                {
                    if (!Config.AudicaCalculation)
                        MelonPrefs.SetBool(Config.Category, nameof(Config.LinearCalculation), false);
                    else
                        MelonPrefs.SetBool(Config.Category, nameof(Config.AudicaCalculation), false);
                }
                else if (!audicaCalc && !linearCalc)
                {
                    if (Config.AudicaCalculation)
                        MelonPrefs.SetBool(Config.Category, nameof(Config.LinearCalculation), true);
                    else
                        MelonPrefs.SetBool(Config.Category, nameof(Config.AudicaCalculation), true);
                }

                Config.AudicaType = audicaType;
                Config.ExType = exType;
                Config.AudicaCalculation = audicaCalc;
                Config.LinearCalculation = linearCalc;
            }
        }

        public override void OnUpdate()
        {
            if (Input.GetKeyDown(KeyCode.P))
            {
                Time.timeScale = Time.timeScale == 0f ? 1f : 0f;
            }

            if (Input.GetKeyDown(KeyCode.F5))
            {
                ReloadSongList();
            }

            if (Input.GetKeyDown(KeyCode.F6))
                SongRequestIntegration.LogSnapshot();

            TrippyMenu.Tick();
        }

        public override void OnLateUpdate()
        {
            VirtualSongList.Sync();
            EnforceLaunchPanelBlank();
            SyncDisplayingDifficulty();
            MarathonSetup.Enforce();
        }

        /// <summary>
        /// Reloads the song list after songs were added to songs or downloads directories.
        /// Should be called while the user is in the main menu.
        /// </summary>
        public static void ReloadSongList(bool fullReload = true)
        {
            MelonLogger.Log("Reloading songlist");
            SongDownloader.needRefresh = false;

            if (fullReload)
            {
                SongList.sFirstTime = true;
                SongList.OnSongListLoaded.mDone = false;
                SongList.SongSourceDirs = new Il2CppSystem.Collections.Generic.List<SongList.SongSourceDir>();
                SongList.AddSongSearchDir(Application.dataPath, downloadsDirectory);
                RegisterSongSubfolders();
                SongList.I.StartAssembleSongList();
            }
            else
            {
                List<SongList.SongSourceDir> sourceDirs = new List<SongList.SongSourceDir>();
                sourceDirs.Add(new SongList.SongSourceDir(Application.streamingAssetsPath, mainSongDirectory));
                sourceDirs.Add(new SongList.SongSourceDir(Application.dataPath, downloadsDirectory));
                // Add subfolders
                if (Directory.Exists(mainSongDirectory))
                {
                    foreach (string subdir in Directory.GetDirectories(mainSongDirectory, "*", SearchOption.TopDirectoryOnly))
                        sourceDirs.Add(new SongList.SongSourceDir(Application.streamingAssetsPath, subdir));
                }
                for (int i = 0; i < sourceDirs.Count; i++)
                {
                    SongList.SongSourceDir sourceDir = sourceDirs[i];
                    string[] files = Directory.GetFiles(sourceDir.dir, "*.audica");
                    for (int j = 0; j < files.Length; j++)
                    {
                        string file = files[j].Replace('\\', '/');
                        if (!SongDownloadTracker.songFilenames.Contains(Path.GetFileName(file)) &&
                            !SongDownloader.downloadedFileNames.Contains(Path.GetFileName(file)))
                        {
                            SongList.I.ProcessSingleSong(sourceDir, file, new Il2CppSystem.Collections.Generic.HashSet<string>());
                        }
                    }
                }
            }

            SongDownloader.downloadedFileNames.Clear();
            SongDownloadTracker.StartSongListUpdate(fullReload);

            KataConfig.I.CreateDebugText("Reloading Songs", new Vector3(0f, -1f, 5f), 5f, null, false, 0.2f);
        }
    }
}