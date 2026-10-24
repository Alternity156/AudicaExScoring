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

        private static ShellPage CurrentShellPage()
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
        private static void ApplyGameFont(TextMeshPro target)
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

        private static Mesh BuildQuadMesh()
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

            string song = lobby.selectedSong != null
                ? $"{Safe(lobby.selectedSong.title)} [{lobby.selectedSong.difficulty}]"
                : "No song selected";
            string phase = lobby.phase == "loading" ? "Loading" : lobby.phase == "playing" ? "Playing" : "In lobby";
            output.Add($"<color=#d8d8d8>{phase} - {song}</color>");
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
                if (!string.IsNullOrEmpty(p.difficulty) && lobby.allowPlayerDifficulty) line.Append("  ").Append(p.difficulty);

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
    }
}