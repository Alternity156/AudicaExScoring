using System;
using System.Collections;
using System.Collections.Generic;
using MelonLoader;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

namespace ExScoringMod
{
    /// <summary>
    /// Injects folder header rows into the SongSelect scroller after ShowSongList runs.
    /// Implements the StepMania-style folder expand/collapse flow:
    ///   - Default view: only folder rows are visible.
    ///   - Shooting a folder expands it (shows its songs below it) and collapses any
    ///     previously open folder.
    ///   - Open folder and scroll position persist across scene transitions and song plays.
    ///
    /// Strategy: Let the game build ALL song items normally, then after the async build
    /// completes, reparent everything to hidden storage, clear the scroller, and rebuild
    /// with folder rows + only the open folder's songs.
    /// </summary>
    internal static class FolderRowManager
    {
        // Dark grey color for folder header backgrounds
        private static readonly Color FolderRowColor = new Color(0.18f, 0.18f, 0.18f, 1f);

        // Tag prefix for folder row GameObjects
        private const string FolderRowTag = "ExScoring_FolderRow";

        // Tracks injected folder row GameObjects so we can destroy them on next rebuild
        private static List<GameObject> injectedRows = new List<GameObject>();

        // Tracks the pending rebuild coroutine so we can cancel it if another fires
        private static object pendingRebuild = null;

        // Hidden item storage — items reparented here are invisible to the scroller
        private static GameObject hiddenStorage = null;

        // ── Song list injection (called from GetSongIDs postfix) ─────────────

        /// <summary>
        /// Replaces the song ID list with ALL songs from songFolderMap, so the game
        /// builds items for every song regardless of the active built-in filter.
        /// Called from the GetSongIDs postfix. Our rebuild then groups them by folder.
        /// </summary>
        public static void InjectAllSongs(Il2CppSystem.Collections.Generic.List<string> result)
        {
            if (SongFolderManager.availableFolders == null || SongFolderManager.availableFolders.Count == 0)
                return;

            result.Clear();
            foreach (var kvp in SongFolderManager.songFolderMap)
                result.Add(kvp.Key);
        }

        // ── Scroller rebuild (called from ShowSongList postfix) ──────────────

        /// <summary>
        /// Called from the ShowSongList postfix hook.
        /// Starts a coroutine that waits for the game's async song list build to finish,
        /// then rebuilds the scroller with folder rows.
        /// </summary>
        public static void Rebuild(SongSelect songSelect)
        {
            if (songSelect == null) return;
            if (SongFolderManager.availableFolders == null || SongFolderManager.availableFolders.Count == 0) return;

            // Only run when no custom filter is active
            if (FilterPanel.IsFiltering("playlists"))
                return;

            // Cancel any pending rebuild
            if (pendingRebuild != null)
            {
                MelonCoroutines.Stop(pendingRebuild);
                pendingRebuild = null;
            }

            // Hide the scroll list while the game builds the full list, so the user
            // doesn't see the entire ungrouped list flash before folders appear.
            if (songSelect.scroller != null && songSelect.scroller.scrollParent != null)
                songSelect.scroller.scrollParent.gameObject.SetActive(false);

            pendingRebuild = MelonCoroutines.Start(RebuildCoroutine(songSelect));
        }

        /// <summary>
        /// Called when the user shoots a folder row. Toggles it open/closed,
        /// then rebuilds the scroller using the already-loaded song items
        /// (deferred one frame, so no ShowSongList async delay).
        /// </summary>
        public static void ToggleFolder(string folderName, SongSelect songSelect)
        {
            if (SongFolderManager.openFolder == folderName)
                SongFolderManager.openFolder = null;
            else
                SongFolderManager.openFolder = folderName;

            MelonLogger.Log($"[FolderRowManager] Toggled folder: {folderName} -> open={SongFolderManager.openFolder ?? "none"}");

            // Defer one frame so the current hit event finishes before we destroy
            // and rebuild the folder rows. All song items already exist, so this is fast.
            if (pendingRebuild != null)
            {
                MelonCoroutines.Stop(pendingRebuild);
                pendingRebuild = null;
            }
            pendingRebuild = MelonCoroutines.Start(ToggleRebuildCoroutine(songSelect));
        }

        private static IEnumerator ToggleRebuildCoroutine(SongSelect songSelect)
        {
            yield return null;

            try
            {
                RebuildInternal(songSelect);
            }
            catch (Exception e)
            {
                MelonLogger.Log($"[FolderRowManager] Error during toggle rebuild: {e}");
            }
            finally
            {
                pendingRebuild = null;
            }
        }

