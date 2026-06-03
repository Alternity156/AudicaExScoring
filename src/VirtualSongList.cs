using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using Harmony;
using MelonLoader;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

namespace ExScoringMod
{
    public enum ViewRowKind { FolderHeader, Song, Action, DownloadableSong }

    /// <summary>
    /// A single row in the song-list view: a folder header, a song, or an action button
    /// (Back / Create / Marathon / Delete / Playlists). Headers and actions share the header
    /// pool's styled, shootable button; songs use the song pool.
    /// </summary>
    public struct ViewRow
    {
        public ViewRowKind kind;
        public string folderName; // header
        public int songCount;     // header
        public string songID;     // song
        public string label;      // action: button text
        public string subLabel;   // action: optional second line
        public Color color;       // header + action: quad color (applied at bind time)
        public Action onHit;      // action: callback when shot
        public string actionId;   // action: optional id for selection highlighting
        public string downloadUrl; // downloadable song: maudica download URL
        public int starDiff;      // star header: (int)KataConfig.Difficulty (gold uses Expert); ignored when starTier==0
        public int starTier;      // star header: 1..5 = N stars of starDiff, 6 = gold; 0 = pips only (no filled stars)
        public bool starHeader;   // true = render star icons (incl. pips-only); false = text title

        /// <summary>Default folder-header grey. Action rows override via their own color.</summary>
        public static readonly Color FolderColor = new Color(0.18f, 0.18f, 0.18f, 1f);

        public static ViewRow Header(string folder, int count, string subLabelOverride = null, Color? color = null)
            => new ViewRow { kind = ViewRowKind.FolderHeader, folderName = folder, songCount = count, color = color ?? FolderColor, subLabel = subLabelOverride };

        /// <summary>A folder header that displays the game's star icons instead of a text title.
        /// starTier 1..5 lights that many of starDiff's colored stars; 6 lights the gold set; 0 shows pips only.</summary>
        public static ViewRow StarHeader(string folder, int count, int starDiff, int starTier)
            => new ViewRow { kind = ViewRowKind.FolderHeader, folderName = folder, songCount = count, color = FolderColor, starDiff = starDiff, starTier = starTier, starHeader = true };

        public static ViewRow SongRow(string id)
            => new ViewRow { kind = ViewRowKind.Song, songID = id };

        public static ViewRow ActionRow(string label, Color color, Action onHit, string subLabel = null, string actionId = null)
            => new ViewRow { kind = ViewRowKind.Action, label = label, color = color, onHit = onHit, subLabel = subLabel, actionId = actionId };

        public static ViewRow DownloadableSongRow(string songID, string title, string artist, string mapper,
                                                  string downloadUrl, Color color, Action onHit)
            => new ViewRow
            {
                kind = ViewRowKind.DownloadableSong,
                songID = songID,
                label = title,
                subLabel = string.IsNullOrEmpty(mapper) ? (artist ?? "") : $"{artist} ({mapper})",
                downloadUrl = downloadUrl,
                color = color,
                onHit = onHit,
                actionId = songID   // lets UpdateActionRowText target this row for live progress (3b)
            };
    }

    /// <summary>
    /// PHASE 1 — Virtualized song list core.
    ///
    /// Owns the on-screen list. Instead of one GameObject per row (the old "build all,
    /// hide most" model, ~25s for big libraries), it keeps the full ordered row list as
    /// lightweight data plus one cheap empty placeholder GameObject per row, and binds a
    /// SMALL recycled pool of real visuals to whatever the scroller makes visible.
    ///
    /// Two pools, because rows are heterogeneous:
    ///   - song pool   : real SongSelectItems, rebound via Init() (~0.6 ms).
    ///   - header pool : real items repurposed as folder headers (toggle to expand/collapse).
    /// Both are sized ~displayCount+buffer, so total live visuals stay ~40 regardless of how
    /// many folders OR songs exist.
    ///
    /// SHARED SOURCE: CurrentView / CurrentViewSongIDs is the single source of truth for
    /// "what songs are in the list". AutoSelect / RandomSong read it (Phase 3). FolderRowManager
    /// pushes it via SetView (Phase 2). For now a small TEST HARNESS (F8) builds it from
    /// SongFolderManager so we can validate the core in isolation.
    /// </summary>
    internal static class VirtualSongList
    {
        private static bool active = false;
        public static bool IsActive => active;

        /// <summary>
        /// Invoked when a folder header is shot. FolderRowManager sets this to its ToggleFolder,
        /// keeping VirtualSongList independent of the folder logic.
        /// </summary>
        public static Action<string> FolderToggleHandler;

        /// <summary>
        /// Id of the action row that should show the selection highlight (e.g. "marathon"). When
        /// set, song rows suppress their own selection indicator so only the action looks selected.
        /// </summary>
        public static string SelectedActionId;

        /// <summary>Set (or clear with null) the selected action row, refreshing visible indicators.</summary>
        public static void SetSelectedAction(string id)
        {
            SelectedActionId = id;
            RefreshSelectionIndicators();
        }

