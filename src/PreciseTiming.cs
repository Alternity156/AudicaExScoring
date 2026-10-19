using System;
using System.Collections.Generic;
using System.Diagnostics;
using Harmony;
using MelonLoader;
using UnityEngine;
using Valve.VR;
using static SongCues;

namespace ExScoringMod
{
    /// <summary>
    /// Precise Shot Timing (Options > Audio/Video And Calibration). Replaces the frame-quantized
    /// timing EX uses with a sub-frame estimate. Native successTick, hit/miss and the native score
    /// are never touched - only what GetTimingMsFromCue (Utils.cs) and exCue.timing (Hooks.cs)
    /// report.
    ///
    /// Two error sources are removed, both measured in-game with a per-frame probe:
    ///
    /// 1. Song clock. AudioDriver.GetTick reads the Harmonix audio plugin's integer tick once per
    ///    frame and caches it. That position moves in ~5.3 ms mixing blocks (256 samples @ 48 kHz),
    ///    so per frame it advances 0/4/8/9/13/17 ticks instead of a steady ~8.6, deviating ~5 ms on
    ///    average (spikes 13-18 ms) from a straight line. We keep our own song clock that advances
    ///    at exactly mSpeed per real millisecond (Stopwatch) and is gently pulled toward the raw
    ///    value each frame, which averages the block jitter out. A disagreement over
    ///    ClockResetThresholdMs (pause, seek, restart) snaps it back to the raw value.
    ///
    /// 2. Trigger. The game fires on the frame IsTriggerHeld first crosses the analog threshold.
    ///    We keep the last two trigger samples per hand with real timestamps and interpolate the
    ///    threshold crossing between them:
    ///      - SteamVR: read each frame from the game's own trigger action source
    ///        (KataInput.sSteamVRTrigger[hand]). Its changedTime is the plugin's record of when the
    ///        value last changed (Unity realtime + OpenVR's fUpdateTime), converted to our
    ///        Stopwatch. The press is placed at that change time directly - no interpolation -
    ///        which also handles digital inputs (e.g. buttons bound to the trigger action, 0 -> 1).
    ///      - Oculus: no input timestamps exist, so samples are timestamped per frame and the
    ///        threshold crossing is interpolated (step changes are placed mid-frame).
    ///
    /// The press is converted to song ms in Gun.UpdateTrigger (when it returns true) and linked to
    /// the cue in Gun.Fire, deliberately not in Target.OnHit (see the wrong-hand note in Hooks.cs).
    /// Cues reached without a fresh trigger press (e.g. chain nodes) still get the smoothed clock
    /// for the frame their successTick was recorded, looked up by that exact tick value.
    /// </summary>
    internal static class PreciseTiming
    {
        private static readonly Stopwatch stopwatch = Stopwatch.StartNew();
        private static double NowMs => stopwatch.Elapsed.TotalMilliseconds;

        // ── Smoothed song clock ──

        // Fraction of the per-frame error applied as correction. ~1/k frames time constant
        // (~0.2 s at 90 Hz); leaves well under 1 ms of residual jitter from ~5 ms raw jitter.
        private const double ClockCorrectionGain = 0.05;
        private const double ClockResetThresholdMs = 40.0;

        private static int lastClockFrame = -1;
        private static bool clockValid;
        private static double clockFrameMs;  // Stopwatch time of this frame's clock update
        private static double clockSongMs;   // smoothed song (audio) position, ms, at clockFrameMs
        private static float clockSpeed = 1f;

        // Fallback for cues hit without a fresh press: exact GetTick(Scoring) value of a frame ->
        // that frame's smoothed scoring ms. successTick is assigned from that same cached value, so
        // float equality is exact.
        private const double FallbackRetentionMs = 30000.0;
        private static readonly Dictionary<float, double> smoothedScoringMsByTick = new Dictionary<float, double>();
        private static readonly Queue<KeyValuePair<double, float>> tickHistory = new Queue<KeyValuePair<double, float>>();

        // ── Trigger samples, indexed by Gun.controller / SteamVR_Input_Sources (1 = left, 2 = right) ──

