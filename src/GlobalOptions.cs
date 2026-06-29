using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using MelonLoader;

namespace ExScoringMod
{
    internal class OptionsCategory
    {
        public string Id;
        public string Title;
        public Action Build;
        public OptionsCategory(string id, string title, Action build) { Id = id; Title = title; Build = build; }
    }

    /// <summary>
    /// Global options: a drill-in level showing Back + category rows in the song list. Shooting a
    /// category hides the launch panel (animated) and draws that category's controls on the clone.
    /// </summary>
    internal static class GlobalOptions
    {
        private static OptionsCategory activeCategory;
        private static bool restorePending;
        private static string restoreCategoryId;
        public static bool HasPendingRestore => restorePending;

        private static GameObject launchPage;

        public static readonly Color OptionsRowColor = new Color(0.24f, 0.22f, 0.30f, 1f);
        private static readonly Color CategoryColor = new Color(0.26f, 0.26f, 0.30f, 1f);

        // Time to let HideLaunchPanel's animation play before showing the clone. Tune if needed.
        private static float hideAnimDelay = 0.25f;

        private static bool panelShown = false;

        private static readonly List<OptionsCategory> Categories = new List<OptionsCategory>
        {
            new OptionsCategory("opt_audiovideo", "Audio/Video And Calibration", () =>            {
                OptionsMenuFunctions.GetParticleMode();
                OptionsMenuFunctions.GetMusicLevel();
                OptionsMenuFunctions.GetSfxLevel();
                OptionsMenuFunctions.GetMissFilter();
                OptionsMenuFunctions.GetWeaponSfxMode();
                OptionsMenuFunctions.GetMineSounds();
                OptionsMenuFunctions.GetInputOffset();
                OptionsMenuFunctions.GetVideoOffset();
                OptionsMenuFunctions.GetGunPitch();
                OptionsMenuFunctions.GetGunRoll();
                OptionsMenuFunctions.GetGunYaw();

                var videoHeader = OptionsMenuClone.CreateHeader(0, "Video");
                OptionsMenuClone.AddRow(videoHeader);

                var particleCycle = OptionsMenuClone.CreateCycle(0, "Particles", OptionsMenuFunctions.ParticleOptions,
                    () => OptionsMenuFunctions.particleMode,
                    v => { OptionsMenuFunctions.particleMode = v; OptionsMenuFunctions.SetParticleMode(v); },
                    1);
                OptionsMenuClone.AddRow(particleCycle);

                var audioHeader = OptionsMenuClone.CreateHeader(0, "Audio");
                OptionsMenuClone.AddRow(audioHeader);

                var musicLevelSlider = OptionsMenuClone.CreateSlider(0, "Music Level",
                    () => OptionsMenuFunctions.musicLevel,
                    v => { OptionsMenuFunctions.musicLevel = v; OptionsMenuFunctions.SetMusicLevel(v); },
                    0f, 10f, 1f, 5f, "N0");
                var sfxLevelSlider = OptionsMenuClone.CreateSlider(1, "Sfx Level",
                    () => OptionsMenuFunctions.sfxLevel,
                    v => { OptionsMenuFunctions.sfxLevel = v; OptionsMenuFunctions.SetSfxLevel(v); },
                    0f, 10f, 1f, 5f, "N0");
                OptionsMenuClone.AddRow(musicLevelSlider, sfxLevelSlider);

                var missFilterToggle = OptionsMenuClone.CreateToggle(0, "Miss Filter",
                    () => OptionsMenuFunctions.missFilter,
                    v => { OptionsMenuFunctions.missFilter = v; OptionsMenuFunctions.SetMissFilter(v); });
                var weaponSfxCycle = OptionsMenuClone.CreateCycle(1, "Weapon SFX", OptionsMenuFunctions.WeaponSfxOptions,
                    () => OptionsMenuFunctions.weaponSfxMode,
                    v => { OptionsMenuFunctions.weaponSfxMode = v; OptionsMenuFunctions.SetWeaponSfxMode(v); },
                    1);
                OptionsMenuClone.AddRow(missFilterToggle, weaponSfxCycle);

                var disableMineSounds = OptionsMenuClone.CreateToggle(0, "Disable Mine Sounds",
                    () => OptionsMenuFunctions.disableMineSounds,
                    v => { OptionsMenuFunctions.disableMineSounds = v; OptionsMenuFunctions.SetMineSounds(v); });
                OptionsMenuClone.AddRow(disableMineSounds);

                var audioVideoCalibrationHeader = OptionsMenuClone.CreateHeader(0, "Audio/Video Calibration");
                OptionsMenuClone.AddRow(audioVideoCalibrationHeader);

                var inputOffsetSlider = OptionsMenuClone.CreateSlider(0, "Input Offset",
                    () => OptionsMenuFunctions.inputOffset,
                    v => { OptionsMenuFunctions.inputOffset = v; OptionsMenuFunctions.SetInputOffset(v); },
                    -200f, 200f, 1f, 0f, "N0");
                var videoOffsetSlider = OptionsMenuClone.CreateSlider(1, "Video Offset",
                    () => OptionsMenuFunctions.videoOffset,
                    v => { OptionsMenuFunctions.videoOffset = v; OptionsMenuFunctions.SetVideoOffset(v); },
                    -200f, 200f, 1f, 0f, "N0");
                OptionsMenuClone.AddRow(inputOffsetSlider, videoOffsetSlider);

                var gunCalibrationHeader = OptionsMenuClone.CreateHeader(0, "Gun Calibration");
                OptionsMenuClone.AddRow(gunCalibrationHeader);

                var gunPitchSlider = OptionsMenuClone.CreateSlider(0, "Pitch",
                    () => OptionsMenuFunctions.gunPitch,
                    v => { OptionsMenuFunctions.gunPitch = v; OptionsMenuFunctions.SetGunPitch(v); },
                    -360f, 360f, 1f, 0f, "N0");
                var gunRollSlider = OptionsMenuClone.CreateSlider(1, "Roll",
                    () => OptionsMenuFunctions.gunRoll,
                    v => { OptionsMenuFunctions.gunRoll = v; OptionsMenuFunctions.SetGunRoll(v); },
                    -360f, 360f, 1f, 0f, "N0");
                OptionsMenuClone.AddRow(gunPitchSlider, gunRollSlider);

                var gunYawSlider = OptionsMenuClone.CreateSlider(0, "Yaw",
                    () => OptionsMenuFunctions.gunYaw,
                    v => { OptionsMenuFunctions.gunYaw = v; OptionsMenuFunctions.SetGunYaw(v); },
                    -360f, 360f, 1f, 0f, "N0");
                OptionsMenuClone.AddRow(gunYawSlider);
            }),
            new OptionsCategory("opt_gameplay", "Gameplay Options", () =>
            {
                OptionsMenuFunctions.GetAimAssist();
                OptionsMenuFunctions.GetTimingWindow();
                OptionsMenuFunctions.GetTemporalAimAssist();
                OptionsMenuFunctions.GetTargetSpeedMultiplier();
                OptionsMenuFunctions.GetMeleeSpeedMultiplier();
                OptionsMenuFunctions.GetDartPreGlowAmount();
                OptionsMenuFunctions.GetDartSpeedMultiplier();
                OptionsMenuFunctions.GetControllerPositionSmoothing();
                OptionsMenuFunctions.GetControllerRotationSmoothing();
                OptionsMenuFunctions.GetMirrorMode();
                OptionsMenuFunctions.GetFlipSlotTargets();
                OptionsMenuFunctions.GetTargetingHapticsStrength();

                var gameplayHeader = OptionsMenuClone.CreateHeader(0, "Gameplay");
                OptionsMenuClone.AddRow(gameplayHeader);

                var aimAssistSlider = OptionsMenuClone.CreateSlider(0, "Aim Assist",
                    () => OptionsMenuFunctions.aimAssist * 100f,
                    v => { OptionsMenuFunctions.aimAssist = v / 100f; OptionsMenuFunctions.SetAimAssist(v / 100f); },
                    0f, 100f, 1f, 100f,
                    v => v.ToString("N0") + "%");
                var timingWindowSlider = OptionsMenuClone.CreateSlider(1, "Timing Window",
                    () => OptionsMenuFunctions.timingWindow * 100f,
                    v => { OptionsMenuFunctions.timingWindow = v / 100f; OptionsMenuFunctions.SetTimingWindow(v / 100f); },
                    5f, 100f, 1f, 100f,
                    v => v.ToString("N0") + "%");
                OptionsMenuClone.AddRow(aimAssistSlider, timingWindowSlider);

                var temporalAimAssistToggle = OptionsMenuClone.CreateToggle(0, "Disable Temporal Aim Assist",
                    () => OptionsMenuFunctions.disableTemporalAimAssist,
                    v => { OptionsMenuFunctions.disableTemporalAimAssist = v; OptionsMenuFunctions.SetTemporalAimAssist(v); });
                OptionsMenuClone.AddRow(temporalAimAssistToggle);

                var speedHeader = OptionsMenuClone.CreateHeader(0, "Speed");
                OptionsMenuClone.AddRow(speedHeader);

                var targetSpeedSlider = OptionsMenuClone.CreateSlider(0, "Target Speed",
                    () => OptionsMenuFunctions.targetSpeedMultiplier * 100f,
                    v => { OptionsMenuFunctions.targetSpeedMultiplier = v / 100f; OptionsMenuFunctions.SetTargetSpeedMultiplier(v / 100f); },
                    100f, 500f, 10f, 100f,
                    v => v.ToString("N0") + "%");
                var meleeSpeedSlider = OptionsMenuClone.CreateSlider(1, "Melee Speed",
                    () => OptionsMenuFunctions.meleeSpeedMultiplier * 100f,
                    v => { OptionsMenuFunctions.meleeSpeedMultiplier = v / 100f; OptionsMenuFunctions.SetMeleeSpeedMultiplier(v / 100f); },
                    100f, 500f, 10f, 100f,
                    v => v.ToString("N0") + "%");
                OptionsMenuClone.AddRow(targetSpeedSlider, meleeSpeedSlider);

                var dartHeader = OptionsMenuClone.CreateHeader(0, "Dart");
                OptionsMenuClone.AddRow(dartHeader);

                var dartPreGlowSlider = OptionsMenuClone.CreateSlider(0, "Cue Dart Pre Glow",
                    () => OptionsMenuFunctions.dartPreGlowAmount * 100f,
                    v => { OptionsMenuFunctions.dartPreGlowAmount = v / 100f; OptionsMenuFunctions.SetDartPreGlowAmount(v / 100f); },
                    100f, 500f, 10f, 100f,
                    v => v.ToString("N0") + "%");
                var dartSpeedSlider = OptionsMenuClone.CreateSlider(1, "Dart Speed",
                    () => OptionsMenuFunctions.dartSpeedMultiplier * 100f,
                    v => { OptionsMenuFunctions.dartSpeedMultiplier = v / 100f; OptionsMenuFunctions.SetDartSpeedMultiplier(v / 100f); },
                    100f, 500f, 10f, 100f,
                    v => v.ToString("N0") + "%");
                OptionsMenuClone.AddRow(dartPreGlowSlider, dartSpeedSlider);

                var controllerHeader = OptionsMenuClone.CreateHeader(0, "Controller");
                OptionsMenuClone.AddRow(controllerHeader);

                var posSmoothingSlider = OptionsMenuClone.CreateSlider(0, "Position Smoothing",
                    () => OptionsMenuFunctions.controllerPositionSmoothing * 100f,
                    v => { OptionsMenuFunctions.controllerPositionSmoothing = v / 100f; OptionsMenuFunctions.SetControllerPositionSmoothing(v / 100f); },
                    0f, 100f, 25f, 0f,
                    v => v.ToString("N0") + "%");
                var rotSmoothingSlider = OptionsMenuClone.CreateSlider(1, "Rotation Smoothing",
                    () => OptionsMenuFunctions.controllerRotationSmoothing * 100f,
                    v => { OptionsMenuFunctions.controllerRotationSmoothing = v / 100f; OptionsMenuFunctions.SetControllerRotationSmoothing(v / 100f); },
                    0f, 100f, 25f, 0f,
                    v => v.ToString("N0") + "%");
                OptionsMenuClone.AddRow(posSmoothingSlider, rotSmoothingSlider);

                var miscHeader = OptionsMenuClone.CreateHeader(0, "Misc");
                OptionsMenuClone.AddRow(miscHeader);

                var mirrorModeToggle = OptionsMenuClone.CreateToggle(0, "Mirror Mode",
                    () => OptionsMenuFunctions.mirrorMode,
                    v => { OptionsMenuFunctions.mirrorMode = v; OptionsMenuFunctions.SetMirrorMode(v); });
                var flipSlotToggle = OptionsMenuClone.CreateToggle(1, "Flip Slot Targets",
                    () => OptionsMenuFunctions.flipSlotTargets,
                    v => { OptionsMenuFunctions.flipSlotTargets = v; OptionsMenuFunctions.SetFlipSlotTargets(v); });
                OptionsMenuClone.AddRow(mirrorModeToggle, flipSlotToggle);

                var hapticsHeader = OptionsMenuClone.CreateHeader(0, "Haptics");
                OptionsMenuClone.AddRow(hapticsHeader);

                var hapticsSlider = OptionsMenuClone.CreateSlider(0, "Targeting Haptics",
                    () => OptionsMenuFunctions.targetingHapticsStrength * 100f,
                    v => { OptionsMenuFunctions.targetingHapticsStrength = v / 100f; OptionsMenuFunctions.SetTargetingHapticsStrength(v / 100f); },
                    0f, 100f, 25f, 0f,
                    v => v.ToString("N0") + "%");
                OptionsMenuClone.AddRow(hapticsSlider);
            }),
            new OptionsCategory("opt_test", "Wide Slider Test", () =>
        {
            var testHeader = OptionsMenuClone.CreateHeader(0, "Wide Slider Test");
            OptionsMenuClone.AddRow(testHeader);

            float testValue = 50f;
            OptionsMenuClone.AddWideSlider("Test Wide Slider",
                () => testValue,
                v => { testValue = v; },
                0f, 100f, 1f, 50f, "N0");
        }),
        };