        /// <summary>Live-update the visible text of the bound action row with the given id (e.g. while typing).</summary>
        public static void UpdateActionRowText(string actionId, string label, string subLabel)
        {
            if (string.IsNullOrEmpty(actionId)) return;
            foreach (var kv in rowBindings)
            {
                if (!kv.Value.isHeader) continue;
                int idx = kv.Key;
                if (idx < 0 || idx >= view.Count) continue;
                var row = view[idx];
                if ((row.kind != ViewRowKind.Action && row.kind != ViewRowKind.DownloadableSong)
                    || row.actionId != actionId) continue;

                var hi = headerPool[kv.Value.slot];
                if (hi.title != null) hi.title.text = label ?? "";
                if (hi.artist != null) hi.artist.text = subLabel ?? "";
                return;
            }
        }

        private static SongSelect songSelect;
        private static ShellScrollable scroller;
        private static Transform scrollParent;
        private static GameObject hiddenHolder;
        private static float savedScroll = 0f; // scroll position remembered across leave/return
        private static int generation = 0; // bumped on every SetView/Teardown; select coroutines bail on mismatch

        // ── The view (shared source of truth) ────────────────────────────────
        private static readonly List<ViewRow> view = new List<ViewRow>();
        private static readonly List<string> viewSongIDs = new List<string>();
        private static readonly Dictionary<string, int> songToViewIndex = new Dictionary<string, int>();

        public static IReadOnlyList<ViewRow> CurrentView => view;
        public static IReadOnlyList<string> CurrentViewSongIDs => viewSongIDs;

        // ── Placeholders: one cheap empty GO per view row ────────────────────
        private static readonly List<GameObject> placeholders = new List<GameObject>();

        // ── Pools ─────────────────────────────────────────────────────────────
        private sealed class SongPoolItem
        {
            public GameObject go;
            public SongSelectItem ssi;
            public bool inUse;
            public string boundSong; // last song Init()'d, to skip redundant rebinds
        }

        private sealed class HeaderPoolItem
        {
            public GameObject go;
            public TextMeshProUGUI title;
            public TextMeshProUGUI artist;
            public GunButton button;
            public Material quadMat;   // recolored per bind
            public bool inUse;

            // Star-display reuse (star-sort headers). Cached once at creation; toggled per bind.
            public GameObject starDisplay;                 // Canvas/StarDisplay (kept off except on star headers)
            public GameObject starPips;                    // StarDisplay/star_pips (the 5 empty slots)
            public Dictionary<string, GameObject> starObjs; // name -> StarDisplay/stars/<child>
        }

        private static readonly List<SongPoolItem> songPool = new List<SongPoolItem>();
        private static readonly List<HeaderPoolItem> headerPool = new List<HeaderPoolItem>();

        // viewIndex -> (isHeader, slot)
        private struct Binding { public bool isHeader; public int slot; }
        private static readonly Dictionary<int, Binding> rowBindings = new Dictionary<int, Binding>();
        private static readonly List<int> tmpRelease = new List<int>();

        private const int PoolBuffer = 6;

        // ══════════════════════════════════════════════════════════════════════
        //  CORE (the keeper — Phase 2/3 build on this)
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Replace the current view with a new ordered row list. Rebuilds placeholders to match,
        /// reusing the pools. This is the push entry point FolderRowManager will call in Phase 2.
        /// </summary>
        public static void SetView(List<ViewRow> rows) => SetView(rows, null);

        /// <summary>
        /// Replace the view, scrolling to <paramref name="targetScroll"/> if given. Navigation
        /// level changes pass an explicit target (0 to drill in, the saved index to back out),
        /// since the live scroll position is meaningless across two different lists.
        /// </summary>
        public static void SetView(List<ViewRow> rows, float? targetScroll)
        {
            if (!EnsureRefs()) return;

            // The game's ShowSongList (skipped in folder mode) used to activate the list panel;
            // make sure it's visible.
            if (!scrollParent.gameObject.activeSelf)
                scrollParent.gameObject.SetActive(true);

            // Preserve scroll position. While already active (e.g. a folder toggle) the live
            // scroller position is meaningful. On a fresh (re)entry the live scroller was zeroed
            // in Teardown, so restore the position we saved when leaving.
            float prevScroll = targetScroll ?? (active ? scroller.GetScrollIndex() : savedScroll);

            view.Clear();
            viewSongIDs.Clear();
            songToViewIndex.Clear();
            for (int i = 0; i < rows.Count; i++)
            {
                view.Add(rows[i]);
                if (rows[i].kind == ViewRowKind.Song)
                {
                    songToViewIndex[rows[i].songID] = i;
                    viewSongIDs.Add(rows[i].songID);
                }
            }

            ReleaseAllBindings();
            ClearPlaceholders();
            BuildPlaceholders();

            // Pools are created lazily on first SetView while active.
            EnsurePools();

            active = true;
            generation++; // a new view invalidates select coroutines targeting the old one

            float maxScroll = Mathf.Max(0f, view.Count - scroller.displayCount);
            scroller.SnapTo(Mathf.Clamp(prevScroll, 0f, maxScroll), true);
            scroller.UpdateScroll(-1);

            MelonLogger.Log($"[VList] SetView: {view.Count} rows, scroll={Mathf.Clamp(prevScroll, 0f, maxScroll):0.0}/{maxScroll:0.0}, displayCount={scroller.displayCount:0.0}");

            Sync();
        }