        private class HandState
        {
            public bool hasSample;
            public bool realTimestamps; // true when curTime came from the runtime, not the frame
            public float prevValue;
            public double prevTime;
            public float curValue;
            public double curTime;
            public double lastPollTime;

            public int pressFrame = -1;
            public double pressScoringMs;
            public PressRecord pendingRecord;
        }

        /// <summary>Debug details of one registered press, for the end-of-song outlier lines.</summary>
        private class PressRecord
        {
            public int hand;
            public int frame;
            public bool runtimeTimed;
            public double leadMs;      // frame time - estimated press time
            public double frameDtMs;   // that frame's unscaledDeltaTime
            public double ageMs;       // last poll time - change time (how old the change was when seen)
            public double clockErrMs;  // raw audio ms - predicted smoothed ms on that frame
        }

        private static readonly HandState[] hands = { new HandState(), new HandState(), new HandState() };

        // A press sample older than this when the game registers the press is treated as
        // unreliable, and the frame time is used instead.
        private const double MaxPressSampleAgeMs = 100.0;

        private static readonly Dictionary<int, double> pressScoringMsByCueIndex = new Dictionary<int, double>();

        private static bool loggedError;
        private static bool loggedScoreMismatch;

        // ── Diagnostics (per song, reset in Reset(), logged by LogSummary at song end) ──

        private static int statClockFrames;
        private static int statClockResets;
        private static int statClockInvalidFrames;
        private static readonly int[] statSamplePolls = new int[3];
        private static readonly int[] statSampleChanges = new int[3];
        private static int statPresses;
        private static int statPressesFrameTimed;
        private static int statPressesRuntimeTimed;
        private static double statPressLeadSumMs; // frame time - estimated press time, summed
        private static int statFireLinks;
        private static int statScoreRecomputed;
        private static int statScoreMismatches;
        private static readonly HashSet<int> statPressTimedCues = new HashSet<int>();
        private static readonly HashSet<int> statClockOnlyCues = new HashSet<int>();
        private static readonly HashSet<int> statNativeCues = new HashSet<int>();
        private static readonly Dictionary<int, float> statDiffByCue = new Dictionary<int, float>();
        private static readonly HashSet<int> statClampedCues = new HashSet<int>();

        // Polling diagnostics
        private const double OutlierDiffMs = 12.0;
        private const int HistogramMaxMs = 25;
        private static double lastClockErrorMs;
        private static double statClockErrAbsSumMs;
        private static double statClockErrAbsMaxMs;
        private static double statFirstFrameMs = -1;
        private static double statLastFrameMs;
        private static int statChangesClamped;
        private static readonly List<double> statChangeTimesMs = new List<double>();
        private static readonly List<double> statChangeAgesMs = new List<double>();
        private static readonly List<double> statPressLeadsMs = new List<double>();
        private static int statPressesOlderThanFrame;
        private static readonly Dictionary<int, PressRecord> statPressRecordByCue = new Dictionary<int, PressRecord>();
        private static readonly Dictionary<int, KeyValuePair<float, float>> statPreciseNativeByCue = new Dictionary<int, KeyValuePair<float, float>>();
        private static readonly Dictionary<int, int> statHandByCue = new Dictionary<int, int>();

        // ── Public API ──

