using System.Collections;
using UnityEngine;
using MelonLoader;
using static ExScoringMod.ExScoring;

namespace ExScoringMod
{
    /// <summary>
    /// In-game account linking with a pairing code (ApiContract.md Section 16). Lets a player get
    /// their existing API key into the game without copy/paste, which doesn't work on some setups.
    ///
    /// Flow: Begin() gets a code from POST /api/mod/link/start and shows it in the Main page's
    /// release-notes text with a countdown; the player types it on audicaex.org > Account while
    /// logged in; this polls POST /api/mod/link/poll every pollInterval seconds. On approval it asks
    /// "Link to &lt;nickname&gt;?" — Accept() saves the key, Decline() throws it away. The confirm step is
    /// the guard against a stream viewer typing the visible code into their own account first.
    ///
    /// While it runs, ApiKeyHelpButtons switches to its link layout: the normal buttons hide and a pair
    /// shows "Open AudicaEX.org" / "New Code", then "Yes" / "No" at the confirm step. When the flow ends
    /// the normal layout comes back. Leaving the Main page (Hooks.cs, MenuState.SetState) or saving a
    /// key any other way cancels.
    ///
    /// SECURITY: the pollToken and the received apiKey live only in memory here. Never log them,
    /// show them, or write them anywhere except Config (the apiKey, and only on Accept).
    /// </summary>
    internal static class AccountLink
    {
        private enum LinkState
        {
            Idle,
            Starting,
            Waiting,
            Confirming
        }

        // Network errors / 5xx in a row before giving up. A single dropped poll shouldn't kill a
        // 10-minute code; a dead connection should.
        private const int MaxConsecutiveNetworkErrors = 3;

        // Shots on "Yes" are ignored for this long after the prompt appears, since it's the same
        // button as "Open AudicaEX.org" and a player could be mid-shot when the approval lands.
        private const float ConfirmInputDelay = 1f;

        // The server keeps an approved code collectable for 5 minutes past expiry. If we're still
        // polling this long after the countdown hit zero, something's wrong — stop locally.
        private const float LocalGiveUpAfterExpiry = 330f;

        private static LinkState state = LinkState.Idle;

        // Bumped whenever a link attempt starts or is cancelled, so a callback or coroutine from an
        // older attempt can tell it's stale and stop.
        private static int version = 0;

        private static string code;
        private static string pollToken;     // never log or display
        private static string pendingApiKey; // never log or display
        private static string pendingNickname;
        private static float confirmShownAt;
        private static int shownSeconds = -1; // last countdown value written, so the text only changes once a second

        /// <summary>True from the moment Begin() is called until the flow ends (success, decline, error or cancel).</summary>
        public static bool IsActive => state != LinkState.Idle;

        /// <summary>True while "Link to &lt;nickname&gt;? Yes / No" is showing.</summary>
        public static bool IsConfirming => state == LinkState.Confirming;

        /// <summary>
        /// Starts a fresh link request. If one is already running, it's cancelled first (the server-side
        /// code just expires unused).
        /// </summary>
        public static void Begin()
        {
            Cancel();

            int thisVersion = version;
            state = LinkState.Starting;
            ApiKeyHelpButtons.ShowLinkLayout(ApiKeyHelpButtons.OpenSiteLabel, ApiKeyHelpButtons.NewCodeLabel);
            AudicaExStatus.SetPanelText("Getting a link code...");
            DebugLog.Log("[ExScoring] AccountLink: starting");

            MelonCoroutines.Start(Run(thisVersion));
        }

        /// <summary>"Yes" on the confirm prompt: saves the received key and re-checks it.</summary>
        public static void Accept()
        {
            if (state != LinkState.Confirming) return;

            if (Time.realtimeSinceStartup - confirmShownAt < ConfirmInputDelay)
            {
                DebugLog.Log("[ExScoring] AccountLink: Yes ignored (prompt just appeared)");
                return;
            }

            string key = pendingApiKey;
            DebugLog.Log("[ExScoring] AccountLink: accepted, saving API key");

            // SetApiKey cancels this flow (clearing the pending key) before AudicaExStatus re-checks,
            // so the status text goes straight to "Connected to AudicaEX.org as ...".
            OptionsMenuFunctions.SetApiKey(key);
        }

        /// <summary>"No" on the confirm prompt: discards the received key, config untouched.</summary>
        public static void Decline()
        {
            if (state != LinkState.Confirming) return;

            DebugLog.Log("[ExScoring] AccountLink: declined, key discarded");
            Cancel();
            AudicaExStatus.RefreshLast(); // put the normal status text back
        }

        /// <summary>
        /// Stops any link in progress and forgets the poll token and any received key. Safe to call at
        /// any time; does nothing when idle. Does not touch the panel text — callers that want the normal
        /// status back call AudicaExStatus.RefreshLast().
        /// </summary>
        public static void Cancel()
        {
            version++;

            if (state == LinkState.Idle) return;

            DebugLog.Log("[ExScoring] AccountLink: cancelled");
            ClearSecrets();
            state = LinkState.Idle;
            ApiKeyHelpButtons.ShowNormalLayout();
        }