        /// <summary>
        /// Reactive driver — call every LateUpdate. Binds whatever placeholders the scroller
        /// has made active to pooled visuals, releases the rest.
        /// </summary>
        public static void Sync()
        {
            if (!active || scroller == null) return;

            var st = MenuState.GetState();
            if (st != MenuState.State.SongPage && st != MenuState.State.LaunchPage)
                return; // idle off-list; teardown happens via the ShowSongList prefix

            // Release bindings whose placeholder is gone/inactive.
            tmpRelease.Clear();
            foreach (var kv in rowBindings)
            {
                int idx = kv.Key;
                if (idx < 0 || idx >= placeholders.Count || placeholders[idx] == null ||
                    !placeholders[idx].activeSelf)
                {
                    tmpRelease.Add(idx);
                }
            }
            for (int i = 0; i < tmpRelease.Count; i++) ReleaseBinding(tmpRelease[i]);

            // Bind active placeholders not yet bound.
            for (int i = 0; i < placeholders.Count; i++)
            {
                var ph = placeholders[i];
                if (ph == null || !ph.activeSelf) continue;
                if (rowBindings.ContainsKey(i)) continue;
                BindRow(i);
            }
        }

        public static int IndexOf(string songID)
            => songToViewIndex.TryGetValue(songID, out int i) ? i : -1;

        /// <summary>Current scroll index (0 when inactive). Used to snapshot per-level scroll.</summary>
        public static float GetScroll() => (active && scroller != null) ? scroller.GetScrollIndex() : 0f;

        /// <summary>The pooled SongSelectItem currently displaying songID, or null if off-screen.</summary>
        public static SongSelectItem GetBoundItem(string songID)
        {
            int idx = IndexOf(songID);
            if (idx < 0) return null;
            if (!rowBindings.TryGetValue(idx, out Binding b) || b.isHeader) return null;
            return songPool[b.slot].ssi;
        }

        /// <summary>
        /// Scroll the view to a song and select it. Replaces the old mSongButtons-based
        /// scroll+OnSelect used by AutoSelect / RandomSong (wired up in Phase 3).
        /// </summary>
        public static void ScrollToAndSelect(string songID) => ScrollToAndSelect(songID, false);

        /// <summary>
        /// Select songID, scrolling to center it. If preserveIfVisible is true and the row is
        /// already within the current window, the scroll position is left untouched (used by the
        /// leave/return restore so an in-view song keeps your exact prior position).
        /// </summary>
        private static int selectSeq = 0; // newest-wins: each select bumps this; older coroutines bail

        public static void ScrollToAndSelect(string songID, bool preserveIfVisible)
        {
            int idx0 = IndexOf(songID);
            if (!active) return;
            if (idx0 < 0) { MelonLogger.Log($"[VList] ScrollToAndSelect: '{songID}' not in current view."); return; }
            int mySeq = ++selectSeq;
            MelonCoroutines.Start(ScrollToAndSelectCo(songID, idx0, preserveIfVisible, generation, mySeq));
        }

        private static bool IsRowInWindow(int viewIndex)
        {
            float top = scroller.GetScrollIndex();
            float bottom = top + scroller.displayCount - 1;
            return viewIndex >= top && viewIndex <= bottom;
        }

        private static IEnumerator ScrollToAndSelectCo(string songID, int viewIndex, bool preserveIfVisible, int gen, int seq)
        {
            // Decide whether to scroll. If restoring and the row is already visible at the current
            // (restored) position, don't move — keeps the user's exact prior scroll. Otherwise
            // center the row.
            bool doScroll = !(preserveIfVisible && IsRowInWindow(viewIndex));
            if (doScroll)
            {
                float maxScroll = Mathf.Max(0f, view.Count - scroller.displayCount);
                float target = Mathf.Clamp(viewIndex - scroller.displayCount / 2f, 0f, maxScroll);
                scroller.SnapTo(target, true);
                scroller.UpdateScroll(-1);
            }

            // Wait for the row to bind (placeholders/scroller settle over a few frames on re-entry).
            const int maxFrames = 30;
            SongSelectItem item = null;
            for (int f = 0; f < maxFrames; f++)
            {
                yield return null;
                // Bail if: torn down, the view was rebuilt under us (stale session — the guard
                // against a pre-leave/return coroutine resuming mid-rebuild), or a newer select
                // superseded this one (rapid Random Song presses — newest wins).
                if (!active || gen != generation || seq != selectSeq) yield break;
                Sync();
                item = GetBoundItem(songID);
                if (item != null) break;
            }

            if (item == null) { MelonLogger.Log($"[VList] ScrollToAndSelect: '{songID}' did not bind after {maxFrames} frames."); yield break; }
            if (gen != generation || seq != selectSeq) yield break;

            SelectBoundItem(songID, item);
        }

        /// <summary>
        /// Select a song that's already within the visible window (e.g. the first song of a
        /// just-opened folder) WITHOUT moving the scroll. Falls back to scroll+select if the
        /// row isn't currently bound.
        /// </summary>
        public static void SelectInView(string songID)
        {
            if (!active || IndexOf(songID) < 0) return;
            MelonCoroutines.Start(SelectInViewCo(songID, generation));
        }