        /// <summary>
        /// Precise timing offset in ms (positive = late) for a cue, if one exists. Clamped to the
        /// Good timing window: this is only ever asked for cues the game counted as hits, and a
        /// press timestamped outside the window (in practice only during heavy lag, when the game
        /// registers a press late at the end of a long frame) must never turn a native hit into an
        /// EX timing Miss.
        /// </summary>
        public static bool TryGetTimingMs(Cue cue, out float timingMs)
        {
            timingMs = 0f;
            if (!Config.PreciseShotTiming || cue == null) return false;

            SongList.SongData songData = GetSongData();
            if (songData == null) return false;

            double scoringMs;
            bool pressTimed = pressScoringMsByCueIndex.TryGetValue(cue.index, out scoringMs);
            if (!pressTimed && !smoothedScoringMsByTick.TryGetValue(cue.successTick, out scoringMs))
            {
                statNativeCues.Add(cue.index);
                return false;
            }

            double cueMs = AudioDriver.TickSpanToMs(songData, 0f, cue.tick);
            float rawMs = (float)(scoringMs - cueMs);
            float window = ExScoring.judgementGoodTimingWindowMs;
            timingMs = Mathf.Clamp(rawMs, -window, window);
            if (timingMs != rawMs && statClampedCues.Add(cue.index))
            {
                DebugLog.Log($"[PreciseTiming] Cue {cue.index} clamped to Good window: precise {rawMs:0.00} ms -> {timingMs:0.00} ms " +
                             $"(native {ExScoring.GetNativeTimingMsFromCue(cue):0.00} ms).");
            }

            if (pressTimed) statPressTimedCues.Add(cue.index);
            else statClockOnlyCues.Add(cue.index);
            if (!statDiffByCue.ContainsKey(cue.index))
            {
                float nativeMs = ExScoring.GetNativeTimingMsFromCue(cue);
                statDiffByCue[cue.index] = timingMs - nativeMs;
                statPreciseNativeByCue[cue.index] = new KeyValuePair<float, float>(timingMs, nativeMs);
                statHandByCue[cue.index] = (int)cue.handType;
            }

            return true;
        }

        /// <summary>
        /// The timing score EX uses (exCue.timing). Recomputed from the precise ms with the same
        /// function native uses (ScoreKeeper.GetTimingSuccessAmount(ms / speed)), but only after
        /// confirming that function reproduces native's own value from the native ms for this cue.
        /// If it doesn't, native's value is returned unchanged.
        /// </summary>
        public static float GetExTimingScore(Cue cue, float nativeTiming)
        {
            if (!Config.PreciseShotTiming || cue == null) return nativeTiming;

            if (cue.behavior != Target.TargetBehavior.Standard &&
                cue.behavior != Target.TargetBehavior.Horizontal &&
                cue.behavior != Target.TargetBehavior.Vertical &&
                cue.behavior != Target.TargetBehavior.ChainStart &&
                cue.behavior != Target.TargetBehavior.Hold)
            {
                return nativeTiming;
            }

            if (!TryGetTimingMs(cue, out float preciseMs)) return nativeTiming;

            try
            {
                float speed = AudioDriver.I != null ? AudioDriver.I.mSpeed : 1f;
                if (speed <= 0f) speed = 1f;

                float nativeMs = ExScoring.GetNativeTimingMsFromCue(cue);
                float reproduced = ScoreKeeper.GetTimingSuccessAmount(nativeMs / speed);
                if (Math.Abs(reproduced - nativeTiming) > 0.002f)
                {
                    statScoreMismatches++;
                    if (!loggedScoreMismatch)
                    {
                        loggedScoreMismatch = true;
                        DebugLog.Log($"[PreciseTiming] Timing score formula mismatch (native {nativeTiming:0.0000}, " +
                                     $"reproduced {reproduced:0.0000}); keeping native timing score.");
                    }
                    return nativeTiming;
                }

                statScoreRecomputed++;
                return ScoreKeeper.GetTimingSuccessAmount(preciseMs / speed);
            }
            catch (Exception ex)
            {
                LogErrorOnce(ex);
                return nativeTiming;
            }
        }

        // ── Internals ──

        private static SongList.SongData GetSongData()
        {
            SongDataHolder holder = SongDataHolder.I;
            return holder != null ? holder.songData : null;
        }

