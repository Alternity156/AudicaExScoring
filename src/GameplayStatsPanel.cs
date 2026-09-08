using MelonLoader;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        // Context keys for the per-context graph/grade-visual dictionaries in TimingGraph.cs/
        // AimGraph.cs/SongTimelineGraph.cs/GradeDisplay.cs. History and Leaderboard are two
        // completely independent panels that can be open at the same time showing different runs
        // — see PlayHistoryButton.cs (ResetHistorySelection) and LeaderboardStatsButton.cs
        // (ResetLeaderboardSelection). Results has its own context too, even though it can never
        // overlap the other two in practice (different menu state entirely), just so it never
        // shares/clobbers either one's dictionary entry if a panel's GameObject somehow survives
        // across the transition (both stats panels use DontDestroyOnLoad clones).
        private const string HistoryStatsContext = "history";
        private const string LeaderboardStatsContext = "leaderboard";
        private const string ResultsStatsContext = "results";

        /// <summary>
        /// Last run shown per context. Not currently read anywhere else, kept for parity with the
        /// pre-refactor single currentGameplayStatsRun field and as a debugging aid (inspectable
        /// live in UnityExplorer).
        /// </summary>
        private static readonly Dictionary<string, RecalculatedRun> currentGameplayStatsRunByContext = new Dictionary<string, RecalculatedRun>();

        // Placeholder world-space position/rotation for each panel — tune live in UnityExplorer.
        // Leaderboard's is a mirror of History's (History opens to the left of the song-info panel,
        // Leaderboard stats should open to the right), starting from History's known-good values.
        private static readonly Vector3 HistoryStatsPanelPosition = new Vector3(-28f, 6.25f, -2.5f);
        private static readonly Vector3 HistoryStatsPanelRotation = new Vector3(0f, -90f, 0f);
        private static readonly Vector3 LeaderboardStatsPanelPosition = new Vector3(29f, 6.5f, -4f);
        private static readonly Vector3 LeaderboardStatsPanelRotation = new Vector3(0f, 90f, 0f);

        /// <summary>
        /// Opens the gameplay-stats panel for a selected history row, via its own completely
        /// independent clone (OptionsMenuClone.ShowHistoryPanel) — separate from the one
        /// GlobalOptions.cs uses for the real Options menu, and separate again from the leaderboard
        /// stats panel's own clone (OptionsMenuClone.ShowLeaderboardStatsPanel). Sharing one clone
        /// between contexts previously caused Play History's overridden rotation and its graphs/
        /// labels to leak onto the Options menu (and vice versa) — same reasoning extends to keeping
        /// Leaderboard fully independent too, so both can be open at once showing different runs.
        /// </summary>
        public static void ShowHistoryGameplayStatsPanel(RecalculatedRun run)
        {
            currentGameplayStatsRunByContext[HistoryStatsContext] = run;

            Transform panel = OptionsMenuClone.ShowHistoryPanel(
                HistoryStatsPanelPosition,
                HistoryStatsPanelRotation,
                $"{run.songId} ({run.difficulty})");
            if (panel == null) return;

            BuildGameplayStatsContent(HistoryStatsContext, panel, run.exCues, run.judgementPercent, run.failed);
        }

        /// <summary>
        /// Leaderboard-stats equivalent of ShowHistoryGameplayStatsPanel — same shared content
        /// builder, its own independent clone/context so it never collides with History's panel or
        /// graphs even when both are open simultaneously.
        /// </summary>
        public static void ShowLeaderboardGameplayStatsPanel(RecalculatedRun run)
        {
            currentGameplayStatsRunByContext[LeaderboardStatsContext] = run;

            Transform panel = OptionsMenuClone.ShowLeaderboardStatsPanel(
                LeaderboardStatsPanelPosition,
                LeaderboardStatsPanelRotation,
                $"{run.songId} ({run.difficulty})");
            if (panel == null) return;

            BuildGameplayStatsContent(LeaderboardStatsContext, panel, run.exCues, run.judgementPercent, run.failed);
        }

        /// <summary>
        /// Opens (or re-titles) the leaderboard stats panel shell without any run content yet —
        /// used by LeaderboardStatsButton.cs to show a "Loading..." placeholder immediately on shot,
        /// before the async GET /api/runs/:runId call resolves. Returns the content parent transform
        /// so the caller can place a temporary label on it.
        /// </summary>
        public static Transform ShowLeaderboardStatsPanelShell(string title)
        {
            return OptionsMenuClone.ShowLeaderboardStatsPanel(LeaderboardStatsPanelPosition, LeaderboardStatsPanelRotation, title);
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

        private static void ShowGameplayStatsPanelOnResultsScreen(Transform resultsPanelParent, List<ExCue> cuesToShow, float judgementPercent, bool failed)
        {
            BuildGameplayStatsContent(ResultsStatsContext, resultsPanelParent, cuesToShow, judgementPercent, failed, ResultsScreenScaleMultiplier, ResultsScreenYOffset);

            // Live-results-only. Position/scale tuned live in UnityExplorer (final runtime values
            // confirmed as localPosition (-770, 600, 0), localScale (60, 60, 60) — this is that,
            // expressed as the pre-multiplier base the rest of this panel uses).
            apiStatusLabel = CreateTimingLabel(resultsPanelParent, "ExApiStatusDisplay (Clone)",
                new Vector3(-23.1f, -0.5f, 0f) * ResultsScreenScaleMultiplier + new Vector3(0f, ResultsScreenYOffset, 0f),
                currentApiStatusColor, TextAlignmentOptions.Right);
            apiStatusLabel.text = currentApiStatusText;
            apiStatusLabel.transform.localScale = new Vector3(1.8f, 1.8f, 1.8f) * ResultsScreenScaleMultiplier;
        }

        /// <summary>
        /// Tracks the state of the current run's online submission (see ApiClient.SubmitRun) so the
        /// results-screen label (above) can be initialized with whatever's already known by the time
        /// it's created, and updated live if the request is still in flight. Reset to NotSubmitted
        /// by ResetExScore() at the start of every new run.
        /// </summary>
        public enum ApiSubmitStatus { NotSubmitted, Sending, Success, Failed }

        private static ApiSubmitStatus currentApiSubmitStatus = ApiSubmitStatus.NotSubmitted;
        private static string currentApiStatusText = "";
        private static Color currentApiStatusColor = Color.white;

        // Only set while the results screen is up (see above) — null otherwise, including for the
        // whole duration of the next song's gameplay, so a late-arriving callback from a run whose
        // results screen has already been left behind has nothing (real) to write into.
        private static TextMeshPro apiStatusLabel;

        public static void SetApiSubmitStatus(ApiSubmitStatus status, string text, Color color)
        {
            currentApiSubmitStatus = status;
            currentApiStatusText = text;
            currentApiStatusColor = color;

            if (apiStatusLabel != null)
            {
                apiStatusLabel.text = text;
                apiStatusLabel.color = color;
            }
        }

        /// <summary>Applies the results-screen scale/offset compensation to one created graph/visual
        /// GameObject, if it was actually built (Create* returns null when parent/data was missing).
        /// Pulled out of BuildGameplayStatsContent since it's now the same four-line dance repeated
        /// once per graph, against a local variable instead of a shared static field.</summary>
        private static void ApplyStatsPanelScaleAndOffset(GameObject go, float scaleMultiplier, float yOffset)
        {
            if (go == null) return;

            if (scaleMultiplier != 1f)
            {
                go.transform.localPosition *= scaleMultiplier;
                go.transform.localScale *= scaleMultiplier;
            }
            go.transform.localPosition += new Vector3(0f, yOffset, 0f);
        }

        /// <summary>
        /// The actual graphs + judgement/misc/score labels, parented onto whatever transform the
        /// caller provides. Shared by the saved-run history browser, the leaderboard stats panel
        /// (both default 1x scale, no Y offset, each its own `context`), and the live results screen
        /// (ShellPanel_Center, needs scaleMultiplier and yOffset to compensate for its much smaller
        /// local unit scale and different vertical anchor). `context` keys the underlying graph/grade
        /// dictionaries (see TimingGraph.cs etc.) so History/Leaderboard/Results never clobber each
        /// other's GameObjects.
        /// </summary>
        private static void BuildGameplayStatsContent(string context, Transform parent, List<ExCue> cuesToShow, float judgementPercent, bool failed, float scaleMultiplier = 1f, float yOffset = 0f)
        {
            GameObject timingGraph = CreateTimingGraph(context, parent, cuesToShow);
            ApplyStatsPanelScaleAndOffset(timingGraph, scaleMultiplier, yOffset);

            GameObject aimGraph = CreateAimGraph(context, parent, cuesToShow);
            ApplyStatsPanelScaleAndOffset(aimGraph, scaleMultiplier, yOffset);

            GameObject songTimelineGraph = CreateSongTimelineGraph(context, parent, cuesToShow);
            ApplyStatsPanelScaleAndOffset(songTimelineGraph, scaleMultiplier, yOffset);

            // Named with "(Clone)" so OptionsMenuClone's Wipe()/panel-reopen sweep (both
            // ShowHistoryPanel and ShowLeaderboardStatsPanel destroy any previous "(Clone)"-tagged
            // child up front) sweeps up the previous run's labels automatically instead of stacking
            // new ones on top — same convention the three graphs already use. Doesn't apply to the
            // results-screen path (no such sweep there), but harmless to keep consistent.
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

            GameObject gradeVisual = CreateGradeVisual(context, parent, judgementPercent, failed);
            ApplyStatsPanelScaleAndOffset(gradeVisual, scaleMultiplier, yOffset);
        }
    }
}