        private static IEnumerator SelectInViewCo(string songID, int gen)
        {
            yield return null;
            if (!active || gen != generation) yield break;
            Sync();
            yield return null;
            if (!active || gen != generation) yield break;

            var item = GetBoundItem(songID);
            if (item == null) { ScrollToAndSelect(songID); yield break; } // not actually visible → scroll to it

            SelectBoundItem(songID, item);
        }

        private static void SelectBoundItem(string songID, SongSelectItem item)
        {
            ExScoring.selectedSong = songID;
            ExScoring.isAutoSelecting = true;
            try
            {
                item.OnSelect();
                ExScoring.UpdateLaunchPanelInfo();
            }
            finally { ExScoring.isAutoSelecting = false; }

            ExScoring.menuState = MenuState.State.SongPage;
        }

        /// <summary>
        /// After opening a folder, keep the current scroll position; only scroll if the folder's
        /// first song is below the visible window (i.e. the header sat at/near the bottom).
        /// Never forces the header to the top. Reused by the Phase 2 FolderRowManager hand-off.
        /// </summary>
        public static void RevealFolderIfNeeded(string folderName)
        {
            if (!active || scroller == null) return;

            int hdr = HeaderIndex(folderName);
            if (hdr < 0) return;

            int firstSong = hdr + 1;
            if (firstSong >= view.Count || view[firstSong].kind != ViewRowKind.Song)
                return; // folder empty or not actually open

            float cur = scroller.GetScrollIndex();

            // Already visible (incl. "already at the top") → leave the scroll alone.
            if (firstSong < cur + scroller.displayCount) return;

            // First song is below the window → scroll down just enough to bring it into view.
            float maxScroll = Mathf.Max(0f, view.Count - scroller.displayCount);
            float target = Mathf.Clamp(firstSong - scroller.displayCount + 1, 0f, maxScroll);
            scroller.SnapTo(target, true);
        }

        /// <summary>DIAGNOSTIC: log the game-side list state the original ShowSongList will see.</summary>
        public static void LogStateBeforeRebuild()
        {
            var ss = songSelect ?? UnityEngine.Object.FindObjectOfType<SongSelect>();
            if (ss == null) { MelonLogger.Log("[VList-DIAG] no SongSelect"); return; }

            int btnCount = -1, btnNulls = 0;
            try
            {
                var b = ss.mSongButtons;
                if (b != null)
                {
                    btnCount = b.Count;
                    for (int i = 0; i < b.Count; i++) if (b[i] == null) btnNulls++;
                }
            }
            catch (Exception e) { MelonLogger.Log("[VList-DIAG] mSongButtons threw: " + e.Message); }

            int rows = -1;
            try { if (ss.scroller != null && ss.scroller.mRows != null) rows = ss.scroller.mRows.Count; }
            catch { }

            MelonLogger.Log($"[VList-DIAG] before rebuild: mSongButtons={btnCount} (nulls={btnNulls}), " +
                            $"mRows={rows}, active={active}, songPool={songPool.Count}, headerPool={headerPool.Count}");
        }

        public static void Teardown()
        {
            active = false;
            generation++; // invalidate any in-flight select coroutines from this session

            ReleaseAllBindings(); // pooled visuals (incl. the just-selected one) go back to hiddenHolder, inactive
            ClearPlaceholders();  // destroys only the empty placeholder GOs and empties mRows

            // CRITICAL: SongSelectItem.Init() registered our pooled items into songSelect.mSongButtons.
            // The game's ShowSongList (which runs right after this teardown, from the patch prefix)
            // DESTROYS every item in that list as part of its rebuild — which would destroy our
            // pooled GameObjects and crash BindRow on .transform next frame. Remove ours first.
            if (songSelect != null && songSelect.mSongButtons != null)
            {
                try
                {
                    for (int i = 0; i < songPool.Count; i++)
                        if (songPool[i] != null && songPool[i].ssi != null)
                            songSelect.mSongButtons.Remove(songPool[i].ssi);
                }
                catch (Exception e) { MelonLogger.Log("[VList] Teardown mSongButtons purge failed: " + e.Message); }
            }

            // Remember where we were so re-entry can restore it (SetView reads savedScroll).
            // We still zero the LIVE scroller below: the game's empty ShowSongList rebuild runs
            // right after and a stale live index against zero rows was a source of NREs.
            if (scroller != null)
            {
                try { savedScroll = scroller.GetScrollIndex(); }
                catch { savedScroll = 0f; }
            }

            // Reset the live scroll position for the game's imminent empty rebuild.
            if (scroller != null)
            {
                scroller.mIndex = 0f;
                scroller.mDestinationIndex = 0f;
            }

            // NOTE: pools are deliberately NOT destroyed. The game caches the SongSelectItem
            // the user last selected; destroying it here left a dangling reference that crashed
            // the game's own ShowSongList on the next entry. Keeping pooled items alive (inactive,
            // under hiddenHolder) avoids that and makes re-activation instant (no re-cloning).

            view.Clear();
            viewSongIDs.Clear();
            songToViewIndex.Clear();
        }