        private static void Reset()
        {
            lastClockFrame = -1;
            clockValid = false;
            smoothedScoringMsByTick.Clear();
            tickHistory.Clear();
            pressScoringMsByCueIndex.Clear();
            for (int i = 0; i < hands.Length; i++) hands[i] = new HandState();

            statClockFrames = 0;
            statClockResets = 0;
            statClockInvalidFrames = 0;
            Array.Clear(statSamplePolls, 0, statSamplePolls.Length);
            Array.Clear(statSampleChanges, 0, statSampleChanges.Length);
            statPresses = 0;
            statPressesFrameTimed = 0;
            statPressesRuntimeTimed = 0;
            statPressLeadSumMs = 0;
            statFireLinks = 0;
            statScoreRecomputed = 0;
            statScoreMismatches = 0;
            statPressTimedCues.Clear();
            statClockOnlyCues.Clear();
            statNativeCues.Clear();
            statDiffByCue.Clear();
            statClampedCues.Clear();

            lastClockErrorMs = 0;
            statClockErrAbsSumMs = 0;
            statClockErrAbsMaxMs = 0;
            statFirstFrameMs = -1;
            statLastFrameMs = 0;
            statChangesClamped = 0;
            statChangeTimesMs.Clear();
            statChangeAgesMs.Clear();
            statPressLeadsMs.Clear();
            statPressesOlderThanFrame = 0;
            statPressRecordByCue.Clear();
            statPreciseNativeByCue.Clear();
            statHandByCue.Clear();
        }

        /// <summary>One debug log line per song summarizing what Precise Shot Timing did.</summary>
        private static void LogSummary(bool failed)
        {
            if (!Config.PreciseShotTiming)
            {
                DebugLog.Log("[PreciseTiming] Song over: option is OFF, EX used native frame timing.");
                return;
            }

            int nativeOnly = 0;
            foreach (int index in statNativeCues)
                if (!statPressTimedCues.Contains(index) && !statClockOnlyCues.Contains(index)) nativeOnly++;

            double sum = 0, sumAbs = 0, maxAbs = 0;
            foreach (float d in statDiffByCue.Values)
            {
                sum += d;
                sumAbs += Math.Abs(d);
                maxAbs = Math.Max(maxAbs, Math.Abs(d));
            }
            int n = statDiffByCue.Count;
            string diffText = n > 0
                ? $"mean {sum / n:0.00} ms, mean |diff| {sumAbs / n:0.00} ms, max |diff| {maxAbs:0.00} ms"
                : "no cues";

            DebugLog.Log(
                $"[PreciseTiming] Song over (failed={failed}) | runtime={(IsOculusRuntime() ? "Oculus" : "SteamVR")} | " +
                $"clock: {statClockFrames} valid frames, {statClockInvalidFrames} invalid, {statClockResets} resets | " +
                $"trigger polls L {statSamplePolls[1]} R {statSamplePolls[2]}, changes L {statSampleChanges[1]} R {statSampleChanges[2]} | " +
                $"presses {statPresses} ({statPressesRuntimeTimed} runtime-timed, " +
                $"{statPresses - statPressesRuntimeTimed - statPressesFrameTimed} interpolated, {statPressesFrameTimed} frame-timed fallback, " +
                $"avg {(statPresses > 0 ? statPressLeadSumMs / statPresses : 0):0.00} ms before frame), linked to cues {statFireLinks} | " +
                $"cues: {statPressTimedCues.Count} press-timed, {statClockOnlyCues.Count} clock-only, {nativeOnly} native | " +
                $"EX timing score: {statScoreRecomputed} recomputed, {statScoreMismatches} kept native (mismatch) | " +
                $"clamped to Good window: {statClampedCues.Count} | " +
                $"precise - native: {diffText}");

            LogPollingDiagnostics();
        }

