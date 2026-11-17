using System;
using MelonLoader;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// Online play: plays a short sound when the host changes the lobby's selected song, so the
    /// other players notice even when they're not looking at the lobby panel
    /// (Config.SongChangeSound, Options > Multiplayer, on by default).
    ///
    /// Driven by MpSession.OnLobbyState (main thread, MpClient's inbound queue is drained in Tick).
    /// The last selected songId is tracked per lobbyId, and the sound plays only when:
    ///  - the state is for the same lobby as the previous one (joining a lobby is silent),
    ///  - the lobby is in the "lobby" phase,
    ///  - the new selection isn't null and is a different song (a difficulty change alone is silent),
    ///  - this player isn't the host (the host just picked it),
    ///  - the last sound was more than <see cref="MinIntervalSeconds"/> ago (a host clicking
    ///    through several songs doesn't spam it).
    /// The selection is tracked in every phase, so coming back from a match on the same song is silent.
    /// </summary>
    internal static class MpSongChangeSound
    {
        private const string SoundEvent = "event:/gameplay/overdrive_complete";
        private const float MinIntervalSeconds = 1f;

        private static bool initialized;
        private static string lastLobbyId;
        private static string lastSongId;
        private static float lastPlayedAt = -100f;

        public static void Init()
        {
            if (initialized) return;
            initialized = true;

            MpSession.OnLobbyState += OnLobbyState;
            // Not reset on a disconnect: a reconnect returns to the same lobby.
            MpSession.OnLeftLobby += _ => Reset();
        }

        private static void Reset()
        {
            lastLobbyId = null;
            lastSongId = null;
        }

        private static void OnLobbyState(MpLobbyState lobby)
        {
            if (lobby == null) return;

            string songId = lobby.selectedSong != null && !string.IsNullOrEmpty(lobby.selectedSong.songId)
                ? lobby.selectedSong.songId
                : null;

            bool sameLobby = lastLobbyId != null && lastLobbyId == lobby.lobbyId;
            bool changed = songId != null && songId != lastSongId;

            lastLobbyId = lobby.lobbyId;
            lastSongId = songId;

            if (!sameLobby || !changed) return;
            if (lobby.phase != "lobby") return;
            if (!Config.EnableOnlinePlay || !Config.SongChangeSound) return;
            if (MpSession.IsHost) return;

            float now = Time.unscaledTime;
            if (now - lastPlayedAt < MinIntervalSeconds) return;
            lastPlayedAt = now;

            try
            {
                DebugLog.Log($"[MpSongChangeSound] host selected {lobby.selectedSong}: playing {SoundEvent}");
                KataUtil.PlayFMODEvent(SoundEvent);
            }
            catch (Exception ex)
            {
                MelonLogger.Log($"[MpSongChangeSound] couldn't play {SoundEvent}: {ex.Message}");
            }
        }
    }
}