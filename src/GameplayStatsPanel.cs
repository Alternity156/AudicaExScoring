using MelonLoader;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        /// <summary>
        /// Opens the gameplay-stats panel for a selected history row, via its own completely
        /// independent clone (OptionsMenuClone.ShowHistoryPanel) — separate from the one
        /// GlobalOptions.cs uses for the real Options menu. They used to share one GameObject,
        /// which caused Play History's overridden rotation and its graphs/labels to leak onto the
        /// Options menu (and vice versa). Content (labels, graphs) is placed manually.
        /// </summary>
        private static RecalculatedRun currentGameplayStatsRun;

        private static void ShowGameplayStatsPanel(RecalculatedRun run)
        {
            currentGameplayStatsRun = run;

            Transform panel = OptionsMenuClone.ShowHistoryPanel(
                new Vector3(-28f, 6.25f, -2.5f),
                new Vector3(0f, -90f, 0f),
                $"{run.songId} ({run.difficulty})");
            if (panel == null) return;

            BuildGameplayStatsContent(panel, run.exCues, run.judgementPercent);
        }

        /// <summary>
        /// Shows the same panel directly on the live results screen, parented onto
        /// InGameUI/ShellPage_Results/page/ShellPanel_Center. No background clone — just the graphs
        /// and labels attached straight onto that transform. ShellPanel_Center's local unit scale is
        /// much smaller than what everything was originally tuned for (OptionsMenuClone.Menu's
        /// transform), so both position and scale get multiplied by 33.333333 to compensate and land
        /// at the same visual size/layout. On top of that, the whole group sits too low relative to
        /// the panel at that scale — confirmed by manually repositioning ExScoreDisplay to Y=600
        /// (vs. its computed -16.667, a difference of +616.667) — so that same offset is applied to
        /// every item's Y position to bring the whole group up together.
        /// </summary>
        private const float ResultsScreenScaleMultiplier = 33.333333f;
        private const float ResultsScreenYOffset = 616.667f;

        private static void ShowGameplayStatsPanelOnResultsScreen(Transform resultsPanelParent, List<ExCue> cuesToShow, float judgementPercent)
        {
            BuildGameplayStatsContent(resultsPanelParent, cuesToShow, judgementPercent, ResultsScreenScaleMultiplier, ResultsScreenYOffset);
        }

        /// <summary>
        /// The actual graphs + judgement/misc/score labels, parented onto whatever transform the
        /// caller provides. Shared by both the saved-run history browser (its own independent
        /// clone via OptionsMenuClone.ShowHistoryPanel, default 1x scale, no Y offset) and the live
        /// results screen (ShellPanel_Center, needs scaleMultiplier and yOffset to compensate for
        /// its much smaller local unit scale and different vertical anchor).
        /// </summary>
        private static void BuildGameplayStatsContent(Transform parent, List<ExCue> cuesToShow, float judgementPercent, float scaleMultiplier = 1f, float yOffset = 0f)
        {
            CreateTimingGraph(parent, cuesToShow);
            if (scaleMultiplier != 1f && timingGraphObject != null)
            {
                timingGraphObject.transform.localPosition *= scaleMultiplier;
                timingGraphObject.transform.localScale *= scaleMultiplier;
            }
            timingGraphObject.transform.localPosition += new Vector3(0f, yOffset, 0f);

            CreateAimGraph(parent, cuesToShow);
            if (scaleMultiplier != 1f && aimGraphObject != null)
            {
                aimGraphObject.transform.localPosition *= scaleMultiplier;
                aimGraphObject.transform.localScale *= scaleMultiplier;
            }
            aimGraphObject.transform.localPosition += new Vector3(0f, yOffset, 0f);

            CreateSongTimelineGraph(parent, cuesToShow);
            if (scaleMultiplier != 1f && songTimelineGraphObject != null)
            {
                songTimelineGraphObject.transform.localPosition *= scaleMultiplier;
                songTimelineGraphObject.transform.localScale *= scaleMultiplier;
            }
            songTimelineGraphObject.transform.localPosition += new Vector3(0f, yOffset, 0f);

            // Named with "(Clone)" so OptionsMenuClone's Wipe() (called at the start of every
            // Draw()) sweeps up the previous run's labels automatically instead of stacking new
            // ones on top — same convention the three graphs already use. Doesn't apply to the
            // results-screen path (no Wipe() there), but harmless to keep consistent.
            var timingLabel = CreateTimingLabel(parent, "ExTimingDisplay (Clone)", new Vector3(4.25f, -4f, 0f) * scaleMultiplier + new Vector3(0f, yOffset, 0f), Color.white, TextAlignmentOptions.Left);
            timingLabel.text = GetTimingJudgementString(cuesToShow);
            timingLabel.transform.localScale = new Vector3(1.25f, 1.25f, 1.25f) * scaleMultiplier;

            var aimLabel = CreateTimingLabel(parent, "ExAimDisplay (Clone)", new Vector3(10f, -4f, 0f) * scaleMultiplier + new Vector3(0f, yOffset, 0f), Color.white, TextAlignmentOptions.Left);
            aimLabel.text = GetAimJudgementString(cuesToShow);
            aimLabel.transform.localScale = new Vector3(1.25f, 1.25f, 1.25f) * scaleMultiplier;

            var chainLabel = CreateTimingLabel(parent, "ExChainDisplay (Clone)", new Vector3(16f, -4f, 0f) * scaleMultiplier + new Vector3(0f, yOffset, 0f), Color.white, TextAlignmentOptions.Left);
            chainLabel.text = GetChainJudgementString(cuesToShow);
            chainLabel.transform.localScale = new Vector3(1.25f, 1.25f, 1.25f) * scaleMultiplier;

            var miscLabel = CreateTimingLabel(parent, "ExMiscDisplay (Clone)", new Vector3(16f, -7f, 0f) * scaleMultiplier + new Vector3(0f, yOffset, 0f), Color.white, TextAlignmentOptions.Left);
            miscLabel.text = GetMiscString(cuesToShow);
            miscLabel.transform.localScale = new Vector3(1.25f, 1.25f, 1.25f) * scaleMultiplier;

            var scoreLabel = CreateTimingLabel(parent, "ExScoreDisplay (Clone)", new Vector3(35f, -0.5f, 0f) * scaleMultiplier + new Vector3(0f, yOffset, 0f), Color.white, TextAlignmentOptions.Left);
            scoreLabel.text = $"Score: {judgementPercent:0.##}%";
            scoreLabel.transform.localScale = new Vector3(4f, 4f, 4f) * scaleMultiplier;
        }
    }
}