        /// <summary>
        /// Extra debug lines answering "how often does the controller actually report?":
        ///  - lead histogram: per press, how far before its frame the runtime placed it. Fast input
        ///    spreads evenly over ~0..frame; slow/frame-locked input clusters at a few values.
        ///  - age histogram: per value change, how old it already was when the plugin saw it.
        ///  - periodicity: vector strength of all change timestamps against candidate periods.
        ///    A strong peak at some period P means the driver only reports on a P ms grid; a peak
        ///    at the frame period means input is effectively frame-locked.
        ///  - outliers: every cue whose precise and native timing differ by more than 12 ms.
        /// </summary>
        private static void LogPollingDiagnostics()
        {
            int frames = Math.Max(1, statClockFrames);
            double meanFrameMs = statFirstFrameMs >= 0 && statClockFrames > 1
                ? (statLastFrameMs - statFirstFrameMs) / (statClockFrames - 1)
                : 0;

            DebugLog.Log($"[PreciseTiming] Clock: mean frame {meanFrameMs:0.000} ms, raw-vs-smoothed error mean |{statClockErrAbsSumMs / frames:0.00}| ms, max |{statClockErrAbsMaxMs:0.00}| ms");

            DebugLog.Log($"[PreciseTiming] Press lead before frame (1 ms buckets, n={statPressLeadsMs.Count}, " +
                         $"{statPressesOlderThanFrame} older than their previous frame): {FormatHistogram(statPressLeadsMs)}");

            DebugLog.Log($"[PreciseTiming] Change age when seen (1 ms buckets, n={statChangeAgesMs.Count}, " +
                         $"{statChangesClamped} clamped to previous poll): {FormatHistogram(statChangeAgesMs)} | " +
                         $"distinct ages @0.1 ms: {CountDistinct(statChangeAgesMs, 0.1)}");

            if (statChangeTimesMs.Count >= 20)
            {
                double bestPeriod = 0, bestStrength = 0;
                for (double period = 1.0; period <= 15.0001; period += 0.05)
                {
                    double strength = VectorStrength(statChangeTimesMs, period);
                    if (strength > bestStrength)
                    {
                        bestStrength = strength;
                        bestPeriod = period;
                    }
                }

                double frameStrength = meanFrameMs > 0 ? VectorStrength(statChangeTimesMs, meanFrameMs) : 0;
                double noiseLevel = 1.0 / Math.Sqrt(statChangeTimesMs.Count);

                DebugLog.Log($"[PreciseTiming] Change-time periodicity (n={statChangeTimesMs.Count}, random ~{noiseLevel:0.00}): " +
                             $"strongest {bestStrength:0.00} at {bestPeriod:0.00} ms | at frame period {meanFrameMs:0.00} ms: {frameStrength:0.00}");
            }

            var outliers = new List<KeyValuePair<int, float>>();
            foreach (var kv in statDiffByCue)
                if (Math.Abs(kv.Value) > OutlierDiffMs) outliers.Add(kv);
            outliers.Sort((a, b) => Math.Abs(b.Value).CompareTo(Math.Abs(a.Value)));

            DebugLog.Log($"[PreciseTiming] Outliers (|precise - native| > {OutlierDiffMs:0} ms): {outliers.Count}");
            int shown = 0;
            foreach (var kv in outliers)
            {
                if (++shown > 30)
                {
                    DebugLog.Log($"[PreciseTiming]   ... {outliers.Count - 30} more");
                    break;
                }

                statPreciseNativeByCue.TryGetValue(kv.Key, out var pn);
                statHandByCue.TryGetValue(kv.Key, out int handType);
                string detail;
                if (statPressRecordByCue.TryGetValue(kv.Key, out PressRecord r))
                {
                    detail = $"press frame {r.frame} ({(r.runtimeTimed ? "runtime" : "frame/interp")}), lead {r.leadMs:0.00} ms, " +
                             $"frame dt {r.frameDtMs:0.00} ms, change age {r.ageMs:0.00} ms, clock err {r.clockErrMs:0.00} ms";
                }
                else
                {
                    detail = "clock-only (no press linked)";
                }

                DebugLog.Log($"[PreciseTiming]   cue {kv.Key} handType {handType}: native {pn.Value:0.00} ms, precise {pn.Key:0.00} ms, " +
                             $"diff {kv.Value:0.00} ms | {detail}");
            }
        }

