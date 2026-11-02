using System;
using Harmony;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// Online play: in a lobby, the song the host selected plays as the preview in every menu
    /// outside the song list (Main page, Online Play screen, Settings, ...), replacing the menu
    /// music. On the song list the native preview is left alone (the song you're browsing).
    ///
    /// The game's own SongPreview does the playing. Its Update picks the song each frame (a hovered
    /// song button, otherwise SongDataHolder.I.songData when OnLaunchScreen() is true and the
    /// launch-panel Song Preview checkbox is on), fades songs in and out over 0.7 s, loops, and uses
    /// gainLaunchScreen while OnLaunchScreen() is true. AudioAmbience.Update already mutes the shell
    /// music while SongPreview is fading in, playing or fading out, so the menu music is replaced
    /// for free.
    ///
    /// While lobby mode is active, a prefix on SongPreview.Update temporarily:
    ///  - puts the lobby song in SongDataHolder.I.songData,
    ///  - turns PlayerPreferences.SongPreviewOnLaunchScreen on,
    ///  - sets gainLaunchScreen from the Lobby Preview Volume setting,
    ///  - raises <see cref="Feeding"/>, which makes the OnLaunchScreen patch in Hooks.cs return true.
    /// The postfix puts all of it back, so nothing else ever sees the swap.
    ///
    /// Lobby mode: online play on, in a lobby in the "lobby" phase with a selected song that is
    /// installed (a missing song stays silent until it's downloaded), Lobby Song Preview on, and
    /// not on the song list, the title screen, the credits or in a song (results screen included).
    /// </summary>
    internal static class MpLobbyPreview
    {
        /// <summary>True only during SongPreview.Update while it's being fed the lobby song.</summary>
        public static bool Feeding { get; private set; }

        // Native MenuState values OnLaunchScreen() treats as the launch screen (from Ghidra), plus
        // the title screen (0) and the credits (9), which have their own music.
        private const int NativeTitleScreenState = 0;
        private const int NativeLaunchStateA = 4;
        private const int NativeLaunchStateB = 5;
        private const int NativeLaunchStateC = 0x15;
        private const int NativeCreditsState = 9;

        // Swapped values, restored in the postfix.
        private static bool swapped;
        private static SongList.SongData savedSongData;
        private static bool savedPreviewPref;
        private static float savedGainLaunchScreen;

        private static bool wasActive;
        private static string lastLoggedSongId;

        /// <summary>The lobby song to play right now, or null when lobby mode isn't active.</summary>
        private static SongList.SongData GetLobbySong()
        {
            if (!Config.EnableOnlinePlay || !Config.LobbySongPreview) return null;
            if (!MpSession.InLobby) return null;

            MpLobbyState lobby = MpSession.Lobby;
            if (lobby == null || lobby.phase != "lobby") return null;
            if (lobby.selectedSong == null || string.IsNullOrEmpty(lobby.selectedSong.songId)) return null;

            if (IsSongListOrOwnMusicState()) return null;

            if (SongList.I == null) return null;
            SongList.SongData song = SongList.I.GetSong(lobby.selectedSong.songId);
            if (song == null || string.IsNullOrEmpty(song.moggSong)) return null; // not installed yet
            return song;
        }

        private static bool IsSongListOrOwnMusicState()
        {
            MenuState.State state = MenuState.GetState();
            if (state == MenuState.State.SongPage ||
                state == MenuState.State.LaunchPage ||
                state == MenuState.State.CompareScoresPage ||
                state == MenuState.State.Launching ||
                state == MenuState.State.Launched ||
                state == MenuState.State.TitleScreen)
                return true;

            int raw = (int)state;
            return raw == NativeTitleScreenState ||
                   raw == NativeLaunchStateA ||
                   raw == NativeLaunchStateB ||
                   raw == NativeLaunchStateC ||
                   raw == NativeCreditsState;
        }

        /// <summary>
        /// The preview's gain for the lobby song. The preview already goes through the Music Level,
        /// so the Lobby Preview Volume is applied relative to it: equal to Music Level = native volume.
        /// </summary>
        private static float LobbyGain(float nativeGain)
        {
            float musicLevel = GetMusicLevel();
            float lobbyLevel = Config.GetLobbySongPreviewVolume();
            if (lobbyLevel <= 0f) return 0f;
            if (musicLevel <= 0f) return nativeGain; // everything on the music bus is silent anyway
            return nativeGain * (lobbyLevel / musicLevel);
        }

        /// <summary>The game's Music Level (0..10), or 5 if it can't be read yet.</summary>
        internal static float GetMusicLevel()
        {
            try
            {
                if (PlayerPreferences.I != null && PlayerPreferences.I.MusicLevel != null)
                    return PlayerPreferences.I.MusicLevel.mVal;
            }
            catch (Exception) { }
            return 5f;
        }

        [HarmonyPatch(typeof(SongPreview), "Update")]
        private static class SongPreviewUpdatePatch
        {
            private static void Prefix(SongPreview __instance)
            {
                swapped = false;
                Feeding = false;

                SongList.SongData lobbySong;
                try
                {
                    lobbySong = GetLobbySong();
                }
                catch (Exception ex)
                {
                    DebugLog.Log($"[LobbyPreview] lobby song check failed: {ex.Message}");
                    lobbySong = null;
                }

                if (lobbySong == null)
                {
                    if (wasActive)
                    {
                        // Leaving lobby mode: let the native gain take over again right away. Native
                        // Update only ever lowers mDesiredFullGain while something plays, so lift it.
                        wasActive = false;
                        lastLoggedSongId = null;
                        __instance.mDesiredFullGain = float.MaxValue;
                        DebugLog.Log("[LobbyPreview] lobby mode off, native preview back in charge");
                    }
                    return;
                }

                SongDataHolder holder = SongDataHolder.I;
                PlayerPreferences prefs = PlayerPreferences.I;
                if (holder == null || prefs == null || prefs.SongPreviewOnLaunchScreen == null) return;

                savedSongData = holder.songData;
                savedPreviewPref = prefs.SongPreviewOnLaunchScreen.mVal;
                savedGainLaunchScreen = __instance.gainLaunchScreen;

                holder.songData = lobbySong;
                prefs.SongPreviewOnLaunchScreen.mVal = true;
                __instance.gainLaunchScreen = LobbyGain(savedGainLaunchScreen);
                // Lets a volume raised while playing apply at once (native keeps the lower of the two).
                __instance.mDesiredFullGain = float.MaxValue;

                swapped = true;
                Feeding = true;
                wasActive = true;

                if (lastLoggedSongId != lobbySong.songID)
                {
                    lastLoggedSongId = lobbySong.songID;
                    DebugLog.Log($"[LobbyPreview] playing lobby song {lobbySong.songID} (gain {__instance.gainLaunchScreen:N2})");
                }
            }

            private static void Postfix(SongPreview __instance)
            {
                Feeding = false;
                if (!swapped) return;
                swapped = false;

                try
                {
                    if (SongDataHolder.I != null) SongDataHolder.I.songData = savedSongData;
                    if (PlayerPreferences.I != null && PlayerPreferences.I.SongPreviewOnLaunchScreen != null)
                        PlayerPreferences.I.SongPreviewOnLaunchScreen.mVal = savedPreviewPref;
                    __instance.gainLaunchScreen = savedGainLaunchScreen;
                }
                catch (Exception ex)
                {
                    DebugLog.Log($"[LobbyPreview] restore failed: {ex.Message}");
                }
                savedSongData = null;
            }
        }
    }
}