        /// <summary>
        /// Rebuilds the folder list in place using the already-loaded song items.
        /// Scroll and selection are preserved. No-ops if the song list isn't active.
        /// </summary>
        public static void RefreshList()
        {
            var songSelect = GameObject.FindObjectOfType<SongSelect>();
            if (songSelect == null) return; // song list not active — nothing to update

            try
            {
                RebuildInternal(songSelect);
            }
            catch (Exception e)
            {
                MelonLogger.Log($"[FolderRowManager] Error during RefreshList: {e}");
            }
        }

        /// <summary>
        /// Called when a song is favorited/unfavorited.
        /// </summary>
        public static void RefreshFavorites()
        {
            RefreshList();
        }

        // ── Coroutine ────────────────────────────────────────────────────────

        private static IEnumerator RebuildCoroutine(SongSelect songSelect)
        {
            // Wait until the game's async song list build finishes populating mSongButtons.
            // Poll GetSongButtons().Count until it stops changing — this directly measures
            // what we care about (the song items), unlike scrollParent.childCount which can
            // stabilize during a pause in the async batch creation.
            var scroller = songSelect.scroller;
            if (scroller == null || scroller.scrollParent == null) yield break;

            int lastCount = -1;
            int stableFrames = 0;

            yield return null;

            while (stableFrames < 4)
            {
                yield return null;
                var btns = songSelect.GetSongButtons();
                int currentCount = (btns != null) ? btns.Count : 0;
                if (currentCount == lastCount)
                    stableFrames++;
                else
                {
                    stableFrames = 0;
                    lastCount = currentCount;
                }
            }

            try
            {
                RebuildInternal(songSelect);
            }
            catch (Exception e)
            {
                MelonLogger.Log($"[FolderRowManager] Error during Rebuild: {e}");
            }
            finally
            {
                pendingRebuild = null;
                // Safety: ensure the list is visible again even if the rebuild errored
                if (scroller.scrollParent != null && !scroller.scrollParent.gameObject.activeSelf)
                    scroller.scrollParent.gameObject.SetActive(true);
            }
        }

        // ── Internal ─────────────────────────────────────────────────────────

        private static void EnsureHiddenStorage()
        {
            if (hiddenStorage == null)
            {
                hiddenStorage = new GameObject("ExScoring_HiddenStorage");
                hiddenStorage.SetActive(false);
                GameObject.DontDestroyOnLoad(hiddenStorage);
            }
        }

