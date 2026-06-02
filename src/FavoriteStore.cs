using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader;
using MelonLoader.TinyJSON;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// Persistent favorites store (UserData/SongBrowserFavorites.json). Extracted from the
    /// retired FilterPanel; the Favorites folder in the virtual list reads through here.
    /// </summary>
    public static class FavoritesStore
    {
        internal static Favorites favorites;

        private static string favoritesPath =
            Application.dataPath + "/../" + "/UserData/" + "SongBrowserFavorites.json";

        public static void Load()
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

        public static void Save()
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
                Save();
            }
            else
            {
                favorites.songIDs.Add(songID);
                KataConfig.I.CreateDebugText($"Added {song.title} to favorites!", new Vector3(0f, -1f, 5f), 5f, null, false, 0.2f);
                Save();
            }

            // Update the Favorites folder if it's currently open
            FolderRowManager.RefreshFavorites();
        }
    }
}

[Serializable]
class Favorites
{
    public List<string> songIDs;
}