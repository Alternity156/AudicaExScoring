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
    /// Also fetches the message of the day (GET /api/motd, ApiContract Section 20) on every Main page
    /// visit and draws it under the connection line, with its optional link button (MotdButton.cs).
    /// The MOTD and its button only show while the API key is confirmed valid (State == Valid): not
    /// with a missing or invalid key, not when the server couldn't be reached, and not while an
    /// account link runs.
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

        // ── Message of the day (ApiContract Section 20.6) ──

        // The line last written above the MOTD (connection status, or an account link's closing
        // message). Kept so the panel can be redrawn when the MOTD arrives after the key check.
        // Null until the first status is written: until then the panel is left alone.
        private static string lastStatusLine;

        // The message to show, or null for none. Kept across fetch errors for the session.
        private static MotdData currentMotd;

        // updatedAt of the message last logged, so a change is logged once (0 = none logged yet).
        private static long loggedMotdUpdatedAt;

        // Set by a 404 (server older than v2.47): no more MOTD requests this session.
        private static bool motdNotSupported;

        // Same staleness guard as requestVersion, for the MOTD fetch.
        private static int motdRequestVersion = 0;

        /// <summary>
        /// No key set: writes the final text synchronously, no request made. Key set: kicks off the
        /// async profile check and leaves the text as whatever the panel already shows (last visit's
        /// status, or the ReleaseNotes wrapper's own default on the very first ever OnEnable) until
        /// the response lands, then writes the resolved state. Safe to call every time the panel is
        /// enabled — a fresh call always wins over an in-flight older one.
        /// Also re-fetches the message of the day (this is the Main page entry point).
        /// </summary>
        public static void Refresh(TextMeshProUGUI text)
        {
            if (text == null) return;

            lastText = text;
            Check();
            FetchMotd();
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
        /// While the link is running the text is written as given and the MOTD button is hidden. A
        /// message written once the flow has ended (AccountLink.Fail) is treated as the status line.
        /// </summary>
        public static void SetPanelText(string value)
        {
            if (AccountLink.IsActive)
            {
                if (lastText != null) lastText.text = value;
                MotdButton.Hide();
                return;
            }

            lastStatusLine = value;
            DrawPanel();
        }

        /// <summary>
        /// Fetches the message of the day. A reply only changes what's shown when the server answered:
        /// errors keep the last message, and a 404 turns the feature off for the session.
        /// </summary>
        private static void FetchMotd()
        {
            if (motdNotSupported) return;

            int thisRequest = ++motdRequestVersion;

            ExScoring.FetchMotd(result =>
            {
                if (thisRequest != motdRequestVersion) return;

                switch (result.status)
                {
                    case MotdFetchStatus.Success:
                        SetMotd(result.motd);
                        break;
                    case MotdFetchStatus.NotSupported:
                        motdNotSupported = true;
                        SetMotd(null);
                        break;
                    case MotdFetchStatus.Error:
                    default:
                        // Keep the last message fetched this session (or nothing).
                        break;
                }
            });
        }

        private static void SetMotd(MotdData motd)
        {
            if (motd != null && string.IsNullOrEmpty(motd.text)) motd = null;

            long updatedAt = motd != null ? motd.updatedAt : 0;
            if (updatedAt != loggedMotdUpdatedAt)
            {
                loggedMotdUpdatedAt = updatedAt;
                DebugLog.Log(motd != null
                    ? $"[ExScoring] MOTD changed: updatedAt={updatedAt}, {motd.text.Length} chars, button={(HasButton(motd) ? "yes" : "no")}"
                    : "[ExScoring] MOTD cleared.");
            }

            currentMotd = motd;

            // An account link owns the panel.
            if (AccountLink.IsActive) return;
            // A key check is in flight (or none has finished yet): its result redraws the panel, with
            // the MOTD if the key is valid. Drawing now would blank the MOTD until then.
            if (State == KeyState.Checking || lastStatusLine == null) return;

            DrawPanel();
        }

        /// <summary>A button needs a label and an https:// link (checked again when it's shot).</summary>
        private static bool HasButton(MotdData motd)
        {
            return motd != null && motd.button != null
                && !string.IsNullOrEmpty(motd.button.label)
                && MotdButton.IsAllowedUrl(motd.button.url);
        }

        /// <summary>
        /// Writes the status line, then a blank line and the MOTD when there is one and the API key is
        /// valid, and shows or hides the MOTD button to match. Only called when no account link is running.
        /// </summary>
        private static void DrawPanel()
        {
            // The MOTD is only for players connected with a valid key.
            MotdData motd = State == KeyState.Valid ? currentMotd : null;

            if (lastText != null)
            {
                lastText.text = motd != null
                    ? lastStatusLine + "\n\n" + motd.text
                    : lastStatusLine;
            }

            if (HasButton(motd))
                MotdButton.Show(motd.button.label, motd.button.url);
            else
                MotdButton.Hide();
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

            lastStatusLine = text;
            DrawPanel();
            ApiKeyHelpButtons.SetVisible(showHelpButtons);
        }
    }
}