        private static string FormatHistogram(List<double> values)
        {
            if (values.Count == 0) return "none";
            int[] buckets = new int[HistogramMaxMs + 2]; // [0..max) by floor, last = >= max, plus negatives at index max+1
            int negatives = 0;
            foreach (double v in values)
            {
                if (v < 0) { negatives++; continue; }
                int b = (int)Math.Floor(v);
                if (b >= HistogramMaxMs) b = HistogramMaxMs;
                buckets[b]++;
            }

            var sb = new System.Text.StringBuilder();
            if (negatives > 0) sb.Append($"<0:{negatives} ");
            for (int i = 0; i < HistogramMaxMs; i++) sb.Append($"{i}:{buckets[i]} ");
            sb.Append($"{HistogramMaxMs}+:{buckets[HistogramMaxMs]}");
            return sb.ToString();
        }

        private static int CountDistinct(List<double> values, double resolution)
        {
            var set = new HashSet<long>();
            foreach (double v in values) set.Add((long)Math.Round(v / resolution));
            return set.Count;
        }

        private static double VectorStrength(List<double> timesMs, double periodMs)
        {
            double c = 0, sn = 0;
            double k = 2.0 * Math.PI / periodMs;
            foreach (double t in timesMs)
            {
                double phase = k * t;
                c += Math.Cos(phase);
                sn += Math.Sin(phase);
            }
            return Math.Sqrt(c * c + sn * sn) / timesMs.Count;
        }

        private static float GetInputOffsetMs()
        {
            PlayerPreferences prefs = PlayerPreferences.I;
            return prefs != null && prefs.InputOffsetMs != null ? prefs.InputOffsetMs.mVal : 0f;
        }

        private static bool IsOculusRuntime()
        {
            VRHardwareSetup setup = VRHardwareSetup.I;
            return setup != null && (int)setup.hardware == 1;
        }

        /// <summary>Smoothed song (audio) ms at an arbitrary Stopwatch time.</summary>
        private static double SongMsAt(double stopwatchMs)
        {
            return clockSongMs + (stopwatchMs - clockFrameMs) * clockSpeed;
        }

        /// <summary>Once per frame, before either gun runs its Update.</summary>
        private static void UpdateClock()
        {
            int frame = Time.frameCount;
            if (frame == lastClockFrame) return;
            lastClockFrame = frame;

            double now = NowMs;

            AudioDriver audioDriver = AudioDriver.I;
            SongList.SongData songData = GetSongData();
            if (audioDriver == null || songData == null || !audioDriver.IsPlaying())
            {
                clockValid = false;
                statClockInvalidFrames++;
                return;
            }

            float rawTick = audioDriver.GetTick(AudioDriver.TickContext.Audio);
            if (rawTick <= 0f)
            {
                clockValid = false;
                statClockInvalidFrames++;
                return;
            }

            double rawMs = AudioDriver.TickSpanToMs(songData, 0f, rawTick);
            float speed = audioDriver.mSpeed;
            if (speed <= 0f) speed = 1f;

            if (!clockValid)
            {
                clockSongMs = rawMs;
                clockValid = true;
            }
            else
            {
                double predicted = clockSongMs + (now - clockFrameMs) * speed;
                double error = rawMs - predicted;
                lastClockErrorMs = error;
                statClockErrAbsSumMs += Math.Abs(error);
                if (Math.Abs(error) > statClockErrAbsMaxMs) statClockErrAbsMaxMs = Math.Abs(error);
                if (Math.Abs(error) > ClockResetThresholdMs)
                {
                    clockSongMs = rawMs;
                    statClockResets++;
                }
                else
                {
                    clockSongMs = predicted + ClockCorrectionGain * error;
                }
            }
            statClockFrames++;

            clockFrameMs = now;
            if (statFirstFrameMs < 0) statFirstFrameMs = now;
            statLastFrameMs = now;
            clockSpeed = speed;

            // Fallback map for cues hit without a fresh press.
            float scoringTick = audioDriver.GetTick(AudioDriver.TickContext.Scoring);
            smoothedScoringMsByTick[scoringTick] = clockSongMs - GetInputOffsetMs();
            tickHistory.Enqueue(new KeyValuePair<double, float>(now, scoringTick));
            while (tickHistory.Count > 0 && now - tickHistory.Peek().Key > FallbackRetentionMs)
            {
                float oldTick = tickHistory.Dequeue().Value;
                smoothedScoringMsByTick.Remove(oldTick);
            }
        }