        // ── Binding ──────────────────────────────────────────────────────────

        private static void BindRow(int viewIndex)
        {
            var row = view[viewIndex];
            var ph = placeholders[viewIndex];

            if (row.kind == ViewRowKind.Song)
            {
                int slot = AcquireSongSlot();
                if (slot < 0) { MelonLogger.Log($"[VList] Song pool exhausted at row {viewIndex}."); return; }
                var pi = songPool[slot];

                // The game's ShowSongList can destroy items we registered via Init. If this slot's
                // GameObject was destroyed, rebuild it before use (touching .transform would throw).
                if (!Alive(pi.go) || (pi.ssi != null && !Alive(pi.ssi)))
                {
                    RevivePoolItem(slot);
                    pi = songPool[slot];
                }

                pi.go.transform.SetParent(ph.transform, false);
                pi.go.transform.localPosition = Vector3.zero;
                pi.go.transform.localRotation = Quaternion.identity;
                pi.go.SetActive(true);

                if (pi.boundSong != row.songID)
                {
                    if (pi.ssi != null) pi.ssi.Init(row.songID, songSelect);
                    pi.boundSong = row.songID;
                }

                // SongSelectItem.Init registers the item into songSelect.mSongButtons. Since the
                // game now builds an empty list and we own the display, keep our recycled pool
                // items OUT of that list — otherwise the game's own ShowSongList iterates a
                // deactivated/reparented pooled item on re-entry and NREs. (Idempotent; harmless
                // if the item isn't present.)
                try
                {
                    if (songSelect != null && songSelect.mSongButtons != null && pi.ssi != null)
                        songSelect.mSongButtons.Remove(pi.ssi);
                }
                catch (Exception e) { MelonLogger.Log("[VList] mSongButtons.Remove failed: " + e.Message); }

                if (pi.ssi != null && pi.ssi.button != null)
                {
                    pi.ssi.button.destroyOnShot = false;
                    pi.ssi.button.disableOnShot = false;
                    pi.ssi.button.doMeshExplosion = false;
                    pi.ssi.button.SetInteractable(true);
                }

                // Selection indicator follows the SONG, not the GameObject (suppressed while an
                // action row is selected).
                if (pi.ssi != null)
                    ApplySongIndicator(pi, row);

                pi.inUse = true;
                rowBindings[viewIndex] = new Binding { isHeader = false, slot = slot };
            }
            else // FolderHeader or Action — both use the header pool's styled button
            {
                int slot = AcquireHeaderSlot();
                if (slot < 0) { MelonLogger.Log($"[VList] Header pool exhausted at row {viewIndex}."); return; }
                var hi = headerPool[slot];

                if (!Alive(hi.go))
                {
                    headerPool[slot] = CreateHeaderPoolItem(songSelect.songSelectItemPrefab.gameObject, slot);
                    hi = headerPool[slot];
                    MelonLogger.Log($"[VList] Revived destroyed header pool slot {slot}.");
                }

                hi.go.transform.SetParent(ph.transform, false);
                hi.go.transform.localPosition = Vector3.zero;
                hi.go.transform.localRotation = Quaternion.identity;
                hi.go.SetActive(true);

                // Per-row color (folder grey, or the action row's own color).
                if (hi.quadMat != null) hi.quadMat.color = row.color;

                // Default: star icons off (header pool is shared with text folders / action rows).
                if (hi.starDisplay != null) hi.starDisplay.SetActive(false);

                if (row.kind == ViewRowKind.FolderHeader)
                {
                    if (row.starHeader && hi.starDisplay != null && hi.starObjs != null)
                    {
                        // Star-icon header: no text title, count on the second line, lit star icons (pips only when tier==0).
                        if (hi.title != null) hi.title.text = "";
                        if (hi.artist != null) hi.artist.text = row.subLabel ?? $"{row.songCount} song{(row.songCount != 1 ? "s" : "")}";
                        ApplyStarIcons(hi, row.starDiff, row.starTier);
                    }
                    else
                    {
                        if (hi.title != null) hi.title.text = row.folderName;
                        if (hi.artist != null) hi.artist.text = row.subLabel ?? $"{row.songCount} song{(row.songCount != 1 ? "s" : "")}";
                    }

                    if (hi.button != null)
                    {
                        string captured = row.folderName;
                        hi.button.onHitEvent = new UnityEvent();
                        hi.button.onHitEvent.AddListener(new Action(() => FolderToggleHandler?.Invoke(captured)));
                    }
                }
                else // Action
                {
                    if (hi.title != null) hi.title.text = row.label ?? "";
                    if (hi.artist != null) hi.artist.text = row.subLabel ?? "";

                    if (hi.button != null)
                    {
                        Action cb = row.onHit;
                        hi.button.onHitEvent = new UnityEvent();
                        hi.button.onHitEvent.AddListener(new Action(() => cb?.Invoke()));
                    }
                }

                hi.inUse = true;
                rowBindings[viewIndex] = new Binding { isHeader = true, slot = slot };
                ApplyHeaderIndicator(hi, row);
            }
        }

        // ── Selection indicators ───────────────────────────────────────────────