        private static void ClearSecrets()
        {
            code = null;
            pollToken = null;
            pendingApiKey = null;
            pendingNickname = null;
        }

        /// <summary>Ends the flow with a message left in the panel and the normal buttons (including "Link Account") back.</summary>
        private static void Fail(string message)
        {
            version++;
            ClearSecrets();
            state = LinkState.Idle;
            ApiKeyHelpButtons.ShowNormalLayout();
            AudicaExStatus.SetPanelText(message);
        }

        private static IEnumerator Run(int thisVersion)
        {
            // ── Get a code ──────────────────────────────────────────────────────
            LinkStartResult startResult = null;
            ExScoring.StartAccountLink(r => startResult = r);
            while (startResult == null)
            {
                if (thisVersion != version) yield break;
                yield return null;
            }
            if (thisVersion != version) yield break;

            if (startResult.status == LinkStartStatus.RateLimited)
            {
                Fail("Too many link attempts.\nTry again in a few minutes.");
                yield break;
            }
            if (startResult.status != LinkStartStatus.Success)
            {
                Fail("Linking failed, try again.");
                yield break;
            }

            code = startResult.response.code;
            pollToken = startResult.response.pollToken;
            float pollInterval = Mathf.Max(1f, startResult.response.pollInterval);
            float expiresAt = Time.realtimeSinceStartup + Mathf.Max(0, startResult.response.expiresIn);

            state = LinkState.Waiting;
            shownSeconds = -1;
            UpdateWaitingText(expiresAt);

            // ── Poll ────────────────────────────────────────────────────────────
            float nextPollAt = Time.realtimeSinceStartup + pollInterval;
            int networkErrors = 0;

            while (true)
            {
                if (thisVersion != version) yield break;

                UpdateWaitingText(expiresAt);

                if (Time.realtimeSinceStartup > expiresAt + LocalGiveUpAfterExpiry)
                {
                    Fail("Code expired.\nShoot Link Account to get a new one.");
                    yield break;
                }

                if (Time.realtimeSinceStartup < nextPollAt)
                {
                    yield return null;
                    continue;
                }

                LinkPollResult poll = null;
                ExScoring.PollAccountLink(pollToken, r => poll = r);
                while (poll == null)
                {
                    if (thisVersion != version) yield break;
                    UpdateWaitingText(expiresAt);
                    yield return null;
                }
                if (thisVersion != version) yield break;

                nextPollAt = Time.realtimeSinceStartup + pollInterval;

                switch (poll.status)
                {
                    case LinkPollStatus.Pending:
                        networkErrors = 0;
                        // Resync the countdown with the server's clock.
                        if (poll.expiresIn.HasValue)
                            expiresAt = Time.realtimeSinceStartup + Mathf.Max(0, poll.expiresIn.Value);
                        break;

                    case LinkPollStatus.Approved:
                        pendingApiKey = poll.apiKey;
                        pendingNickname = poll.nickname;
                        pollToken = null; // used up server-side
                        code = null;
                        state = LinkState.Confirming;
                        confirmShownAt = Time.realtimeSinceStartup;
                        ApiKeyHelpButtons.ShowLinkLayout(ApiKeyHelpButtons.YesLabel, ApiKeyHelpButtons.NoLabel);
                        AudicaExStatus.SetPanelText(
                            $"Link to <b><noparse>{pendingNickname}</noparse></b>?\n" +
                            "Only say Yes if this is your account.");
                        DebugLog.Log("[ExScoring] AccountLink: approved, waiting for confirmation");
                        yield break;

                    case LinkPollStatus.Expired:
                        Fail("Code expired.\nShoot Link Account to get a new one.");
                        yield break;

                    case LinkPollStatus.AccountDisabled:
                        Fail("This account has been disabled.");
                        yield break;

                    case LinkPollStatus.NetworkError:
                        networkErrors++;
                        if (networkErrors >= MaxConsecutiveNetworkErrors)
                        {
                            Fail("Linking failed, try again.");
                            yield break;
                        }
                        break;

                    case LinkPollStatus.Failed:
                    default:
                        Fail("Linking failed, try again.");
                        yield break;
                }
            }
        }

        /// <summary>Rewrites the code/countdown text, but only when the displayed second changes.</summary>
        private static void UpdateWaitingText(float expiresAt)
        {
            int remaining = Mathf.Max(0, Mathf.CeilToInt(expiresAt - Time.realtimeSinceStartup));
            if (remaining == shownSeconds) return;
            shownSeconds = remaining;

            AudicaExStatus.SetPanelText(
                $"<size=180%><b>{code}</b></size>\n" +
                "Go to audicaex.org > Account\nand enter this code\n" +
                $"Expires in {remaining / 60}:{remaining % 60:00}");
        }
    }
}