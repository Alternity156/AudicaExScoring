using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TMPro;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// Read-only lobby panel to the player's left: a mirror of the in-song ScoreKeeperDisplay
    /// (world 0.549, 1.03, 1.33, rotation 37.74/0/0). ScoreKeeperDisplay only exists during
    /// gameplay, so this is our own object: a dark translucent quad with TextMeshPro text on top,
    /// parentless and DontDestroyOnLoad so it survives the menu/gameplay scene change.
    ///
    /// Shown whenever the player is in a lobby (menus and songs). Lists every player with where
    /// they are in the menus, ready, song status and difficulty; during a match, the live scores
    /// sorted by the player's own scoring mode; after a match, the last results until the next.
    ///
    /// When the lobby allows player difficulty, a column of difficulty toggles sits to the left of
    /// the panel (setDifficulty). The pick is remembered and re-applied to every newly selected
    /// song (or the closest difficulty that song has).
    ///
    /// Rebuilt from MpSession's state at most 4 times a second, and only pushed to the text when
    /// it changed. All layout values are constants below so they can be tuned.
    /// </summary>
    internal static class LobbyPanel
    {
        private static readonly Vector3 Position = new Vector3(-0.549f, 1.03f, 1.33f);
        private static readonly Vector3 Rotation = new Vector3(37.74f, 0f, 0f);

        private const float Width = 0.62f;          // text area width (m)
        private const float Padding = 0.02f;        // background margin around the text (m)
        private const float FontSize = 0.3f;        // TextMeshPro world font size
        private const float LineHeight = 0.036f;    // estimated height per line for the background (m)
        // Almost invisible: targets can spawn behind the panel, so it has to stay see-through.
        // Readability comes from the text's outline instead. Alpha 0 removes the background.
        private static readonly Color BackgroundColor = new Color(0f, 0f, 0f, 0.12f);
        private const float OutlineWidth = 0.2f;
        private static readonly Color OutlineColor = Color.black;

        // A native label whose font and material render well in this game; the text copies them.
        private const string TemplateTextPath = "menu/ShellPage_Main/page/ShellPanel_Left";
        // Background always drawn before the text, so it can never end up over it.
        private const int BackgroundRenderQueue = 2990;
        private const int TextRenderQueue = 3010;

        private const long RefreshIntervalMs = 250;

        // Ready toggle: an options-menu toggle row (label + checkbox square, like the Options
        // toggles), made by the song-page options clone's OptionsMenu and moved onto the panel,
        // just under its bottom edge. Menus only, outside matches. Position is relative to the
        // panel's bottom center (the panel grows upward from there).
        // Position tuned in game at scale 0.1; may need re-tuning at 0.05.
        private static readonly Vector3 ReadyButtonPosition = new Vector3(-0.125f, -0.044f, -0.005f);
        private static readonly Vector3 PanelButtonScale = new Vector3(0.05f, 0.05f, 0.05f);

        // Host-only Start Song button: a plain options-menu button (no checkbox), same scale, under
        // the Ready toggle. Starts the lobby's selected song (startMatch, with the host's Force
        // Start setting). Greyed out until a song is selected and every connected player is ready,
        // unless Force Start is on. Position from in-game tuning (2026-10-24).
        private static readonly Vector3 StartButtonPosition = new Vector3(-0.125f, -0.125f, -0.005f);
        private const float DisabledLabelAlpha = 0.25f;

        // Difficulty toggles (only when the lobby allows player difficulty): a column of four
        // options-menu toggle rows to the left of the panel, same scale as Ready / Start, Expert on
        // top. The checkbox shows this player's effective difficulty (PlayerState.difficulty).
        // Difficulties the selected song doesn't have for this player (songStatus
        // .availableDifficulties) are greyed out. Positions are relative to the panel's bottom
        // center, like Ready / Start; same 0.081 spacing as Ready -> Start. Tuned in game
        // (2026-11-09): Beginner (bottom) at (-0.53, 0.05), the others above it.
        private const float DifficultyColumnX = -0.53f;
        private const float DifficultyTopY = 0.293f; // = Beginner's 0.05 + 3 * 0.081
        private const float DifficultySpacingY = 0.081f;
        private static readonly KataConfig.Difficulty[] DifficultyOrder =
        {
            KataConfig.Difficulty.Expert, KataConfig.Difficulty.Hard, KataConfig.Difficulty.Normal, KataConfig.Difficulty.Easy
        };
        // Easiest to hardest, for the closest-available fallback.
        private static readonly KataConfig.Difficulty[] DifficultyAscending =
        {
            KataConfig.Difficulty.Easy, KataConfig.Difficulty.Normal, KataConfig.Difficulty.Hard, KataConfig.Difficulty.Expert
        };

        private static readonly GameObject[] difficultyObjects = new GameObject[4];
        private static readonly GunButton[] difficultyButtons = new GunButton[4];
        private static readonly TextMeshPro[] difficultyLabels = new TextMeshPro[4];

        // The difficulty this player last picked on the panel (enum name), null until they pick
        // one. Kept for the whole session: each newly selected song gets it again (or the closest
        // one the song has) once the server knows which difficulties this player has for it.
        private static string preferredDifficulty;
        // songId|difficulty of the setDifficulty in flight, so it isn't re-sent every refresh.
        // Cleared once the server's state shows it (so a later reset gets corrected again).
        private static string pendingDifficultyKey;

        private static GameObject readyObject;
        private static GunButton readyButton;

        private static GameObject startObject;
        private static GunButton startButton;
        private static TextMeshPro startLabel;

        private static GameObject root;
        private static GameObject background;
        private static TextMeshPro text;
        private static string lastText;
        private static long nextRefreshMs;

        /// <summary>Once per frame from ExScoring.OnUpdate.</summary>
        public static void Tick()
        {
            PickCounter.EnsureSubscribed();

            bool show = Config.EnableOnlinePlay && MpSession.InLobby;
            if (!show)
            {
                if (root != null && root.activeSelf) root.SetActive(false);
                return;
            }

            if (!EnsureCreated()) return;
            if (!root.activeSelf)
            {
                root.SetActive(true);
                lastText = null;
            }

            long now = MpSession.LocalNowMs;
            if (now < nextRefreshMs) return;
            nextRefreshMs = now + RefreshIntervalMs;

            try
            {
                UpdateReadyButton();
            }
            catch (Exception ex)
            {
                DebugLog.Log("[LobbyPanel] ready button update failed: " + ex.Message);
            }

            try
            {
                SyncPreferredDifficulty();
                UpdateDifficultyButtons();
            }
            catch (Exception ex)
            {
                DebugLog.Log("[LobbyPanel] difficulty buttons update failed: " + ex.Message);
            }

            string content;
            try
            {
                content = BuildText(out int lines);
                if (content == lastText) return;
                lastText = content;
                text.text = content;
                ResizeBackground(lines);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[LobbyPanel] update failed: " + ex);
            }
        }

        // ── Ready toggle ──

        private static void UpdateReadyButton()
        {
            MenuState.State state = ExScoring.menuState;
            bool inSong = state == MenuState.State.Launching || state == MenuState.State.Launched;
            bool want = !inSong && MpSession.Lobby != null && MpSession.Lobby.phase == "lobby";

            if (!want)
            {
                if (readyObject != null && readyObject.activeSelf) readyObject.SetActive(false);
                if (startObject != null && startObject.activeSelf) startObject.SetActive(false);
                return;
            }

            if (!EnsureReadyButton()) return;
            if (!readyObject.activeSelf) readyObject.SetActive(true);

            // The checkbox follows MpSession's ready flag through the toggle's isChecked callback.
            // Tie the button to whichever menu page is showing, like the page's own buttons.
            ShellPage page = CurrentShellPage();
            if (page != null) readyButton.mShellPage = page;
            readyButton.SetInteractable(true);

            UpdateStartButton(page);
        }

        // ── Start Song (host) ──

        private static void UpdateStartButton(ShellPage page)
        {
            if (!MpSession.IsHost)
            {
                if (startObject != null && startObject.activeSelf) startObject.SetActive(false);
                return;
            }

            if (!EnsureStartButton()) return;
            if (!startObject.activeSelf) startObject.SetActive(true);
            if (page != null) startButton.mShellPage = page;

            bool enabled = CanStart();
            startButton.SetInteractable(enabled);
            if (startLabel != null)
            {
                float alpha = enabled ? 1f : DisabledLabelAlpha;
                if (startLabel.alpha != alpha) startLabel.alpha = alpha;
            }
        }

        /// <summary>
        /// A selected song that is also the host's song-list selection, and every connected player
        /// ready (the host counts as ready), or Force Start on.
        /// </summary>
        private static bool CanStart()
        {
            MpLobbyState lobby = MpSession.Lobby;
            if (lobby == null || lobby.phase != "lobby" || lobby.selectedSong == null) return false;
            // On the song page, the host must be on the lobby's song (LobbyLaunchButton). Anywhere
            // else it's fine: the match always plays the lobby's selected song (the last pool song
            // the host picked); matchPrepare sets the local selection to it before launching.
            if (ExScoring.menuState == MenuState.State.SongPage && !LobbyLaunchButton.HostOnLobbySong()) return false;
            if (OnlinePlayMenu.ForceStart) return true;
            if (lobby.players == null) return false;
            foreach (MpPlayerState p in lobby.players)
            {
                if (p == null || !p.connected || p.userId == lobby.hostUserId) continue;
                if (!p.ready) return false;
            }
            return true;
        }

        private static bool EnsureStartButton()
        {
            if (startObject != null && startButton != null) return true; // Unity-null: rebuilt if destroyed

            if (!OptionsMenuClone.EnsureClone() || OptionsMenuClone.Menu == null) return false;

            // A null isChecked makes a plain button, with no checkbox square.
            OptionsMenuButton button = OptionsMenuClone.Menu.AddButton(0, "Start Song",
                new Action(OnStartShot),
                (OptionsMenuButton.IsCheckedDelegate)null,
                "Starts the lobby's selected song for everyone");
            if (button == null || button.button == null)
            {
                DebugLog.Log("[LobbyPanel] couldn't make the start button");
                if (button != null) GameObject.Destroy(button.gameObject);
                return false;
            }

            startObject = button.gameObject;
            // No "(Clone)" in the name, and off the options menu, so its Wipe() never touches it.
            startObject.name = "StartSongButton";
            startObject.transform.SetParent(root.transform, false);
            startObject.transform.localPosition = StartButtonPosition;
            startObject.transform.localRotation = Quaternion.identity;
            startObject.transform.localScale = PanelButtonScale;

            startButton = button.button;
            // It must stay after a shot (a shot that fails, e.g. NOT_ALL_READY, leaves the lobby as it was).
            startButton.disableOnShot = false;
            startButton.destroyOnShot = false;
            startLabel = startObject.GetComponentInChildren<TextMeshPro>(true);
            if (button.help != null) button.help.forceDisable = true; // no hover help bubble on the panel

            DebugLog.Log("[LobbyPanel] start button created");
            return true;
        }

        private static void OnStartShot()
        {
            if (!MpSession.IsHost || !CanStart()) return;
            MpSession.StartMatch(OnlinePlayMenu.ForceStart, error =>
            {
                if (error == null) return;
                string message = OnlinePlayMenu.Describe(error);
                DebugLog.Log("[LobbyPanel] start: " + message);
                try
                {
                    KataConfig.I.CreateDebugText(message, new Vector3(0f, -1f, 5f), 5f, null, false, 0.2f);
                }
                catch
                {
                    // The popup is a nicety; the log has it.
                }
            });
            nextRefreshMs = 0;
        }

        private static bool EnsureReadyButton()
        {
            if (readyObject != null && readyButton != null) return true; // Unity-null: rebuilt if destroyed

            // The song-page options clone (DontDestroyOnLoad) makes the row; it needs the menu
            // scene the first time only.
            if (!OptionsMenuClone.EnsureClone() || OptionsMenuClone.Menu == null) return false;

            OptionsMenuButton toggle = OptionsMenuClone.Menu.AddButton(0, "Ready",
                new Action(OnReadyShot),
                new Func<bool>(() => MpSession.MyPlayer != null && MpSession.MyPlayer.ready),
                "Ready to play the lobby's selected song");
            if (toggle == null || toggle.button == null)
            {
                DebugLog.Log("[LobbyPanel] couldn't make the ready toggle");
                if (toggle != null) GameObject.Destroy(toggle.gameObject);
                return false;
            }

            readyObject = toggle.gameObject;
            // No "(Clone)" in the name, and off the options menu, so its Wipe() never touches it.
            readyObject.name = "ReadyButton";
            readyObject.transform.SetParent(root.transform, false);
            readyObject.transform.localPosition = ReadyButtonPosition;
            readyObject.transform.localRotation = Quaternion.identity;
            readyObject.transform.localScale = PanelButtonScale;

            readyButton = toggle.button;
            readyButton.SetInteractable(true);
            if (toggle.help != null) toggle.help.forceDisable = true; // no hover help bubble on the panel

            DebugLog.Log("[LobbyPanel] ready toggle created");
            return true;
        }

        private static void OnReadyShot()
        {
            bool ready = MpSession.MyPlayer != null && MpSession.MyPlayer.ready;
            MpSession.SetReady(!ready, error =>
            {
                if (error != null) DebugLog.Log("[LobbyPanel] ready: " + OnlinePlayMenu.Describe(error));
            });
            nextRefreshMs = 0;
        }

        // ── Player difficulty (setDifficulty, 19.4.1) ──

        /// <summary>
        /// Shown in the lobby phase, outside songs, when the lobby allows player difficulty and a
        /// song is selected. Each toggle is usable only for a difficulty the server lists in this
        /// player's songStatus.availableDifficulties (setDifficulty is refused otherwise).
        /// </summary>
        private static void UpdateDifficultyButtons()
        {
            MpLobbyState lobby = MpSession.Lobby;
            MenuState.State state = ExScoring.menuState;
            bool inSong = state == MenuState.State.Launching || state == MenuState.State.Launched;
            bool want = !inSong && lobby != null && lobby.phase == "lobby"
                        && lobby.allowPlayerDifficulty && lobby.selectedSong != null;

            if (!want)
            {
                SetDifficultyButtonsActive(false);
                return;
            }

            if (!EnsureDifficultyButtons()) return;
            SetDifficultyButtonsActive(true);

            ShellPage page = CurrentShellPage();
            string[] available = MyAvailableDifficulties();
            for (int i = 0; i < DifficultyOrder.Length; i++)
            {
                if (difficultyButtons[i] == null) continue;
                if (page != null) difficultyButtons[i].mShellPage = page;

                bool enabled = available != null && Array.IndexOf(available, DifficultyOrder[i].ToString()) >= 0;
                difficultyButtons[i].SetInteractable(enabled);
                if (difficultyLabels[i] != null)
                {
                    float alpha = enabled ? 1f : DisabledLabelAlpha;
                    if (difficultyLabels[i].alpha != alpha) difficultyLabels[i].alpha = alpha;
                }
            }
        }

        private static void SetDifficultyButtonsActive(bool active)
        {
            foreach (GameObject obj in difficultyObjects)
            {
                if (obj != null && obj.activeSelf != active) obj.SetActive(active);
            }
        }

        /// <summary>The difficulties the server has for this player on the selected song, or null.</summary>
        private static string[] MyAvailableDifficulties()
        {
            MpSongStatus status = MpSession.MyPlayer?.songStatus;
            if (status == null || status.status != "have") return null;
            if (status.availableDifficulties == null || status.availableDifficulties.Length == 0) return null;
            return status.availableDifficulties;
        }

        private static bool EnsureDifficultyButtons()
        {
            bool all = true;
            for (int i = 0; i < DifficultyOrder.Length; i++)
            {
                if (difficultyObjects[i] != null && difficultyButtons[i] != null) continue; // Unity-null: rebuilt if destroyed
                if (!CreateDifficultyButton(i)) all = false;
            }
            return all;
        }

        private static bool CreateDifficultyButton(int index)
        {
            if (!OptionsMenuClone.EnsureClone() || OptionsMenuClone.Menu == null) return false;

            KataConfig.Difficulty difficulty = DifficultyOrder[index];
            string name = difficulty.ToString();

            OptionsMenuButton toggle = OptionsMenuClone.Menu.AddButton(0, DifficultyDisplayName(name),
                new Action(() => OnDifficultyShot(name)),
                new Func<bool>(() => MpSession.MyPlayer != null && MpSession.MyPlayer.difficulty == name),
                "Your difficulty for the lobby's selected song");
            if (toggle == null || toggle.button == null)
            {
                DebugLog.Log($"[LobbyPanel] couldn't make the {name} difficulty toggle");
                if (toggle != null) GameObject.Destroy(toggle.gameObject);
                return false;
            }

            GameObject obj = toggle.gameObject;
            // No "(Clone)" in the name, and off the options menu, so its Wipe() never touches it.
            obj.name = "DifficultyButton_" + name;
            obj.transform.SetParent(root.transform, false);
            obj.transform.localPosition = new Vector3(DifficultyColumnX, DifficultyTopY - index * DifficultySpacingY, -0.005f);
            obj.transform.localRotation = Quaternion.identity;
            obj.transform.localScale = PanelButtonScale;

            GunButton button = toggle.button;
            button.disableOnShot = false;
            button.destroyOnShot = false;
            if (toggle.help != null) toggle.help.forceDisable = true; // no hover help bubble on the panel

            difficultyObjects[index] = obj;
            difficultyButtons[index] = button;
            difficultyLabels[index] = obj.GetComponentInChildren<TextMeshPro>(true);

            DebugLog.Log($"[LobbyPanel] {name} difficulty toggle created");
            return true;
        }

        private static void OnDifficultyShot(string difficulty)
        {
            preferredDifficulty = difficulty;
            pendingDifficultyKey = null;
            DebugLog.Log($"[LobbyPanel] difficulty picked: {difficulty}");
            SyncPreferredDifficulty();
            nextRefreshMs = 0;
        }

        /// <summary>
        /// Makes this player's effective difficulty on the server follow their pick: right after a
        /// pick, and again on every newly selected song once its songStatus (with this player's
        /// availableDifficulties) is in. A song without the picked difficulty gets the closest one
        /// it has (easier side on ties, like SongListUI.EnsureValidDifficultySelected); the pick
        /// itself is kept, so the next song that has it goes back to it.
        /// </summary>
        private static void SyncPreferredDifficulty()
        {
            MpLobbyState lobby = MpSession.Lobby;
            MpPlayerState me = MpSession.MyPlayer;
            if (lobby == null || me == null || !lobby.allowPlayerDifficulty)
            {
                pendingDifficultyKey = null;
                return;
            }
            if (preferredDifficulty == null || lobby.phase != "lobby" || lobby.selectedSong == null) return;
            if (!MpSession.IsConnected) return;

            string target = ClosestAvailable(preferredDifficulty, MyAvailableDifficulties());
            if (target == null) return; // song status not in yet (or nothing playable)

            if (me.difficulty == target)
            {
                pendingDifficultyKey = null;
                return;
            }

            string key = lobby.selectedSong.songId + "|" + target;
            if (key == pendingDifficultyKey) return; // sent; waiting for the lobbyState (or it was refused)
            pendingDifficultyKey = key;

            DebugLog.Log($"[LobbyPanel] setting difficulty {target} for {lobby.selectedSong.songId} (picked {preferredDifficulty}, was {me.difficulty ?? "none"})");
            MpSession.SetDifficulty(target, error =>
            {
                if (error != null) DebugLog.Log("[LobbyPanel] difficulty: " + OnlinePlayMenu.Describe(error));
            });
        }

        /// <summary>The wanted difficulty if available, else the closest available one (easier on ties), or null.</summary>
        private static string ClosestAvailable(string wanted, string[] available)
        {
            if (available == null || available.Length == 0) return null;
            if (Array.IndexOf(available, wanted) >= 0) return wanted;

            int wantedIndex = -1;
            for (int i = 0; i < DifficultyAscending.Length; i++)
            {
                if (DifficultyAscending[i].ToString() == wanted) wantedIndex = i;
            }
            if (wantedIndex < 0) return null;

            int best = -1;
            int bestDist = int.MaxValue;
            for (int i = 0; i < DifficultyAscending.Length; i++) // ascending + strict '<' keeps the easier side on ties
            {
                if (Array.IndexOf(available, DifficultyAscending[i].ToString()) < 0) continue;
                int dist = Mathf.Abs(i - wantedIndex);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = i;
                }
            }
            return best >= 0 ? DifficultyAscending[best].ToString() : null;
        }

        /// <summary>The game's (localized) name for a difficulty enum name; the enum name as a fallback.</summary>
        private static string DifficultyDisplayName(string difficulty)
        {
            if (string.IsNullOrEmpty(difficulty)) return "";
            try
            {
                if (Enum.TryParse(difficulty, out KataConfig.Difficulty parsed))
                {
                    string name = KataConfig.GetDifficultyName(parsed);
                    if (!string.IsNullOrEmpty(name)) return name;
                }
            }
            catch
            {
                // Fall back to the enum name.
            }
            return difficulty;
        }

        internal static ShellPage CurrentShellPage()
        {
            if (OnlinePlayScreen.IsOpen && OnlinePlayScreen.Page != null) return OnlinePlayScreen.Page;

            string path;
            switch (ExScoring.menuState)
            {
                case MenuState.State.MainPage: path = "menu/ShellPage_Main"; break;
                case MenuState.State.SongPage: path = "menu/ShellPage_Song"; break;
                case MenuState.State.SettingsPage: path = "menu/ShellPage_Settings"; break;
                default: return null;
            }
            GameObject page = GameObject.Find(path);
            return page != null ? page.GetComponent<ShellPage>() : null;
        }

        // ── Building the object ──

        private static bool EnsureCreated()
        {
            if (root != null && text != null) return true; // Unity-null: rebuilt if destroyed

            try
            {
                root = new GameObject("ExScoring_LobbyPanel");
                GameObject.DontDestroyOnLoad(root);
                root.transform.position = Position;
                root.transform.eulerAngles = Rotation;
                root.transform.localScale = Vector3.one;

                // Root origin = bottom center of the panel; the background and text grow upward,
                // so new lines push the top up and the bottom edge (and the Ready toggle) stay put.
                background = new GameObject("Background");
                background.transform.SetParent(root.transform, false);
                MeshFilter meshFilter = background.AddComponent<MeshFilter>();
                meshFilter.mesh = BuildQuadMesh();
                MeshRenderer meshRenderer = background.AddComponent<MeshRenderer>();
                meshRenderer.material = new Material(Shader.Find("Sprites/Default"));
                meshRenderer.material.color = BackgroundColor;
                meshRenderer.material.renderQueue = BackgroundRenderQueue;

                GameObject textObject = new GameObject("Text");
                textObject.transform.SetParent(root.transform, false);
                text = textObject.AddComponent<TextMeshPro>();
                text.fontSize = FontSize;
                text.alignment = TextAlignmentOptions.BottomLeft;
                text.enableWordWrapping = false;
                text.richText = true;
                text.color = Color.white;
                ApplyGameFont(text);
                // fontMaterial makes this text's own material instance, so the outline and render
                // queue never touch the game's shared font material.
                text.fontMaterial.renderQueue = TextRenderQueue;
                text.outlineWidth = OutlineWidth;
                text.outlineColor = OutlineColor;
                text.rectTransform.pivot = new Vector2(0.5f, 0f);
                text.rectTransform.sizeDelta = new Vector2(Width, 1f);
                // Text sits just in front of the background (toward the player, -Z).
                textObject.transform.localPosition = new Vector3(0f, Padding, -0.002f);

                ResizeBackground(3);
                root.SetActive(false);
                DebugLog.Log("[LobbyPanel] created");
                return true;
            }
            catch (Exception ex)
            {
                DebugLog.Log("[LobbyPanel] couldn't create the panel: " + ex);
                if (root != null) GameObject.Destroy(root);
                root = null;
                text = null;
                return false;
            }
        }

        /// <summary>
        /// The default TextMeshPro material came out dim in game. Copies the font and material of a
        /// native Main page label instead (the Main page is where the panel usually first appears);
        /// keeps the default when none is around (e.g. created mid-song after a reconnect).
        /// </summary>
        internal static void ApplyGameFont(TextMeshPro target)
        {
            try
            {
                GameObject source = GameObject.Find(TemplateTextPath);
                TextMeshPro template = source != null ? source.GetComponentInChildren<TextMeshPro>(true) : null;
                if (template == null || template.font == null)
                {
                    DebugLog.Log("[LobbyPanel] no native label to copy the font from, keeping the default");
                    return;
                }

                target.font = template.font;
                target.fontSharedMaterial = template.fontSharedMaterial;
                DebugLog.Log($"[LobbyPanel] using font {template.font.name} from {template.gameObject.name}");
            }
            catch (Exception ex)
            {
                DebugLog.Log("[LobbyPanel] couldn't copy the game font: " + ex.Message);
            }
        }

        internal static Mesh BuildQuadMesh()
        {
            Mesh mesh = new Mesh();
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f)
            };
            // Both windings, so it shows from either side.
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateNormals();
            return mesh;
        }

        private static void ResizeBackground(int lines)
        {
            if (background == null) return;
            float height = Mathf.Max(1, lines) * LineHeight + 2f * Padding;
            background.transform.localScale = new Vector3(Width + 2f * Padding, height, 1f);
            background.transform.localPosition = new Vector3(0f, height / 2f, 0f);
        }

        // ── Content ──

        private static string BuildText(out int lines)
        {
            List<string> output = new List<string>();
            MpLobbyState lobby = MpSession.Lobby;

            output.Add($"<b>Lobby {lobby.code}</b>  {Safe(lobby.name)}");
            if (MpSession.ConnectionState == MpConnectionState.Reconnecting)
            {
                output.Add("<color=#ffaa33>Connection lost, reconnecting...</color>");
            }

            // With player difficulty on, the song's own difficulty means nothing to the others:
            // each player's is on their line instead.
            string song = lobby.selectedSong == null
                ? "No song selected"
                : lobby.allowPlayerDifficulty
                    ? Safe(lobby.selectedSong.title)
                    : $"{Safe(lobby.selectedSong.title)} [{DifficultyDisplayName(lobby.selectedSong.difficulty)}]";
            string phase = lobby.phase == "loading" ? "Loading" : lobby.phase == "playing" ? "Playing" : "In lobby";
            // Spectators get a countdown to the end of the current song (MatchTimer).
            string timer = ExScoring.MatchTimer.GetSpectatorText(lobby);
            string timerText = string.IsNullOrEmpty(timer) ? "" : $"  <color=#66ccff>{timer}</color>";
            output.Add($"<color=#d8d8d8>{phase} - {song}</color>{timerText}");
            output.Add("");

            if (MpSession.CurrentMatch != null)
            {
                AddMatchLines(output, lobby);
            }
            else
            {
                if (MpSession.LastMatchResults != null) AddResultLines(output);
                AddPlayerLines(output, lobby);
            }

            lines = output.Count;
            return string.Join("\n", output);
        }

        private static void AddPlayerLines(List<string> output, MpLobbyState lobby)
        {
            int count = lobby.players != null ? lobby.players.Length : 0;
            output.Add($"<b>Players {count}/{lobby.maxPlayers}</b>");
            if (lobby.players == null) return;

            foreach (MpPlayerState p in lobby.players)
            {
                if (p == null) continue;

                StringBuilder line = new StringBuilder();
                line.Append(p.isHost ? "<color=#ffd24d>*</color> " : "  ");
                line.Append(Name(p));

                int picks = PickCounter.Get(p.userId);
                if (picks > 0) line.Append(" <color=#a0a0a0>(").Append(picks).Append(")</color>");

                if (!p.connected)
                {
                    line.Append("  <color=#ffaa33>reconnecting...</color>");
                    output.Add(line.ToString());
                    continue;
                }

                line.Append("  <color=#d8d8d8>").Append(FriendlyLocation(p.location)).Append("</color>");
                line.Append(p.ready ? "  <color=#66ff66>Ready</color>" : "  <color=#c0c0c0>Not ready</color>");

                string songStatus = SongStatusText(p.songStatus);
                if (songStatus.Length > 0) line.Append("  ").Append(songStatus);
                if (!string.IsNullOrEmpty(p.difficulty) && lobby.allowPlayerDifficulty)
                {
                    line.Append("  <color=#66ccff>").Append(DifficultyDisplayName(p.difficulty)).Append("</color>");
                }

                output.Add(line.ToString());
            }
        }

        private static void AddMatchLines(List<string> output, MpLobbyState lobby)
        {
            bool ex = Config.ExType;
            Dictionary<string, MpScoreEntry> scores = new Dictionary<string, MpScoreEntry>();
            if (MpSession.LastMatchScores != null && MpSession.LastMatchScores.scores != null)
            {
                foreach (MpScoreEntry s in MpSession.LastMatchScores.scores)
                {
                    if (s != null && s.userId != null) scores[s.userId] = s;
                }
            }

            List<MpMatchParticipant> participants = MpSession.CurrentMatch.participants != null
                ? MpSession.CurrentMatch.participants.Where(x => x != null).ToList()
                : new List<MpMatchParticipant>();

            // Sorted by the viewer's own mode; players without a score yet go last.
            participants.Sort((a, b) =>
            {
                scores.TryGetValue(a.userId, out MpScoreEntry sa);
                scores.TryGetValue(b.userId, out MpScoreEntry sb);
                double va = sa == null ? -1 : ex ? (sa.exPercent ?? -1) : (sa.classicScore ?? -1);
                double vb = sb == null ? -1 : ex ? (sb.exPercent ?? -1) : (sb.classicScore ?? -1);
                return vb.CompareTo(va);
            });

            output.Add($"<b>{(ex ? "EX" : "Score")}</b>");
            int rank = 0;
            foreach (MpMatchParticipant participant in participants)
            {
                rank++;
                MpPlayerState p = lobby.FindPlayer(participant.userId);
                string name = p != null ? Name(p) : Safe(participant.userId);
                scores.TryGetValue(participant.userId, out MpScoreEntry s);

                if (s == null)
                {
                    output.Add($"{rank}. {name}  <color=#d8d8d8>{(lobby.phase == "loading" ? "loading" : "waiting")}</color>");
                    continue;
                }

                string score = ex ? $"{s.exPercent ?? 0f:F2}%" : $"{s.classicScore ?? 0:N0}";
                string state;
                switch (s.status)
                {
                    case "finished": state = s.failed == true ? "<color=#ff6666>failed</color>" : "<color=#66ff66>finished</color>"; break;
                    case "quit": state = "<color=#ff6666>quit</color>"; break;
                    case "disconnected": state = "<color=#ffaa33>disconnected</color>"; break;
                    default: state = s.failed == true ? "<color=#ff6666>failed</color>" : ""; break;
                }

                output.Add($"{rank}. {name}  <b>{score}</b>  x{s.combo ?? 0}  {s.misses ?? 0} miss {state}".TrimEnd());
            }

            // Everyone in the lobby who isn't playing this match.
            List<string> spectators = new List<string>();
            if (lobby.players != null)
            {
                foreach (MpPlayerState p in lobby.players)
                {
                    if (p == null) continue;
                    if (participants.Any(x => x.userId == p.userId)) continue;
                    spectators.Add(Name(p));
                }
            }
            if (spectators.Count > 0)
            {
                output.Add("");
                output.Add("<color=#d8d8d8>Spectating: </color>" + string.Join(", ", spectators));
            }
        }

        private static void AddResultLines(List<string> output)
        {
            MpMatchResults results = MpSession.LastMatchResults;
            if (results.results == null || results.results.Length == 0) return;

            bool ex = Config.ExType;
            string song = results.song != null ? $"{Safe(results.song.title)} [{results.song.difficulty}]" : "";
            output.Add($"<b>Last match</b>  <color=#d8d8d8>{song}</color>");

            // The server ranks both ways; show the ranking that matches the viewer's mode.
            IEnumerable<MpMatchResultEntry> ordered = results.results.Where(r => r != null)
                .OrderBy(r => (ex ? r.exRank : r.classicRank) ?? int.MaxValue);

            foreach (MpMatchResultEntry r in ordered)
            {
                int? rank = ex ? r.exRank : r.classicRank;
                string place = rank.HasValue ? rank.Value + "." : "-";
                string score = ex ? $"{r.exPercent ?? 0f:F2}%" : $"{r.classicScore ?? 0:N0}";
                string state = r.status == "finished"
                    ? (r.failed ? "<color=#ff6666>failed</color>" : r.fullCombo ? "<color=#66ccff>FC</color>" : "")
                    : $"<color=#ff6666>{r.status}</color>";
                output.Add($"{place} {Safe(r.nickname)}  <b>{score}</b>  {r.misses ?? 0} miss {state}".TrimEnd());
            }
            output.Add("");
        }

        // ── Formatting ──

        private static string Name(MpPlayerState p)
        {
            string name = Safe(p.nickname);
            if (p.userId == MpSession.MyUserId) name = "<b>" + name + "</b>";
            if (p.bot) name += " <color=#c0c0c0>(bot)</color>";
            else if (p.guest) name += " <color=#c0c0c0>(guest)</color>";
            return name;
        }

        /// <summary>Player-chosen text can't inject TextMeshPro tags.</summary>
        private static string Safe(string s)
        {
            return "<noparse>" + (s ?? "").Replace("</noparse>", "") + "</noparse>";
        }

        private static string SongStatusText(MpSongStatus status)
        {
            if (status == null) return "";
            switch (status.status)
            {
                case "have": return "<color=#66ff66>has song</color>";
                case "missing": return "<color=#ffaa33>missing song</color>";
                case "downloading":
                    return status.progress.HasValue
                        ? $"<color=#66ccff>downloading {status.progress.Value * 100f:F0}%</color>"
                        : "<color=#66ccff>downloading</color>";
                case "unavailable": return "<color=#ff6666>can't get song</color>";
                default: return status.status ?? "";
            }
        }

        private static string FriendlyLocation(string location)
        {
            switch (location)
            {
                case null:
                case "": return "-";
                case "InSong":
                case "Launching":
                case "Launched": return "In Song";
                case "MainPage": return "Main Menu";
                case "SongPage":
                case "LaunchPage": return "Song List";
                case "SettingsPage": return "Settings";
                case "TitleScreen": return "Title Screen";
                case "CompareScoresPage": return "Comparing Scores";
            }

            // Anything else: "SomethingPage" -> "Something".
            string name = location.EndsWith("Page") ? location.Substring(0, location.Length - 4) : location;
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1])) sb.Append(' ');
                sb.Append(name[i]);
            }
            return sb.ToString();
        }

        // ── Pick counter ──

        /// <summary>
        /// Client-side count of how many times each player's suggestion got played in this lobby,
        /// shown as a grey "(n)" after their name. Counted at matchPrepare: the players whose
        /// current songChoice has the match's songId (difficulty ignored) are found, and only the
        /// one who suggested it first gets +1. "First" is the order this client saw each choice
        /// appear or change; choices already there when the lobby was joined are ordered by seat.
        /// A match is counted once (by matchId), so a catch-up matchPrepare isn't counted twice.
        /// Kept per userId for the lobby (a player who leaves and comes back keeps theirs), wiped
        /// on leaving or switching lobby. A late joiner sees nothing for earlier matches.
        /// </summary>
        private static class PickCounter
        {
            private static bool subscribed;
            private static string lobbyId;
            private static long nextSequence;
            private static string lastCountedMatchId;

            private static readonly Dictionary<string, int> counts = new Dictionary<string, int>();
            // userId -> songId of their current choice, and when this client first saw it.
            private static readonly Dictionary<string, string> choiceSongIds = new Dictionary<string, string>();
            private static readonly Dictionary<string, long> choiceSequences = new Dictionary<string, long>();

            public static void EnsureSubscribed()
            {
                if (subscribed) return;
                subscribed = true;

                MpSession.OnLobbyState += OnLobbyState;
                MpSession.OnLeftLobby += _ => Reset(null);
                MpSession.OnMatchPrepare += OnMatchPrepare;

                // Already in a lobby (subscribed after its first lobbyState): start from it now.
                if (MpSession.Lobby != null) OnLobbyState(MpSession.Lobby);
            }

            public static int Get(string userId)
            {
                if (userId == null) return 0;
                return counts.TryGetValue(userId, out int n) ? n : 0;
            }

            private static void Reset(string newLobbyId)
            {
                lobbyId = newLobbyId;
                nextSequence = 0;
                lastCountedMatchId = null;
                counts.Clear();
                choiceSongIds.Clear();
                choiceSequences.Clear();
            }

            private static void OnLobbyState(MpLobbyState lobby)
            {
                try
                {
                    if (lobby == null) return;
                    if (lobby.lobbyId != lobbyId) Reset(lobby.lobbyId);
                    if (lobby.players == null) return;

                    // Players come in seat order, so choices seen for the first time together
                    // (e.g. on joining) are ordered by seat.
                    HashSet<string> present = new HashSet<string>();
                    foreach (MpPlayerState p in lobby.players)
                    {
                        if (p == null || p.userId == null) continue;
                        present.Add(p.userId);

                        string songId = p.songChoice?.songId;
                        choiceSongIds.TryGetValue(p.userId, out string known);
                        if (songId == known) continue;

                        if (songId == null)
                        {
                            choiceSongIds.Remove(p.userId);
                            choiceSequences.Remove(p.userId);
                        }
                        else
                        {
                            choiceSongIds[p.userId] = songId;
                            choiceSequences[p.userId] = nextSequence++;
                        }
                    }

                    // Someone who left takes their choice with them (counts stay).
                    foreach (string userId in choiceSongIds.Keys.Where(id => !present.Contains(id)).ToList())
                    {
                        choiceSongIds.Remove(userId);
                        choiceSequences.Remove(userId);
                    }
                }
                catch (Exception ex)
                {
                    DebugLog.Log("[LobbyPanel] pick counter lobbyState failed: " + ex.Message);
                }
            }

            private static void OnMatchPrepare(MpMatchPrepare match)
            {
                try
                {
                    if (match == null || match.song == null || string.IsNullOrEmpty(match.song.songId)) return;
                    if (match.matchId != null && match.matchId == lastCountedMatchId) return;
                    lastCountedMatchId = match.matchId;

                    // Catch up on the latest choices first, in case lobbyState hasn't been handled yet.
                    if (MpSession.Lobby != null) OnLobbyState(MpSession.Lobby);

                    string first = null;
                    long firstSequence = long.MaxValue;
                    foreach (KeyValuePair<string, string> choice in choiceSongIds)
                    {
                        if (choice.Value != match.song.songId) continue;
                        long sequence = choiceSequences.TryGetValue(choice.Key, out long s) ? s : long.MaxValue;
                        if (first == null || sequence < firstSequence)
                        {
                            first = choice.Key;
                            firstSequence = sequence;
                        }
                    }

                    if (first == null)
                    {
                        DebugLog.Log($"[LobbyPanel] match {match.matchId}: {match.song.title} wasn't anyone's suggestion, no pick counted");
                        return;
                    }

                    counts[first] = Get(first) + 1;
                    DebugLog.Log($"[LobbyPanel] match {match.matchId}: pick counted for {first} ({counts[first]} total)");
                }
                catch (Exception ex)
                {
                    DebugLog.Log("[LobbyPanel] pick counter matchPrepare failed: " + ex.Message);
                }
            }
        }
    }
}