        /// <summary>Re-apply selection highlights to all currently-bound rows.</summary>
        public static void RefreshSelectionIndicators()
        {
            foreach (var kv in rowBindings)
            {
                int idx = kv.Key;
                if (idx < 0 || idx >= view.Count) continue;
                var b = kv.Value;
                if (b.isHeader) ApplyHeaderIndicator(headerPool[b.slot], view[idx]);
                else ApplySongIndicator(songPool[b.slot], view[idx]);
            }
        }

        /// <summary>Song selection highlight — shown for the selected song unless an action is selected.</summary>
        private static void ApplySongIndicator(SongPoolItem pi, ViewRow row)
        {
            if (pi.ssi == null) return;
            if (SelectedActionId == null && row.songID == ExScoring.selectedSong)
            {
                ExScoring.UpdateSongSelectionIndicator(pi.ssi);
            }
            else
            {
                var stale = pi.ssi.transform.Find("SelectedIndicator");
                if (stale != null) stale.gameObject.SetActive(false);
            }
        }

        /// <summary>Action/header selection highlight — shown when the row's actionId is selected.</summary>
        private static void ApplyHeaderIndicator(HeaderPoolItem hi, ViewRow row)
        {
            bool sel = !string.IsNullOrEmpty(row.actionId) && row.actionId == SelectedActionId;
            Transform existing = hi.go.transform.Find("SelectedIndicator");

            if (!sel)
            {
                if (existing != null) existing.gameObject.SetActive(false);
                return;
            }

            if (existing == null)
            {
                if (ExScoring.difficultyIndicatorSource == null) return;
                var ind = UnityEngine.Object.Instantiate(ExScoring.difficultyIndicatorSource, hi.go.transform);
                ind.name = "SelectedIndicator";
                ind.transform.localPosition = new Vector3(0f, 0f, -0.05f);
                ind.transform.localRotation = Quaternion.identity;
                ind.transform.localScale = new Vector3(3.625f, 1.5f, 1f); // match song-row indicator
                var r = ind.GetComponent<MeshRenderer>();
                if (r != null) { r.material.color = new Color(1f, 1f, 1f, 1f); r.sortingOrder = -1; }
                existing = ind.transform;
            }
            existing.gameObject.SetActive(true);
        }

        private static void ReleaseBinding(int viewIndex)
        {
            if (!rowBindings.TryGetValue(viewIndex, out Binding b)) return;
            rowBindings.Remove(viewIndex);

            if (b.isHeader)
            {
                var hi = headerPool[b.slot];
                if (Alive(hi.go))
                {
                    hi.go.SetActive(false);
                    hi.go.transform.SetParent(hiddenHolder.transform, false);
                }
                hi.inUse = false;
            }
            else
            {
                var pi = songPool[b.slot];
                if (Alive(pi.go))
                {
                    pi.go.SetActive(false);
                    pi.go.transform.SetParent(hiddenHolder.transform, false);
                }
                pi.inUse = false;
            }
        }

        private static void ReleaseAllBindings()
        {
            tmpRelease.Clear();
            foreach (var kv in rowBindings) tmpRelease.Add(kv.Key);
            for (int i = 0; i < tmpRelease.Count; i++) ReleaseBinding(tmpRelease[i]);
            tmpRelease.Clear();
        }

        private static int AcquireSongSlot()
        {
            for (int i = 0; i < songPool.Count; i++) if (!songPool[i].inUse) return i;
            return -1;
        }

        private static int AcquireHeaderSlot()
        {
            for (int i = 0; i < headerPool.Count; i++) if (!headerPool[i].inUse) return i;
            return -1;
        }

        // ── Placeholders ───────────────────────────────────────────────────────

        private static void BuildPlaceholders()
        {
            for (int i = 0; i < view.Count; i++)
            {
                var go = new GameObject("VList_Row_" + i);
                go.transform.SetParent(scrollParent, false);
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                go.transform.localScale = Vector3.one;
                placeholders.Add(go);
                scroller.AddRow(go);
            }
        }

        private static void ClearPlaceholders()
        {
            // Our pooled visuals were already released to hiddenHolder, so clearing the
            // scroller's rows only destroys the empty placeholders.
            if (scroller != null)
            {
                bool prev = scroller.destroyChildren;
                scroller.destroyChildren = true;
                try { scroller.ClearRows(); }
                catch (Exception e) { MelonLogger.Log("[VList] ClearRows failed: " + e); }
                scroller.destroyChildren = prev;
            }
            placeholders.Clear();
        }

        // ── Pools (created once per activation, reused across SetView calls) ────

        private static void EnsurePools()
        {
            int target = Mathf.CeilToInt(scroller.displayCount) + PoolBuffer;

            var prefabGO = songSelect.songSelectItemPrefab.gameObject;

            while (songPool.Count < target)
                songPool.Add(CreateSongPoolItem(prefabGO, songPool.Count));

            while (headerPool.Count < target)
            {
                var hi = CreateHeaderPoolItem(prefabGO, headerPool.Count);
                headerPool.Add(hi);
            }
        }

