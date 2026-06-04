using MelonLoader;
using TMPro;
using UnityEngine;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        // ── Song selection indicator tuning ──
        private static Vector3 songSelectionIndicatorScale = new Vector3(3.625f, 1.5f, 1f);
        private static Vector3 songSelectionIndicatorPosition = new Vector3(0f, 0f, -0.05f);

        private static GameObject currentSongSelectionIndicator = null;

        /// <summary>
        /// Shows a selection indicator on the currently selected song list item.
        /// Creates the indicator lazily on first selection of each item.
        /// </summary>
        public static void UpdateSongSelectionIndicator(SongSelectItem selectedItem)
        {
            if (difficultyIndicatorSource == null) return;

            // Hide the previous indicator
            if (currentSongSelectionIndicator != null)
                currentSongSelectionIndicator.SetActive(false);

            if (selectedItem == null) return;

            // Reuse existing indicator if already created for this item
            Transform existing = selectedItem.transform.Find("SelectedIndicator");
            if (existing != null)
            {
                currentSongSelectionIndicator = existing.gameObject;

                // Keep the indicator below the row's Canvas (canvas = 0, indicator = -1)
                MeshRenderer existingRenderer = existing.GetComponent<MeshRenderer>();
                if (existingRenderer != null)
                    existingRenderer.sortingOrder = -1;

                currentSongSelectionIndicator.SetActive(true);
                return;
            }

            // First time selecting this item — create the indicator
            GameObject indicator = GameObject.Instantiate(difficultyIndicatorSource, selectedItem.transform);
            indicator.name = "SelectedIndicator";
            indicator.transform.localPosition = songSelectionIndicatorPosition;
            indicator.transform.localRotation = Quaternion.identity;
            indicator.transform.localScale = songSelectionIndicatorScale;

            MeshRenderer renderer = indicator.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                renderer.material.color = new Color(1f, 1f, 1f, 1f);
                renderer.sortingOrder = -1; // draw beneath the row Canvas (sortingOrder 0)
            }

            currentSongSelectionIndicator = indicator;
            currentSongSelectionIndicator.SetActive(true);
        }

        public static void SongListUISetup()
        {
            if (songListUISetup) return;

            GameObject songSelectObject = GameObject.Find("menu/ShellPage_Song/page/ShellPanel_Center/SongSelect");
            if (songSelectObject == null) return;

            songListUISetup = true;

            GameObject songSelectScrollUpArrowObject = GameObject.Find("menu/ShellPage_Song/page/ShellPanel_Center/SongSelect/ScrollUpArrow");
            GameObject songSelectScrollDownArrowObject = GameObject.Find("menu/ShellPage_Song/page/ShellPanel_Center/SongSelect/ScrollDownArrow");
            GameObject songSelectButtonParentObject = GameObject.Find("menu/ShellPage_Song/page/ShellPanel_Center/SongSelect/ButtonParent");
            GameObject songSelectSortButtonObject = GameObject.Find("menu/ShellPage_Song/page/ShellPanel_Center/SongSelect/SortButton");
            GameObject songSelectSortMenuObject = GameObject.Find("menu/ShellPage_Song/page/ShellPanel_Center/SongSelect/SortMenu");

            ShellScrollable songSelectScrollable = songSelectObject.GetComponent<ShellScrollable>();

            songSelectObject.transform.localScale = new Vector3(0.5f, 1f, 0.5f);
            songSelectButtonParentObject.transform.localScale = new Vector3(1f, 0.5f, 1f);
            songSelectScrollUpArrowObject.transform.localScale = new Vector3(3f, 1.5f, 3f);
            songSelectScrollDownArrowObject.transform.localScale = new Vector3(3f, 1.5f, 3f);
            songSelectSortButtonObject.transform.localScale = new Vector3(1f, 0.5f, 1f);
            songSelectSortMenuObject.transform.localScale = new Vector3(1f, 0.5f, 1f);

            songSelectObject.transform.localPosition = new Vector3(-5.98f, 8.91f, 0f);
            songSelectSortButtonObject.transform.localPosition = new Vector3(-10.17f, 2.26f, -0.46f);
            songSelectScrollUpArrowObject.transform.localPosition = new Vector3(0f, 0.75f, -3.71f);
            songSelectScrollDownArrowObject.transform.localPosition = new Vector3(0f, -24.75f, -2.99f);
            songSelectSortMenuObject.transform.localPosition = new Vector3(-17.74f, 0.82f, -2.7913f);

            songSelectSortMenuObject.transform.localEulerAngles = new Vector3(0f, 330f, 0f);

            songSelectScrollable.spacing = 3.1f;
            songSelectScrollable.displayCount = 15;

            FixMappers();
        }

        public static void LaunchPanelUISetup()
        {
            if (launchPanelUISetup) return;
            launchPanelUISetup = true;

            GameObject launchPanelLeftObject = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Left");
            GameObject launchPanelBackButton = GameObject.Find("menu/ShellPage_Launch/page/backParent");

            GameObject launchPanelCenterObject = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center");
            GameObject launchPanelCenterGlass = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center/Glass");
            GameObject launchPanelCenterPanelFrame = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center/PanelFrame");
            GameObject launchPanelCenterPanelFramePanelLeft = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center/PanelFrame/panel_left");
            GameObject launchPanelCenterPanelFramePanelMid = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center/PanelFrame/panel_mid");
            GameObject launchPanelCenterPanelFramePanelRight = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center/PanelFrame/panel_right");
            GameObject launchPanelCenterPanelFrameHighlightTop = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center/PanelFrame/highlights/highlight_top");
            GameObject launchPanelCenterContestInfoButton = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center/ContestInfo");
            GameObject launchPanelCenterCommunityMapsButton = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center/CommunityMaps");
            GameObject launchPanelCenterHarmonixMapsButton = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center/HarmonixMaps");
            GameObject launchPanelCenterCustomizeButton = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center/CustomizeMenuButton");
            GameObject launchPanelCenterTitleLabel = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center/Title");
            GameObject launchPanelSongPreviewCheckbox = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center/SongPreviewCheckbox");
            GameObject launchPanelModifiersButton = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center/Modifiers");
            GameObject launchPanelNoFailToggle = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center/NoFailPracticeToggle/NoFailToggle");
            GameObject launchPanelPracticeToggle = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center/NoFailPracticeToggle/PracticeToggle");
            GameObject launchPanelDifficultyButtonEasy = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center/ChooseDiff_easy");
            GameObject launchPanelDifficultyButtonNormal = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center/ChooseDiff_normal");
            GameObject launchPanelDifficultyButtonHard = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center/ChooseDiff_hard");
            GameObject launchPanelDifficultyButtonExpert = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center/ChooseDiff_expert");
            GameObject launchPanelChooseDiffLabel = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center/choosediff");
            GameObject launchPanelPlayButton = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center/play");

            GameObject launchPanelRightObject = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Right");

            launchPanelBackButton.SetActive(false);
            launchPanelCenterContestInfoButton.SetActive(false);
            launchPanelCenterPanelFrameHighlightTop.SetActive(true);
            launchPanelCenterCommunityMapsButton.SetActive(false);
            launchPanelCenterHarmonixMapsButton.SetActive(false);
            launchPanelCenterCustomizeButton.SetActive(false);
            launchPanelModifiersButton.SetActive(false);
            launchPanelChooseDiffLabel.SetActive(false);

            launchPanelLeftObject.transform.localPosition = new Vector3(-22.0736f, 14f, 14.567f);

            launchPanelCenterObject.transform.localPosition = new Vector3(9.18f, 6f, 24f);
            launchPanelCenterGlass.transform.localPosition = new Vector3(0f, 1.4f, 0.15f);
            launchPanelCenterPanelFrame.transform.localPosition = new Vector3(0f, 14f, -0.1f);
            launchPanelCenterPanelFrameHighlightTop.transform.localPosition = new Vector3(0.11f, 10.2322f, 0.21f);
            launchPanelCenterPanelFramePanelLeft.transform.localPosition = new Vector3(-6.6f, -0.75f, 0.18f);
            launchPanelCenterPanelFramePanelMid.transform.localPosition = new Vector3(0.3f, -0.75f, 0.18f);
            launchPanelCenterPanelFramePanelRight.transform.localPosition = new Vector3(6.67f, -0.75f, 0.18f);
            launchPanelCenterTitleLabel.transform.localPosition = new Vector3(-1.3f, 13.4f, 0f);
            launchPanelSongPreviewCheckbox.transform.localPosition = new Vector3(-4.5f, 10.25f, 0f);
            launchPanelNoFailToggle.transform.localPosition = new Vector3(0.5f, 17.075f, 0f);
            launchPanelPracticeToggle.transform.localPosition = new Vector3(5f, 17.075f, 0f);
            launchPanelDifficultyButtonEasy.transform.localPosition = new Vector3(-3.5f, -7.25f, 0f);
            launchPanelDifficultyButtonNormal.transform.localPosition = new Vector3(-3.5f, -9.5f, 0f);
            launchPanelDifficultyButtonHard.transform.localPosition = new Vector3(3.5f, -7.25f, 0f);
            launchPanelDifficultyButtonExpert.transform.localPosition = new Vector3(3.5f, -9.5f, 0f);
            launchPanelPlayButton.transform.localPosition = new Vector3(-3.45f, -4.8f, 0f);

            launchPanelRightObject.transform.localPosition = new Vector3(22.0736f, 9.5f, 14.567f);

            launchPanelCenterGlass.transform.localScale = new Vector3(14.4f, 25f, 3f);
            launchPanelCenterPanelFrame.transform.localScale = new Vector3(0.7f, 1.8f, 1f);
            launchPanelCenterPanelFrameHighlightTop.transform.localScale = new Vector3(2.61f, 0.0798f, 1.74f);
            launchPanelCenterPanelFramePanelLeft.transform.localScale = new Vector3(0.9858f, 0.85f, 2.5276f);
            launchPanelCenterPanelFramePanelMid.transform.localScale = new Vector3(0.9858f, 0.85f, 2.5276f);
            launchPanelCenterPanelFramePanelRight.transform.localScale = new Vector3(0.9858f, 0.85f, 2.5276f);
            launchPanelCenterTitleLabel.transform.localScale = new Vector3(1.75f, 1.75f, 1.75f);
            launchPanelSongPreviewCheckbox.transform.localScale = new Vector3(0.75f, 0.75f, 0.75f);
            launchPanelNoFailToggle.transform.localScale = new Vector3(0.75f, 0.75f, 0.75f);
            launchPanelPracticeToggle.transform.localScale = new Vector3(0.75f, 0.75f, 0.75f);
            launchPanelDifficultyButtonEasy.transform.localScale = new Vector3(2f, 2f, 2f);
            launchPanelDifficultyButtonNormal.transform.localScale = new Vector3(2f, 2f, 2f);
            launchPanelDifficultyButtonHard.transform.localScale = new Vector3(2f, 2f, 2f);
            launchPanelDifficultyButtonExpert.transform.localScale = new Vector3(2f, 2f, 2f);
            launchPanelPlayButton.transform.localScale = new Vector3(2f, 2f, 2f);

            launchPanelCenterTitleLabel.GetComponent<TextMeshPro>().alignment = TextAlignmentOptions.Left;

            CreateTextObject(
                "ArtistLabel",
                "artist",
                launchPanelCenterTitleLabel.transform.parent,
                launchPanelCenterTitleLabel.gameObject.layer,
                launchPanelCenterTitleLabel.GetComponent<TextMeshPro>(),
                launchPanelCenterTitleLabel.GetComponent<RectTransform>(),
                new Vector3(-2.15f, 12.3f, 0f),
                new Vector3(1.5f, 1.5f, 1.5f)
                );

            CreateTextObject(
                "MapperLabel",
                "mapper",
                launchPanelCenterTitleLabel.transform.parent,
                launchPanelCenterTitleLabel.gameObject.layer,
                launchPanelCenterTitleLabel.GetComponent<TextMeshPro>(),
                launchPanelCenterTitleLabel.GetComponent<RectTransform>(),
                new Vector3(-3.2f, 11.4f, 0f),
                new Vector3(1.2f, 1.2f, 1.2f)
                );

            CreateTextObject(
                "TempoLabel",
                "tempo",
                launchPanelCenterTitleLabel.transform.parent,
                launchPanelCenterTitleLabel.gameObject.layer,
                launchPanelCenterTitleLabel.GetComponent<TextMeshPro>(),
                launchPanelCenterTitleLabel.GetComponent<RectTransform>(),
                new Vector3(3f, 11.4f, 0f),
                new Vector3(1.2f, 1.2f, 1.2f),
                TextAlignmentOptions.Right
                );

            CreateTextObject(
                "TotalTargetsLabel",
                "Total",
                launchPanelCenterTitleLabel.transform.parent,
                launchPanelCenterTitleLabel.gameObject.layer,
                launchPanelCenterTitleLabel.GetComponent<TextMeshPro>(),
                launchPanelCenterTitleLabel.GetComponent<RectTransform>(),
                new Vector3(-3f, 9f, 0f),
                new Vector3(1.2f, 1.2f, 1.2f)
                );

            CreateTextObject(
                "TotalTargets",
                "targets",
                launchPanelCenterTitleLabel.transform.parent,
                launchPanelCenterTitleLabel.gameObject.layer,
                launchPanelCenterTitleLabel.GetComponent<TextMeshPro>(),
                launchPanelCenterTitleLabel.GetComponent<RectTransform>(),
                new Vector3(10f, 6.2f, 0f),
                new Vector3(5f, 5f, 5f)
                );

            CreateTextObject(
                "RightHandTargetsLabel",
                "Right",
                launchPanelCenterTitleLabel.transform.parent,
                launchPanelCenterTitleLabel.gameObject.layer,
                launchPanelCenterTitleLabel.GetComponent<TextMeshPro>(),
                launchPanelCenterTitleLabel.GetComponent<RectTransform>(),
                new Vector3(-1.5f, 9f, 0f),
                new Vector3(1.2f, 1.2f, 1.2f)
                );

            CreateTextObject(
                "TotalRightHandTargets",
                "rightHandTargets",
                launchPanelCenterTitleLabel.transform.parent,
                launchPanelCenterTitleLabel.gameObject.layer,
                launchPanelCenterTitleLabel.GetComponent<TextMeshPro>(),
                launchPanelCenterTitleLabel.GetComponent<RectTransform>(),
                new Vector3(9.5f, 6.5f, 0f),
                new Vector3(4.4f, 4.4f, 4.4f)
                );

            CreateTextObject(
                "LeftHandTargetsLabel",
                "Left",
                launchPanelCenterTitleLabel.transform.parent,
                launchPanelCenterTitleLabel.gameObject.layer,
                launchPanelCenterTitleLabel.GetComponent<TextMeshPro>(),
                launchPanelCenterTitleLabel.GetComponent<RectTransform>(),
                new Vector3(0f, 9f, 0f),
                new Vector3(1.2f, 1.2f, 1.2f)
                );

            CreateTextObject(
                "TotalLeftHandTargets",
                "leftHandTargets",
                launchPanelCenterTitleLabel.transform.parent,
                launchPanelCenterTitleLabel.gameObject.layer,
                launchPanelCenterTitleLabel.GetComponent<TextMeshPro>(),
                launchPanelCenterTitleLabel.GetComponent<RectTransform>(),
                new Vector3(11f, 6.5f, 0f),
                new Vector3(4.4f, 4.4f, 4.4f)
                );

            CreateTextObject(
                "EitherHandTargetsLabel",
                "Either",
                launchPanelCenterTitleLabel.transform.parent,
                launchPanelCenterTitleLabel.gameObject.layer,
                launchPanelCenterTitleLabel.GetComponent<TextMeshPro>(),
                launchPanelCenterTitleLabel.GetComponent<RectTransform>(),
                new Vector3(1.5f, 9f, 0f),
                new Vector3(1.2f, 1.2f, 1.2f)
                );

            CreateTextObject(
                "TotalEitherHandTargets",
                "eitherHandTargets",
                launchPanelCenterTitleLabel.transform.parent,
                launchPanelCenterTitleLabel.gameObject.layer,
                launchPanelCenterTitleLabel.GetComponent<TextMeshPro>(),
                launchPanelCenterTitleLabel.GetComponent<RectTransform>(),
                new Vector3(12.5f, 6.5f, 0f),
                new Vector3(4.4f, 4.4f, 4.4f)
                );

            RefreshIntensityGraph();
            SetupDifficultyIndicators();
            SetupFavoriteButton();
            CreateDeleteButton(ButtonUtils.ButtonLocation.Menu);
            AddPlaylistButton.CreatePlaylistButton(ButtonUtils.ButtonLocation.Menu);

            if (songDataLoaderInstalled)
            {
                Transform parent = launchPanelCenterTitleLabel.transform.parent;
                int layer = launchPanelCenterTitleLabel.gameObject.layer;
                AlbumArt.CreateAlbumArtCanvas(parent, layer);
            }

            if (string.IsNullOrEmpty(selectedSong))
                SetLaunchPanelContentVisible(false);
        }

        public static void UpdateLaunchPanelInfo()
        {
            SetLaunchPanelContentVisible(true);

            EnsureValidDifficultySelected();

            CueStatsData stats = GetCueStats(SongDataHolder.I.songData, KataConfig.I.mDifficulty);

            GameObject launchPanelCenterArtistLabel = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center/ArtistLabel");
            GameObject launchPanelCenterTitleLabel = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center/Title");
            GameObject launchPanelCenterMapperLabel = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center/MapperLabel");
            GameObject launchPanelCenterTempoLabel = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center/TempoLabel");
            GameObject launchPanelCenterTotalTargets = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center/TotalTargets");
            GameObject launchPanelCenterTotalRightHandTargets = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center/TotalRightHandTargets");
            GameObject launchPanelCenterTotalLeftHandTargets = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center/TotalLeftHandTargets");
            GameObject launchPanelCenterTotalEitherHandTargets = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center/TotalEitherHandTargets");

            launchPanelCenterArtistLabel.GetComponent<TextMeshPro>().text = SongDataHolder.I.songData.artist;
            launchPanelCenterTitleLabel.GetComponent<TextMeshPro>().text = SongDataHolder.I.songData.title;
            launchPanelCenterMapperLabel.GetComponent<TextMeshPro>().text = SongDataHolder.I.songData.author;
            launchPanelCenterTempoLabel.GetComponent<TextMeshPro>().text = $"BPM: {GetTempoString(SongDataHolder.I.songData.tempos)}";
            launchPanelCenterTotalTargets.GetComponent<TextMeshPro>().text = GetCueStatsString(stats);
            launchPanelCenterTotalRightHandTargets.GetComponent<TextMeshPro>().text = GetRightHandCueStatsString(stats);
            launchPanelCenterTotalLeftHandTargets.GetComponent<TextMeshPro>().text = GetLeftHandCueStatsString(stats);
            launchPanelCenterTotalEitherHandTargets.GetComponent<TextMeshPro>().text = GetEitherHandCueStatsString(stats);

            UpdateFavoriteIndicator();
            UpdateDeleteButtonEnabled();
            UpdateDifficultyButtonsEnabled();
            AddPlaylistButton.CreatePlaylistButton(ButtonUtils.ButtonLocation.Menu);

            // These get re-activated by the game's LaunchPanel.OnEnable (notably on the first song
            // after a fresh launch). They are never part of the redesigned panel, so keep them hidden.
            Transform launchCenter = launchPanelCenterTitleLabel.transform.parent;
            HideLaunchPanelElement(launchCenter, "Modifiers"); // capital: button w/ children
            HideLaunchPanelElement(launchCenter, "modifiers"); // lowercase: separate leaf object
            HideLaunchPanelElement(launchCenter, "MapperAlert");

            if (songDataLoaderInstalled)
                AlbumArt.UpdateAlbumArt(SongDataHolder.I.songData.songID);
        }

        /// <summary>Recursively find a descendant by name (incl. inactive) under root and deactivate it.</summary>
        private static void HideLaunchPanelElement(Transform root, string name)
        {
            if (root == null) return;
            Transform t = FindDescendant(root, name);
            if (t != null && t.gameObject.activeSelf) t.gameObject.SetActive(false);
        }

        private static Transform FindDescendant(Transform parent, string name)
        {
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform c = parent.GetChild(i);
                if (c.name == name) return c;
                Transform r = FindDescendant(c, name);
                if (r != null) return r;
            }
            return null;
        }

        public static void SetupDifficultyIndicators()
        {
            if (difficultyUISetup) return;
            difficultyUISetup = true;

            difficultyIndicatorSource = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center/play/SelectedIndicator");

            string basePath = "menu/ShellPage_Launch/page/ShellPanel_Center/";
            string[] diffs = new string[] { "ChooseDiff_easy", "ChooseDiff_normal", "ChooseDiff_hard", "ChooseDiff_expert" };

            foreach (string diff in diffs)
            {
                GameObject button = GameObject.Find(basePath + diff);

                GunButton gunButton = button.transform.Find("Button").GetComponent<GunButton>();
                gunButton.destroyOnShot = false;
                gunButton.disableOnShot = false;
                gunButton.doMeshExplosion = false;
                gunButton.doParticles = false;

                Transform existing = button.transform.Find("SelectedIndicator");
                if (existing != null) GameObject.Destroy(existing.gameObject);

                GameObject clone = GameObject.Instantiate(difficultyIndicatorSource, button.transform);
                clone.name = "SelectedIndicator";
                clone.transform.localPosition = new Vector3(0f, 0f, -0.005f);
                clone.transform.localRotation = Quaternion.identity;
                clone.transform.localScale = new Vector3(0.39f, 0.49f, 1f);

                MeshRenderer renderer = clone.GetComponent<MeshRenderer>();
                if (renderer != null)
                    renderer.material.color = new Color(1f, 1f, 1f, 1f);

                clone.SetActive(false);
            }

            UpdateDifficultyIndicator(KataConfig.I.GetDifficulty());
        }

        public static void UpdateDifficultyIndicator(KataConfig.Difficulty difficulty)
        {
            string basePath = "menu/ShellPage_Launch/page/ShellPanel_Center/";
            string[] diffs = new string[] { "ChooseDiff_easy", "ChooseDiff_normal", "ChooseDiff_hard", "ChooseDiff_expert" };
            KataConfig.Difficulty[] difficulties = new KataConfig.Difficulty[] { KataConfig.Difficulty.Easy, KataConfig.Difficulty.Normal, KataConfig.Difficulty.Hard, KataConfig.Difficulty.Expert };

            for (int i = 0; i < diffs.Length; i++)
            {
                GameObject button = GameObject.Find(basePath + diffs[i]);
                if (button == null) continue;
                Transform indicator = button.transform.Find("SelectedIndicator");
                if (indicator != null)
                    indicator.gameObject.SetActive(difficulties[i] == difficulty);
            }
        }

        /// <summary>True if the given song actually has cues for the given difficulty.</summary>
        public static bool SongHasDifficulty(SongList.SongData songData, KataConfig.Difficulty difficulty)
        {
            if (songData == null) return false;
            var cues = SongCues.GetCues(songData, difficulty);
            return cues != null && cues.Length > 0;
        }

        /// <summary>
        /// Dims difficulty buttons (label alpha only) and blocks selection for difficulties
        /// the current song doesn't have, matching the disabled delete button style.
        /// </summary>
        public static void UpdateDifficultyButtonsEnabled()
        {
            if (SongDataHolder.I == null || SongDataHolder.I.songData == null) return;

            string basePath = "menu/ShellPage_Launch/page/ShellPanel_Center/";
            string[] diffs = new string[] { "ChooseDiff_easy", "ChooseDiff_normal", "ChooseDiff_hard", "ChooseDiff_expert" };
            KataConfig.Difficulty[] difficulties = new KataConfig.Difficulty[] { KataConfig.Difficulty.Easy, KataConfig.Difficulty.Normal, KataConfig.Difficulty.Hard, KataConfig.Difficulty.Expert };

            for (int i = 0; i < diffs.Length; i++)
            {
                GameObject button = GameObject.Find(basePath + diffs[i]);
                if (button == null) continue;

                bool available = SongHasDifficulty(SongDataHolder.I.songData, difficulties[i]);

                Transform buttonChild = button.transform.Find("Button");
                if (buttonChild != null)
                {
                    GunButton gunButton = buttonChild.GetComponent<GunButton>();
                    if (gunButton != null) gunButton.SetInteractable(available);
                }

                TextMeshPro label = button.GetComponentInChildren<TextMeshPro>(true);
                if (label != null) label.alpha = available ? 1.0f : 0.25f;
            }
        }

        /// <summary>
        /// If the currently selected difficulty isn't present on the current song, switch to the
        /// closest available one (ties resolve to the easier side).
        ///
        /// Only the gameplay difficulty (mDifficulty) is set here, via the field directly rather
        /// than SetDifficulty() (which triggers the game's song-list refresh). The displayed
        /// difficulty is kept in sync separately, every frame, by SyncDisplayingDifficulty().
        /// </summary>
        public static void EnsureValidDifficultySelected()
        {
            if (SongDataHolder.I == null || SongDataHolder.I.songData == null) return;

            KataConfig.Difficulty[] difficulties = new KataConfig.Difficulty[] { KataConfig.Difficulty.Easy, KataConfig.Difficulty.Normal, KataConfig.Difficulty.Hard, KataConfig.Difficulty.Expert };

            bool[] available = new bool[difficulties.Length];
            for (int i = 0; i < difficulties.Length; i++)
                available[i] = SongHasDifficulty(SongDataHolder.I.songData, difficulties[i]);

            KataConfig.Difficulty current = KataConfig.I.GetDifficulty();
            int currentIndex = System.Array.IndexOf(difficulties, current);
            if (currentIndex >= 0 && available[currentIndex]) return; // already valid

            // Pick closest available; ascending iteration with strict '<' keeps the easier side on ties.
            int best = -1;
            int bestDist = int.MaxValue;
            for (int i = 0; i < difficulties.Length; i++)
            {
                if (!available[i]) continue;
                int dist = Mathf.Abs(i - currentIndex);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = i;
                }
            }

            if (best < 0) return; // song has no difficulties at all

            KataConfig.Difficulty chosen = difficulties[best];

            // Set the field the gameplay reads, NOT SetDifficulty() (which triggers ShowSongList).
            // SyncDisplayingDifficulty() propagates this to mDisplayingForDifficulty next frame.
            KataConfig.I.mDifficulty = chosen;

            UpdateDifficultyIndicator(chosen);
            RefreshIntensityGraph();
        }

        private static SongSelect cachedSongSelect;
        private static KataConfig.Difficulty? lastIndicatorDifficulty = null;

        /// <summary>
        /// Keeps both the song list's displayed difficulty AND the difficulty-button selection
        /// indicator in sync with the gameplay difficulty. The game tries to keep the displayed
        /// difficulty matched itself by calling ShowSongList whenever they differ, but in folder
        /// mode that refresh NREs and never reconciles, so a mismatch makes it re-request the
        /// rebuild every frame (a hang). The indicator is driven here too because the one-time
        /// update at correction time can run before the buttons exist. Both run from OnLateUpdate,
        /// when the relevant objects are reliably present. Setting these does not trigger a refresh.
        /// </summary>
        public static void SyncDisplayingDifficulty()
        {
            if (string.IsNullOrEmpty(selectedSong)) { lastIndicatorDifficulty = null; return; }

            var state = MenuState.GetState();
            if (state != MenuState.State.SongPage && state != MenuState.State.LaunchPage)
            {
                lastIndicatorDifficulty = null;
                return;
            }

            KataConfig.Difficulty diff = KataConfig.I.mDifficulty;

            // Displayed difficulty (song-list scores/stars) — what stops the ShowSongList loop.
            if (cachedSongSelect == null)
            {
                var obj = GameObject.Find("menu/ShellPage_Song/page/ShellPanel_Center/SongSelect");
                if (obj != null) cachedSongSelect = obj.GetComponent<SongSelect>();
            }
            if (cachedSongSelect != null && cachedSongSelect.mDisplayingForDifficulty != diff)
                cachedSongSelect.mDisplayingForDifficulty = diff;

            // Difficulty-button selection indicator — only re-applies on change (no per-frame Find churn).
            if (lastIndicatorDifficulty != diff)
            {
                UpdateDifficultyIndicator(diff);
                lastIndicatorDifficulty = diff;
            }
        }

        public static void ShowLaunchPanel()
        {
            // Activate the panel
            GameObject launchPageObj = GameObject.Find("menu/ShellPage_Launch");
            launchPageObj.SetActive(true);

            // Play appear animation
            ShellPage shellPage = launchPageObj.GetComponent<ShellPage>();

            if (!suppressShellPageAnimations)
            {
                shellPage.mAnimation.clip = shellPage.appearAnim;
                shellPage.mAnimation.Play();
            }

            // Set ShellPage to active and interactable state
            shellPage.mActive = true;
            shellPage.mInteractable = true;
            shellPage.mTransitioning = false;
        }

        public static void HideLaunchPanel()
        {
            GameObject launchPageObj = GameObject.Find("menu/ShellPage_Launch");
            ShellPage shellPage = launchPageObj.GetComponent<ShellPage>();

            shellPage.mActive = false;
            shellPage.mInteractable = false;

            shellPage.mAnimation.clip = shellPage.hideAnim;
            shellPage.mAnimation.Play();
        }

        public static void AutoSelectSong()
        {
            MelonCoroutines.Start(AutoSelectSongCoroutine());
        }

        private static System.Collections.IEnumerator AutoSelectSongCoroutine()
        {
            float timeout = 3f, elapsed = 0f;
            while (elapsed < timeout && !VirtualSongList.IsActive)
            {
                yield return new WaitForSeconds(0.1f);
                elapsed += 0.1f;
            }
            if (!VirtualSongList.IsActive) yield break;

            if (string.IsNullOrEmpty(selectedSong))
            {
                SetLaunchPanelContentVisible(false);
                yield break;
            }

            yield return null;
            FolderRowManager.RevealAndSelect(selectedSong);
        }
    }
}