        // ── Song-list view (Back + categories) ────────────────────────────────
        public static List<ViewRow> BuildView()
        {
            var rows = new List<ViewRow>();
            rows.Add(ViewRow.ActionRow("Back", PlaylistNav.BackColor, FolderRowManager.NavBack));
            foreach (var cat in Categories)
            {
                var c = cat; // capture
                rows.Add(ViewRow.ActionRow(c.Title, CategoryColor, () => OnCategoryShot(c), null, c.Id));
            }
            return rows;
        }

        private static void OnCategoryShot(OptionsCategory cat)
        {
            MelonLogger.Log($"[Options] category shot: {cat.Title}");
            activeCategory = cat;
            VirtualSongList.SetSelectedAction(cat.Id);
            ShowCategory(cat);
        }

        private static void ShowCategory(OptionsCategory cat)
        {
            if (!OptionsMenuClone.EnsureClone()) return;

            if (!panelShown)
            {
                panelShown = true;
                launchPage = GameObject.Find("menu/ShellPage_Launch");
                MelonLogger.Log("[Options] hiding launch panel (animated), then showing clone");
                ExScoring.HideLaunchPanel();
                MelonCoroutines.Start(ShowAfterHide(cat));
            }
            else
            {
                OptionsMenuClone.Draw(cat.Title, cat.Build); // already up: redraw, no animation
            }
        }

