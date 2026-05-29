using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader;

namespace ExScoringMod
{
    /// <summary>
    /// Manages song folder groupings for the folder filter system.
    /// Hardcoded folders: "Audica" (base OST), "Audica DLC" (extras/PSVR/paid DLC).
    /// Dynamic folders: subfolders of the songs directory map to folder names.
    /// Root-level custom songs go in "Custom Songs".
    /// </summary>
    internal static class SongFolderManager
    {
        public const string FolderAudica = "Audica";
        public const string FolderAudicaDLC = "Audica DLC";
        public const string FolderCustom = "Custom Songs";

        public static bool shouldAutoSelectOnReturn = false;

        /// <summary>Maps songID -> folder name.</summary>
        public static Dictionary<string, string> songFolderMap = new Dictionary<string, string>();

        /// <summary>Ordered list of folder names that have at least one loaded song.</summary>
        public static List<string> availableFolders = new List<string>();

        /// <summary>Currently selected folder name, or null if none.</summary>
        public static string selectedFolder = null;

        /// <summary>Getter for the folder filter, mirroring PlaylistManager.playlistFilter.</summary>
        public static Func<FilterPanel.Filter> folderFilter;

        // ── Hardcoded base OST song IDs ──────────────────────────────────────

        private static readonly HashSet<string> audicaSongIDs = new HashSet<string>
        {
            "addictedtoamemory", "adrenaline", "boomboom", "breakforme", "channel42",
            "collider", "decodeme", "destiny", "everyday", "eyeforaneye",
            "gametime", "goatpolyphia", "golddust", "hr8938cephei", "highwaytooblivion_short",
            "ifeellove", "iwantu", "illmerica", "lazerface", "loyal",
            "overtime", "popstars", "perfectexceeder", "predator", "raiseyourweapon_noisia",
            "resistance", "smoke", "splinter", "synthesized", "thespace",
            "timeforcrime", "titanium_cazzette", "tothestars"
        };

        // ── Hardcoded DLC song IDs (extras, PSVR exclusives, paid DLC) ───────

        private static readonly HashSet<string> audicaDLCSongIDs = new HashSet<string>
        {
            // Extras (album versions)
            "addictedtoamemory_full", "destiny_full", "highwaytooblivion_full", "popstars_full",
            // PSVR exclusives (now free)
            "exitwounds", "funkycomputer", "reedsofmitatrush", "weallbecome",
            // Paid DLC
            "allstars", "avalanche", "badguy", "believer", "betternow",
            "cantfeelmyface", "centuries", "countingstars", "dontletmedown",
            "gdfr", "girlsbedancing", "highhopes", "howweknow", "intoyou",
            "juice", "longrun", "methanebreather", "moveslikejagger", "newrules",
            "preexistingcondition", "sorryforpartyrocking", "starships", "stook",
            "thegreatest", "themiddle", "themotherweshare", "urprey", "youngblood"
        };

        /// <summary>
        /// Rebuilds songFolderMap and availableFolders from the current SongList
        /// and the subfolder structure of the songs directory.
        /// Call after the song list has finished loading/reloading.
        /// </summary>
        public static void Rebuild(string mainSongDirectory)
        {
            songFolderMap.Clear();
            availableFolders.Clear();

            // Build a map from filename -> subfolder name for files in subdirectories.
            // e.g.  songs/MyPack/song.audica  ->  "MyPack"
            Dictionary<string, string> subfolderByFilename = BuildSubfolderMap(mainSongDirectory);

            bool hasAudica = false;
            bool hasAudicaDLC = false;
            bool hasCustom = false;
            // Use a sorted dict so custom subfolder names appear alphabetically
            SortedDictionary<string, bool> customFolders = new SortedDictionary<string, bool>();

            for (int i = 0; i < SongList.I.songs.Count; i++)
            {
                var song = SongList.I.songs[i];
                string id = song.songID;
                string filename = Path.GetFileName(song.zipPath);
                string folder;

                if (audicaSongIDs.Contains(id))
                {
                    folder = FolderAudica;
                    hasAudica = true;
                }
                else if (audicaDLCSongIDs.Contains(id))
                {
                    folder = FolderAudicaDLC;
                    hasAudicaDLC = true;
                }
                else if (subfolderByFilename.TryGetValue(filename, out string subfolderName))
                {
                    folder = subfolderName;
                    customFolders[subfolderName] = true;
                }
                else
                {
                    folder = FolderCustom;
                    hasCustom = true;
                }

                if (!songFolderMap.ContainsKey(id))
                    songFolderMap.Add(id, folder);
            }

            // Build availableFolders in a consistent order:
            // Audica → Audica DLC → Custom Songs → custom subfolders (alphabetical)
            if (hasAudica) availableFolders.Add(FolderAudica);
            if (hasAudicaDLC) availableFolders.Add(FolderAudicaDLC);
            if (hasCustom) availableFolders.Add(FolderCustom);
            foreach (string name in customFolders.Keys)
                availableFolders.Add(name);

            MelonLogger.Log($"[SongFolderManager] Rebuilt: {songFolderMap.Count} songs across {availableFolders.Count} folder(s).");
        }

        /// <summary>
        /// Returns the folder name for a given songID, or null if unknown.
        /// </summary>
        public static string GetFolder(string songID)
        {
            return songFolderMap.TryGetValue(songID, out string folder) ? folder : null;
        }

        /// <summary>
        /// Selects a folder and activates the folder filter.
        /// </summary>
        public static void SelectFolder(string folderName)
        {
            selectedFolder = folderName;
            FilterPanel.ActivateFilter("folders");
        }

        /// <summary>
        /// Clears the selected folder and deactivates the filter.
        /// </summary>
        public static void ClearFolder()
        {
            selectedFolder = null;
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        /// <summary>
        /// Walks one level of subdirectories under mainSongDirectory and maps
        /// each .audica filename found there to its parent subfolder name.
        /// </summary>
        private static Dictionary<string, string> BuildSubfolderMap(string mainSongDirectory)
        {
            var map = new Dictionary<string, string>();

            if (!Directory.Exists(mainSongDirectory))
                return map;

            string[] subdirs;
            try
            {
                subdirs = Directory.GetDirectories(mainSongDirectory, "*", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                return map;
            }

            foreach (string subdir in subdirs)
            {
                string folderName = Path.GetFileName(subdir);
                string[] files;
                try
                {
                    files = Directory.GetFiles(subdir, "*.audica", SearchOption.TopDirectoryOnly);
                }
                catch
                {
                    continue;
                }

                foreach (string file in files)
                {
                    string filename = Path.GetFileName(file);
                    if (!map.ContainsKey(filename))
                        map.Add(filename, folderName);
                }
            }

            return map;
        }
    }
}