        /// <summary>
        /// Records a trigger value for a hand. sampleTime is when the value was produced, pollTime
        /// is when we read it. The previous value is known to have held at least until the previous
        /// poll, so that is the earliest the change can be placed.
        /// </summary>
        private static void RecordSample(int hand, float value, double sampleTime, double pollTime, bool realTimestamps)
        {
            HandState state = hands[hand];
            state.realTimestamps = realTimestamps;
            statSamplePolls[hand]++;

            if (!state.hasSample)
            {
                state.hasSample = true;
                state.prevValue = value;
                state.prevTime = sampleTime;
                state.curValue = value;
                state.curTime = sampleTime;
                state.lastPollTime = pollTime;
                return;
            }

            if (value != state.curValue)
            {
                statSampleChanges[hand]++;
                double anchor = Math.Max(state.curTime, state.lastPollTime);
                if (realTimestamps)
                {
                    statChangeTimesMs.Add(sampleTime);
                    statChangeAgesMs.Add(pollTime - sampleTime);
                    if (sampleTime < anchor) statChangesClamped++;
                }
                state.prevValue = state.curValue;
                state.prevTime = anchor;
                state.curValue = value;
                state.curTime = Math.Min(Math.Max(sampleTime, anchor), pollTime);
            }

            state.lastPollTime = pollTime;
        }

        private static double EstimatePressTime(HandState state, float threshold)
        {
            double fallback = clockFrameMs;
            if (!state.hasSample) return fallback;

            double pressTime = state.curTime;

            // Runtime-timestamped (SteamVR): the change time IS when the over-threshold value
            // arrived. Interpolating toward the previous poll would only pull it early.
            if (!state.realTimestamps && state.curTime > state.prevTime)
            {
                if (state.prevValue <= 0.05f && state.curValue >= 0.95f)
                {
                    // Step change (button, or a pull faster than a frame): no slope to read, so
                    // the unbiased estimate is the middle of the interval.
                    pressTime = (state.prevTime + state.curTime) * 0.5;
                }
                else if (threshold <= 1.001f)
                {
                    // Analog ramp: interpolate the threshold crossing (anything over 1.001 means
                    // the game uses the digital button instead).
                    threshold = Mathf.Clamp(threshold, 0.01f, 0.99f);
                    if (state.prevValue < threshold && state.curValue >= threshold)
                    {
                        double fraction = (threshold - state.prevValue) / (state.curValue - state.prevValue);
                        pressTime = state.prevTime + fraction * (state.curTime - state.prevTime);
                    }
                }
            }

            double now = NowMs;
            if (pressTime > now || now - pressTime > MaxPressSampleAgeMs) return fallback;
            return pressTime;
        }

        /// <summary>
        /// Reads this hand's source of the game's trigger action (the same one KataInput.GetAxis
        /// reads for Gun) and records it with the plugin's own change time, converted from Unity
        /// realtime to our Stopwatch.
        /// </summary>
        private static void SampleSteamVRTrigger(int hand)
        {
            SteamVR_Action_Single trigger = KataInput.sSteamVRTrigger;
            if (trigger == null) return;

            SteamVR_Action_Single_Source source = trigger[(SteamVR_Input_Sources)hand];
            if (source == null) return;

            double realtimeToStopwatchMs = NowMs - Time.realtimeSinceStartup * 1000.0;
            double changeTime = source.changedTime * 1000.0 + realtimeToStopwatchMs;
            double pollTime = source.updateTime * 1000.0 + realtimeToStopwatchMs;

            RecordSample(hand, source.axis, changeTime, pollTime, true);
        }

        private static void LogErrorOnce(Exception ex)
        {
            if (loggedError) return;
            loggedError = true;
            MelonLogger.LogError($"[PreciseTiming] {ex.Message}");
            MelonLogger.LogError(ex.StackTrace);
        }

        // ── Patches ──

