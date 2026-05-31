using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader;
using MelonLoader.TinyJSON;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

namespace ExScoringMod
{
    public static class FilterPanel
    {
        internal static GameObject notificationPanel;
        private static TextMeshPro notificationText;

        internal static Favorites favorites;

        private static string favoritesPath = Application.dataPath + "/../" + "/UserData/" + "SongBrowserFavorites.json";

        private static Dictionary<string, Filter> filters;

        private static SongSelect songSelect = null;
        private static SongListControls songListControls = null;

        private static bool filterStateChanged = false;

        private static GameObject panel;
        private static GameObject glass;
        private static GameObject highlights;
        private static GameObject allButton;
        private static GameObject mainButton;
        private static GameObject extrasButton;

        private static List<string> buttonOrder = new List<string>() { "All", "Main", "Extras" };

        public class Filter
        {
            public string FilterID { get; internal set; }
            public bool IsActive { get; internal set; }
            public string DefaultButtonText { get; internal set; }
            public string SongListText { get; set; }
            public GameObject Button { get; internal set; }
            public TextMeshPro ButtonText { get; internal set; }

            internal GameObject ButtonSelectedIndicator = null;

            internal Action OnHit = null;
            internal Action OnDisable = null;
            internal Func<Il2CppSystem.Collections.Generic.List<string>, bool> Apply = null;

            internal Filter() { }
        }

        /// <summary>
        /// Allows adding a filter (including its button) to the filter panel.
        /// </summary>
        public static Func<Filter> RegisterFilter(string defaultButtonText, bool placeAboveOriginalFilters, string songListText,
                                                   Action onHitListener, Action onDisable,
                                                   Func<Il2CppSystem.Collections.Generic.List<string>, bool> applyFilter)
        {
            if (filters == null)
            {
                filters = new Dictionary<string, Filter>();
            }

            if (filters.ContainsKey(defaultButtonText))
                return null;

            Filter filter = new Filter();
            filter.FilterID = defaultButtonText;
            filter.IsActive = false;
            filter.SongListText = songListText;
            filter.DefaultButtonText = defaultButtonText;
            filter.OnHit = onHitListener;
            filter.OnDisable = onDisable;
            filter.Apply = applyFilter;

            filters.Add(defaultButtonText, filter);

            if (placeAboveOriginalFilters)
                buttonOrder.Insert(0, filter.FilterID);
            else
                buttonOrder.Add(filter.FilterID);

            return new Func<Filter>(() => { return filter; });
        }

        /// <summary>
        /// Returns true if filter with given ID is currently active.
        /// </summary>
        public static bool IsFiltering(string filterID)
        {
            return filters != null && filters.ContainsKey(filterID) ? filters[filterID].IsActive : false;
        }

        internal static void OnApplicationStart()
        {
            LoadFavorites();

            PlaylistManager.playlistFilter = RegisterFilter("playlists", false, "Playlist",
                () =>
                {
                    PlaylistManager.OnFilterApplied();
                    SelectPlaylistButton.ShowPlaylistButton();
                    PlaylistEndlessButton.CreatePlaylistButton();
                },
                () =>
                {
                    SelectPlaylistButton.HidePlaylistButton();
                    PlaylistEndlessButton.HidePlaylistButton();
                },
                (result) =>
                {
                    if (PlaylistManager.selectedPlaylist != null)
                    {
                        result.Clear();
                        foreach (string song in PlaylistManager.selectedPlaylist.songs)
                        {
                            if (SongDownloadTracker.songIDs.Contains(song))
                                result.Add(song);
                        }
                        return true;
                    }
                    return false;
                })();
        }

        internal static void Initialize()
        {
            if (songListControls == null)
            {
                songSelect = GameObject.FindObjectOfType<SongSelect>();
                songListControls = GameObject.FindObjectOfType<SongListControls>();
                GetReferences();

                foreach (string filterKey in filters.Keys)
                {
                    PrepareFilterButton(filters[filterKey]);
                }

                // Remove the built-in filter buttons from the layout — the folder
                // system replaces them. Custom filter buttons move up to fill the space.
                buttonOrder.Remove("All");
                buttonOrder.Remove("Main");
                buttonOrder.Remove("Extras");

                SetFilterUIGeometry();

                // Hide the built-in filter button GameObjects (after cloning is done)
                if (allButton != null) allButton.SetActive(false);
                if (mainButton != null) mainButton.SetActive(false);
                if (extrasButton != null) extrasButton.SetActive(false);
            }
        }

