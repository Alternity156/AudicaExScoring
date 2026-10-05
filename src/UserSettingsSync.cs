using System;
using System.Collections;
using MelonLoader;
using UnityEngine;
using static ExScoringMod.ExScoring;

namespace ExScoringMod
{
    /// <summary>
    /// Keeps the player's server-side settings (ApiContract.md Section 14) in step with the game.
    /// Gun colors are the only setting so far. The game is the only writer — nothing is ever applied
    /// from the server back into the game.
    ///
    /// Two entry points:
    ///  - SyncFromProfile: called once the API key has been confirmed valid (AudicaExStatus.cs, i.e.
    ///    every main-menu visit and after pasting a key). Sends only if the server's copy differs.
    ///  - OnGunColorsChanged: called from ColorPrefSetPatch (Hooks.cs) when a gun color pref is set.
    ///    Debounced, and sends only if the colors differ from what the server is known to have.
    ///
    /// Nothing is ever read or sent while a song is launching/running: other mods temporarily recolor
    /// the guns mid-song, and those colors must never reach the server.
    /// </summary>
    public static class UserSettingsSync
    {
        private const float DebounceSeconds = 1f;

        // What the server is known to hold (lowercase "#rrggbb"), from the last profile fetch or the
        // last successful PATCH. Null = unknown, so the next change is always sent.
        private static string knownLeft;
        private static string knownRight;

        // Bumped on every change so only the newest pending debounce actually sends.
        private static int changeVersion = 0;

        /// <summary>
        /// True while a song (or calibration etc.) is launching or running, pause menu included.
        /// </summary>
        private static bool IsInSong()
        {
            MenuState.State state = MenuState.GetState();
            return state == MenuState.State.Launching || state == MenuState.State.Launched;
        }

        private static string ToHex(Color color)
        {
            // ToHtmlStringRGB drops alpha and clamps to 0-255, giving exactly the RRGGBB the API wants.
            return ("#" + ColorUtility.ToHtmlStringRGB(color)).ToLowerInvariant();
        }

        /// <summary>Reads the saved gun colors. Returns false if preferences aren't available yet.</summary>
        private static bool TryGetLocalGunColors(out string left, out string right)
        {
            left = null;
            right = null;

            PlayerPreferences prefs = PlayerPreferences.I;
            if (prefs == null || prefs.GunColorLeft == null || prefs.GunColorRight == null) return false;

            left = ToHex(prefs.GunColorLeft.mVal);
            right = ToHex(prefs.GunColorRight.mVal);
            return true;
        }

        private static bool SameColor(string a, string b)
        {
            return a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Compares the server's settings (from a successful GET /api/users/me) with the game's and
        /// sends an update only if they differ or the server has none yet.
        /// </summary>
        public static void SyncFromProfile(ProfileApiResponse profile)
        {
            if (profile == null) return;

            if (IsInSong())
            {
                MelonLogger.Log("[ExScoring] UserSettingsSync: in a song, skipping profile sync.");
                return;
            }

            if (!TryGetLocalGunColors(out string left, out string right))
            {
                MelonLogger.Log("[ExScoring] UserSettingsSync: PlayerPreferences not ready, skipping profile sync.");
                return;
            }

            if (profile.gunColors != null)
            {
                knownLeft = profile.gunColors.left;
                knownRight = profile.gunColors.right;
            }
            else
            {
                knownLeft = null;
                knownRight = null;
            }

            if (SameColor(left, knownLeft) && SameColor(right, knownRight))
            {
                MelonLogger.Log($"[ExScoring] UserSettingsSync: gun colors already up to date ({left}, {right}).");
                return;
            }

            MelonLogger.Log($"[ExScoring] UserSettingsSync: server has ({knownLeft ?? "none"}, {knownRight ?? "none"}), game has ({left}, {right}) - updating.");
            SendGunColors(left, right);
        }

        /// <summary>
        /// A gun color pref was just set. Ignored during a song; otherwise waits for the changes to
        /// settle (a picker fires many times while dragging) and then sends if anything really changed.
        /// </summary>
        public static void OnGunColorsChanged()
        {
            if (string.IsNullOrEmpty(Config.ApiKey)) return;
            if (IsInSong()) return;

            int thisChange = ++changeVersion;
            MelonCoroutines.Start(DebouncedSend(thisChange));
        }

        private static IEnumerator DebouncedSend(int thisChange)
        {
            float sendAt = Time.unscaledTime + DebounceSeconds;
            while (Time.unscaledTime < sendAt)
                yield return null;

            if (thisChange != changeVersion) yield break; // a newer change superseded this one

            // Re-check: a song could have started during the wait, and from then on the prefs may hold
            // another mod's temporary colors.
            if (IsInSong())
            {
                MelonLogger.Log("[ExScoring] UserSettingsSync: song started before gun colors were sent, dropping (next main-menu visit will catch up).");
                yield break;
            }

            if (string.IsNullOrEmpty(Config.ApiKey)) yield break;
            if (!TryGetLocalGunColors(out string left, out string right)) yield break;

            if (SameColor(left, knownLeft) && SameColor(right, knownRight)) yield break;

            MelonLogger.Log($"[ExScoring] UserSettingsSync: gun colors changed to ({left}, {right}) - updating.");
            SendGunColors(left, right);
        }

        private static void SendGunColors(string left, string right)
        {
            UserSettingsData settings = new UserSettingsData
            {
                gunColors = new GunColorsData { left = left, right = right }
            };

            ExScoring.UpdateUserSettings(settings, response =>
            {
                if (response == null) return; // already logged; the next main-menu visit retries

                if (response.gunColors != null)
                {
                    knownLeft = response.gunColors.left;
                    knownRight = response.gunColors.right;
                }
                else
                {
                    knownLeft = left;
                    knownRight = right;
                }
            });
        }
    }
}