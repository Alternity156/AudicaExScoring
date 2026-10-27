using System.Collections.Generic;
using MelonLoader;
using UnityEngine;

namespace ExScoringMod
{
    internal enum ColorSection
    {
        Judgements,
        SongList
    }

    /// <summary>One user-customisable colour: saved as three floats (Key + "R"/"G"/"B") in MelonPrefs.</summary>
    internal class ColorSetting
    {
        public readonly string Key;
        public readonly string Label;
        public readonly Color Default;
        public readonly ColorSection Section;
        public Color Value;

        public ColorSetting(string key, string label, Color def, ColorSection section)
        {
            Key = key;
            Label = label;
            Default = def;
            Section = section;
            Value = def;
        }
    }

    /// <summary>
    /// Registry of every customisable colour outside the target colours (those live in KataConfig /
    /// Config.EitherHandColor). Defaults are the colours that used to be hardcoded, so nothing changes
    /// until the player picks something. Prefs are registered with an empty description (hidden
    /// bookkeeping, same convention as the rest of Config).
    /// </summary>
    internal static class ColorSettings
    {
        private static Color C(float r, float g, float b) => new Color(r, g, b, 1f);

        // ── Judgements (OK follows Fantastic) ─────────────────────────────────
        public static readonly ColorSetting JudgementImpeccable = new ColorSetting("ColorJudgementImpeccable", "Impeccable", C(0f, 1f, 1f), ColorSection.Judgements);
        public static readonly ColorSetting JudgementFantastic = new ColorSetting("ColorJudgementFantastic", "Fantastic", C(1f, 1f, 1f), ColorSection.Judgements);
        public static readonly ColorSetting JudgementExcellent = new ColorSetting("ColorJudgementExcellent", "Excellent", C(1f, 0.84f, 0f), ColorSection.Judgements);
        public static readonly ColorSetting JudgementGreat = new ColorSetting("ColorJudgementGreat", "Great", C(0f, 1f, 0f), ColorSection.Judgements);
        public static readonly ColorSetting JudgementGood = new ColorSetting("ColorJudgementGood", "Good", C(0.133f, 0.545f, 0.133f), ColorSection.Judgements);
        public static readonly ColorSetting JudgementMiss = new ColorSetting("ColorJudgementMiss", "Miss", C(1f, 0f, 0f), ColorSection.Judgements);

        // ── Song list: folders ────────────────────────────────────────────────
        public static readonly ColorSetting FolderFavorites = new ColorSetting("ColorFolderFavorites", "Favorites", C(0.30f, 0.30f, 0.30f), ColorSection.SongList);
        public static readonly ColorSetting FolderAudica = new ColorSetting("ColorFolderAudica", "Audica", C(0.30f, 0.30f, 0.30f), ColorSection.SongList);
        public static readonly ColorSetting FolderAudicaDLC = new ColorSetting("ColorFolderAudicaDLC", "Audica DLC", C(0.30f, 0.30f, 0.30f), ColorSection.SongList);
        public static readonly ColorSetting FolderUnsorted = new ColorSetting("ColorFolderUnsorted", "Unsorted", C(0.30f, 0.30f, 0.30f), ColorSection.SongList);
        public static readonly ColorSetting FolderSongRequests = new ColorSetting("ColorFolderSongRequests", "Song Requests", C(0.30f, 0.30f, 0.30f), ColorSection.SongList);
        public static readonly ColorSetting FolderLatestMaudica = new ColorSetting("ColorFolderLatestMaudica", "Latest Maudica", C(0.30f, 0.30f, 0.30f), ColorSection.SongList);
        public static readonly ColorSetting FolderSearch = new ColorSetting("ColorFolderSearch", "Search Results", C(0.30f, 0.30f, 0.30f), ColorSection.SongList);
        public static readonly ColorSetting FolderOther = new ColorSetting("ColorFolderOther", "Other Folders", C(0.18f, 0.18f, 0.18f), ColorSection.SongList);

        // ── Song list: action rows ────────────────────────────────────────────
        public static readonly ColorSetting RowOptions = new ColorSetting("ColorRowOptions", "Options", C(0.24f, 0.22f, 0.30f), ColorSection.SongList);
        public static readonly ColorSetting RowOptionCategory = new ColorSetting("ColorRowOptionCategory", "Option Categories", C(0.26f, 0.26f, 0.30f), ColorSection.SongList);
        public static readonly ColorSetting RowPlaylists = new ColorSetting("ColorRowPlaylists", "Playlists", C(0.30f, 0.30f, 0.30f), ColorSection.SongList);
        public static readonly ColorSetting RowPlaylistEntry = new ColorSetting("ColorRowPlaylistEntry", "Playlist Entries", C(0.30f, 0.30f, 0.30f), ColorSection.SongList);
        public static readonly ColorSetting RowPlaylistTitle = new ColorSetting("ColorRowPlaylistTitle", "Playlist Title", C(0.30f, 0.30f, 0.30f), ColorSection.SongList);
        public static readonly ColorSetting RowBack = new ColorSetting("ColorRowBack", "Back", C(0.20f, 0.24f, 0.34f), ColorSection.SongList);
        public static readonly ColorSetting RowCreate = new ColorSetting("ColorRowCreate", "Create Playlist", C(0.10f, 0.30f, 0.14f), ColorSection.SongList);
        public static readonly ColorSetting RowDelete = new ColorSetting("ColorRowDelete", "Delete", C(0.34f, 0.10f, 0.10f), ColorSection.SongList);
        public static readonly ColorSetting RowConfirmDelete = new ColorSetting("ColorRowConfirmDelete", "Confirm Delete", C(0.65f, 0.10f, 0.10f), ColorSection.SongList);
        public static readonly ColorSetting RowMarathon = new ColorSetting("ColorRowMarathon", "Marathon", C(0.28f, 0.20f, 0.40f), ColorSection.SongList);