        private static void RebuildInternal(SongSelect songSelect)
        {
            var scroller = songSelect.scroller;
            if (scroller == null) return;

            Transform scrollParent = scroller.scrollParent;
            if (scrollParent == null) return;

            EnsureHiddenStorage();

            // Save the current scroll position so the list stays put after rebuild
            float savedScrollIndex = scroller.GetScrollIndex();

            // ── 1. Collect ALL song items grouped by folder ──────────────────
            var songButtons = songSelect.GetSongButtons();
            var folderSongs = new Dictionary<string, List<SongSelectItem>>();

            foreach (string folder in SongFolderManager.availableFolders)
                folderSongs[folder] = new List<SongSelectItem>();

            if (songButtons != null)
            {
                for (int i = 0; i < songButtons.Count; i++)
                {
                    var item = songButtons[i];
                    if (item == null || item.mSongData == null) continue;

                    string folder = SongFolderManager.GetFolder(item.mSongData.songID);
                    if (folder == null) folder = SongFolderManager.FolderCustom;

                    if (!folderSongs.ContainsKey(folder))
                        folderSongs[folder] = new List<SongSelectItem>();

                    folderSongs[folder].Add(item);

                    // Favorited songs also appear in the virtual Favorites folder
                    if (FilterPanel.IsFavorite(item.mSongData.songID))
                        folderSongs[SongFolderManager.FolderFavorites].Add(item);

                    // Search hits also appear in the virtual Search Results folder
                    if (SongFolderManager.searchFolderName != null &&
                        SongSearch.searchResult != null &&
                        folderSongs.ContainsKey(SongFolderManager.searchFolderName) &&
                        SongSearch.searchResult.Contains(item.mSongData.songID))
                    {
                        folderSongs[SongFolderManager.searchFolderName].Add(item);
                    }
                }
            }

            // ── 2. Destroy previously injected folder rows ───────────────────
            for (int i = 0; i < injectedRows.Count; i++)
            {
                if (injectedRows[i] != null)
                    GameObject.Destroy(injectedRows[i]);
            }
            injectedRows.Clear();

            // ── 3. Reparent ALL children of scrollParent to hidden storage ───
            //       This removes everything (songs, headers, etc.) from the
            //       scroller's view. We'll bring back only what we need.
            List<Transform> children = new List<Transform>();
            for (int i = 0; i < scrollParent.childCount; i++)
                children.Add(scrollParent.GetChild(i));
            foreach (var child in children)
                child.SetParent(hiddenStorage.transform, false);

            // ── 4. Clear the scroller tracking ───────────────────────────────
            bool prevDestroy = scroller.destroyChildren;
            scroller.destroyChildren = false;
            scroller.ClearRows();
            scroller.destroyChildren = prevDestroy;

            // ── 5. Build the folder list ─────────────────────────────────────
            string openFolder = SongFolderManager.openFolder;
            int currentIndex = 0;
            int openFolderIndex = -1;

            foreach (string folderName in SongFolderManager.availableFolders)
            {
                int songCount = folderSongs.ContainsKey(folderName) ? folderSongs[folderName].Count : 0;

                // Create folder header row under scrollParent
                GameObject folderRow = CreateFolderRow(songSelect, folderName, songCount, scrollParent);
                if (folderRow == null) continue;

                injectedRows.Add(folderRow);
                scroller.AddRow(folderRow);

                bool isOpen = (openFolder == folderName);
                if (isOpen)
                    openFolderIndex = currentIndex;
                currentIndex++;

                // If this folder is open, move its songs back to scrollParent
                if (isOpen && folderSongs.ContainsKey(folderName))
                {
                    var songs = folderSongs[folderName];
                    for (int i = 0; i < songs.Count; i++)
                    {
                        songs[i].transform.SetParent(scrollParent, false);
                        songs[i].gameObject.SetActive(true);
                        scroller.AddRow(songs[i].gameObject);
                        currentIndex++;
                    }
                }
            }

            // ── 6. Reveal the list and choose scroll position ────────────────
            if (!scrollParent.gameObject.activeSelf)
                scrollParent.gameObject.SetActive(true);

            scroller.UpdateScroll(-1);

            int totalRows = currentIndex;
            float displayCount = scroller.displayCount;
            float maxScroll = Mathf.Max(0f, totalRows - displayCount);

            float targetScroll;
            if (totalRows <= displayCount)
            {
                // Everything fits on screen — scroll all the way up
                targetScroll = 0f;
            }
            else if (openFolder != null && openFolderIndex >= 0)
            {
                // A folder is open. If its header is already visible, keep the
                // current position; otherwise center the folder in the view.
                bool inView = (savedScrollIndex <= openFolderIndex) &&
                              (openFolderIndex < savedScrollIndex + displayCount);
                if (inView)
                    targetScroll = Mathf.Clamp(savedScrollIndex, 0f, maxScroll);
                else
                    targetScroll = Mathf.Clamp(openFolderIndex - displayCount / 2f, 0f, maxScroll);
            }
            else
            {
                // No folder open (or just closed) — keep position, but clamp so
                // we never sit in empty space past the end of the list.
                targetScroll = Mathf.Clamp(savedScrollIndex, 0f, maxScroll);
            }

            // Auto-select the first song in the open folder if the current
            // selection isn't part of that folder.
            AutoSelectFirstIfNeeded(openFolder, folderSongs);

            scroller.SnapTo(targetScroll, true);

            MelonLogger.Log($"[FolderRowManager] Rebuilt: {injectedRows.Count} folders, scroller rows={currentIndex}, open={openFolder ?? "none"}, scroll={targetScroll}");
        }

        // ── Folder row creation ──────────────────────────────────────────────

        private static void AutoSelectFirstIfNeeded(string openFolder, Dictionary<string, List<SongSelectItem>> folderSongs)
        {
            if (openFolder == null) return;
            if (!folderSongs.ContainsKey(openFolder)) return;

            var songs = folderSongs[openFolder];
            if (songs.Count == 0) return;

            // Is the currently selected song part of this folder?
            for (int i = 0; i < songs.Count; i++)
            {
                if (songs[i].mSongData != null && songs[i].mSongData.songID == ExScoring.selectedSong)
                    return; // selection is already in the folder — leave it
            }

            // Selection isn't in the folder — point it at the folder's first song
            // and run the established auto-select flow (handles menu state and the
            // launch panel correctly, so the preview fires).
            var first = songs[0];
            if (first == null || first.mSongData == null) return;

            ExScoring.selectedSong = first.mSongData.songID;
            ExScoring.AutoSelectSong();
        }


