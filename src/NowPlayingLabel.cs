using System;
using System.Collections;
using MelonLoader;
using TMPro;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// "Now Playing" label for the Random Song menu music: centered on the platform at the lobby
    /// panel's height and tilt (world 0, 1.03, 1.33, rotation 37.74/0/0; the lobby panel is the same
    /// at x = -0.549). Same look as <see cref="LobbyPanel"/>: game font, black outline, an almost
    /// invisible background sized to the text. Parentless and DontDestroyOnLoad so it survives scene
    /// changes. Driven by MenuMusic: shown while Random Song plays in the menus and no song preview
    /// is audible, hidden otherwise.
    ///
    /// Next Song button under the label: a plain options-menu button like the lobby panel's Start
    /// Song (made by the song-page options clone's OptionsMenu and moved onto the label), same scale,
    /// at the Ready toggle's offset from the lobby panel. A child of the label, so it shows and hides
    /// with it. Shooting it calls MenuMusic.NextRandomSong().
    ///
    /// Go to Song button under Next Song (same gap as the lobby panel's Ready → Start Song): opens
    /// the song list from any menu page and selects the song that's playing, the way an online match
    /// launch does (MpGameplay): close the song page's own menus (Song Settings, Options, Edit Custom
    /// Folders), or GoToSongPage() from another page, then FolderRowManager.RevealForMatch, retried
    /// while the list builds. Always the plain song (any community map selection is dropped), at the
    /// current difficulty. Greyed out in Campaign or Party, during a song-list reload, while a jump
    /// is running, and on the song list when that song is already the selected one.
    /// </summary>
    internal static class NowPlayingLabel
    {
        private static readonly Vector3 Position = new Vector3(0f, 1.03f, 1.33f);
        private static readonly Vector3 Rotation = new Vector3(37.74f, 0f, 0f);

        private const float FontSize = 0.3f;        // same as the lobby panel
        private const float Padding = 0.02f;        // background margin around the text (m)
        private const float MaxWidth = 1.2f;        // text area width (m); long titles stay on one line
        private static readonly Color BackgroundColor = new Color(0f, 0f, 0f, 0.12f);
        private const float OutlineWidth = 0.2f;
        private static readonly Color OutlineColor = Color.black;
        private const int BackgroundRenderQueue = 2990;
        private const int TextRenderQueue = 3010;

        // Relative to the label's bottom center; same height and scale as the lobby panel's Ready
        // toggle, centered (tuned in game 2026-11-08). The button's text is centered on it.
        private static readonly Vector3 NextButtonPosition = new Vector3(0f, -0.044f, -0.005f);
        private static readonly Vector3 NextButtonScale = new Vector3(0.05f, 0.05f, 0.05f);

        // One row below Next Song: the lobby panel's Ready → Start Song gap.
        private static readonly Vector3 GoToButtonPosition = new Vector3(0f, -0.125f, -0.005f);
        private const float DisabledLabelAlpha = 0.25f; // same as the lobby panel's greyed buttons

        // Go to Song timing (same values as the online match launch in MpGameplay).
        private const float SongPageTimeoutSeconds = 8f;   // GoToSongPage → song list up
        private const float AutoSelectGraceSeconds = 1f;   // let the page's own auto-select run first
        private const float SelectTimeoutSeconds = 6f;     // RevealForMatch retries
        private const float SelectRetrySeconds = 1.5f;

        private static GameObject nextObject;
        private static GunButton nextButton;

        private static GameObject goToObject;
        private static GunButton goToButton;
        private static TextMeshPro goToLabel;
        private static bool goingToSong;

        private static GameObject root;
        private static GameObject background;
        private static TextMeshPro text;
        private static string shownSongId;

        public static void Show(SongList.SongData song)
        {
            if (song == null)
            {
                Hide();
                return;
            }

            try
            {
                if (!EnsureCreated()) return;

                if (shownSongId != song.songID)
                {
                    shownSongId = song.songID;
                    text.text = BuildText(song);
                    text.ForceMeshUpdate();
                    ResizeBackground();
                }
                if (!root.activeSelf) root.SetActive(true);
                ShellPage page = LobbyPanel.CurrentShellPage();
                UpdateNextButton(page);
                UpdateGoToButton(page, song);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[NowPlaying] couldn't show the label: " + ex.Message);
            }
        }

        public static void Hide()
        {
            if (root != null && root.activeSelf) root.SetActive(false);
        }

        // ── Next Song button ──

        private static void UpdateNextButton(ShellPage page)
        {
            try
            {
                if (!EnsureNextButton()) return;
                if (!nextObject.activeSelf) nextObject.SetActive(true);
                // Tie the button to whichever menu page is showing, like the page's own buttons.
                if (page != null) nextButton.mShellPage = page;
                nextButton.SetInteractable(true);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[NowPlaying] next button update failed: " + ex.Message);
            }
        }

        private static bool EnsureNextButton()
        {
            if (nextObject != null && nextButton != null) return true; // Unity-null: rebuilt if destroyed

            OptionsMenuButton button = MakeLabelButton("Next Song", new Action(OnNextShot),
                "Skips to another random song", "NextSongButton", NextButtonPosition);
            if (button == null) return false;

            nextObject = button.gameObject;
            nextButton = button.button;
            DebugLog.Log("[NowPlaying] next song button created");
            return true;
        }

        private static void OnNextShot()
        {
            MenuMusic.NextRandomSong();
        }

        // ── Go to Song button ──

        private static void UpdateGoToButton(ShellPage page, SongList.SongData song)
        {
            try
            {
                if (!EnsureGoToButton()) return;
                if (!goToObject.activeSelf) goToObject.SetActive(true);
                if (page != null) goToButton.mShellPage = page;

                bool enabled = CanGoToSong(song);
                goToButton.SetInteractable(enabled);
                if (goToLabel != null)
                {
                    float alpha = enabled ? 1f : DisabledLabelAlpha;
                    if (goToLabel.alpha != alpha) goToLabel.alpha = alpha;
                }
            }
            catch (Exception ex)
            {
                DebugLog.Log("[NowPlaying] go to song button update failed: " + ex.Message);
            }
        }

        private static bool EnsureGoToButton()
        {
            if (goToObject != null && goToButton != null) return true; // Unity-null: rebuilt if destroyed

            OptionsMenuButton button = MakeLabelButton("Go to Song", new Action(OnGoToShot),
                "Opens the song list on this song", "GoToSongButton", GoToButtonPosition);
            if (button == null) return false;

            goToObject = button.gameObject;
            goToButton = button.button;
            goToLabel = goToObject.GetComponentInChildren<TextMeshPro>(true);
            DebugLog.Log("[NowPlaying] go to song button created");
            return true;
        }

        /// <summary>
        /// Not in Campaign or Party, not during a song-list reload (Solo is greyed out then too),
        /// not while a jump is running, and not on the song list when this song is already selected.
        /// </summary>
        private static bool CanGoToSong(SongList.SongData song)
        {
            if (song == null || string.IsNullOrEmpty(song.songID)) return false;
            if (goingToSong) return false;
            if (MenuState.IsInCampaign() || MenuState.IsInParty()) return false;
            if (SongLoadingManager.ButtonsDisabled) return false;
            if (OnSongPage() && IsSelected(song.songID)) return false;
            return true;
        }

        private static void OnGoToShot()
        {
            SongList.SongData song = MenuMusic.MenuTrackSong;
            if (!CanGoToSong(song)) return;
            MelonCoroutines.Start(GoToSong(song));
        }

        /// <summary>The song list is the page showing (the mod's menuState can read LaunchPage on a select).</summary>
        private static bool OnSongPage()
        {
            MenuState.State state = MenuState.GetState();
            return state == MenuState.State.SongPage || state == MenuState.State.LaunchPage;
        }

        /// <summary>
        /// The game itself has this song selected (native OnSelect sets SongDataHolder; the mod's
        /// OnSelect postfix sets selectedSong), as the plain song rather than a community map row.
        /// </summary>
        private static bool IsSelected(string songID)
        {
            if (ExScoring.selectedSong != songID) return false;
            if (CommunityMapManager.IsActive) return false;
            SongDataHolder holder = SongDataHolder.I;
            return holder != null && holder.songData != null && holder.songData.songID == songID;
        }

        private static IEnumerator GoToSong(SongList.SongData song)
        {
            goingToSong = true;
            string songID = song.songID;
            DebugLog.Log($"[NowPlaying] go to song {songID} ({song.artist} - {song.title}) from {MenuState.GetState()}");

            try
            {
                // The plain song, not a map row of it (before anything selects: it restores the old difficulty).
                try { CommunityMapManager.Deselect(); }
                catch (Exception ex) { DebugLog.Log("[NowPlaying] go to song: releasing the community map failed: " + ex.Message); }

                bool navigated = !OnSongPage();
                if (!navigated)
                {
                    CloseSongPageMenus();
                }
                else
                {
                    // The song page's own auto-select restores selectedSong on entry, so it
                    // usually lands on the song by itself; the loop below covers the rest.
                    ExScoring.selectedSong = songID;
                    ExScoring.selectedSongData = song;

                    bool started = false;
                    try { MenuState.I.GoToSongPage(); started = true; }
                    catch (Exception ex) { DebugLog.Log("[NowPlaying] go to song: GoToSongPage failed: " + ex.Message); }
                    if (!started) yield break;

                    float deadline = Time.unscaledTime + SongPageTimeoutSeconds;
                    while (Time.unscaledTime < deadline &&
                           !(OnSongPage() && VirtualSongList.IsActive && !ExScoring.songPageSetupQueued))
                        yield return new WaitForSecondsRealtime(0.1f);

                    if (!(OnSongPage() && VirtualSongList.IsActive))
                    {
                        DebugLog.Log($"[NowPlaying] go to song: the song list didn't come up (state {MenuState.GetState()})");
                        yield break;
                    }

                    yield return new WaitForSecondsRealtime(AutoSelectGraceSeconds);
                }

                float selectDeadline = Time.unscaledTime + SelectTimeoutSeconds;
                while (!IsSelected(songID) && Time.unscaledTime < selectDeadline)
                {
                    if (!OnSongPage())
                    {
                        DebugLog.Log("[NowPlaying] go to song: left the song list, giving up");
                        yield break;
                    }

                    bool revealed = false;
                    try
                    {
                        // Song page menus can reopen meanwhile only by the player; close them again.
                        CloseSongPageMenus();
                        revealed = FolderRowManager.RevealForMatch(songID);
                    }
                    catch (Exception ex)
                    {
                        DebugLog.Log($"[NowPlaying] go to song: selecting {songID} failed: {ex.Message}");
                    }
                    if (!revealed) DebugLog.Log($"[NowPlaying] go to song: {songID} isn't in the song list yet, retrying");

                    yield return new WaitForSecondsRealtime(SelectRetrySeconds);
                }

                if (IsSelected(songID))
                    DebugLog.Log($"[NowPlaying] go to song: {songID} selected");
                else
                    DebugLog.Log($"[NowPlaying] go to song: couldn't select {songID} (hidden, or grades still loading?)");
            }
            finally
            {
                goingToSong = false;
            }
        }

        /// <summary>
        /// Closes the song page's menus that stand in for the launch page, like CloseMenusForMatch
        /// in MpGameplay: Song Settings, the Options level, Edit Custom Folders (pending changes are
        /// kept). RevealForMatch backs out of playlist navigation and the add-to-playlist picker.
        /// </summary>
        private static void CloseSongPageMenus()
        {
            try { PerSongOptionsPage.CloseIfOpen(); }
            catch (Exception ex) { DebugLog.Log("[NowPlaying] go to song: closing Song Settings failed: " + ex.Message); }

            try
            {
                if (FolderRowManager.InGlobalOptions)
                    FolderRowManager.NavBack(); // GlobalOptions.HidePanel re-activates the launch page
            }
            catch (Exception ex) { DebugLog.Log("[NowPlaying] go to song: leaving Options failed: " + ex.Message); }

            try
            {
                if (FolderRowManager.InFolderEdit)
                    FolderEditMode.Done(); // restores the launch page
            }
            catch (Exception ex) { DebugLog.Log("[NowPlaying] go to song: leaving Edit Custom Folders failed: " + ex.Message); }
        }

        // ── Shared button setup ──

        /// <summary>
        /// A plain options-menu button (made by the options clone's OptionsMenu) moved onto the
        /// label at <paramref name="position"/>, label scale, centered text, no help bubble, kept
        /// after a shot. Null if it couldn't be made.
        /// </summary>
        private static OptionsMenuButton MakeLabelButton(string label, Action onShot, string help, string objectName, Vector3 position)
        {
            if (!OptionsMenuClone.EnsureClone() || OptionsMenuClone.Menu == null) return null;

            // A null isChecked makes a plain button, with no checkbox square.
            OptionsMenuButton button = OptionsMenuClone.Menu.AddButton(0, label,
                onShot,
                (OptionsMenuButton.IsCheckedDelegate)null,
                help);
            if (button == null || button.button == null)
            {
                DebugLog.Log($"[NowPlaying] couldn't make the {label} button");
                if (button != null) GameObject.Destroy(button.gameObject);
                return null;
            }

            GameObject obj = button.gameObject;
            // No "(Clone)" in the name, and off the options menu, so its Wipe() never touches it.
            obj.name = objectName;
            obj.transform.SetParent(root.transform, false);
            obj.transform.localPosition = position;
            obj.transform.localRotation = Quaternion.identity;
            obj.transform.localScale = NextButtonScale;

            // It must stay after a shot.
            button.button.disableOnShot = false;
            button.button.destroyOnShot = false;
            if (button.help != null) button.help.forceDisable = true; // no hover help bubble

            // Options-menu buttons have left-aligned text; center it on the button.
            TextMeshPro buttonText = obj.GetComponentInChildren<TextMeshPro>(true);
            if (buttonText != null) buttonText.alignment = TextAlignmentOptions.Center;

            return button;
        }

        private static string BuildText(SongList.SongData song)
        {
            string line = $"{song.artist} - {song.title}";
            if (!string.IsNullOrEmpty(song.author))
                line += $" <size=70%>(mapped by {song.author})</size>";
            return "<size=70%><color=#cccccc>Now Playing</color></size>\n" + line;
        }

        // ── Building the object ──

        private static bool EnsureCreated()
        {
            if (root != null && text != null) return true; // Unity-null: rebuilt if destroyed

            try
            {
                root = new GameObject("ExScoring_NowPlayingLabel");
                GameObject.DontDestroyOnLoad(root);
                root.transform.position = Position;
                root.transform.eulerAngles = Rotation;
                root.transform.localScale = Vector3.one;

                // Root origin = bottom center; the background and text grow upward from it.
                background = new GameObject("Background");
                background.transform.SetParent(root.transform, false);
                MeshFilter meshFilter = background.AddComponent<MeshFilter>();
                meshFilter.mesh = LobbyPanel.BuildQuadMesh();
                MeshRenderer meshRenderer = background.AddComponent<MeshRenderer>();
                meshRenderer.material = new Material(Shader.Find("Sprites/Default"));
                meshRenderer.material.color = BackgroundColor;
                meshRenderer.material.renderQueue = BackgroundRenderQueue;

                GameObject textObject = new GameObject("Text");
                textObject.transform.SetParent(root.transform, false);
                text = textObject.AddComponent<TextMeshPro>();
                text.fontSize = FontSize;
                text.alignment = TextAlignmentOptions.Bottom;
                text.enableWordWrapping = false;
                text.richText = true;
                text.color = Color.white;
                LobbyPanel.ApplyGameFont(text);
                // fontMaterial makes this text's own material instance, so the outline and render
                // queue never touch the game's shared font material.
                text.fontMaterial.renderQueue = TextRenderQueue;
                text.outlineWidth = OutlineWidth;
                text.outlineColor = OutlineColor;
                text.rectTransform.pivot = new Vector2(0.5f, 0f);
                text.rectTransform.sizeDelta = new Vector2(MaxWidth, 1f);
                // Text sits just in front of the background (toward the player, -Z).
                textObject.transform.localPosition = new Vector3(0f, Padding, -0.002f);

                root.SetActive(false);
                shownSongId = null;
                DebugLog.Log("[NowPlaying] created");
                return true;
            }
            catch (Exception ex)
            {
                DebugLog.Log("[NowPlaying] couldn't create the label: " + ex);
                if (root != null) GameObject.Destroy(root);
                root = null;
                text = null;
                return false;
            }
        }

        private static void ResizeBackground()
        {
            if (background == null || text == null) return;
            Vector2 size = text.GetPreferredValues(text.text, MaxWidth, 1f);
            float width = Mathf.Min(size.x, MaxWidth) + 2f * Padding;
            float height = size.y + 2f * Padding;
            background.transform.localScale = new Vector3(width, height, 1f);
            background.transform.localPosition = new Vector3(0f, height / 2f, 0f);
        }
    }
}