        private static float LaunchHideAnimLength()
        {
            if (launchPage != null)
            {
                var sp = launchPage.GetComponent<ShellPage>();
                if (sp != null && sp.hideAnim != null) return sp.hideAnim.length;
            }
            return hideAnimDelay; // fallback
        }

        private static IEnumerator ShowAfterHide(OptionsCategory cat)
        {
            float len = LaunchHideAnimLength();
            MelonLogger.Log($"[Options] waiting hideAnim length {len:N2}s before disabling launch page");
            yield return new WaitForSeconds(len);

            if (launchPage != null) launchPage.SetActive(false); // the missing SetActive(false)
            OptionsMenuClone.Show();
            yield return null; // let ShowPage settle a frame
            OptionsMenuClone.Draw(cat.Title, cat.Build);
            MelonLogger.Log("[Options] launch page disabled, clone shown");
        }

        public static void HidePanel()
        {
            VirtualSongList.SetSelectedAction(null);
            activeCategory = null;
            if (!panelShown) return;
            panelShown = false;
            MelonLogger.Log("[Options] hiding clone, restoring launch panel");

            OptionsMenuClone.Wipe();
            OptionsMenuClone.Hide();

            if (launchPage != null) launchPage.SetActive(true);

            bool prevSuppress = ExScoring.suppressShellPageAnimations;
            ExScoring.suppressShellPageAnimations = false;
            ExScoring.ShowLaunchPanel();
            ExScoring.suppressShellPageAnimations = prevSuppress;

            // Restore the same state we left: blank/no-preview if nothing was selected,
            // otherwise the populated song info.
            if (string.IsNullOrEmpty(ExScoring.selectedSong))
                ExScoring.SetLaunchPanelContentVisible(false);
            else
                ExScoring.UpdateLaunchPanelInfo();

            launchPage = null;
        }

