using System;
using System.Collections.Generic;
using Harmony;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// What happens to a watched player's targets, for the spectator window (step 3, part A).
    /// Findings: claude/Spectator-Window-Hit-Findings.md.
    ///
    /// The watched player's side (hooks below, only while someone is watching them and they allow
    /// it, outside practice mode, or while Replays records): each result is queued with the song position in ticks
    /// (AudioDriver's Audio tick, the same clock as the poses' songTick):
    /// (every result names its cue by the cue's own tick and pitch: the index the game gives a cue
    /// in a song isn't there on a chart loaded in the menus, log 2026-11-14)
    ///   hit         Target.OnHit returned true: the gun's hand, shot or melee,
    ///               and for a shot where the beam met the target (world = their play space)
    ///   miss        ScoreKeeper.OnFailure(cue, pass: true): the target went by
    ///   holdBroken  ScoreKeeper.OnFailure for a sustain whose hit was reported: let go
    ///   holdDone    Target.CompleteTarget on a sustain: held to its end
    /// FlushIfDue (from MpPresence's in-song branch and from the replay recorder) sends the queue
    /// as one spectateEvents message at most once per pose interval (FlushIntervalMs), and hands it to the recorder.
    ///
    /// The watcher's side: batches from the server (MpSession.OnSpectateEvents) or from the
    /// replay player (Replays) are kept per player until SpectatorTargets takes them (TakeFor).
    /// </summary>
    internal static class SpectatorEvents
    {
        public const string Hit = "hit";
        public const string Miss = "miss";
        public const string HoldDone = "holdDone";
        public const string HoldBroken = "holdBroken";
        /// <summary>A text popup the player saw (score, early, late): shown by SpectatorPopups, not a target result.</summary>
        public const string Popup = "popup";
        private const int MaxPopupText = 300;      // characters per line of a popup (an EX judgement text with its tags is about 150)

        // At most one batch per pose interval: the server limits spectateEvents like poses (its
        // pose rate, MpSession.PoseRateHz, 15..90 Hz, set on the server). As short as allowed on
        // purpose: the watcher has to know a hit before its window's song position (RenderDelayMs
        // = 100 ms behind) reaches the cue, for the target's sound to start on the cue's time
        // (SpectatorSounds).
        private static long FlushIntervalMs
        {
            get { return 1000 / Math.Max(15, Math.Min(90, MpSession.PoseRateHz)); }
        }
        private const int MaxQueued = 64;          // more than this between two flushes: the oldest go
        private const double RestartTicks = 480;   // the tick going back this much = the song restarted

        // ── Watched player ───────────────────────────────────────────────────
        private static readonly List<JObject> queue = new List<JObject>();
        private static readonly HashSet<string> sent = new HashSet<string>(); // "index|kind" already reported this run
        private static double lastTick = -1;
        private static long nextFlushAtMs;
        private static bool loggedError;

        // ── Watcher ──────────────────────────────────────────────────────────
        private static readonly Dictionary<string, List<MpSpectateEvent>> incoming = new Dictionary<string, List<MpSpectateEvent>>();
        private static bool initialized;

        private static void Log(string message) => DebugLog.Log("[SpectatorEvents] " + message);

        /// <summary>Someone is watching this player and they allow it (never in practice mode), or a replay is being recorded.</summary>
        private static bool Capturing
        {
            get
            {
                if (Replays.IsRecording) return true;
                return SendingToWatchers;
            }
        }

        private static bool SendingToWatchers =>
            Config.AllowSpectators && !Replays.InPracticeMode && MpSession.SpectateEnabled && MpSession.WatcherCount > 0;

        private static void Queue(SongCues.Cue cue, string kind, int hand, bool shot, bool hasPoint, Vector3 point)
        {
            try
            {
                if (cue == null || !Capturing) return;
                AudioDriver audio = AudioDriver.I;
                if (audio == null) return;
                double tick = audio.GetTick(AudioDriver.TickContext.Audio);
                if (double.IsNaN(tick) || double.IsInfinity(tick) || tick < 0) return;

                if (tick < lastTick - RestartTicks) sent.Clear(); // a restart: every cue can be reported again
                lastTick = tick;
                if (!sent.Add(cue.tick + "|" + cue.pitch + "|" + kind)) return;

                JObject e = new JObject
                {
                    ["i"] = cue.index,
                    ["ct"] = cue.tick,
                    ["cp"] = cue.pitch,
                    ["k"] = kind,
                    ["tick"] = Math.Round(tick, 1)
                };
                if (kind == Hit)
                {
                    e["hand"] = hand;
                    e["shot"] = shot;
                    if (shot && hasPoint)
                        e["p"] = new JArray(Math.Round(point.x, 3), Math.Round(point.y, 3), Math.Round(point.z, 3));
                }

                if (queue.Count >= MaxQueued) queue.RemoveAt(0);
                queue.Add(e);
            }
            catch (Exception ex)
            {
                if (loggedError) return;
                loggedError = true;
                Log("queueing a result failed (logged once): " + ex.Message);
            }
        }

        /// <summary>
        /// A popup the game just showed (after every prefix on CreatePopup, so the text is the
        /// finished one: the game's points, or this mod's EX judgement text with the player's
        /// popup settings). Sent with where the game put it.
        /// </summary>
        private static void QueuePopup(TextPopupPool pool, string extraText)
        {
            try
            {
                if (pool == null || !Capturing) return;
                AudioDriver audio = AudioDriver.I;
                if (audio == null || pool.mPopups == null || pool.count <= 0) return;
                double tick = audio.GetTick(AudioDriver.TickContext.Audio);
                if (double.IsNaN(tick) || double.IsInfinity(tick) || tick < 0) return;

                // CreatePopup has moved on to the next one: the popup it used is the one before.
                TextPopup popup = pool.mPopups[(pool.mIndex + pool.count - 1) % pool.count];
                if (popup == null || popup.text == null) return;

                string kind = SpectatorPopups.KindScore;
                ScoreKeeper keeper = ScoreKeeper.I;
                if (keeper != null)
                {
                    if (keeper.earlyPopupPool != null && keeper.earlyPopupPool.Pointer == pool.Pointer) kind = SpectatorPopups.KindEarly;
                    else if (keeper.latePopupPool != null && keeper.latePopupPool.Pointer == pool.Pointer) kind = SpectatorPopups.KindLate;
                }

                string text = popup.text.text ?? "";
                if (text.Length > MaxPopupText) text = text.Substring(0, MaxPopupText);
                // The second line (the multiplier) only shows when CreatePopup was given one.
                string extra = !string.IsNullOrEmpty(extraText) && popup.extraText != null ? (popup.extraText.text ?? "") : "";
                if (extra.Length > MaxPopupText) extra = extra.Substring(0, MaxPopupText);

                Transform t = popup.transform;
                Vector3 position = t.position;
                Quaternion rotation = t.rotation;
                Vector3 scale = t.lossyScale;

                JObject e = new JObject
                {
                    ["k"] = Popup,
                    ["tick"] = Math.Round(tick, 1),
                    ["pk"] = kind,
                    ["text"] = text,
                    ["rich"] = popup.text.richText,
                    ["p"] = new JArray(Math.Round(position.x, 3), Math.Round(position.y, 3), Math.Round(position.z, 3)),
                    ["q"] = new JArray(Math.Round(rotation.x, 4), Math.Round(rotation.y, 4), Math.Round(rotation.z, 4), Math.Round(rotation.w, 4)),
                    ["s"] = new JArray(Math.Round(scale.x, 3), Math.Round(scale.y, 3), Math.Round(scale.z, 3))
                };
                if (extra.Length > 0) e["extra"] = extra;

                if (queue.Count >= MaxQueued) queue.RemoveAt(0);
                queue.Add(e);
            }
            catch (Exception ex)
            {
                if (loggedError) return;
                loggedError = true;
                Log("queueing a popup failed (logged once): " + ex.Message);
            }
        }

        [HarmonyPatch(typeof(TextPopupPool), "CreatePopup", new Type[] { typeof(Vector3), typeof(Quaternion), typeof(Vector3), typeof(string), typeof(string) })]
        private static class TextPopupPoolCreatePopupPatch
        {
            private static void Postfix(TextPopupPool __instance, string extraText)
            {
                try
                {
                    QueuePopup(__instance, extraText);
                }
                catch
                {
                    // never in the way of the game
                }
            }
        }

        /// <summary>Sends what was queued (when watched) and hands it to the replay recorder (when recording).</summary>
        internal static void FlushIfDue()
        {
            if (queue.Count == 0) return;
            long now = MpSession.LocalNowMs;
            if (now < nextFlushAtMs) return;
            nextFlushAtMs = now + FlushIntervalMs;

            JArray events = new JArray();
            for (int i = 0; i < queue.Count; i++) events.Add(queue[i]);
            queue.Clear();

            if (SendingToWatchers)
                MpSession.SendSpectateEvents(new JObject { ["events"] = events });
            Replays.RecordEvents(events);
        }

        [HarmonyPatch(typeof(Target), "OnHit")]
        private static class TargetOnHitPatch
        {
            private static void Postfix(Target __instance, Gun gun, Gun.AttackType attackType, Vector3 intersectionPoint, bool __result)
            {
                if (!__result || __instance == null) return;
                try
                {
                    bool shot = (int)attackType == 0;
                    int hand = gun != null ? (int)gun.hand : 0;
                    Queue(__instance.mCue, Hit, hand, shot, shot, intersectionPoint);
                }
                catch
                {
                    // never in the way of a hit
                }
            }
        }

        [HarmonyPatch(typeof(ScoreKeeper), "OnFailure", new Type[] { typeof(SongCues.Cue), typeof(bool), typeof(bool) })]
        private static class ScoreKeeperOnFailurePatch
        {
            private static void Prefix(SongCues.Cue cue, bool pass)
            {
                if (cue == null) return; // a shot at nothing
                try
                {
                    // A sustain whose hit was already reported: it was let go. (Not judged by
                    // cue.success: the hold wasn't reported as broken that way, in game 2026-11-14.)
                    bool heldSustain = cue.behavior == Target.TargetBehavior.Hold
                        && (cue.success || sent.Contains(cue.tick + "|" + cue.pitch + "|" + Hit));
                    if (heldSustain) Queue(cue, HoldBroken, 0, false, false, Vector3.zero);
                    else if (pass) Queue(cue, Miss, 0, false, false, Vector3.zero);
                }
                catch
                {
                    // never in the way of the game
                }
            }
        }

        [HarmonyPatch(typeof(Target), "CompleteTarget")]
        private static class TargetCompleteTargetPatch
        {
            private static void Prefix(Target __instance)
            {
                try
                {
                    SongCues.Cue cue = __instance != null ? __instance.mCue : null;
                    if (cue != null && cue.behavior == Target.TargetBehavior.Hold) Queue(cue, HoldDone, 0, false, false, Vector3.zero);
                }
                catch
                {
                    // never in the way of the game
                }
            }
        }

        // ── Watcher ──────────────────────────────────────────────────────────

        /// <summary>Called once a frame by SpectatorTargets: makes sure the server's batches are being listened for.</summary>
        internal static void EnsureInit()
        {
            if (initialized) return;
            initialized = true;
            MpSession.OnSpectateEvents += batch => Receive(batch.userId, batch.events);
            MpSession.OnLeftLobby += reason => incoming.Clear();
        }

        /// <summary>A batch of a watched player's results (from the server, or from a replay).</summary>
        internal static void Receive(string userId, MpSpectateEvent[] events)
        {
            if (userId == null || events == null || events.Length == 0) return;
            if (!incoming.TryGetValue(userId, out List<MpSpectateEvent> list))
            {
                list = new List<MpSpectateEvent>();
                incoming[userId] = list;
            }
            for (int i = 0; i < events.Length; i++)
            {
                if (events[i] != null && events[i].k != null) list.Add(events[i]);
            }
            // Nobody taking them (the window is closed): don't let them pile up.
            if (list.Count > 512) list.RemoveRange(0, list.Count - 512);
        }

        /// <summary>Moves everything received for userId into dest. Returns how many.</summary>
        internal static int TakeFor(string userId, List<MpSpectateEvent> dest)
        {
            if (userId == null || !incoming.TryGetValue(userId, out List<MpSpectateEvent> list) || list.Count == 0) return 0;
            int count = list.Count;
            dest.AddRange(list);
            list.Clear();
            return count;
        }

        internal static void ClearFor(string userId)
        {
            if (userId != null) incoming.Remove(userId);
        }
    }
}