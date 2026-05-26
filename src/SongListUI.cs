using MelonLoader;
using TMPro;
using UnityEngine;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
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
            songSelectScrollUpArrowObject.transform.localPosition = new Vector3(0f, 2.13f, -3.71f);
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

            launchPanelLeftObject.SetActive(false);
            launchPanelBackButton.SetActive(false);
            launchPanelCenterContestInfoButton.SetActive(false);
            launchPanelCenterPanelFrameHighlightTop.SetActive(true);
            launchPanelCenterCommunityMapsButton.SetActive(false);
            launchPanelCenterHarmonixMapsButton.SetActive(false);
            launchPanelCenterCustomizeButton.SetActive(false);
            launchPanelModifiersButton.SetActive(false);
            launchPanelChooseDiffLabel.SetActive(false);

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
            launchPanelPlayButton.transform.localPosition = new Vector3(0f, -4.5f, 0f);

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
            launchPanelPlayButton.transform.localScale = new Vector3(2.5f, 2.5f, 2.5f);

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
        }

        public static void UpdateLaunchPanelInfo()
        {
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
                Transform indicator = button.transform.Find("SelectedIndicator");
                if (indicator != null)
                    indicator.gameObject.SetActive(difficulties[i] == difficulty);
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
            SongSelect songSelect = null;
            Il2CppSystem.Collections.Generic.List<SongSelectItem> buttons = null;

            float timeout = 3f;
            float elapsed = 0f;

            while (elapsed < timeout)
            {
                yield return new WaitForSeconds(0.1f);
                elapsed += 0.1f;

                var songSelectObj = GameObject.Find("menu/ShellPage_Song/page/ShellPanel_Center/SongSelect");
                if (songSelectObj == null) continue;

                songSelect = songSelectObj.GetComponent<SongSelect>();
                if (songSelect == null) continue;

                buttons = songSelect.mSongButtons;
                if (buttons != null && buttons.Count > 0) break;
            }

            if (buttons == null || buttons.Count == 0) yield break;

            SongSelectItem targetItem = null;

            if (selectedSong != null)
            {
                for (int i = 0; i < buttons.Count; i++)
                {
                    var item = buttons[i];
                    if (item != null && item.mSongData != null && item.mSongData.songID == selectedSong)
                    {
                        targetItem = item;
                        break;
                    }
                }
            }

            if (targetItem == null)
            {
                for (int i = 0; i < buttons.Count; i++)
                {
                    var item = buttons[i];
                    if (item != null && item.mSongData != null)
                    {
                        targetItem = item;
                        break;
                    }
                }
            }

            if (targetItem == null) yield break;

            isAutoSelecting = true;
            try
            {
                targetItem.OnSelect();
                UpdateLaunchPanelInfo();
            }
            finally
            {
                isAutoSelecting = false;
            }

            menuState = MenuState.State.SongPage;
        }
    }
}
