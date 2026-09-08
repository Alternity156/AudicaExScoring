using System;
using System.Collections.Generic;
using Harmony;
using MelonLoader;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        /// <summary>
        /// Mirrors PlayHistoryButton.cs's hit-box trick, but over LeaderboardDisplay.rowsStandard
        /// instead of SongInfoPanel's history[] — LeaderboardRow has no working shootable component
        /// of its own to repurpose (its `compareButton` is a bare GameObject, not confirmed to carry
        /// a live GunButton), so the same invisible-cloned-SongSelectItem overlay is used here too.
        ///
        /// Keyed by the row GameObject's instance ID rather than array index — same convention
        /// GradeDisplay.cs already uses for leaderboardRowGradeVisuals, since rowsStandard is a
        /// small fixed pool of row objects reused across every leaderboard refresh (see
        /// ExLeaderboardDisplay.cs), same as songRowGradeVisuals/leaderboardRowGradeVisuals there.
        /// </summary>
        private static readonly Dictionary<int, GameObject> leaderboardHitboxes = new Dictionary<int, GameObject>();
        private static readonly Dictionary<int, string> leaderboardHitboxRunIds = new Dictionary<int, string>();
        private static readonly Dictionary<int, Transform> leaderboardHitboxQuads = new Dictionary<int, Transform>();
        private static readonly Dictionary<int, MeshCollider> leaderboardHitboxColliders = new Dictionary<int, MeshCollider>();

        // Cached lazily the first time we need it — the RectMask2D on the leaderboard scroll's
        // Viewport. Same purpose/limitation as historyViewportMaskRect in PlayHistoryButton.cs, kept
        // as its own field since this is a different scroll view (LeaderboardDisplay.scrollRect).
        private static RectTransform leaderboardViewportMaskRect;

        // TUNE THESE IN UNITYEXPLORER, same process as PlayHistoryButton's historyHitboxLocalPosition/
        // historyHitboxLocalScale: find a "LeaderboardStatsHitbox_N" object (child of a leaderboard
        // row) after a leaderboard is populated, adjust its Transform's Local Position/Local Scale
        // live until it lines up with the row's visible text, then report the numbers back here.
        // Starting from History's own tuned values as a placeholder — LeaderboardRow's actual layout
        // is very likely a different width/height, so treat these as a rough starting guess only.
        private static Vector3 leaderboardHitboxLocalPosition = new Vector3(275f, -15f, 0f);
        private static Vector3 leaderboardHitboxLocalScale = new Vector3(19.5f, 10f, 1f);

        // TUNE THESE IN UNITYEXPLORER too, same process as historyIndicatorLocalPosition/Scale.
        private static Vector3 leaderboardIndicatorLocalPosition = new Vector3(0f, 0f, -0.005f);
        private static Vector3 leaderboardIndicatorLocalScale = new Vector3(1f, 1f, 1f);

        /// <summary>
        /// Ensures a shootable hit-box exists for this leaderboard row, parented under the row,
        /// sized to the row's current RectTransform bounds, and wired to fire OnLeaderboardRowShot.
        /// Safe to call every refresh — reuses the existing hit-box if this row instance already has
        /// one. Same clone-the-full-prefab-then-hide-everything-but-Quad approach as
        /// PlayHistoryButton.EnsureHistoryHitbox, for the same reason (GunButton.Awake() needs the
        /// surrounding prefab context to initialize its highlight state correctly).
        /// </summary>
        private static void EnsureLeaderboardHitbox(int slot, LeaderboardRow row)
        {
            if (leaderboardHitboxes.TryGetValue(slot, out GameObject existing) && existing != null)
            {
                // Deliberately not reapplying position/scale here — same reasoning as
                // EnsureHistoryHitbox, lets live UnityExplorer tuning persist across refreshes.
                return;
            }

            GameObject prefabItem = VirtualSongList.SongItemPrefab;
            if (prefabItem == null)
            {
                MelonLogger.Log("[ExScoring] Could not find song row prefab for leaderboard stats hit-box.");
                return;
            }

            GameObject clone = UnityEngine.Object.Instantiate(prefabItem, row.transform, false);
            clone.name = "LeaderboardStatsHitbox_" + slot;
            clone.transform.localPosition = leaderboardHitboxLocalPosition;
            clone.transform.localRotation = Quaternion.identity;
            clone.transform.localScale = leaderboardHitboxLocalScale;

            var songSelectItem = clone.GetComponent<SongSelectItem>();
            if (songSelectItem != null) UnityEngine.Object.Destroy(songSelectItem);

            Transform quad = FindChildRecursive(clone.transform, "Quad");
            if (quad == null)
            {
                MelonLogger.Log("[ExScoring] Cloned leaderboard stats hit-box has no Quad child — check the prefab structure.");
                leaderboardHitboxes[slot] = clone;
                return;
            }

            HideAllExceptPath(clone.transform, quad);
            leaderboardHitboxQuads[slot] = quad;

            for (Transform t = quad; t != null; t = t.parent)
            {
                t.gameObject.SetActive(true);
                if (t == clone.transform) break;
            }

            var renderer = quad.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                renderer.enabled = true;
                Shader sh = Shader.Find("Sprites/Default");
                if (sh != null)
                {
                    // Reuses history's hit-box tint color (historyHitboxColor, PlayHistoryButton.cs)
                    // so both stats-panel triggers read as the same kind of shootable overlay.
                    var mat = new Material(sh) { color = historyHitboxColor };
                    mat.renderQueue = 3100;
                    renderer.material = mat;
                }
            }

            GunButton gunButton = quad.GetComponent<GunButton>();
            if (gunButton != null)
            {
                gunButton.destroyOnShot = false;
                gunButton.disableOnShot = false;
                gunButton.doMeshExplosion = false;
                gunButton.doParticles = false;
                gunButton.doHighlightSound = false;
                gunButton.fakeBloom = null;
                gunButton.highlightFakeBloom = null;

                int capturedSlot = slot;
                gunButton.onHitEvent = new UnityEvent();
                gunButton.onHitEvent.AddListener(new Action(() => OnLeaderboardRowShot(capturedSlot)));
            }
            else
            {
                MelonLogger.Log("[ExScoring] Cloned leaderboard stats hit-box's Quad has no GunButton — check the prefab structure.");
            }

            MeshCollider quadCollider = quad.GetComponent<MeshCollider>();
            if (quadCollider != null) leaderboardHitboxColliders[slot] = quadCollider;

            leaderboardHitboxes[slot] = clone;
        }

        /// <summary>Called from ExLeaderboardDisplay.ClearLeaderboardRow whenever a row is blanked
        /// (no entry for that slot, or falling back to native). Drops the cached runId — the hit-box
        /// GameObject itself is left in place (deactivated along with its parent row), same pooling
        /// convention as history — and closes the stats panel if this row happened to be selected,
        /// since its run is no longer the row's content.</summary>
        private static void ClearLeaderboardHitboxRun(int slot)
        {
            leaderboardHitboxRunIds.Remove(slot);

            if (leaderboardSelectedRowId.HasValue && leaderboardSelectedRowId.Value == slot)
            {
                ResetLeaderboardSelection();
            }
        }

        /// <summary>Full teardown of every EX-only leaderboard-stats addition — call when EX scoring
        /// is switched off, mirroring CleanupExHistoryUI in PlayHistoryUI.cs. Safe to call repeatedly.</summary>
        private static void CleanupExLeaderboardStatsUI()
        {
            ResetLeaderboardSelection();

            foreach (var kvp in leaderboardHitboxes)
            {
                if (kvp.Value != null) UnityEngine.Object.Destroy(kvp.Value);
            }
            leaderboardHitboxes.Clear();
            leaderboardHitboxQuads.Clear();
            leaderboardHitboxColliders.Clear();
            leaderboardHitboxRunIds.Clear();
        }

        private static int? leaderboardSelectedRowId = null;

        /// <summary>Bumped on every new leaderboard-stats fetch. A response applies itself only if
        /// this still matches the value it was started with — same pattern as
        /// leaderboardRequestVersion in ExLeaderboardDisplay.cs, but for the per-run detail fetch
        /// rather than the row list itself, so a fast re-shoot or panel close can't have a stale
        /// GET /api/runs/:runId response reopen a panel the player already dismissed.</summary>
        private static int leaderboardStatsRequestVersion = 0;

        private static void SelectLeaderboardRow(int slot)
        {
            if (leaderboardSelectedRowId.HasValue && leaderboardSelectedRowId.Value == slot) return;

            SetLeaderboardIndicatorActive(leaderboardSelectedRowId, false);
            leaderboardSelectedRowId = slot;
            SetLeaderboardIndicatorActive(leaderboardSelectedRowId, true);
        }

        /// <summary>Clears the current leaderboard-stats selection with no new one active, and closes
        /// the panel. Call whenever the underlying rows are about to change (new fetch, song switch,
        /// leaving the song list) or the selected row is shot again to toggle it closed.</summary>
        private static void ResetLeaderboardSelection()
        {
            SetLeaderboardIndicatorActive(leaderboardSelectedRowId, false);
            leaderboardSelectedRowId = null;

            // Invalidates any in-flight GET /api/runs/:runId fetch so a late response can't reopen
            // a panel the player (or a fresher selection) already closed.
            ++leaderboardStatsRequestVersion;

            OptionsMenuClone.HideLeaderboardStatsPanel();
            currentGameplayStatsRunByContext.Remove(LeaderboardStatsContext);
            DestroyTimingGraph(LeaderboardStatsContext);
            DestroyAimGraph(LeaderboardStatsContext);
            DestroySongTimelineGraph(LeaderboardStatsContext);
            DestroyGradeVisual(LeaderboardStatsContext);
        }

        private static void SetLeaderboardIndicatorActive(int? slot, bool active)
        {
            if (!slot.HasValue) return;
            if (!leaderboardHitboxQuads.TryGetValue(slot.Value, out Transform quad) || quad == null) return;

            Transform indicator = quad.Find("SelectedIndicator");
            if (indicator == null)
            {
                if (!active) return;
                if (difficultyIndicatorSource == null) return;

                GameObject ind = UnityEngine.Object.Instantiate(difficultyIndicatorSource, quad);
                ind.name = "SelectedIndicator";
                ind.transform.localPosition = leaderboardIndicatorLocalPosition;
                ind.transform.localRotation = Quaternion.identity;
                ind.transform.localScale = leaderboardIndicatorLocalScale;

                var renderer = ind.GetComponent<MeshRenderer>();
                if (renderer != null)
                {
                    renderer.material.color = new Color(1f, 1f, 1f, 1f);
                    renderer.sortingOrder = -1;
                }

                indicator = ind.transform;
            }

            indicator.gameObject.SetActive(active);
        }

        private static void OnLeaderboardRowShot(int slot)
        {
            if (!leaderboardHitboxRunIds.TryGetValue(slot, out string runId) || string.IsNullOrEmpty(runId)) return;

            MelonLogger.Log($"[ExScoring] Leaderboard row shot: slot={slot} runId={runId}");

            // Shooting the already-selected row again closes the panel instead of re-fetching it.
            if (leaderboardSelectedRowId.HasValue && leaderboardSelectedRowId.Value == slot)
            {
                ResetLeaderboardSelection();
                return;
            }

            SelectLeaderboardRow(slot);
            FetchAndShowLeaderboardStats(runId);
        }

        /// <summary>
        /// Opens the leaderboard stats panel immediately with a "Loading..." placeholder, then
        /// fetches the full run via FetchRun (ApiClient.cs) and swaps in the real graphs once it
        /// lands. Discards the result if a fresher fetch/selection/close has happened in the
        /// meantime (leaderboardStatsRequestVersion mismatch) — see ResetLeaderboardSelection.
        /// </summary>
        private static void FetchAndShowLeaderboardStats(string runId)
        {
            int requestVersion = ++leaderboardStatsRequestVersion;

            Transform panel = ShowLeaderboardStatsPanelShell("Loading...");
            if (panel == null)
            {
                MelonLogger.Log("[ExScoring] FetchAndShowLeaderboardStats: could not open leaderboard stats panel shell.");
                return;
            }

            var loadingLabel = CreateTimingLabel(panel, "ExLeaderboardStatsLoading (Clone)", Vector3.zero, Color.white);
            loadingLabel.text = "Loading run...";

            FetchRun(runId, response =>
            {
                if (requestVersion != leaderboardStatsRequestVersion)
                {
                    MelonLogger.Log($"[ExScoring] FetchAndShowLeaderboardStats: fetch for runId={runId} discarded (stale — current is #{leaderboardStatsRequestVersion}).");
                    return;
                }

                if (response == null)
                {
                    Transform errorPanel = ShowLeaderboardStatsPanelShell("Error");
                    if (errorPanel != null)
                    {
                        var errorLabel = CreateTimingLabel(errorPanel, "ExLeaderboardStatsError (Clone)", Vector3.zero, Color.red);
                        errorLabel.text = "Failed to load run.";
                    }
                    return;
                }

                RecalculatedRun run = RecalculateFromApiResponse(response);
                if (run == null)
                {
                    MelonLogger.Log($"[ExScoring] FetchAndShowLeaderboardStats: RecalculateFromApiResponse returned null for runId={runId}.");
                    return;
                }

                // Re-shows the panel (sweeps the "Loading..." label via its own "(Clone)" cleanup)
                // with the real title and builds the graphs into it.
                ShowLeaderboardGameplayStatsPanel(run);
            });
        }

        /// <summary>Mimics the native Viewport's RectMask2D clipping for the leaderboard's cloned
        /// hit-box quads, same reasoning/limitation as IsHistoryRowVisible in PlayHistoryButton.cs
        /// (a mask only clips UI Graphics, not our MeshRenderer/MeshCollider quads).</summary>
        private static bool IsLeaderboardRowVisible(Transform quad)
        {
            if (leaderboardViewportMaskRect == null)
            {
                RectMask2D mask = quad.GetComponentInParent<RectMask2D>();
                if (mask == null) return true; // fail open
                leaderboardViewportMaskRect = mask.rectTransform;
            }

            Vector3 local = leaderboardViewportMaskRect.InverseTransformPoint(quad.position);
            return leaderboardViewportMaskRect.rect.Contains(local);
        }

        private static void UpdateLeaderboardHitboxVisibility()
        {
            foreach (var kvp in leaderboardHitboxQuads)
            {
                Transform quad = kvp.Value;
                if (quad == null) continue;

                bool visible = IsLeaderboardRowVisible(quad);

                var renderer = quad.GetComponent<MeshRenderer>();
                if (renderer != null) renderer.enabled = visible;

                if (leaderboardHitboxColliders.TryGetValue(kvp.Key, out MeshCollider col) && col != null)
                    col.enabled = visible;
            }
        }

        [HarmonyPatch(typeof(LeaderboardDisplay), "Update")]
        private static class LeaderboardHitboxVisibilityPatch
        {
            private static void Postfix()
            {
                UpdateLeaderboardHitboxVisibility();
            }
        }
    }
}