        // ── Song list: Maudica / downloads ────────────────────────────────────
        public static readonly ColorSetting RowDownloadable = new ColorSetting("ColorRowDownloadable", "Downloadable Song", C(0.15f, 0.28f, 0.40f), ColorSection.SongList);
        public static readonly ColorSetting RowDownloadAll = new ColorSetting("ColorRowDownloadAll", "Download All", C(0.18f, 0.34f, 0.22f), ColorSection.SongList);
        public static readonly ColorSetting RowLoadMore = new ColorSetting("ColorRowLoadMore", "Load More / Status", C(0.22f, 0.22f, 0.30f), ColorSection.SongList);

        // ── Song list: online lobby ───────────────────────────────────────────
        public static readonly ColorSetting LobbyFolder = new ColorSetting("ColorLobbyFolder", "Lobby Folder", C(0.30f, 0.18f, 0.40f), ColorSection.SongList);
        public static readonly ColorSetting LobbyDownloadable = new ColorSetting("ColorLobbyDownloadable", "Lobby Downloadable", C(0.15f, 0.28f, 0.40f), ColorSection.SongList);
        public static readonly ColorSetting LobbyUnavailable = new ColorSetting("ColorLobbyUnavailable", "Lobby Unavailable", C(0.45f, 0.10f, 0.10f), ColorSection.SongList);
        public static readonly ColorSetting LobbyLooking = new ColorSetting("ColorLobbyLooking", "Lobby Looking", C(0.22f, 0.22f, 0.30f), ColorSection.SongList);

        /// <summary>Every setting, in the order they appear on the Colors page.</summary>
        public static readonly List<ColorSetting> All = new List<ColorSetting>
        {
            JudgementImpeccable, JudgementFantastic, JudgementExcellent, JudgementGreat, JudgementGood, JudgementMiss,

            FolderFavorites, FolderAudica, FolderAudicaDLC, FolderUnsorted,
            FolderSongRequests, FolderLatestMaudica, FolderSearch, FolderOther,

            RowOptions, RowOptionCategory, RowPlaylists, RowPlaylistEntry, RowPlaylistTitle,
            RowBack, RowCreate, RowDelete, RowConfirmDelete, RowMarathon,

            RowDownloadable, RowDownloadAll, RowLoadMore,

            LobbyFolder, LobbyDownloadable, LobbyUnavailable, LobbyLooking,
        };

        public static List<ColorSetting> InSection(ColorSection section)
        {
            var list = new List<ColorSetting>();
            foreach (var s in All) if (s.Section == section) list.Add(s);
            return list;
        }

        /// <summary>Called from Config.RegisterConfig (before OnModSettingsApplied loads values).</summary>
        public static void Register()
        {
            foreach (var s in All)
            {
                MelonPrefs.RegisterFloat(Config.Category, s.Key + "R", s.Default.r, "");
                MelonPrefs.RegisterFloat(Config.Category, s.Key + "G", s.Default.g, "");
                MelonPrefs.RegisterFloat(Config.Category, s.Key + "B", s.Default.b, "");
            }
        }

        /// <summary>Called from Config.OnModSettingsApplied.</summary>
        public static void Load()
        {
            foreach (var s in All)
            {
                s.Value = new Color(
                    Mathf.Clamp01(MelonPrefs.GetFloat(Config.Category, s.Key + "R")),
                    Mathf.Clamp01(MelonPrefs.GetFloat(Config.Category, s.Key + "G")),
                    Mathf.Clamp01(MelonPrefs.GetFloat(Config.Category, s.Key + "B")),
                    1f);
            }
        }

        public static void Set(ColorSetting s, Color c, bool save = true)
        {
            s.Value = new Color(Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), 1f);
            MelonPrefs.SetFloat(Config.Category, s.Key + "R", s.Value.r);
            MelonPrefs.SetFloat(Config.Category, s.Key + "G", s.Value.g);
            MelonPrefs.SetFloat(Config.Category, s.Key + "B", s.Value.b);
            if (save) MelonPrefs.SaveConfig();
        }

        public static void ResetSection(ColorSection section)
        {
            foreach (var s in All)
                if (s.Section == section) Set(s, s.Default, false);
            MelonPrefs.SaveConfig();
        }
    }
}