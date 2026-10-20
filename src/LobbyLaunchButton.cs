using System;
using TMPro;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// The launch panel's Play button while the player is in an online lobby. For the song (and
    /// difficulty) selected in the song list:
    ///  - "Select": suggests it (setSongChoice);
    ///  - "Unselect": it's your suggestion, withdraws it;
    ///  - "Select" greyed out: someone else already suggested this song (any difficulty);
    ///  - host, on a suggested song: "Play", which starts the match. Greyed out until every
    ///    connected player is ready, unless the host turned Force Start on (OnlinePlayMenu).
    /// Out of a lobby the button is put back exactly as it was.
    ///
    /// The host selecting a suggested song in the song list also makes it the lobby's song
    /// (selectSong fromChoiceOf), so the other players can get it and ready up while the host
    /// waits on Play.
    /// </summary>
    internal static class LobbyLaunchButton
    {
        private const string PlayButtonPath = "menu/ShellPage_Launch/page/ShellPanel_Center/play";
        private const float DisabledLabelAlpha = 0.25f;
        private const long RefreshIntervalMs = 200;

        private enum Mode { None, Select, Unselect, Taken, Play }

        private static GameObject playObject;
        private static GunButton playButton;
        private static TextMeshPro playLabel;
        private static string originalLabel;
        private static bool originalDisableOnShot;
        private static bool originalDestroyOnShot;
        private static bool modified;
        private static long nextRefreshMs;

        /// <summary>From ExScoring.OnUpdate.</summary>
        public static void Tick()
        {
            if (ExScoring.menuState != MenuState.State.SongPage) return;

            long now = MpSession.LocalNowMs;
            if (now < nextRefreshMs) return;
            nextRefreshMs = now + RefreshIntervalMs;

            try
            {
                if (!FindButton()) return;

                if (!MpSession.InLobby)
                {
                    if (modified) Restore();
                    return;
                }

                Mode mode = CurrentMode(out bool enabled);
                Apply(LabelFor(mode), enabled);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[LobbyLaunchButton] update failed: " + ex.Message);
            }
        }

        /// <summary>
        /// From PlayPatch.Prefix (Hooks.cs). True = handled here, skip the native Play. A match
        /// launch (MpGameplay, and AuthorableModifiers' deferred Play for it) always goes through.
        /// </summary>
        public static bool InterceptPlay()
        {
            if (!MpSession.InLobby || MpGameplay.IsLaunchingMatch) return false;

            Mode mode = CurrentMode(out bool enabled);
            if (!enabled) return true;

            switch (mode)
            {
                case Mode.Select:
                    MpSongRef song = CurrentSongRef();
                    if (song != null) MpSession.SetSongChoice(song, ReportError);
                    break;
                case Mode.Unselect:
                    MpSession.SetSongChoice(null, ReportError);
                    break;
                case Mode.Play:
                    MpSession.StartMatch(OnlinePlayMenu.ForceStart, ReportError);
                    break;
            }
            nextRefreshMs = 0;
            return true;
        }

        /// <summary>
        /// From MpGameplay.OnSongBrowsed (song list selection). The host picking a suggested song
        /// makes it the lobby's song.
        /// </summary>
        public static void OnSongSelected(SongList.SongData song)
        {
            if (song == null || !MpSession.IsConnected || !MpSession.IsHost) return;
            MpLobbyState lobby = MpSession.Lobby;
            if (lobby == null || lobby.phase != "lobby") return;

            MpPlayerState suggester = LobbySongList.FindSuggester(song.songID);
            if (suggester == null) return;
            if (lobby.selectedSong != null && lobby.selectedSong.songId == song.songID) return;

            DebugLog.Log($"[LobbyLaunchButton] host picked {song.songID}, suggested by {suggester.nickname}: selecting it for the lobby");
            MpSession.SelectSongFromChoiceOf(suggester.userId, ReportError);
        }

        // ── State ──

        private static Mode CurrentMode(out bool enabled)
        {
            enabled = false;
            MpLobbyState lobby = MpSession.Lobby;
            string songId = ExScoring.selectedSong;
            if (lobby == null || string.IsNullOrEmpty(songId)) return Mode.Select;

            MpPlayerState me = MpSession.MyPlayer;
            MpPlayerState suggester = LobbySongList.FindSuggester(songId);

            if (MpSession.IsHost && suggester != null)
            {
                enabled = lobby.phase == "lobby"
                          && lobby.selectedSong != null && lobby.selectedSong.songId == songId
                          && (OnlinePlayMenu.ForceStart || EveryoneReady(lobby));
                return Mode.Play;
            }

            if (me != null && me.songChoice != null && me.songChoice.songId == songId)
            {
                enabled = true;
                return Mode.Unselect;
            }

            if (LobbySongList.FindSuggester(songId, excludeMe: true) != null) return Mode.Taken;

            enabled = true;
            return Mode.Select;
        }

        /// <summary>Every connected player other than the host is ready (the host counts as ready).</summary>
        private static bool EveryoneReady(MpLobbyState lobby)
        {
            if (lobby.players == null) return false;
            foreach (MpPlayerState p in lobby.players)
            {
                if (p == null || !p.connected || p.userId == lobby.hostUserId) continue;
                if (!p.ready) return false;
            }
            return true;
        }

        private static string LabelFor(Mode mode)
        {
            switch (mode)
            {
                case Mode.Unselect: return "Unselect";
                case Mode.Play: return "Play";
                default: return "Select";
            }
        }

        private static MpSongRef CurrentSongRef()
        {
            SongList.SongData song = ExScoring.selectedSongData;
            if (song == null || KataConfig.I == null) return null;
            return new MpSongRef
            {
                songId = song.songID,
                difficulty = KataConfig.I.GetDifficulty().ToString(),
                title = song.title,
                artist = song.artist,
                mapper = song.author,
                maudicaId = null
            };
        }

        private static void ReportError(MpError error)
        {
            if (error == null) return;
            string text = OnlinePlayMenu.Describe(error);
            DebugLog.Log("[LobbyLaunchButton] " + text);
            try
            {
                KataConfig.I.CreateDebugText(text, new Vector3(0f, -1f, 5f), 5f, null, false, 0.2f);
            }
            catch
            {
                // The popup is a nicety; the log has it.
            }
        }

        // ── Button ──

        private static bool FindButton()
        {
            if (playObject != null && playButton != null) return true; // Unity-null: menu rebuilt

            playObject = GameObject.Find(PlayButtonPath);
            if (playObject == null) return false;

            Transform buttonT = playObject.transform.Find("Button");
            playButton = buttonT != null ? buttonT.GetComponent<GunButton>() : playObject.GetComponentInChildren<GunButton>(true);
            playLabel = playObject.GetComponentInChildren<TextMeshPro>(true);
            originalLabel = playLabel != null ? playLabel.text : null;
            if (playButton != null)
            {
                originalDisableOnShot = playButton.disableOnShot;
                originalDestroyOnShot = playButton.destroyOnShot;
            }
            modified = false;
            return playButton != null;
        }

        private static void Apply(string label, bool enabled)
        {
            modified = true;

            // Natively Play hides itself when shot, since the song launches right after. In a
            // lobby the shot is Select / Unselect / Play handled here, so the button must stay.
            playButton.disableOnShot = false;
            playButton.destroyOnShot = false;
            if (!playButton.gameObject.activeSelf) playButton.gameObject.SetActive(true);

            if (playLabel != null)
            {
                if (playLabel.text != label) playLabel.text = label;
                float alpha = enabled ? 1f : DisabledLabelAlpha;
                if (playLabel.alpha != alpha) playLabel.alpha = alpha;
            }
            playButton.SetInteractable(enabled);
        }

        private static void Restore()
        {
            modified = false;
            playButton.disableOnShot = originalDisableOnShot;
            playButton.destroyOnShot = originalDestroyOnShot;
            if (!playButton.gameObject.activeSelf) playButton.gameObject.SetActive(true);
            if (playLabel != null)
            {
                if (originalLabel != null) playLabel.text = originalLabel;
                playLabel.alpha = 1f;
            }
            playButton.SetInteractable(true);
        }
    }
}