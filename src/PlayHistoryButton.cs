using System;
using System.Collections.Generic;
using Harmony;
using MelonLoader;
using UnityEngine;
using UnityEngine.Events;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        // One invisible shootable hit-box per history[] slot (0-49), lazily created and reused
        // across panel refreshes — history[] itself is a fixed 50-slot pool baked into the scene,
        // so slot index is a stable key for the lifetime of the game session.
        private static readonly Dictionary<int, GameObject> historyHitboxes = new Dictionary<int, GameObject>();
        private static readonly Dictionary<int, RecalculatedRun> historyHitboxRuns = new Dictionary<int, RecalculatedRun>();
        private static readonly Dictionary<int, Transform> historyHitboxQuads = new Dictionary<int, Transform>();

        // Visible, semi-transparent grey — matches the tone of the rest of the mod's buttons
        // (e.g. ViewRow.FolderColor) but translucent since this sits over existing row text.
        private static readonly Color historyHitboxColor = new Color(0.6f, 0.6f, 0.6f, 0.25f);

        // TUNE THESE IN UNITYEXPLORER: find a "HistoryHitbox_N" object (child of a history row),
        // adjust its Transform's Local Position / Local Scale live until it lines up with the row's
        // visible text, then report the numbers back so they can be hardcoded here as defaults.
        private static Vector3 historyHitboxLocalPosition = new Vector3(275f, -12f, 0f);
        private static Vector3 historyHitboxLocalScale = new Vector3(19.5f, 10f, 1f);

        /// <summary>Same recursive name search as VirtualSongList.FindChild, duplicated locally
        /// since that one's private to VirtualSongList.</summary>
        private static Transform FindChildRecursive(Transform parent, string childName)
        {
            Transform found = parent.Find(childName);
            if (found != null) return found;

            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child.name == childName) return child;
                found = FindChildRecursive(child, childName);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>
        /// Deactivates every branch under `root` that isn't on the path down to `target`, preserving
        /// target's own subtree fully intact (so e.g. Quad's `highlight` child stays untouched) while
        /// hiding decorative siblings (title/artist text, album art, etc.) at every level.
        /// </summary>
        private static void HideAllExceptPath(Transform root, Transform target)
        {
            var keepPath = new HashSet<Transform>();
            Transform t = target;
            while (t != null)
            {
                keepPath.Add(t);
                if (t == root) break;
                t = t.parent;
            }

            HideSiblingsNotInPath(root, keepPath);
        }

        private static void HideSiblingsNotInPath(Transform node, HashSet<Transform> keepPath)
        {
            for (int i = 0; i < node.childCount; i++)
            {
                Transform child = node.GetChild(i);
                if (keepPath.Contains(child))
                {
                    HideSiblingsNotInPath(child, keepPath); // recurse to hide deeper siblings too
                }
                else
                {
                    child.gameObject.SetActive(false);
                }
            }
        }

        /// <summary>
        /// Ensures a shootable hit-box exists for this history slot, parented under the row,
        /// sized to the row's current RectTransform bounds, and wired to fire OnHistoryRowShot.
        /// Safe to call every refresh — reuses the existing hit-box if the slot already has one.
        ///
        /// Clones the FULL native SongSelectItem prefab (not just its "Quad" child in isolation) —
        /// confirmed via testing that folder headers (built the same way by VirtualSongList) show a
        /// working aim-highlight while an isolated Quad clone never does, meaning GunButton.Awake()
        /// needs the surrounding context to initialize correctly. We hide everything except the path
        /// down to Quad afterward, so decorative content doesn't show.
        /// </summary>
        private static void EnsureHistoryHitbox(int slot, SongInfoHistoryItem item)
        {
            if (historyHitboxes.TryGetValue(slot, out GameObject existing) && existing != null)
            {
                // Deliberately not reapplying position/scale here — while tuning in UnityExplorer,
                // this lets live edits on the existing hit-box persist across panel refreshes.
                return;
            }

            GameObject prefabItem = VirtualSongList.SongItemPrefab;
            if (prefabItem == null)
            {
                MelonLogger.Log("[ExScoring] Could not find song row prefab for history hit-box.");
                return;
            }

            GameObject clone = UnityEngine.Object.Instantiate(prefabItem, item.transform, false);
            clone.name = "HistoryHitbox_" + slot;
            clone.transform.localPosition = historyHitboxLocalPosition;
            clone.transform.localRotation = Quaternion.identity;
            clone.transform.localScale = historyHitboxLocalScale;

            // Same as VirtualSongList's header pool — remove the native item-selection component so
            // its own Update/OnSelect logic never runs on our clone.
            var songSelectItem = clone.GetComponent<SongSelectItem>();
            if (songSelectItem != null) UnityEngine.Object.Destroy(songSelectItem);

            Transform quad = FindChildRecursive(clone.transform, "Quad");
            if (quad == null)
            {
                MelonLogger.Log("[ExScoring] Cloned history hit-box has no Quad child — check the prefab structure.");
                historyHitboxes[slot] = clone;
                return;
            }

            // Hide decorative content (title/artist text, album art, etc.) but leave Quad's own
            // subtree (including `highlight`) fully intact.
            HideAllExceptPath(clone.transform, quad);

            historyHitboxQuads[slot] = quad;


            // Force Quad (and its ancestor chain within the clone) active — destroying
            // SongSelectItem above removes whatever native logic normally manages Quad's active
            // state, so it can otherwise be left inactive from however the prefab starts.
            for (Transform t = quad; t != null; t = t.parent)
            {
                t.gameObject.SetActive(true);
                if (t == clone.transform) break;
            }

            // Visible, translucent grey overlay so it reads as a shootable row like the rest of
            // the mod's buttons — same Shader/Material pattern as VirtualSongList.StyleFolderQuad.
            var renderer = quad.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                renderer.enabled = true;
                Shader sh = Shader.Find("Sprites/Default");
                if (sh != null)
                {
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
                gunButton.onHitEvent.AddListener(new Action(() => OnHistoryRowShot(capturedSlot)));
            }
            else
            {
                MelonLogger.Log("[ExScoring] Cloned history hit-box's Quad has no GunButton — check the prefab structure.");
            }

            historyHitboxes[slot] = clone;
        }

        private static void OnHistoryRowShot(int slot)
        {
            if (!historyHitboxRuns.TryGetValue(slot, out RecalculatedRun run) || run == null) return;

            MelonLogger.Log($"[ExScoring] History row shot: {run.songId} ({run.difficulty}), " +
                            $"judgement {run.judgementScore:0.00}/{run.maxJudgementScore:0.00} ({run.judgementPercent:0.00}%)");

            SelectHistoryRow(slot);
            ShowGameplayStatsPanel(run);
        }

        // TUNE THESE IN UNITYEXPLORER, same process as the hit-box position/scale: find a
        // "SelectedIndicator" child under a row's Quad after selecting it, adjust Local Position /
        // Local Scale live, then report back the numbers.
        private static Vector3 historyIndicatorLocalPosition = new Vector3(0f, 0f, -0.005f);
        private static Vector3 historyIndicatorLocalScale = new Vector3(1f, 1f, 1f);

        private static int historySelectedSlot = -1;

        /// <summary>
        /// Only one history row can be selected at a time — hides the previous selection's
        /// indicator (if any) and shows/creates this one's, following the same lazy-create-then-
        /// reuse pattern as SongListUI.UpdateSongSelectionIndicator.
        /// </summary>
        private static void SelectHistoryRow(int slot)
        {
            if (historySelectedSlot == slot) return;

            SetHistoryIndicatorActive(historySelectedSlot, false);
            historySelectedSlot = slot;
            SetHistoryIndicatorActive(historySelectedSlot, true);
        }

        /// <summary>
        /// Clears the current selection with no new one active — call this whenever the list is
        /// repopulated for a different song, since slot indices get reused for different runs and a
        /// leftover indicator would otherwise point at the wrong row. Also hides the gameplay-stats
        /// panel, so leaving and returning to a song's info panel doesn't leave it open with no
        /// selection behind it.
        /// </summary>
        private static void ResetHistorySelection()
        {
            SetHistoryIndicatorActive(historySelectedSlot, false);
            historySelectedSlot = -1;

            OptionsMenuClone.HideHistoryPanel();
            currentGameplayStatsRun = null;
            DestroyTimingGraph();
            DestroyAimGraph();
            DestroySongTimelineGraph();
        }

        private static void SetHistoryIndicatorActive(int slot, bool active)
        {
            if (slot < 0) return;
            if (!historyHitboxQuads.TryGetValue(slot, out Transform quad) || quad == null) return;

            Transform indicator = quad.Find("SelectedIndicator");
            if (indicator == null)
            {
                if (!active) return; // nothing to hide, don't bother creating just to hide it
                if (difficultyIndicatorSource == null) return;

                GameObject ind = UnityEngine.Object.Instantiate(difficultyIndicatorSource, quad);
                ind.name = "SelectedIndicator";
                ind.transform.localPosition = historyIndicatorLocalPosition;
                ind.transform.localRotation = Quaternion.identity;
                ind.transform.localScale = historyIndicatorLocalScale;

                var renderer = ind.GetComponent<MeshRenderer>();
                if (renderer != null)
                {
                    renderer.material.color = new Color(1f, 1f, 1f, 1f);
                    renderer.sortingOrder = -1; // draw beneath the row's own Canvas content
                }

                indicator = ind.transform;
            }

            indicator.gameObject.SetActive(active);
        }

        /// <summary>
        /// The native highlight-tints-to-gun-color system doesn't reliably transfer to a Quad cloned
        /// in isolation. Worse: GunButton.IsHighlighted() itself consistently reports false for our
        /// clone even during confirmed successful hits (shots landing, aim verified via shot sound),
        /// so it's not just the `highlight` object that's broken — IsHighlighted() must depend on
        /// some internal state (likely renderer arrays GunButton.Awake() caches by scanning children
        /// at clone time) that never gets populated correctly for us. Since OnHighlight() itself DOES
        /// fire correctly, we track "recently aimed at" ourselves via a timestamp instead of trusting
        /// IsHighlighted() at all.
        /// </summary>
        private static readonly Dictionary<GunButton, Color> historyHitboxHoverColor = new Dictionary<GunButton, Color>();
        private static readonly Dictionary<GunButton, float> historyHitboxLastHighlightTime = new Dictionary<GunButton, float>();
        private const float HistoryHitboxHighlightHoldSeconds = 0.15f;

        [HarmonyPatch(typeof(GunButton), "OnHighlight", new Type[] { typeof(Vector3), typeof(Gun) })]
        private static class HistoryHitboxHighlightPatch
        {
            private static void Postfix(GunButton __instance, Gun gun)
            {
                if (__instance == null || gun == null) return;
                if (!__instance.gameObject.name.StartsWith("HistoryHitbox_")) return;

                historyHitboxHoverColor[__instance] = ChainArrow.GetHandColor(gun.hand);
                historyHitboxLastHighlightTime[__instance] = Time.time;
            }
        }

        [HarmonyPatch(typeof(GunButton), "Update")]
        private static class HistoryHitboxUpdatePatch
        {
            private static void Postfix(GunButton __instance)
            {
                if (__instance == null) return;
                if (!__instance.gameObject.name.StartsWith("HistoryHitbox_")) return;

                var renderer = __instance.GetComponent<MeshRenderer>();
                if (renderer == null) return;

                bool recentlyHighlighted = historyHitboxLastHighlightTime.TryGetValue(__instance, out float lastTime)
                    && (Time.time - lastTime) <= HistoryHitboxHighlightHoldSeconds;

                Color target = historyHitboxColor;
                if (recentlyHighlighted && historyHitboxHoverColor.TryGetValue(__instance, out Color hoverColor))
                {
                    target = new Color(hoverColor.r, hoverColor.g, hoverColor.b, historyHitboxColor.a);
                }

                renderer.material.color = target;
            }
        }
    }
}