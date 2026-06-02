using System.Collections.Generic;
using System.IO;
using MelonLoader;

namespace ExScoringMod
{
    /// <summary>
    /// Manages song folder groupings for the inline folder row system.
    /// Hardcoded folders: "Audica" (base OST), "Audica DLC" (extras/PSVR/paid DLC).
    /// Dynamic folders: subfolders of the songs directory map to folder names.
    /// Root-level custom songs go in "Unsorted".
    /// </summary>
    internal static class SongFolderManager
    {
        public const string FolderFavorites = "Favorites";
        public const string FolderAudica = "Audica";
        public const string FolderAudicaDLC = "Audica DLC";
        public const string FolderCustom = "Unsorted";
        public const string FolderSongRequests = "Song Requests";

        /// <summary>Maps songID -> folder name.</summary>
        public static Dictionary<string, string> songFolderMap = new Dictionary<string, string>();

        /// <summary>Ordered list of folder names that have at least one loaded song.</summary>
        public static List<string> availableFolders = new List<string>();

        /// <summary>
        /// The folder whose songs are currently expanded in the song list.
        /// Null means all folders are collapsed (only folder rows visible).
        /// Persists across scene transitions so the list restores its state.
        /// </summary>
        public static string openFolder = null;

        /// <summary>
        /// Name of the active search-results folder ("Search Results (query)"),
        /// or null when no search is active. When set, it appears at the top of
        /// the folder list. Virtual: not stored in songFolderMap.
        /// </summary>
        public static string searchFolderName = null;

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

            Dictionary<string, string> subfolderByFilename = BuildSubfolderMap(mainSongDirectory);

            bool hasAudica = false;
            bool hasAudicaDLC = false;
            bool hasCustom = false;
            SortedDictionary<string, bool> customFolders = new SortedDictionary<string, bool>();

            for (int i = 0; i < SongList.I.songs.Count; i++)
            {
                var song = SongList.I.songs[i];
                string id = song.songID;
                string filename = Path.GetFileName(song.zipPath);
                string folder;

                // Skip hidden songs (e.g. the tutorial)
                if (song.hidden)
                    continue;

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

            // Favorites (virtual) → Audica → Audica DLC → Unsorted → custom subfolders
            availableFolders.Add(FolderFavorites);
            if (SongRequestIntegration.IsPresent)
                availableFolders.Add(FolderSongRequests);
            if (hasAudica) availableFolders.Add(FolderAudica);
            if (hasAudicaDLC) availableFolders.Add(FolderAudicaDLC);
            if (hasCustom) availableFolders.Add(FolderCustom);
            foreach (string name in customFolders.Keys)
                availableFolders.Add(name);

            // Keep the search-results folder pinned at the top across rebuilds
            if (searchFolderName != null && !availableFolders.Contains(searchFolderName))
                availableFolders.Insert(0, searchFolderName);

            MelonLogger.Log($"[SongFolderManager] Rebuilt: {songFolderMap.Count} songs across {availableFolders.Count} folder(s). Open: {openFolder ?? "none"}");
        }

        /// <summary>
        /// Returns the folder name for a given songID, or null if unknown.
        /// </summary>
        public static string GetFolder(string songID)
        {
            return songFolderMap.TryGetValue(songID, out string folder) ? folder : null;
        }

        /// <summary>
        /// Sets (or clears, with null) the active search folder, keeping it pinned
        /// at the top of availableFolders.
        /// </summary>
        public static void SetSearchFolder(string name)
        {
            if (searchFolderName != null)
                availableFolders.Remove(searchFolderName);

            searchFolderName = name;

            if (name != null && !availableFolders.Contains(name))
                availableFolders.Insert(0, name);
        }

        // ── Helpers ──────────────────────────────────────────────────────────

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