using MelonLoader;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;

namespace ExScoringMod
{
    internal static class SongLoadingManager
    {
        public static HashSet<string> songIDs = new HashSet<string>();
        public static HashSet<string> songFilenames = new HashSet<string>();
        public static Dictionary<string, string> songDictionary = new Dictionary<string, string>();

        private static GunButton soloButton = null;
        private static TextMeshPro soloButtonLabel = null;
        private static string originalSoloButtonText = null;

        private static GunButton campaignButton = null;
        private static TextMeshPro campaignButtonLabel = null;
        private static string originalCampaignButtonText = null;

        private static GunButton partyButton = null;
        private static TextMeshPro partyButtonLabel = null;
        private static string originalPartyButtonText = null;

        private static bool searching = false;
        private static bool disabled = false;

        private static List<Action> postProcessingActions = new List<Action>();

        /// <summary>
        /// Register a callback to run after the song list finishes loading,
        /// before the UI is re-enabled.
        /// </summary>
        public static void AddPostProcessingCB(Action callback)
        {
            postProcessingActions.Add(callback);
        }

        /// <summary>
        /// Disables menu buttons and starts post-processing once the song list is loaded.
        /// </summary>
        public static void StartSongListUpdate(bool processOnSongListLoaded = false)
        {
            if (searching) return;

            searching = true;
            UpdateUI();

            if (processOnSongListLoaded)
            {
                SongList.OnSongListLoaded.On(new Action(() =>
                {
                    MelonCoroutines.Start(PostProcess());
                }));
            }
            else
            {
                MelonCoroutines.Start(PostProcess());
            }
        }

        /// <summary>
        /// Disables Solo, Campaign, and (if ModSettings is not installed) Party buttons
        /// while a song list reload is in progress.
        /// </summary>
        public static void UpdateUI()
        {
            if (!Config.SafeSongListReload) return;
            if ((!searching || disabled || MenuState.GetState() != MenuState.State.MainPage) &&
                !PlaylistDownloadManager.IsDownloadingMissing) return;

            MelonLogger.Log("SongLoadingManager: Disabling buttons");
            disabled = true;

            if (soloButton == null)
            {
                soloButton = GameObject.Find("menu/ShellPage_Main/page/ShellPanel_Center/Solo/Button").GetComponent<GunButton>();
                soloButtonLabel = GameObject.Find("menu/ShellPage_Main/page/ShellPanel_Center/Solo/Label").GetComponent<TextMeshPro>();
                GameObject.Destroy(soloButtonLabel.gameObject.GetComponent<Localizer>());
            }
            originalSoloButtonText = soloButtonLabel.text;
            soloButtonLabel.text = "Loading...";
            soloButton.SetInteractable(false);

            if (campaignButton == null)
            {
                campaignButton = GameObject.Find("menu/ShellPage_Main/page/ShellPanel_Center/Campaign/Button").GetComponent<GunButton>();
                campaignButtonLabel = GameObject.Find("menu/ShellPage_Main/page/ShellPanel_Center/Campaign/Label").GetComponent<TextMeshPro>();
                GameObject.Destroy(campaignButtonLabel.gameObject.GetComponent<Localizer>());
            }
            originalCampaignButtonText = campaignButtonLabel.text;
            campaignButtonLabel.text = "Loading...";
            campaignButton.SetInteractable(false);

            if (!ExScoring.modSettingsInstalled)
            {
                if (partyButton == null)
                {
                    partyButton = GameObject.Find("menu/ShellPage_Main/page/ShellPanel_Center/Party/Button").GetComponent<GunButton>();
                    partyButtonLabel = GameObject.Find("menu/ShellPage_Main/page/ShellPanel_Center/Party/Label").GetComponent<TextMeshPro>();
                    GameObject.Destroy(partyButtonLabel.gameObject.GetComponent<Localizer>());
                }
                originalPartyButtonText = partyButtonLabel.text;
                partyButtonLabel.text = "Loading...";
                partyButton.SetInteractable(false);
            }
        }

        private static IEnumerator PostProcess()
        {
            ExScoring.FixMappers();
            yield return null;

            // Pre-cache difficulty ratings for all songs
            // (only slow on first run; results are cached afterwards)
            songIDs.Clear();
            songFilenames.Clear();
            songDictionary.Clear();

            for (int i = 0; i < SongList.I.songs.Count; i++)
            {
                string songID = SongList.I.songs[i].songID;
                songIDs.Add(songID);

                string path = Path.GetFileName(SongList.I.songs[i].zipPath);
                if (!songFilenames.Contains(path)) songFilenames.Add(path);
                if (!songDictionary.ContainsKey(path)) songDictionary.Add(path, songID);

                DifficultyCalculator.GetRating(songID, KataConfig.Difficulty.Easy);
                DifficultyCalculator.GetRating(songID, KataConfig.Difficulty.Normal);
                DifficultyCalculator.GetRating(songID, KataConfig.Difficulty.Hard);
                DifficultyCalculator.GetRating(songID, KataConfig.Difficulty.Expert);
                yield return null;
            }

            SongSearch.Search();
            yield return null;

            foreach (Action cb in postProcessingActions)
            {
                cb();
                yield return null;
            }

            KataConfig.I.CreateDebugText("Songs Loaded", new Vector3(0f, -1f, 5f), 5f, null, false, 0.2f);

            EnableButtons();
            searching = false;
            yield return null;
        }

        /// <summary>
        /// Re-enables menu buttons after song list loading is complete.
        /// </summary>
        public static void EnableButtons()
        {
            if (!Config.SafeSongListReload) return;

            if (disabled)
            {
                soloButton.SetInteractable(true);
                soloButtonLabel.text = originalSoloButtonText;

                campaignButton.SetInteractable(true);
                campaignButtonLabel.text = originalCampaignButtonText;

                if (!ExScoring.modSettingsInstalled)
                {
                    partyButton.SetInteractable(true);
                    partyButtonLabel.text = originalPartyButtonText;
                }
            }

            disabled = false;
        }
    }
}