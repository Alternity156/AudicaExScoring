using System;
using MelonLoader;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

namespace ExScoringMod
{
    /// <summary>
    /// Two buttons on the Main page's center panel, left and right of Solo, that switch between
    /// Audica scoring and EX scoring (Config.AudicaType / Config.ExType) and show which one is
    /// currently active via the button's own SelectedIndicator (lit on the active mode, off on the
    /// other). Cloned from the panel's native Campaign button so size and style match the
    /// neighbouring buttons. Created from ReleaseNotesPanel.Setup() (every Main page entry) and
    /// re-synced from Config.SetScoringType, so changing the scoring type from the options menu
    /// keeps these in step.
    /// </summary>
    internal static class ModeButtons
    {
        private const string CenterPanelPath = "menu/ShellPage_Main/page/ShellPanel_Center";
        private const string TemplateName = "Campaign";
        private const string IndicatorName = "SelectedIndicator";

        private const string AudicaButtonName = "ExScoring_AudicaModeButton";
        private const string ExButtonName = "ExScoring_ExModeButton";

        // Same columns/angles as Party/Tutorial and Settings/Quit, on Solo's row.
        private static readonly Vector3 AudicaButtonPos = new Vector3(-8f, 1f, -1.5f);
        private static readonly Vector3 AudicaButtonRot = new Vector3(0f, 340f, 0f);
        private static readonly Vector3 ExButtonPos = new Vector3(8f, 1f, -1.5f);
        private static readonly Vector3 ExButtonRot = new Vector3(0f, 20f, 0f);
        private static readonly Vector3 ButtonScale = new Vector3(2f, 2f, 2f);

        private static GameObject audicaButton;
        private static GameObject exButton;
        private static GameObject audicaIndicator;
        private static GameObject exIndicator;

        /// <summary>
        /// Creates both buttons if they don't already exist, then syncs the indicators. Idempotent -
        /// safe to call every time the Main page is entered.
        /// </summary>
        public static void Setup()
        {
            if (audicaButton == null || exButton == null) // Unity-null: recreated after a scene change
            {
                GameObject center = GameObject.Find(CenterPanelPath);
                if (center == null)
                {
                    MelonLogger.Log("ModeButtons: ShellPanel_Center not found");
                    return;
                }

                // transform.Find also traverses inactive children, unlike GameObject.Find.
                Transform template = center.transform.Find(TemplateName);
                if (template == null)
                {
                    MelonLogger.Log("ModeButtons: " + TemplateName + " button template not found");
                    return;
                }

                if (audicaButton == null)
                {
                    audicaButton = CreateButton(template, center.transform, AudicaButtonName, "Audica Mode",
                        new Action(OnAudicaButtonShot), AudicaButtonPos, AudicaButtonRot);
                    audicaIndicator = FindIndicator(audicaButton);
                }

                if (exButton == null)
                {
                    exButton = CreateButton(template, center.transform, ExButtonName, "EX Mode",
                        new Action(OnExButtonShot), ExButtonPos, ExButtonRot);
                    exIndicator = FindIndicator(exButton);
                }
            }

            Refresh();
        }

        /// <summary>
        /// Lights the SelectedIndicator on the active mode's button and turns it off on the other.
        /// No-op for any button not yet created. Call whenever the scoring type changes.
        /// </summary>
        public static void Refresh()
        {
            if (audicaIndicator != null) audicaIndicator.SetActive(!Config.ExType);
            if (exIndicator != null) exIndicator.SetActive(Config.ExType);
        }

        private static GameObject CreateButton(Transform template, Transform parent, string name, string label,
                                               Action listener, Vector3 localPosition, Vector3 localRotation)
        {
            GameObject button = GameObject.Instantiate(template.gameObject, parent);
            button.name = name;

            Localizer localizer = button.GetComponentInChildren<Localizer>(true);
            if (localizer != null) GameObject.Destroy(localizer);

            TextMeshPro text = button.GetComponentInChildren<TextMeshPro>(true);
            if (text != null) text.text = label;

            GunButton gb = button.GetComponentInChildren<GunButton>(true);
            if (gb != null)
            {
                gb.destroyOnShot = false;
                gb.disableOnShot = false;
                gb.doMeshExplosion = false;
                gb.doParticles = false;
                gb.onHitEvent = new UnityEvent();
                gb.onHitEvent.AddListener(listener);
            }
            else
            {
                MelonLogger.Log("ModeButtons: no GunButton found on " + name);
            }

            // Set absolutely rather than via Rotate(), since the template's own rotation is copied.
            button.transform.localPosition = localPosition;
            button.transform.localEulerAngles = localRotation;
            button.transform.localScale = ButtonScale;
            button.SetActive(true);

            return button;
        }

        private static GameObject FindIndicator(GameObject button)
        {
            Transform indicator = button.transform.Find(IndicatorName);
            if (indicator == null)
            {
                MelonLogger.Log("ModeButtons: " + IndicatorName + " not found on " + button.name);
                return null;
            }

            return indicator.gameObject;
        }

        private static void OnAudicaButtonShot()
        {
            SwitchMode(false);
        }

        private static void OnExButtonShot()
        {
            SwitchMode(true);
        }

        private static void SwitchMode(bool ex)
        {
            if (Config.ExType == ex)
            {
                // Already in this mode - just make sure the indicators are right.
                Refresh();
                return;
            }

            Config.SetScoringType(ex); // also calls Refresh()
            RefreshTotalLeaderboard(ex);
        }

        /// <summary>
        /// Reloads the Main page's Total leaderboard so it shows the newly selected mode's data.
        /// To Audica: native ViewTop(), which goes through OnlineLeaderboardUpdateLeaderboardPatch
        /// (ExLeaderboardDisplay.cs) - that restores the rows/buttons and lets native load.
        /// To EX: the AudicaEX Total fetch is started directly, because ViewTop() in that direction
        /// just re-shows the native results the panel already holds and never reaches the patch.
        /// </summary>
        private static void RefreshTotalLeaderboard(bool ex)
        {
            var displays = UnityEngine.Object.FindObjectsOfType<LeaderboardDisplay>();
            if (displays == null || displays.Length == 0)
            {
                MelonLogger.Log("[ExScoring] ModeButtons: no LeaderboardDisplay found, skipping leaderboard refresh.");
                return;
            }

            for (int i = 0; i < displays.Length; i++)
            {
                LeaderboardDisplay display = displays[i];
                if (display == null || !display.totalLeaderboards) continue;

                if (ex)
                {
                    MelonLogger.Log("[ExScoring] ModeButtons: refreshing Total leaderboard via direct EX fetch.");
                    ExScoring.RefreshExTotalLeaderboard(display);
                }
                else
                {
                    MelonLogger.Log("[ExScoring] ModeButtons: refreshing Total leaderboard via ViewTop().");
                    display.ViewTop();
                }
                return;
            }

            MelonLogger.Log("[ExScoring] ModeButtons: no Total LeaderboardDisplay among " + displays.Length + " found, skipping leaderboard refresh.");
        }
    }
}