        internal static void ResetFilterState()
        {
            filterStateChanged = true;
        }

        internal static void ApplyFilter(SongSelect __instance, ref bool extras, ref Il2CppSystem.Collections.Generic.List<string> __result)
        {
            if (filters == null) return;

            foreach (Filter filter in filters.Values)
            {
                if (filter.IsActive)
                {
                    extras = true;
                    if (filter.Apply(__result))
                    {
                        __instance.songSelectHeaderItems.mItems[0].titleLabel.text = filter.SongListText;
                    }
                    UpdateScrollPosition(__instance.scroller);
                    break; // only one active at a time
                }
            }
        }

        internal static void DisableCustomFilters(string exceptFilterId = "")
        {
            if (filters == null) return;

            filterStateChanged = true;
            foreach (Filter filter in filters.Values)
            {
                if (filter.FilterID != exceptFilterId)
                {
                    filter.IsActive = false;
                    if (filter.ButtonSelectedIndicator != null)
                        filter.ButtonSelectedIndicator.SetActive(false);
                    filter.OnDisable();
                }
            }
        }

        internal static void SetNotificationText(string text)
        {
            if (notificationText != null)
            {
                notificationText.text = text;
            }
        }

        private static void OnFilterHit(string filterId)
        {
            songListControls.FilterExtras();
            Filter filter = filters[filterId];
            if (!filter.IsActive)
            {
                filter.IsActive = true;
                filterStateChanged = true;
                filter.ButtonSelectedIndicator.SetActive(true);
                filter.OnHit();

                DisableCustomFilters(filterId);
            }
            else
            {
                DisableCustomFilters();
            }
            songSelect.ShowSongList();
        }

        private static void UpdateScrollPosition(ShellScrollable scroller)
        {
            if (filterStateChanged)
            {
                scroller.SnapTo(0, true);
                filterStateChanged = false;
            }
        }

        private static void PrepareFilterButton(Filter filter)
        {
            filter.Button = GameObject.Instantiate(extrasButton, extrasButton.transform.parent);
            GameObject.Destroy(filter.Button.GetComponentInChildren<Localizer>());
            GunButton button = filter.Button.GetComponentInChildren<GunButton>();
            button.onHitEvent = new UnityEvent();
            button.onHitEvent.AddListener(new Action(() => { OnFilterHit(filter.FilterID); }));
            filter.ButtonText = filter.Button.GetComponentInChildren<TextMeshPro>();
            filter.ButtonText.text = filter.DefaultButtonText;
            filter.ButtonSelectedIndicator = filter.Button.transform.GetChild(3).gameObject;
            filter.ButtonSelectedIndicator.SetActive(filter.IsActive);
        }

        private static void SetFilterUIGeometry()
        {
            var filterKeys = filters.Keys;
            int filterCount = filterKeys.Count;

            Vector3 panelLocalPositionOffset = new Vector3(0.0f, 1.5f, 0f);
            Vector3 notificationLocalPosition = new Vector3(0.0f, -17.85f, 0f);
            Vector3 glassLocalScale = new Vector3(11.0f, 20.0f, 3f);
            Vector3 glassLocalPosition = new Vector3(0.0f, -3.75f, 0.15f);
            Vector3 highlightsLocalScale = new Vector3(1.0f, 1.4f, 1f);
            Vector3 highlightsLocalPosition = new Vector3(0.0f, -14.0f, 0f);

            if (filterCount > 2)
            {
                for (int i = 0; i < filterCount - 2; i++)
                {
                    panelLocalPositionOffset += new Vector3(0f, 1.5f, 0f);
                    notificationLocalPosition += new Vector3(0f, -3.5f, 0f);
                    glassLocalScale += new Vector3(0f, 3.15f, 0f);
                    glassLocalPosition += new Vector3(0f, -1.875f, 0f);
                    highlightsLocalScale += new Vector3(0f, 0.25f, 0f);
                    highlightsLocalPosition += new Vector3(0f, -1.85f, 0f);
                }
            }

            panel.transform.localPosition += panelLocalPositionOffset;
            notificationPanel.transform.localPosition = notificationLocalPosition;
            glass.transform.localScale = glassLocalScale;
            glass.transform.localPosition = glassLocalPosition;
            highlights.transform.localScale = highlightsLocalScale;
            highlights.transform.localPosition = highlightsLocalPosition;

            Vector3 localPos = new Vector3(0f, 2.5f, 0f);
            foreach (string key in buttonOrder)
            {
                GameObject button = null;
                if (key == "All")
                    button = allButton;
                else if (key == "Main")
                    button = mainButton;
                else if (key == "Extras")
                    button = extrasButton;
                else
                    button = filters[key].Button;

                button.transform.localPosition = localPos;
                localPos += new Vector3(0f, -3.5f, 0f);
            }
        }

