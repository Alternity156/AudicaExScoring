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
    /// this panel's OnEnable both fire from MenuState.SetState's MenuState.State.MainPage handling.
    /// Also owns the show/hide state of ApiKeyHelpButtons (the "open site" / "paste key" buttons),
    /// since this is the only place that resolves whether the key is missing, invalid, valid, or the
    /// server is simply unreachable.
    /// </summary>
    public static class AudicaExStatus
    {
        private const string NotConnectedText = "Not currently connected to AudicaEX.org";
        private const string InvalidKeyText = "Invalid AudicaEX.org API key";
        private const string ConnectionErrorText = "Could not connect to AudicaEX.org";

        // Bumped on every Refresh() call so a slow/late response from an earlier visit can never
        // overwrite a newer visit's text — same staleness-guard pattern as leaderboardRequestVersion
        // in ExLeaderboardDisplay.cs / leaderboardStatsRequestVersion in LeaderboardStatsButton.cs.
        private static int requestVersion = 0;

        // Cached so ApiKeyHelpButtons' "Paste API Key" button can trigger a re-check via
        // RefreshLast() without needing access to the Harmony patch's __instance.text.
        private static TextMeshProUGUI lastText;

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
            int thisRequest = ++requestVersion;

            if (string.IsNullOrEmpty(Config.ApiKey))
            {
                text.text = NotConnectedText;
                ApiKeyHelpButtons.SetVisible(true);
                return;
            }

            ExScoring.FetchProfile(result =>
            {
                if (thisRequest != requestVersion)
                {
                    MelonLogger.Log("[ExScoring] AudicaExStatus: stale profile fetch result discarded.");
                    return;
                }

                if (text == null) return; // panel could have been torn down while the request was in flight

                switch (result.status)
                {
                    case ProfileFetchStatus.Success:
                        text.text = $"Connected to AudicaEX.org as {result.response.nickname}";
                        ApiKeyHelpButtons.SetVisible(false);
                        break;
                    case ProfileFetchStatus.InvalidKey:
                        text.text = InvalidKeyText;
                        ApiKeyHelpButtons.SetVisible(true);
                        break;
                    case ProfileFetchStatus.ConnectionError:
                    default:
                        text.text = ConnectionErrorText;
                        ApiKeyHelpButtons.SetVisible(false);
                        break;
                }
            });
        }

        /// <summary>
        /// Re-runs Refresh against the last status text seen. Used by ApiKeyHelpButtons after
        /// pasting a new API key from the clipboard, so the panel's text and button visibility
        /// update immediately instead of waiting for the next OnEnable (leaving/re-entering Main).
        /// </summary>
        public static void RefreshLast()
        {
            if (lastText != null) Refresh(lastText);
        }
    }
}