        private static SongPoolItem CreateSongPoolItem(GameObject prefabGO, int index)
        {
            var clone = UnityEngine.Object.Instantiate(prefabGO, hiddenHolder.transform);
            clone.name = "VList_SongPool_" + index;
            clone.SetActive(false);
            return new SongPoolItem
            {
                go = clone,
                ssi = clone.GetComponent<SongSelectItem>(),
                inUse = false,
                boundSong = null
            };
        }

        /// <summary>
        /// IL2CPP-safe "is this object still alive" check. A destroyed Il2Cpp object's managed
        /// wrapper is non-null but compares equal to null via the Unity == override; touching its
        /// .transform throws. Use this before reusing any pooled GameObject.
        /// </summary>
        private static bool Alive(UnityEngine.Object o) => o != null;

        /// <summary>Rebuild a song pool slot whose GameObject was destroyed (e.g. by the game).</summary>
        private static void RevivePoolItem(int slot)
        {
            var prefabGO = songSelect.songSelectItemPrefab.gameObject;
            songPool[slot] = CreateSongPoolItem(prefabGO, slot);
            MelonLogger.Log($"[VList] Revived destroyed song pool slot {slot}.");
        }

        private static HeaderPoolItem CreateHeaderPoolItem(GameObject prefabGO, int index)
        {
            var clone = UnityEngine.Object.Instantiate(prefabGO, hiddenHolder.transform);
            clone.name = "VList_HeaderPool_" + index;
            clone.SetActive(false);

            // A header has no song data — remove SongSelectItem so its Update/OnSelect never run.
            var ssi = clone.GetComponent<SongSelectItem>();
            if (ssi != null) UnityEngine.Object.Destroy(ssi);

            // Cache the title/artist labels before re-styling.
            TextMeshProUGUI title = null, artist = null;
            var tmps = clone.GetComponentsInChildren<TextMeshProUGUI>(true);
            foreach (var tmp in tmps)
            {
                if (tmp.name == "title") { title = tmp; tmp.fontStyle = FontStyles.Bold; }
                else if (tmp.name == "artist") artist = tmp;
            }

            HideDecorations(clone);
            HideCanvasElements(clone);
            DisableBloom(clone);
            GunButton button = StyleFolderQuad(clone, out Material quadMat);

            // Cache the StarDisplay subtree so star-sort headers can light icons later.
            // HideCanvasElements left StarDisplay inactive; we re-enable it only on star headers.
            GameObject starDisplay = null, starPips = null;
            Dictionary<string, GameObject> starObjs = null;
            Transform sd = FindChild(clone.transform, "StarDisplay");
            if (sd != null)
            {
                starDisplay = sd.gameObject;
                Transform pips = FindChild(sd, "star_pips");
                if (pips != null) starPips = pips.gameObject;
                Transform starsRoot = FindChild(sd, "stars");
                if (starsRoot != null)
                {
                    starObjs = new Dictionary<string, GameObject>();
                    for (int i = 0; i < starsRoot.childCount; i++)
                    {
                        Transform c = starsRoot.GetChild(i);
                        if (!starObjs.ContainsKey(c.name)) starObjs.Add(c.name, c.gameObject);
                    }
                }
            }

            return new HeaderPoolItem
            {
                go = clone,
                title = title,
                artist = artist,
                button = button,
                quadMat = quadMat,
                inUse = false,
                starDisplay = starDisplay,
                starPips = starPips,
                starObjs = starObjs
            };
        }

        private static void DestroyPool()
        {
            for (int i = 0; i < songPool.Count; i++)
                if (songPool[i].go != null) UnityEngine.Object.Destroy(songPool[i].go);
            songPool.Clear();

            for (int i = 0; i < headerPool.Count; i++)
                if (headerPool[i].go != null) UnityEngine.Object.Destroy(headerPool[i].go);
            headerPool.Clear();
        }

        // ── Header styling (ported from FolderRowManager; consolidated in Phase 2) ──

        private static void HideDecorations(GameObject row)
        {
            Transform t = row.transform;
            for (int i = 0; i < t.childCount; i++)
            {
                Transform child = t.GetChild(i);
                if (child.name == "Quad" || child.name == "Canvas") continue;
                child.gameObject.SetActive(false);
            }
        }

        private static void HideCanvasElements(GameObject row)
        {
            string[] toHide = { "StarDisplay", "mapper", "high_score", "percent",
                                "friends", "friends_icon", "global", "global_icon" };
            foreach (string name in toHide)
            {
                Transform target = FindChild(row.transform, name);
                if (target != null) target.gameObject.SetActive(false);
            }
        }

        private static void DisableBloom(GameObject row)
        {
            var all = row.GetComponentsInChildren<Transform>(true);
            foreach (var t in all)
            {
                string n = t.name;
                if (n.Contains("Bloom") || n.Contains("bloom") || n.Contains("Glow") || n.Contains("glow"))
                    t.gameObject.SetActive(false);
            }
        }

        private static GunButton StyleFolderQuad(GameObject row, out Material mat)
        {
            mat = null;
            Transform quad = FindChild(row.transform, "Quad");
            if (quad == null) return null;

            var renderer = quad.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                Shader sh = Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
                if (sh != null)
                {
                    mat = new Material(sh) { color = ViewRow.FolderColor };
                    renderer.material = mat;
                }
            }

