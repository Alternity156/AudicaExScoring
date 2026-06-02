using System;
using System.Collections.Generic;
using MelonLoader;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

namespace ExScoringMod
{
    /// <summary>
    /// The "marathon menu on the launch panel". Shooting a playlist's Marathon row blanks the
    /// launch panel's song-specific content but KEEPS the difficulty buttons + Play, and adds
    /// four marathon option toggles (Shuffle / Show Score / NoFail / Reset Health). The user picks
    /// a difficulty (handled by the game's existing difficulty buttons), then shoots Play — which
    /// is intercepted in the LaunchPanel.Play prefix to start the marathon instead of the song.
    ///
    /// Selecting a song or navigating the list cancels setup and restores the panel.
    ///
    /// NOTE: option-toggle positions/scale are tunable via the TogglePositions / ToggleScale
    /// fields at the top of the class.
    /// </summary>
    internal static class MarathonSetup
    {
        // ════════════════════════════════════════════════════════════════════════
        //  TWEAK HERE — marathon option-toggle layout on the launch panel.
        //  Positions are LOCAL to NoFailPracticeToggle, in order top -> bottom.
        // ════════════════════════════════════════════════════════════════════════
        private static readonly Vector3[] TogglePositions =
        {
            new Vector3(5f, 14.6f, 0f), // Shuffle
            new Vector3(5f, 12.2f, 0f), // Show Score
            new Vector3(5f,  9.8f, 0f), // NoFail
            new Vector3(5f,  7.4f, 0f), // Reset Health
        };
        private static readonly Vector3 ToggleScale = new Vector3(0.75f, 0.75f, 0.75f);
        // ════════════════════════════════════════════════════════════════════════

        public static bool Active { get; private set; }

        /// <summary>Id used to highlight the Marathon row as "selected" while setup is active.</summary>
        public const string MarathonActionId = "marathon";

        private static Transform center;
        private static readonly List<GameObject> hidden = new List<GameObject>();
        private static readonly List<GameObject> created = new List<GameObject>();

        // Direct children of ShellPanel_Center to keep visible during setup.
        private static readonly HashSet<string> keep = new HashSet<string>
        {
            "Glass", "PanelFrame", "play",
            "choosediff", "ChooseDiff_easy", "ChooseDiff_normal", "ChooseDiff_hard", "ChooseDiff_expert",
            "NoFailPracticeToggle", // kept active as the parent for our option toggles
        };

        // Always hidden during setup, searched recursively so nesting/timing can't leave them shown.
        private static readonly string[] alwaysHide = { "Modifiers", "modifiers", "MapperAlert" };

        // ── Lifecycle ──────────────────────────────────────────────────────────

        public static void Begin(string playlistName)
        {
            if (Active) return; // already in setup — ignore re-shots of Marathon

            var songs = PlaylistNav.ResolvePlaylistSongs(playlistName);
            if (songs == null || songs.Count == 0)
            {
                PlaylistUtil.Popup("No installed songs to play");
                return;
            }

            if (!EnsureCenter())
            {
                PlaylistUtil.Popup("Launch panel not ready");
                return;
            }

            PlaylistEndlessManager.SetQueue(songs);

            BuildOptionButtons(); // clone BEFORE hiding originals so the clones start active
            HideContent();
            Active = true;

            AddPlaylistButton.Hide();                            // no Add/Remove button during marathon
            VirtualSongList.SetSelectedAction(MarathonActionId); // move the selection highlight to Marathon

            PlaylistUtil.Popup("Pick a difficulty, then Play to start the marathon");
        }

        /// <summary>Re-assert the blank state each frame (game re-activates its own content).</summary>
        public static void Enforce()
        {
            if (!Active) return;
            HideContent();
        }

        public static void Cancel()
        {
            if (!Active) return;
            Active = false;

            for (int i = 0; i < created.Count; i++)
                if (created[i] != null) UnityEngine.Object.Destroy(created[i]);
            created.Clear();

            for (int i = 0; i < hidden.Count; i++)
                if (hidden[i] != null) hidden[i].SetActive(true);
            hidden.Clear();

            VirtualSongList.SetSelectedAction(null); // restore song selection highlight
            AddPlaylistButton.Show();                // bring back the Add/Remove button (label refreshed)
        }

        public static void CancelIfActive()
        {
            if (Active) Cancel();
        }

        /// <summary>
        /// Called from the LaunchPanel.Play prefix. If we're in marathon setup, tears down the
        /// setup UI and starts the marathon, returning true so the normal single-song Play is
        /// skipped. (The endless manager's own Play() call lands here with Active already false.)
        /// </summary>
        public static bool TryStartFromPlay()
        {
            if (!Active) return false;

            Cancel(); // restore panel objects; Active -> false so the inner Play() passes through
            PlaylistManager.state = PlaylistManager.PlaylistState.Endless;
            PlaylistEndlessManager.StartEndlessSession();
            return true;
        }

        // ── Internals ────────────────────────────────────────────────────────

        private static bool EnsureCenter()
        {
            if (center == null)
            {
                var go = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center");
                if (go != null) center = go.transform;
            }
            return center != null;
        }

