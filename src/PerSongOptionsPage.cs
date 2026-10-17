using System;
using System.Collections;
using MelonLoader;
using TMPro;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// The per-song settings page: a "Song Settings" button on the launch panel opens it in place of
    /// the launch panel (same hide-then-show flow as GlobalOptions' categories, drawn on the shared
    /// OptionsMenuClone — the two can't be on screen together since GlobalOptions owns the song
    /// list while it's open). Not reachable from the Options menu. Its Back row returns to the
    /// launch panel; selecting any song while it's open does the same, showing the new song.
    ///
    /// Values are signed offsets on top of the global settings (see PerSongSettings).
    /// </summary>
    internal static class PerSongOptionsPage
    {
        // ════════════════════════════════════════════════════════════════════════
        //  TWEAK HERE — button layout. LOCAL to NoFailPracticeToggle (same parent as the
        //  Add to Playlist button, which sits at 2.5, 2.4).
        // ════════════════════════════════════════════════════════════════════════
        private static Vector3 buttonPosition = new Vector3(2.5f, 1.3f, 0f);
        private static Vector3 buttonScale = new Vector3(0.75f, 0.75f, 0.75f);
        private static Vector3 buttonRotation = new Vector3(0f, 0f, 0f);
        private static Vector3 buttonLabelScale = new Vector3(1.5f, 1.5f, 1.5f);
        // ════════════════════════════════════════════════════════════════════════

        private const string ButtonLabel = "Song Settings";
        private const string ButtonLabelCustom = "Song Settings*";

        private static readonly string[] ForceHitSoundsOptions = { "Use Global", "On", "Off" };

        // Fallback if the launch page's hide animation length can't be read.
        private static float hideAnimDelay = 0.25f;

        private enum PageState { Closed, Opening, Open }

        private static GameObject songSettingsButton;
        private static PageState state = PageState.Closed;
        private static string openSongID;
        private static int openSeq = 0; // bumped on every open/close so a pending delayed draw bails
        private static GameObject launchPage;

        /// <summary>
        /// True while our page is (or is about to be) on screen. If GlobalOptions drew one of its
        /// categories over the shared clone since we opened, our page is gone even though we never
        /// closed it — treat that as closed.
        /// </summary>
        public static bool IsActive
        {
            get
            {
                if (state == PageState.Opening) return true;
                if (state != PageState.Open) return false;
                if (FolderRowManager.InGlobalOptions) return false;
                var menu = OptionsMenuClone.Menu;
                return menu != null && menu.gameObject.activeInHierarchy;
            }
        }

        // ── Launch panel button ────────────────────────────────────────────────

        public static void CreateButton()
        {
            if (PlaylistManager.state == PlaylistManager.PlaylistState.Endless) return;

            if (songSettingsButton != null) // Unity-null after a scene change → recreated below
            {
                UpdateButton();
                if (!MarathonSetup.Active) songSettingsButton.SetActive(true);
                return;
            }

            var refButton = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center/NoFailPracticeToggle/PracticeToggle");
            if (refButton == null) return;

            songSettingsButton = GameObject.Instantiate(refButton, refButton.transform.parent.transform);
            songSettingsButton.name = "SongSettingsButton";
            songSettingsButton.transform.localScale = buttonScale;
            ButtonUtils.InitButton(songSettingsButton, ButtonLabel,
                new Action(() => OnButtonShot()), buttonPosition, buttonRotation);

            TextMeshPro label = songSettingsButton.GetComponentInChildren<TextMeshPro>();
            if (label != null) label.transform.localScale = buttonLabelScale;

            if (MarathonSetup.Active) songSettingsButton.SetActive(false);
            UpdateButton();
        }

        /// <summary>Refreshes the label — an asterisk marks songs that have custom settings.</summary>
        public static void UpdateButton()
        {
            if (songSettingsButton == null) return;
            bool custom = PerSongSettings.HasCustomSettings(ExScoring.selectedSong);
            ButtonUtils.UpdateButtonLabel(songSettingsButton, custom ? ButtonLabelCustom : ButtonLabel);
        }

        public static void HideButton()
        {
            if (songSettingsButton != null) songSettingsButton.SetActive(false);
        }

        public static void ShowButton()
        {
            if (songSettingsButton != null) songSettingsButton.SetActive(true);
            UpdateButton();
        }

        private static void OnButtonShot()
        {
            if (string.IsNullOrEmpty(ExScoring.selectedSong)) return;
            if (MarathonSetup.Active) return;
            Open();
        }

        // ── Open / close ───────────────────────────────────────────────────────

        private static void Open()
        {
            if (IsActive) return;
            if (!OptionsMenuClone.EnsureClone()) return;

            openSongID = ExScoring.selectedSong;
            state = PageState.Opening;
            int seq = ++openSeq;

            launchPage = GameObject.Find("menu/ShellPage_Launch");
            DebugLog.Log($"[SongSettings] opening for {openSongID}");
            ExScoring.HideLaunchPanel();
            MelonCoroutines.Start(ShowAfterHide(seq));
        }

        private static float LaunchHideAnimLength()
        {
            if (launchPage != null)
            {
                var sp = launchPage.GetComponent<ShellPage>();
                if (sp != null && sp.hideAnim != null) return sp.hideAnim.length;
            }
            return hideAnimDelay;
        }

        private static IEnumerator ShowAfterHide(int seq)
        {
            yield return new WaitForSeconds(LaunchHideAnimLength());
            if (seq != openSeq || state != PageState.Opening) yield break;

            if (launchPage != null) launchPage.SetActive(false);
            yield return null;
            if (seq != openSeq || state != PageState.Opening) yield break;

            state = PageState.Open;
            Draw();
        }

        /// <summary>Returns to the launch panel (Back row, or another song was selected).</summary>
        public static void Close()
        {
            if (state == PageState.Closed) return;

            bool wasShowing = IsActive;
            state = PageState.Closed;
            openSeq++;
            openSongID = null;

            if (!wasShowing)
            {
                // GlobalOptions took over the clone since we opened, and its own HidePanel already
                // restored the launch panel — nothing left for us to undo.
                launchPage = null;
                UpdateButton();
                return;
            }

            DebugLog.Log("[SongSettings] closing, restoring launch panel");
            OptionsMenuClone.Wipe();
            OptionsMenuClone.Hide();

            if (launchPage != null) launchPage.SetActive(true);

            bool prevSuppress = ExScoring.suppressShellPageAnimations;
            ExScoring.suppressShellPageAnimations = false;
            ExScoring.ShowLaunchPanel(); // also re-activates the launch page if our cached ref is stale
            ExScoring.suppressShellPageAnimations = prevSuppress;

            if (string.IsNullOrEmpty(ExScoring.selectedSong))
                ExScoring.SetLaunchPanelContentVisible(false);
            else
                ExScoring.UpdateLaunchPanelInfo();

            launchPage = null;
            UpdateButton();
        }

        /// <summary>
        /// Called from the song-select hook BEFORE anything touches the launch panel (it's inactive
        /// while we're open, and GameObject.Find can't see inactive objects).
        /// </summary>
        public static void OnSongSelected()
        {
            if (state != PageState.Closed) Close();
        }

        /// <summary>
        /// Called when the marathon menu takes over the launch panel.
        /// </summary>
        public static void CloseIfOpen()
        {
            if (state != PageState.Closed) Close();
        }

        /// <summary>
        /// Song page is being torn down (e.g. SongPage -> MainPage). Clears our state without
        /// restoring the launch panel — RunSongPageSetup re-shows it when the song page returns.
        /// </summary>
        public static void ForceTeardown()
        {
            if (state == PageState.Closed) return;

            bool wasShowing = IsActive;
            state = PageState.Closed;
            openSeq++;
            openSongID = null;
            launchPage = null;

            if (wasShowing)
            {
                OptionsMenuClone.Wipe();
                OptionsMenuClone.Hide();
            }
        }

        // ── Page content ───────────────────────────────────────────────────────

        private static void Draw()
        {
            OptionsMenuClone.RestoreDefaultPlacement();
            OptionsMenuClone.Show();
            OptionsMenuClone.Draw(GetTitle(), Build);
        }

        /// <summary>Redraw next frame — never destroy rows from inside one of their own hit events.</summary>
        private static void RedrawNextFrame()
        {
            int seq = openSeq;
            MelonCoroutines.Start(RedrawRoutine(seq));
        }

        /// <summary>Back row: closing wipes the rows, so do it after the button's hit event returns.</summary>
        private static void CloseNextFrame()
        {
            int seq = openSeq;
            MelonCoroutines.Start(CloseRoutine(seq));
        }

        private static IEnumerator CloseRoutine(int seq)
        {
            yield return null;
            if (seq != openSeq) yield break; // already closed / reopened by something else
            Close();
        }

        private static IEnumerator RedrawRoutine(int seq)
        {
            yield return null;
            if (seq != openSeq || state != PageState.Open || !IsActive) yield break;
            Draw();
        }

        private static string GetTitle()
        {
            string title = null;
            var data = ExScoring.selectedSongData;
            if (data != null && data.songID == openSongID) title = data.title;
            return string.IsNullOrEmpty(title) ? "Song Settings" : "Song Settings - " + title;
        }

        private static void Build()
        {
            string songID = openSongID;

            var backButton = OptionsMenuClone.CreateButton(0, "Back",
                () => CloseNextFrame(),
                "Returns to the launch panel");
            var resetButton = OptionsMenuClone.CreateButton(1, "Reset Song Settings",
                () => { PerSongSettings.ResetSong(songID); UpdateButton(); RedrawNextFrame(); },
                "Sets every setting for this song back to the global value");
            OptionsMenuClone.AddRow(backButton, resetButton);

            var audioHeader = OptionsMenuClone.CreateHeader(0, "Audio");
            OptionsMenuClone.AddRow(audioHeader);

            var musicSlider = CreateOffsetSlider(0, "Music Level", songID, PerSongSettings.Field.MusicLevel,
                -10f, 10f, 1f,
                "Added to your global Music Level for this song only");
            var sfxSlider = CreateOffsetSlider(1, "Sfx Level", songID, PerSongSettings.Field.SfxLevel,
                -10f, 10f, 1f,
                "Added to your global Sfx Level for this song only");
            OptionsMenuClone.AddRow(musicSlider, sfxSlider);

            var hitSoundsCycle = OptionsMenuClone.CreateCycle(0, "Force Hit Sounds", ForceHitSoundsOptions,
                () => PerSongSettings.GetForceHitSoundsMode(songID),
                v => { PerSongSettings.SetForceHitSoundsMode(songID, v); UpdateButton(); },
                PerSongSettings.ForceHitSoundsGlobal,
                "Use Global follows your Options setting (currently " + (Config.ForceHitSounds ? "On" : "Off") + "). On/Off overrides it for this song.");
            OptionsMenuClone.AddRow(hitSoundsCycle);

            var calibrationHeader = OptionsMenuClone.CreateHeader(0, "Calibration");
            OptionsMenuClone.AddRow(calibrationHeader);

            var inputOffsetSlider = CreateOffsetSlider(0, "Input Offset", songID, PerSongSettings.Field.InputOffset,
                -200f, 200f, 1f,
                "Added to your global Input Offset (ms) for this song only");
            OptionsMenuClone.AddRow(inputOffsetSlider);

            var speedHeader = OptionsMenuClone.CreateHeader(0, "Speed");
            OptionsMenuClone.AddRow(speedHeader);

            var targetSpeedSlider = CreateOffsetSlider(0, "Target Speed", songID, PerSongSettings.Field.TargetSpeed,
                -400f, 400f, 10f,
                "Added to your global Target Speed for this song only");
            var meleeSpeedSlider = CreateOffsetSlider(1, "Melee Speed", songID, PerSongSettings.Field.MeleeSpeed,
                -400f, 400f, 10f,
                "Added to your global Melee Speed for this song only");
            OptionsMenuClone.AddRow(targetSpeedSlider, meleeSpeedSlider);

            var dartHeader = OptionsMenuClone.CreateHeader(0, "Dart");
            OptionsMenuClone.AddRow(dartHeader);

            var dartPreGlowSlider = CreateOffsetSlider(0, "Cue Dart Pre Glow", songID, PerSongSettings.Field.DartPreGlow,
                -400f, 400f, 10f,
                "Added to your global Cue Dart Pre Glow for this song only");
            var dartSpeedSlider = CreateOffsetSlider(1, "Dart Speed", songID, PerSongSettings.Field.DartSpeed,
                -400f, 400f, 10f,
                "Added to your global Dart Speed for this song only");
            OptionsMenuClone.AddRow(dartPreGlowSlider, dartSpeedSlider);
        }

        /// <summary>
        /// Offset slider: shows the signed offset and, in brackets, what the setting will actually
        /// be during the song (global + offset, clamped to the game's range).
        /// </summary>
        private static GameObject CreateOffsetSlider(int col, string label, string songID, PerSongSettings.Field field,
                                                     float min, float max, float step, string hover)
        {
            return OptionsMenuClone.CreateSlider(col, label,
                () => PerSongSettings.GetOffset(songID, field),
                v => { PerSongSettings.SetOffset(songID, field, v); UpdateButton(); },
                min, max, step, 0f,
                v => FormatOffset(songID, field, v),
                hover);
        }

        private static string FormatOffset(string songID, PerSongSettings.Field field, float offset)
        {
            bool percent = PerSongSettings.IsPercentField(field);
            string unit = percent ? "%" : (field == PerSongSettings.Field.InputOffset ? " ms" : "");

            string sign = offset > 0f ? "+" : "";
            string offsetText = sign + offset.ToString("N0") + unit;

            float result = PerSongSettings.GetResultValue(songID, field);
            string resultText = percent
                ? (result * 100f).ToString("N0") + "%"
                : result.ToString("N0");

            return offsetText + " (" + resultText + ")";
        }
    }
}