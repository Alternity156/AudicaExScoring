using System;
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

        private static GameObject nextObject;
        private static GunButton nextButton;

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
                UpdateNextButton();
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

        private static void UpdateNextButton()
        {
            try
            {
                if (!EnsureNextButton()) return;
                if (!nextObject.activeSelf) nextObject.SetActive(true);
                // Tie the button to whichever menu page is showing, like the page's own buttons.
                ShellPage page = LobbyPanel.CurrentShellPage();
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

            if (!OptionsMenuClone.EnsureClone() || OptionsMenuClone.Menu == null) return false;

            // A null isChecked makes a plain button, with no checkbox square.
            OptionsMenuButton button = OptionsMenuClone.Menu.AddButton(0, "Next Song",
                new Action(OnNextShot),
                (OptionsMenuButton.IsCheckedDelegate)null,
                "Skips to another random song");
            if (button == null || button.button == null)
            {
                DebugLog.Log("[NowPlaying] couldn't make the next song button");
                if (button != null) GameObject.Destroy(button.gameObject);
                return false;
            }

            nextObject = button.gameObject;
            // No "(Clone)" in the name, and off the options menu, so its Wipe() never touches it.
            nextObject.name = "NextSongButton";
            nextObject.transform.SetParent(root.transform, false);
            nextObject.transform.localPosition = NextButtonPosition;
            nextObject.transform.localRotation = Quaternion.identity;
            nextObject.transform.localScale = NextButtonScale;

            nextButton = button.button;
            // It must stay after a shot.
            nextButton.disableOnShot = false;
            nextButton.destroyOnShot = false;
            if (button.help != null) button.help.forceDisable = true; // no hover help bubble

            // Options-menu buttons have left-aligned text; center it on the button.
            TextMeshPro label = nextObject.GetComponentInChildren<TextMeshPro>(true);
            if (label != null) label.alignment = TextAlignmentOptions.Center;

            DebugLog.Log("[NowPlaying] next song button created");
            return true;
        }

        private static void OnNextShot()
        {
            MenuMusic.NextRandomSong();
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