            var gunButton = quad.GetComponent<GunButton>();
            if (gunButton != null)
            {
                gunButton.destroyOnShot = false;
                gunButton.disableOnShot = false;
                gunButton.doMeshExplosion = false;
                gunButton.doParticles = false;
                gunButton.fakeBloom = null;
                gunButton.highlightFakeBloom = null;
                gunButton.onHitEvent = new UnityEvent();
            }
            return gunButton;
        }

        /// <summary>Light the star icons on a star-sort header: enable the StarDisplay, show the 5
        /// background pips, then turn on starDiff's first `tier` stars (or all 5 gold stars when tier==6).</summary>
        private static void ApplyStarIcons(HeaderPoolItem hi, int starDiff, int tier)
        {
            hi.starDisplay.SetActive(true);
            if (hi.starPips != null) hi.starPips.SetActive(true);

            // Reset every star child off, then light the ones we want.
            foreach (var kv in hi.starObjs) kv.Value.SetActive(false);

            if (tier <= 0) return; // pips only (Unplayed)

            if (tier >= 6)
            {
                for (int n = 1; n <= 5; n++)
                    if (hi.starObjs.TryGetValue($"star_gold_{n:00}", out var g)) g.SetActive(true);
                return;
            }

            string prefix = "star_" + DiffPrefix(starDiff) + "_";
            int count = tier > 5 ? 5 : tier;
            for (int n = 1; n <= count; n++)
                if (hi.starObjs.TryGetValue($"{prefix}{n:00}", out var s)) s.SetActive(true);
        }

        /// <summary>GameObject name prefix for each difficulty's star set (Normal uses "medium").</summary>
        private static string DiffPrefix(int diff)
        {
            switch ((KataConfig.Difficulty)diff)
            {
                case KataConfig.Difficulty.Easy: return "easy";
                case KataConfig.Difficulty.Normal: return "medium";
                case KataConfig.Difficulty.Hard: return "hard";
                default: return "expert";
            }
        }

        private static Transform FindChild(Transform parent, string childName)
        {
            Transform found = parent.Find(childName);
            if (found != null) return found;
            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child.name == childName) return child;
                found = FindChild(child, childName);
                if (found != null) return found;
            }
            return null;
        }

        // ── Refs / lifecycle helpers ────────────────────────────────────────────

        private static bool EnsureRefs()
        {
            if (songSelect == null) songSelect = UnityEngine.Object.FindObjectOfType<SongSelect>();
            if (songSelect == null) { MelonLogger.Log("[VList] No SongSelect — be on the song page."); return false; }
            scroller = songSelect.scroller;
            if (scroller == null) { MelonLogger.Log("[VList] No scroller."); return false; }
            scrollParent = scroller.scrollParent;
            if (scrollParent == null) { MelonLogger.Log("[VList] No scrollParent."); return false; }
            if (songSelect.songSelectItemPrefab == null) { MelonLogger.Log("[VList] No songSelectItemPrefab."); return false; }
            EnsureHiddenHolder();
            return true;
        }

        private static void EnsureHiddenHolder()
        {
            if (hiddenHolder == null)
            {
                hiddenHolder = new GameObject("VList_Hidden");
                hiddenHolder.SetActive(false);
                UnityEngine.Object.DontDestroyOnLoad(hiddenHolder);
            }
        }

        private static int HeaderIndex(string folderName)
        {
            for (int i = 0; i < view.Count; i++)
                if (view[i].kind == ViewRowKind.FolderHeader && view[i].folderName == folderName)
                    return i;
            return -1;
        }
    }

    /// <summary>
    /// The game's ShowSongList must run (it sets up the scroller's input/scroll machinery), but
    /// we tear our virtual list down and reset the scroll position FIRST, so the original's body
    /// doesn't index a stale scroll position against the (intentionally empty) id list — that was
    /// the recurring NRE. The FolderRowManager.Rebuild postfix then repopulates via SetView.
    /// </summary>
    [HarmonyPatch(typeof(SongSelect), "ShowSongList", new Type[0])]
    internal static class VListShowSongListPatch
    {
        private static void Prefix()
        {
            VirtualSongList.LogStateBeforeRebuild();
            if (VirtualSongList.IsActive) VirtualSongList.Teardown();
        }

        /// <summary>
        /// The game's ShowSongList body intermittently NREs after pooled song items have been
        /// Init'd (it touches some game-side state that our recycling invalidates — the failure
        /// is inside its compiled body with no usable IL2CPP stack). In folder mode that body has
        /// nothing useful to build (we suppress its ids) and the scroll setup it does runs before
        /// the throw, so we swallow the exception. Because the original threw, the normal
        /// FolderRowManager.Rebuild POSTFIX is skipped — so we rebuild here instead.
        /// </summary>
        private static Exception Finalizer(SongSelect __instance, Exception __exception)
        {
            if (__exception == null) return null; // success path: the postfix handled the rebuild

            MelonLogger.Log("[VList] Swallowed ShowSongList exception: " + __exception.Message);
            try { FolderRowManager.Rebuild(__instance); }
            catch (Exception e) { MelonLogger.Log("[VList] post-exception rebuild failed: " + e); }
            return null; // suppress
        }
    }
}