        [HarmonyPatch(typeof(AudioDriver), "StartPlaying", new Type[0])]
        private static class AudioDriver_StartPlaying_ResetPreciseTiming
        {
            private static void Postfix()
            {
                Reset();
                DebugLog.Log($"[PreciseTiming] Song start, option {(Config.PreciseShotTiming ? "ON" : "OFF")}.");
            }
        }

        [HarmonyPatch(typeof(SongEnd), "OnSongOver", new Type[] { typeof(bool) })]
        private static class SongEnd_OnSongOver_PreciseTimingSummary
        {
            private static void Postfix(bool failed)
            {
                try
                {
                    LogSummary(failed);
                }
                catch (Exception ex)
                {
                    LogErrorOnce(ex);
                }
            }
        }

        /// <summary>Clock update (first gun each frame) and Oculus trigger sampling.</summary>
        [HarmonyPatch(typeof(Gun), "Update")]
        private static class Gun_Update_PreciseTiming
        {
            private static void Prefix(Gun __instance)
            {
                if (!Config.PreciseShotTiming) return;

                try
                {
                    UpdateClock();

                    int hand = (int)__instance.controller;
                    if (hand != 1 && hand != 2) return;

                    if (IsOculusRuntime())
                    {
                        RecordSample(hand, __instance.GetTriggerPullAmount(), clockFrameMs, clockFrameMs, false);
                    }
                    else
                    {
                        SampleSteamVRTrigger(hand);
                    }
                }
                catch (Exception ex)
                {
                    LogErrorOnce(ex);
                }
            }
        }

        /// <summary>Converts a registered press to scoring ms (same frame the game registers it).</summary>
        [HarmonyPatch(typeof(Gun), "UpdateTrigger")]
        private static class Gun_UpdateTrigger_PreciseTiming
        {
            private static void Postfix(Gun __instance, bool __result)
            {
                if (!__result || !Config.PreciseShotTiming || !clockValid) return;

                try
                {
                    int hand = (int)__instance.controller;
                    if (hand != 1 && hand != 2) return;

                    HandState state = hands[hand];
                    double pressTime = EstimatePressTime(state, __instance.GetTriggerThreshold());
                    statPresses++;
                    if (pressTime == clockFrameMs) statPressesFrameTimed++;
                    else if (state.realTimestamps) statPressesRuntimeTimed++;
                    statPressLeadSumMs += clockFrameMs - pressTime;

                    state.pressFrame = Time.frameCount;
                    state.pressScoringMs = SongMsAt(pressTime) - GetInputOffsetMs();

                    double frameDtMs = Time.unscaledDeltaTime * 1000.0;
                    double leadMs = clockFrameMs - pressTime;
                    statPressLeadsMs.Add(leadMs);
                    if (leadMs > frameDtMs + 1.0) statPressesOlderThanFrame++;

                    state.pendingRecord = new PressRecord
                    {
                        hand = hand,
                        frame = Time.frameCount,
                        runtimeTimed = state.realTimestamps && pressTime != clockFrameMs,
                        leadMs = leadMs,
                        frameDtMs = frameDtMs,
                        ageMs = state.lastPollTime - state.curTime,
                        clockErrMs = lastClockErrorMs,
                    };
                }
                catch (Exception ex)
                {
                    LogErrorOnce(ex);
                }
            }
        }

        /// <summary>Links this frame's press to the cue being shot.</summary>
        [HarmonyPatch(typeof(Gun), "Fire")]
        private static class Gun_Fire_PreciseTiming
        {
            private static void Prefix(Gun __instance, Target target)
            {
                if (!Config.PreciseShotTiming || target == null) return;

                try
                {
                    Cue cue = target.mCue;
                    if (cue == null) return;

                    int hand = (int)__instance.controller;
                    if (hand != 1 && hand != 2) return;

                    HandState state = hands[hand];
                    if (state.pressFrame != Time.frameCount) return;

                    pressScoringMsByCueIndex[cue.index] = state.pressScoringMs;
                    statFireLinks++;
                    if (state.pendingRecord != null) statPressRecordByCue[cue.index] = state.pendingRecord;
                }
                catch (Exception ex)
                {
                    LogErrorOnce(ex);
                }
            }
        }
    }
}