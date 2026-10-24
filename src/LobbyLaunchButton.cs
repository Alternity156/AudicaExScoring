using System;
using TMPro;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// The launch panel's Play button while the player is in an online lobby, the same for
    /// everyone, host included: always "Select", which suggests the song (and difficulty)
    /// selected in the song list (setSongChoice). A new suggestion replaces your previous one; the
    /// same song at the same difficulty again does nothing. Several players may suggest the same
    /// song (LobbySongList shows it once, with every name). Withdrawing is on the Online Play
    /// screen. Out of a lobby the button is put back exactly as it was.
    ///
    /// Host selection: the lobby's song is the last song the host selected in the song list that
    /// is in the online pool (someone's suggestion, the host's own included), at the host's
    /// difficulty. Every RefreshIntervalMs on the song page, when the host's song-list selection is
    /// a pool song and differs from the lobby's (song or difficulty), it goes out as selectSong
    /// (lobby phase only). Selecting a song outside the pool changes nothing for the others; the
    /// lobby keeps the last pool song, and the lobby panel's Start Song is greyed out until the
    /// host is back on the lobby's song (HostOnLobbySong). Becoming host doesn't send the song you
    /// happen to be on; only picking a song or difficulty afterwards does.
    /// </summary>
    internal static class LobbyLaunchButton
    {
        private const string PlayButtonPath = "menu/ShellPage_Launch/page/ShellPanel_Center/play";
        private const float DisabledLabelAlpha = 0.25f;
        private const long RefreshIntervalMs = 200;

        // Host selection: the last song|difficulty sent (or seen), for the lobby it was sent in.
        private static string lastSelectionKey;
        private static string lastSelectionLobbyId;
        private static bool wasHost;

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
                // Before the button lookup: the host's pick counts even if the launch panel's
                // button can't be found right now.
                if (MpSession.InLobby) UpdateHostSelection();
                else wasHost = false;

                if (!FindButton()) return;

                if (!MpSession.InLobby)
                {
                    if (modified) Restore();
                    return;
                }

                Apply("Select", MpSession.Lobby != null && CurrentSongRef() != null);
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

            MpSongRef song = CurrentSongRef();
            if (song == null) return true;

            // Already your suggestion at this difficulty: nothing to send.
            MpSongRef mine = MpSession.MyPlayer?.songChoice;
            if (mine != null && mine.songId == song.songId && mine.difficulty == song.difficulty) return true;

            MpSession.SetSongChoice(song, ReportError);
            nextRefreshMs = 0;
            return true;
        }

        /// <summary>
        /// From MpGameplay.OnSongBrowsed (song list selection): check the host's selection on the
        /// next tick instead of waiting for the timer.
        /// </summary>
        public static void OnSongSelected(SongList.SongData song)
        {
            nextRefreshMs = 0;
        }

        // ── Host selection ──

        /// <summary>
        /// Sends the host's song-list selection (song + difficulty) as the lobby's song when it is a
        /// pool song (someone's suggestion) and differs from the lobby's. Not during a match, and
        /// not right when becoming host.
        /// </summary>
        private static void UpdateHostSelection()
        {
            MpLobbyState lobby = MpSession.Lobby;
            bool isHost = MpSession.IsHost;
            MpSongRef current = isHost ? CurrentSongRef() : null;
            string key = current != null ? current.songId + "|" + current.difficulty : null;
            bool inPool = current != null && LobbySongList.FindSuggester(current.songId) != null;

            if (!isHost || lobby == null)
            {
                wasHost = false;
                return;
            }

            if (!wasHost || lastSelectionLobbyId != lobby.lobbyId)
            {
                // Just became host (or joined/created this lobby as host): only later picks count.
                wasHost = true;
                lastSelectionLobbyId = lobby.lobbyId;
                lastSelectionKey = key;
                return;
            }

            // Not a pool song: nothing changes for the others (Start Song stays greyed out).
            // A song that joins the pool later (someone suggests it) is picked up then.
            if (key == null || !inPool) return;
            if (!MpSession.IsConnected || lobby.phase != "lobby") return; // retried once back in the lobby

            MpSongRef selected = lobby.selectedSong;
            if (selected != null && selected.songId == current.songId && selected.difficulty == current.difficulty)
            {
                lastSelectionKey = key;
                return;
            }
            if (key == lastSelectionKey) return; // already sent; waiting for the lobbyState (or it was refused)

            lastSelectionKey = key;

            DebugLog.Log($"[LobbyLaunchButton] host picked {current.songId} [{current.difficulty}]: selecting it for the lobby");
            MpSession.SelectSong(current, ReportError);
        }

        // ── State ──

        /// <summary>
        /// True when the host's song-list selection (song + difficulty) is the lobby's selected song,
        /// so the lobby panel's Start Song can start it.
        /// </summary>
        public static bool HostOnLobbySong()
        {
            MpSongRef selected = MpSession.Lobby?.selectedSong;
            MpSongRef current = CurrentSongRef();
            return selected != null && current != null
                   && selected.songId == current.songId && selected.difficulty == current.difficulty;
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