        private static void GetReferences()
        {
            panel = GameObject.Find("menu/ShellPage_Song/page/ShellPanel_Left");
            glass = GameObject.Find("menu/ShellPage_Song/page/ShellPanel_Left/Glass");
            highlights = GameObject.Find("menu/ShellPage_Song/page/ShellPanel_Left/PanelFrame/highlights");
            allButton = GameObject.Find("menu/ShellPage_Song/page/ShellPanel_Left/FilterAll");
            mainButton = GameObject.Find("menu/ShellPage_Song/page/ShellPanel_Left/FilterMain");
            extrasButton = GameObject.Find("menu/ShellPage_Song/page/ShellPanel_Left/FilterExtras");
            notificationPanel = GameObject.Find("menu/ShellPage_Song/page/ShellPanel_Left/ShellPanel_SongListNotification");
            notificationText = notificationPanel.GetComponentInChildren<TextMeshPro>();
        }

        /// <summary>
        /// Activates the filter with the given ID, deactivating all others.
        /// </summary>
        public static void ActivateFilter(string filterID)
        {
            if (filters == null || !filters.ContainsKey(filterID)) return;
            DisableCustomFilters(filterID);
            filters[filterID].IsActive = true;
            filterStateChanged = true;
            filters[filterID].ButtonSelectedIndicator?.SetActive(true);
            filters[filterID].OnHit?.Invoke();
        }

        #region Favorites
        public static void SaveFavorites()
        {
            string text = JSON.Dump(favorites);
            try
            {
                int favCount = favorites.songIDs.Count;
                File.WriteAllText(favoritesPath + ".tmp", text);

                string saved = File.ReadAllText(favoritesPath + ".tmp");
                Favorites favs = JSON.Load(saved).Make<Favorites>();
                if (favCount == favs.songIDs.Count)
                {
                    if (File.Exists(favoritesPath))
                        File.Delete(favoritesPath);
                    File.Copy(favoritesPath + ".tmp", favoritesPath);
                }
                else
                {
                    KataConfig.I.CreateDebugText("Unable to save favorites", new Vector3(0f, -1f, 5f), 5f, null, false, 0.2f);
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Log($"[WARNING] Unable to save favorites: {ex.Message}");
                KataConfig.I.CreateDebugText("Unable to save favorites", new Vector3(0f, -1f, 5f), 5f, null, false, 0.2f);
            }
        }

        public static bool IsFavorite(string songID)
        {
            return favorites != null && favorites.songIDs.Contains(songID);
        }

        public static void AddFavorite(string songID)
        {
            var song = SongList.I.GetSong(songID);
            if (song == null) return;
            if (favorites.songIDs.Contains(songID))
            {
                favorites.songIDs.Remove(songID);
                KataConfig.I.CreateDebugText($"Removed {song.title} from favorites!", new Vector3(0f, -1f, 5f), 5f, null, false, 0.2f);
                SaveFavorites();
            }
            else
            {
                favorites.songIDs.Add(songID);
                KataConfig.I.CreateDebugText($"Added {song.title} to favorites!", new Vector3(0f, -1f, 5f), 5f, null, false, 0.2f);
                SaveFavorites();
            }

            // Update the Favorites folder if it's currently open
            FolderRowManager.RefreshFavorites();
        }

        private static void LoadFavorites()
        {
            if (File.Exists(favoritesPath))
            {
                try
                {
                    string text = File.ReadAllText(favoritesPath);
                    favorites = JSON.Load(text).Make<Favorites>();
                }
                catch (Exception ex)
                {
                    MelonLogger.Log($"[WARNING] Unable to load favorites from file: {ex.Message}");

                    string backupPath = favoritesPath + DateTime.Now.ToString("yyyyMMdd_hhmmss") + ".temp";
                    File.Copy(favoritesPath, backupPath);

                    favorites = new Favorites();
                    favorites.songIDs = new List<string>();
                }
            }
            else
            {
                favorites = new Favorites();
                favorites.songIDs = new List<string>();
            }
        }
        #endregion
    }
}

[Serializable]
class Favorites
{
    public List<string> songIDs;
}