        private static void HideContent()
        {
            if (!EnsureCenter()) return;

            for (int i = 0; i < center.childCount; i++)
            {
                var child = center.GetChild(i).gameObject;
                if (keep.Contains(child.name)) continue;
                if (!child.activeSelf) continue;
                child.SetActive(false);
                if (!hidden.Contains(child)) hidden.Add(child);
            }

            // Keep the NoFailPracticeToggle container (our toggles live under it) but hide its
            // original toggles so only the marathon options show.
            HideChild("NoFailPracticeToggle/PracticeToggle");
            HideChild("NoFailPracticeToggle/NoFailToggle");

            // These must never show during setup (the game re-activates them per song).
            for (int i = 0; i < alwaysHide.Length; i++)
                HideDeep(center, alwaysHide[i]);
        }

        /// <summary>Recursively hide every active object named <paramref name="name"/> under root.</summary>
        private static void HideDeep(Transform root, string name)
        {
            for (int i = 0; i < root.childCount; i++)
            {
                Transform c = root.GetChild(i);
                if (c.name == name)
                {
                    if (c.gameObject.activeSelf)
                    {
                        c.gameObject.SetActive(false);
                        if (!hidden.Contains(c.gameObject)) hidden.Add(c.gameObject);
                    }
                }
                else
                {
                    HideDeep(c, name);
                }
            }
        }

        private static void HideChild(string path)
        {
            Transform t = center.Find(path);
            if (t != null && t.gameObject.activeSelf)
            {
                t.gameObject.SetActive(false);
                if (!hidden.Contains(t.gameObject)) hidden.Add(t.gameObject);
            }
        }

        private static void BuildOptionButtons()
        {
            var practice = GameObject.Find("menu/ShellPage_Launch/page/ShellPanel_Center/NoFailPracticeToggle/PracticeToggle");
            if (practice == null) return;
            Transform parent = practice.transform.parent;

            created.Add(MakeToggle(practice, parent, "MarathonShuffle", TogglePositions[0], "Shuffle",
                () => PlaylistConfig.Shuffle, v => PlaylistConfig.Shuffle = v, nameof(PlaylistConfig.Shuffle)));
            created.Add(MakeToggle(practice, parent, "MarathonShowScore", TogglePositions[1], "Show Score",
                () => PlaylistConfig.ShowScores, v => PlaylistConfig.ShowScores = v, nameof(PlaylistConfig.ShowScores)));
            created.Add(MakeToggle(practice, parent, "MarathonNoFail", TogglePositions[2], "NoFail",
                () => PlaylistConfig.NoFail, v => PlaylistConfig.NoFail = v, nameof(PlaylistConfig.NoFail)));
            created.Add(MakeToggle(practice, parent, "MarathonResetHealth", TogglePositions[3], "Reset Health",
                () => PlaylistConfig.ResetHealth, v => PlaylistConfig.ResetHealth = v, nameof(PlaylistConfig.ResetHealth)));
        }

        private static GameObject MakeToggle(GameObject template, Transform parent, string name, Vector3 pos,
            string text, Func<bool> get, Action<bool> set, string prefKey)
        {
            GameObject go = UnityEngine.Object.Instantiate(template, parent);
            go.name = name;
            go.SetActive(true);
            go.transform.localPosition = pos;
            go.transform.localScale = ToggleScale;

            var localizer = go.GetComponentInChildren<Localizer>();
            if (localizer != null) UnityEngine.Object.Destroy(localizer);

            TextMeshPro label = go.GetComponentInChildren<TextMeshPro>();
            if (label != null) label.text = text; // plain white-ish label (no ON/OFF coloring)

            // Selection indicator — same approach as the Favorite button: shown when enabled.
            Transform existingInd = go.transform.Find("SelectedIndicator");
            if (existingInd != null) UnityEngine.Object.Destroy(existingInd.gameObject);

            GameObject indicator = null;
            if (ExScoring.difficultyIndicatorSource != null)
            {
                indicator = UnityEngine.Object.Instantiate(ExScoring.difficultyIndicatorSource, go.transform);
                indicator.name = "SelectedIndicator";
                indicator.transform.localPosition = new Vector3(0f, 0f, -0.005f);
                indicator.transform.localRotation = Quaternion.identity;
                indicator.transform.localScale = new Vector3(0.675f, 0.75f, 1f);

                var rend = indicator.GetComponent<MeshRenderer>();
                if (rend != null) rend.material.color = new Color(1f, 1f, 1f, 1f);

                indicator.SetActive(get());
            }

            GunButton gb = go.GetComponentInChildren<GunButton>();
            if (gb != null)
            {
                gb.destroyOnShot = false;
                gb.disableOnShot = false;
                gb.doMeshExplosion = false;
                gb.doParticles = false;
                gb.onHitEvent = new UnityEvent();
                GameObject ind = indicator;
                gb.onHitEvent.AddListener(new Action(() =>
                {
                    bool nv = !get();
                    set(nv);
                    MelonPrefs.SetBool(PlaylistConfig.Category, prefKey, nv);
                    if (ind != null) ind.SetActive(nv);
                }));
            }

            return go;
        }
    }
}