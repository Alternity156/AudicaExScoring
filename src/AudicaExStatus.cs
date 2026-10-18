using MelonLoader;
using TMPro;
using static ExScoringMod.ExScoring;

namespace ExScoringMod
{
    /// <summary>
    /// Drives the AudicaEX.org connection-status text shown in the Main page's release-notes panel
    /// (see ReleaseNotesPanel.cs and ReleaseNotesOnEnablePatch in Hooks.cs). Checks Config.ApiKey
    /// against GET /api/users/me (ApiContract.md Section 7.1, via ApiClient.FetchProfile) every time
    /// the panel is (re)enabled — i.e. every main-menu visit, since ReleaseNotesPanel.Setup() and
    /// this panel's OnEnable both fire from MenuState.SetState's MenuState.State.MainPage handling —
    /// and whenever the key changes (paste, account link, delete).
    /// Also owns the show/hide state of ApiKeyHelpButtons (the "open site" / "paste key" / "link
    /// account" buttons), since this is the only place that resolves whether the key is missing,
    /// invalid, valid, or the server is simply unreachable. The last resolved state is kept in
    /// <see cref="State"/> so the Options > Leaderboard page can show Paste vs Delete.
    /// While an account link is in progress (AccountLink.cs), that flow owns the panel text and
    /// this class leaves the text and buttons alone.
    /// </summary>
    public static class AudicaExStatus
    {
        private const string NotConnectedText = "Not currently connected to AudicaEX.org";
        private const string InvalidKeyText = "Invalid AudicaEX.org API key";
        private const string ConnectionErrorText = "Could not connect to AudicaEX.org";

        public enum KeyState
        {
            /// <summary>No check has run yet this session.</summary>
            Unknown,
            /// <summary>No key configured.</summary>
            Missing,
            /// <summary>A key is set and a check is in flight.</summary>
            Checking,
            /// <summary>The server accepted the key.</summary>
            Valid,
            /// <summary>The server rejected the key (401).</summary>
            Invalid,
            /// <summary>A key is set but the server couldn't be reached.</summary>
            ConnectionError
        }

        /// <summary>Result of the most recent key check.</summary>
        public static KeyState State { get; private set; } = KeyState.Unknown;

        /// <summary>True only when the last check confirmed the key with the server.</summary>
        public static bool IsKeyValid => State == KeyState.Valid;

        // Bumped on every check so a slow/late response from an earlier check can never overwrite a
        // newer one — same staleness-guard pattern as leaderboardRequestVersion in
        // ExLeaderboardDisplay.cs / leaderboardStatsRequestVersion in LeaderboardStatsButton.cs.
        private static int requestVersion = 0;

        // Cached so a re-check can be triggered (paste, link, delete) without needing access to the
        // Harmony patch's __instance.text, and so AccountLink can write the pairing code into it.
        private static TextMeshProUGUI lastText;

        // Set when a new key has just been entered (paste or account link). If the next check
        // confirms the key, Enable Score Upload is switched on unless the player already set it
        // themselves (see OptionsMenuFunctions.AutoEnableScoreUploadIfUnset). Cleared by every
        // check result, so it only ever applies to the key that was just entered.
        private static bool autoEnableUploadOnSuccess;

        /// <summary>
        /// No key set: writes the final text synchronously, no request made. Key set: kicks off the
        /// async profile check and leaves the text as whatever the panel already shows (last visit's
        /// status, or the ReleaseNotes wrapper's own default on the very first ever OnEnable) until
        /// the response lands, then writes the resolved state. Safe to call every time the panel is
        /// enabled — a fresh call always wins over an in-flight older one.
        /// </summary>
        public static void Refresh(TextMeshProUGUI text)
        {
            if (text == null) return;

            lastText = text;
            Check();
        }

        /// <summary>
        /// Re-runs the key check and updates the last status text seen (if any). Works from anywhere,
        /// including the Options page on the song screen, where the Main page's text isn't showing —
        /// the cached state and the Options Paste/Delete button still update.
        /// </summary>
        public static void RefreshLast()
        {
            Check();
        }

        /// <summary>
        /// Called right after a new key is saved (paste or account link). Re-checks the key and, if the
        /// server accepts it, applies the Enable Score Upload auto-enable rule.
        /// </summary>
        public static void OnNewKeyEntered()
        {
            autoEnableUploadOnSuccess = true;
            Check();
        }

        /// <summary>
        /// Writes directly into the release-notes status text. Used by AccountLink to show the pairing
        /// code, countdown and confirm prompt. No-op if the panel hasn't been seen yet.
        /// </summary>
        public static void SetPanelText(string value)
        {
            if (lastText != null) lastText.text = value;
        }

        private static void Check()
        {
            int thisRequest = ++requestVersion;

            if (string.IsNullOrEmpty(Config.ApiKey))
            {
                autoEnableUploadOnSuccess = false;
                SetState(KeyState.Missing);
                ApplyToPanel(NotConnectedText, true);
                return;
            }

            SetState(KeyState.Checking);

            ExScoring.FetchProfile(result =>
            {
                if (thisRequest != requestVersion)
                {
                    DebugLog.Log("[ExScoring] AudicaExStatus: stale profile fetch result discarded.");
                    return;
                }

                bool autoEnable = autoEnableUploadOnSuccess;
                autoEnableUploadOnSuccess = false;

                switch (result.status)
                {
                    case ProfileFetchStatus.Success:
                        if (autoEnable) OptionsMenuFunctions.AutoEnableScoreUploadIfUnset();
                        SetState(KeyState.Valid);
                        ApplyToPanel($"Connected to AudicaEX.org as <noparse>{result.response.nickname}</noparse>", false);
                        // Key is confirmed valid: push any settings the server doesn't have yet
                        // (ApiContract.md Section 14 — only sends if they actually differ).
                        UserSettingsSync.SyncFromProfile(result.response);
                        break;
                    case ProfileFetchStatus.InvalidKey:
                        SetState(KeyState.Invalid);
                        ApplyToPanel(InvalidKeyText, true);
                        break;
                    case ProfileFetchStatus.ConnectionError:
                    default:
                        SetState(KeyState.ConnectionError);
                        ApplyToPanel(ConnectionErrorText, false);
                        break;
                }
            });
        }

        private static void SetState(KeyState state)
        {
            if (State == state) return;
            State = state;
            // Only the final states change the Options Paste/Delete label; Checking shows Paste, same
            // as before the check, so there's no need to redraw for it.
            if (state != KeyState.Checking) GlobalOptions.RedrawIfShowing("opt_leaderboard");
        }

        private static void ApplyToPanel(string text, bool showHelpButtons)
        {
            // An account link in progress owns the panel (code, countdown, confirm prompt).
            if (AccountLink.IsActive) return;

            if (lastText != null) lastText.text = text;
            ApiKeyHelpButtons.SetVisible(showHelpButtons);
        }
    }
}