        private static GameObject CreateFolderRow(SongSelect songSelect, string folderName, int songCount, Transform parent)
        {
            // Find a source to clone from — use an existing song button for correct sizing
            GameObject source = null;
            var buttons = songSelect.GetSongButtons();
            if (buttons != null)
            {
                for (int i = 0; i < buttons.Count; i++)
                {
                    if (buttons[i] != null && buttons[i].gameObject != null)
                    {
                        source = buttons[i].gameObject;
                        break;
                    }
                }
            }
            if (source == null && songSelect.songSelectItemPrefab != null)
                source = songSelect.songSelectItemPrefab.gameObject;
            if (source == null)
            {
                MelonLogger.Log("[FolderRowManager] No source for folder row clone");
                return null;
            }

            GameObject row = GameObject.Instantiate(source, parent);
            row.name = $"{FolderRowTag}_{folderName}";
            row.SetActive(true);

            // ── Set title and artist text ────────────────────────────────────
            var tmps = row.GetComponentsInChildren<TextMeshProUGUI>(true);
            foreach (var tmp in tmps)
            {
                if (tmp.name == "title")
                {
                    tmp.text = folderName;
                    tmp.fontStyle = FontStyles.Bold;
                }
                else if (tmp.name == "artist")
                {
                    tmp.text = $"{songCount} song{(songCount != 1 ? "s" : "")}";
                }
            }

            // ── Hide decorations (stars, glows, highlights, panel frame) ─────
            HideDecorations(row);

            // ── Hide unwanted Canvas elements (keep only title & artist) ─────
            HideCanvasElements(row);

            // ── Disable bloom/glow renderers (prevents bloom when aiming) ────
            DisableBloom(row);

            // ── Destroy SongSelectItem so its OnSelect patch can't fire ──────
            //     (folder rows have no song data, which would crash OnSelect)
            var songSelectItem = row.GetComponent<SongSelectItem>();
            if (songSelectItem != null)
                GameObject.Destroy(songSelectItem);

            // ── Style the Quad and wire the GunButton ────────────────────────
            StyleFolderQuad(row, folderName, songSelect);

            return row;
        }

        private static void HideDecorations(GameObject row)
        {
            // Hide ALL children except Quad (button/background) and Canvas (text labels).
            // Use index-based iteration — foreach over a Transform throws in IL2CPP.
            Transform t = row.transform;
            for (int i = 0; i < t.childCount; i++)
            {
                Transform child = t.GetChild(i);
                if (child.name == "Quad" || child.name == "Canvas")
                    continue;
                child.gameObject.SetActive(false);
            }
        }

        private static void HideCanvasElements(GameObject row)
        {
            // Disable everything inside the Canvas except title and artist
            string[] toHide = new string[]
            {
                "StarDisplay", "mapper", "high_score", "percent",
                "friends", "friends_icon", "global", "global_icon"
            };

            foreach (string name in toHide)
            {
                Transform target = FindChild(row.transform, name);
                if (target != null)
                    target.gameObject.SetActive(false);
            }
        }

        private static void DisableBloom(GameObject row)
        {
            // Disable any bloom/glow objects so aiming at the folder doesn't bloom.
            // (FakeBloom / FakeHighlightBloom use the DefaultGlowColor material.)
            var all = row.GetComponentsInChildren<Transform>(true);
            foreach (var t in all)
            {
                string n = t.name;
                if (n.Contains("Bloom") || n.Contains("bloom") || n.Contains("Glow") || n.Contains("glow"))
                    t.gameObject.SetActive(false);
            }
        }

        private static void StyleFolderQuad(GameObject row, string folderName, SongSelect songSelect)
        {
            Transform quadTransform = FindChild(row.transform, "Quad");
            if (quadTransform == null)
            {
                MelonLogger.Log($"[FolderRowManager] Could not find Quad for '{folderName}'");
                return;
            }

            // ── Replace material with opaque gold ────────────────────────────
            var renderer = quadTransform.GetComponent<MeshRenderer>();
            if (renderer != null)
            {
                Material mat = null;
                Shader unlitColor = Shader.Find("Unlit/Color");
                if (unlitColor != null)
                {
                    mat = new Material(unlitColor);
                    mat.color = FolderRowColor;
                }
                else
                {
                    Shader standard = Shader.Find("Standard");
                    if (standard != null)
                    {
                        mat = new Material(standard);
                        mat.color = FolderRowColor;
                    }
                }
                if (mat != null)
                    renderer.material = mat;
            }

            // ── Configure GunButton ──────────────────────────────────────────
            var gunButton = quadTransform.GetComponent<GunButton>();
            if (gunButton != null)
            {
                gunButton.destroyOnShot = false;
                gunButton.disableOnShot = false;
                gunButton.doMeshExplosion = false;
                gunButton.doParticles = false;

                // Kill the bloom/glow that fires when aiming at the button
                gunButton.fakeBloom = null;
                gunButton.highlightFakeBloom = null;

                gunButton.onHitEvent = new UnityEvent();
                string capturedFolder = folderName;
                gunButton.onHitEvent.AddListener(new Action(() =>
                {
                    ToggleFolder(capturedFolder, songSelect);
                }));
            }
            else
            {
                MelonLogger.Log($"[FolderRowManager] No GunButton on Quad for '{folderName}'");
            }
        }

        private static Transform FindChild(Transform parent, string childName)
        {
            Transform found = parent.Find(childName);
            if (found != null) return found;

            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                if (child.name == childName)
                    return child;
                found = FindChild(child, childName);
                if (found != null) return found;
            }
            return null;
        }
    }
}