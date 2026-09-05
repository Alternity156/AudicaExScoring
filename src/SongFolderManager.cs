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

        /// <summary>
        /// Maps songID -> additional folder names beyond its "home" folder in
        /// songFolderMap. Populated when the same filename is found in more than one
        /// pack subfolder (e.g. a track duplicated across two curated bundles, or once
        /// loose and once inside a bundle folder) — the song still loads once (one
        /// SongData, see SongListAssembler's dedup), but its file is genuinely present
        /// in multiple folders on disk, so the folder browser lists it in all of them.
        /// Same additive pattern as Favorites/Song Requests: a song's single "home"
        /// folder (used for navigation — RevealAndSelect, sort-based views) never
        /// changes, this only adds extra places it's ALSO listed.
        /// </summary>
        public static Dictionary<string, List<string>> extraSongFolders = new Dictionary<string, List<string>>();

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

        internal static readonly HashSet<string> audicaSongIDs = new HashSet<string>
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

        internal static readonly HashSet<string> audicaDLCSongIDs = new HashSet<string>
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
            extraSongFolders.Clear();
            availableFolders.Clear();

            Dictionary<string, List<string>> subfolderByFilename = BuildSubfolderMap(mainSongDirectory);

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
                bool isOfficial = false;

                // Skip hidden songs (e.g. the tutorial)
                if (song.hidden)
                    continue;

                List<string> allSubfolders;
                subfolderByFilename.TryGetValue(filename, out allSubfolders);

                if (audicaSongIDs.Contains(id))
                {
                    folder = FolderAudica;
                    hasAudica = true;
                    isOfficial = true;
                }
                else if (audicaDLCSongIDs.Contains(id))
                {
                    folder = FolderAudicaDLC;
                    hasAudicaDLC = true;
                    isOfficial = true;
                }
                else if (allSubfolders != null && allSubfolders.Count > 0 && allSubfolders[0] != FolderCustom)
                {
                    // A real pack subfolder always sorts before a loose/"Unsorted"
                    // entry within a filename's list (BuildSubfolderMap scans
                    // subfolders first), so this only takes the "Unsorted" home below
                    // when there's no real pack subfolder copy at all.
                    folder = allSubfolders[0];
                    customFolders[folder] = true;
                }
                else
                {
                    folder = FolderCustom;
                    hasCustom = true;
                }

                if (!songFolderMap.ContainsKey(id))
                    songFolderMap.Add(id, folder);

                // "folder" above is just this song's single home (used for navigation —
                // RevealAndSelect, sort-based views). If its filename is ALSO present
                // elsewhere (another pack subfolder, or loose in the base directory),
                // record those as additional listings — same additive pattern already
                // used for Favorites/Song Requests. Skipped for official/DLC songs:
                // those are always Audica/Audica DLC only, even if a file with the same
                // name also happens to sit loose in the songs directory.
                if (!isOfficial && allSubfolders != null)
                {
                    for (int j = 0; j < allSubfolders.Count; j++)
                    {
                        string extra = allSubfolders[j];
                        if (extra == folder)
                            continue;

                        // Keep "Unsorted" in its original fixed list position rather
                        // than letting it sort in alphabetically among pack names.
                        if (extra == FolderCustom)
                            hasCustom = true;
                        else
                            customFolders[extra] = true;

                        List<string> extras;
                        if (!extraSongFolders.TryGetValue(id, out extras))
                        {
                            extras = new List<string>();
                            extraSongFolders[id] = extras;
                        }
                        if (!extras.Contains(extra))
                            extras.Add(extra);
                    }
                }
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
        /// True if songID belongs to folder — either as its home folder (songFolderMap)
        /// or as one of the additional pack folders its underlying file also physically
        /// exists in (extraSongFolders). Use this instead of GetFolder(id) == folder
        /// anywhere a song might need to be listed under more than one folder.
        /// </summary>
        public static bool IsInFolder(string songID, string folder)
        {
            if (GetFolder(songID) == folder)
                return true;

            List<string> extras;
            return extraSongFolders.TryGetValue(songID, out extras) && extras.Contains(folder);
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

        private static Dictionary<string, List<string>> BuildSubfolderMap(string mainSongDirectory)
        {
            var map = new Dictionary<string, List<string>>();

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
                    AddFilenameFolder(map, Path.GetFileName(file), folderName);
            }

            // Loose files sitting directly in mainSongDirectory (no subfolder) were
            // previously invisible to this map entirely — a filename that exists BOTH
            // loose here AND inside a pack subfolder needs both locations recorded so
            // Rebuild() can list it in both, instead of only ever seeing the subfolder
            // copy. Registered under FolderCustom ("Unsorted"), the same bucket a solo
            // loose file already falls back to today.
            try
            {
                string[] looseFiles = Directory.GetFiles(mainSongDirectory, "*.audica", SearchOption.TopDirectoryOnly);
                foreach (string file in looseFiles)
                    AddFilenameFolder(map, Path.GetFileName(file), FolderCustom);
            }
            catch
            {
                // Leave map as whatever the subfolder scan already produced.
            }

            return map;
        }

        private static void AddFilenameFolder(Dictionary<string, List<string>> map, string filename, string folderName)
        {
            List<string> folders;
            if (!map.TryGetValue(filename, out folders))
            {
                folders = new List<string>();
                map[filename] = folders;
            }
            if (!folders.Contains(folderName))
                folders.Add(folderName);
        }
    }
}