        /// Called when the song page is being torn down (e.g. SongPage -> MainPage).
        /// Re-activates the launch page so the game's own HideLaunchPanel can find it,
        /// and clears our options state. Does NOT restore the song-select look.
        public static void ForceTeardown()
        {
            // Remember where we were so returning to the song page lands back here.
            restorePending = FolderRowManager.InGlobalOptions;
            restoreCategoryId = activeCategory?.Id;

            VirtualSongList.SetSelectedAction(null);
            activeCategory = null;
            if (panelShown)
            {
                panelShown = false;
                OptionsMenuClone.Wipe();
                OptionsMenuClone.Hide();
                if (launchPage != null) launchPage.SetActive(false);
            }
            launchPage = null;

            if (restorePending)
                FolderRowManager.ResetNav(); // RestoreIfPending re-enters cleanly on return
        }

        public static void RestoreIfPending()
        {
            if (!restorePending) return;
            restorePending = false;
            string catId = restoreCategoryId;
            restoreCategoryId = null;

            FolderRowManager.EnterGlobalOptions();      // re-show the Back + categories list
            if (!string.IsNullOrEmpty(catId))
            {
                var cat = Categories.Find(c => c.Id == catId);
                if (cat != null) OnCategoryShot(cat);   // reopen the category panel
            }

            // in RestoreIfPending, log entry + which category
            MelonLogger.Log($"[Options] RestoreIfPending: catId={restoreCategoryId}");
        }
    }
}