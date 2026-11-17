using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// The watched player's targets in the spectator window (step 2 of the target work). Findings
    /// behind it: claude/Spectator-Window-Target-Findings.md, claude/Spectator-Window-Hit-Findings.md
    /// and claude/Spectator-Window-Targets-Build-Notes.md.
    ///
    /// What is drawn: Standard, Vertical, Horizontal, Hold (sustain) and Melee targets with their
    /// telegraphs, and the line between two targets that land together, from this client's own
    /// copy of the chart (SpectatorSong.Cues), at the watched player's song position
    /// (SpectatorSong.TryGetPosition, the same position their avatar is drawn at), flying in at
    /// their target and melee speed, melee targets at their player height (spectateInfo settings).
    /// Everything is on MpPresence.SongOnlyLayer, which only the window's camera renders.
    ///
    /// Results (step 3, part A; SpectatorEvents): the watched player's hits, misses and sustain
    /// results arrive with the song tick they happened at and are applied when the window's song
    /// position reaches that tick:
    ///   hit         the target goes at that moment; the beam MpPresence draws for that shot is
    ///               aimed at where it hit (TryGetShotAim). A sustain stays on its spot, held:
    ///               it spins (Target.LateUpdate: local z = (t^2 + t) x 300 degrees, t = seconds
    ///               held / clamp(tickLength / 3840, 1, 4), one way for the left hand, the other
    ///               for the right) and the sustain beam runs from the gun to it (below)
    ///   miss        nothing: the target flies on past its spot and goes when its clip is over
    ///               (clip time 2.733, melee 2.45), which is also what happens without any result
    ///   holdDone    the sustain goes
    ///   holdBroken  the sustain goes dark (colour halfway to black) and stays until its end
    /// Until the first result of a chart arrives (an old debug recording has none), targets go at
    /// their cue's time and sustains at their end, as before.
    ///
    /// Sustain beam (SustainBeam / SustainBeamLine, Ghidra 2026-11-14): one copy of
    /// KataConfig.sustainBeamPrefab per hand, on the song-only layer. Natively its Update puts four
    /// points (start = the gun's firepoint, startForward = 2 m ahead of it, middle = 40% of the
    /// way to the target, end = the target), then updates its bezier and redraws each line along
    /// it. Without a Gun (ours has none: the watched player's gun is a model copy) that Update
    /// skips the points and still does the rest, so the four points are placed here every frame
    /// from the watched player's firepoint (MpPresence.TryGetRemoteFirepoint) and the held target.
    /// Each line is set up with SustainBeamLine.Init(hand, audio tracker). The tracker
    /// (SustainAudioTracker) is what makes the lines wobble with the sustain track and pulse in
    /// width (SustainBeamLine.UpdateBeam: sideways offsets from GetHistory, the track's recent
    /// samples read through a native signal tap; width from GetWidthModulation); without one the
    /// lines are straight. The game's two belong to AudioDriver (sustainAudioTrackerLeft/Right),
    /// which isn't there in the menus, so a copy of each is made here with the same settings
    /// (dump 2026-11-14: bufferLength 15000, sampleDelay 4320, signalTapId 2 left / 3 right) on
    /// an object that is inactive until they are set, so Awake sees them. Whether the tap carries
    /// the sustain track SpectatorSounds plays in the menus is not known: if not, the history is
    /// silence and the lines only pulse. Init's colours (this player's) are replaced by the watched player's the same way (hand colour
    /// toward white by bandIndex x 0.5; start alpha 0, end alpha toward 0.25).
    ///
    /// Sounds: SpectatorSounds plays each hit's target sound (HitKnown, from TellSounds when a hit is received) and the
    /// sustain sound while a sustain is held (IsHolding).
    ///
    /// Chains (Target.InitFromSpawner / Update / UpdateChainLineAnim, Ghidra 2026-11-14): the whole
    /// chain is made with its start, and every node flies in on the START's time (not its own),
    /// at half size, without a telegraph. Once the start was hit and its time has come, a node
    /// sits on its spot (animation off, local position zero) until its own result: hit = gone;
    /// missed after the start was hit (the chain broke) = gone at once; start missed = the nodes
    /// fly on past like it. Each node's chain_line (world space, width 0.35, the hand's colour at
    /// half strength) runs from the previous node to itself, both 0.15 m toward the player, and
    /// is hidden once the previous node is gone; _Travel = 1 - clamp01(ticks until the node / 240),
    /// _Glow = clamp01(InverseLerp(1.47, 1.6, clip time)).
    ///
    /// Score, early and late popups: SpectatorPopups (fed from PullResults, updated from Tick).
    ///
    /// Not yet: dodge targets; the chain beam (KataConfig.chainBeamPrefab); hit particles
    /// and the beam's dust; the target's particle trail.
    ///
    /// How a target is made: the native prefabs (KataConfig.targetStandard / targetVertical /
    /// targetHorizontal / targetSustain / targetMelee, and each one's Target.telegraphPrefab) are
    /// copied once into templates under an inactive root, where their Target / Telegraph
    /// components are removed before they ever wake up (so none of the native target logic runs,
    /// and nothing is added to Target.ActiveTargets), colliders are switched off and the sustain
    /// trails hidden. A cue's target is a copy of its template under our own spawner and holder:
    ///   root      at the watched avatar's root (their play space: MpPresence puts it at their seat)
    ///   dome      local (0, -1.5, 6)                              = SpawnerDome
    ///   spawner   local rotation Euler(-16.5 + 11 x row, 86.5 + 17 x col, 0),
    ///             local position rotation x back x 15 x (1 + zOffset)   = TargetSpawner.SpawnCue
    ///             with col = pitch % 12 + gridOffset.x, row = pitch / 12 + gridOffset.y
    ///   holder    scale 2 (KataConfig.targetScale)
    ///   target    its own position comes from its clip (target_left / target_right)
    ///   telegraph scale 1.5, clip "telegraph"
    /// Melee (pitches 98..101):
    ///   melee root  local (0, player height, 0)                   = MeleeHeightAdjust
    ///   spawner     (-/+0.35, 0.371 or 0.871, 0.75), yaw 141.38 / 218.62, scale 0.5
    ///   holder      local (2 x gridOffset.x, 2 x gridOffset.y, zOffset), scale 1
    ///   target      clip target_left_melee_soccer / target_right_melee_soccer by side
    ///   telegraph   a child of the target, scale 0.75, turned to face the watched player's head
    ///
    /// How it moves: the clips aren't played, they are posed every frame at
    ///   clip time = 2.18 - (seconds until the cue) x speed,   speed = 2.18 / lookahead
    /// (Target.InitFromSpawner: mAnimationSpeed = 2.18 / KataConfig.GetSecondsLookahead(behavior)).
    /// Lookahead: 3.1 / 2.5 / 2.18 / 1.9 s for Easy / Normal / Hard / Expert, divided by the target
    /// speed; for melee 2.18 s divided by target speed x melee speed squared (fitted to the one
    /// dump there is: 2.016 at 1.4 and 1.2). A paused or looping watched player needs nothing
    /// special. The target's own mesh shows from clip time 1.083 (melee 0.55; before that only the
    /// telegraph shows), the timing ring inside the cue's slop window, and the telegraph goes at
    /// the cue's time, as natively. Posing is AnimationState.time + Animation.Sample() (no
    /// animation events fire: their receiver, Target, is gone); if that throws once, it falls
    /// back to a playing state at speed 0.
    ///
    /// Lines (Target.InitFromSpawner, UpdateSimultaneousLineAnim): two cues on the same tick, on
    /// opposite hands (or both either-hand), not chain nodes, both melee or neither, are a pair.
    /// The later one of the two shows its simultaneous_line (melee: melee_connector) until their
    /// cue's time. As natively (UnityExplorer dump 2026-11-14): world space, from its own holder
    /// to the other's holder (where the two targets land, not where they are; a melee pair's
    /// connector runs between the balls themselves, which is a guess); through a property block,
    /// _ColorA = its own hand's colour, _ColorB = the other hand's, and every frame _BorderAmount
    /// = 1 + 0.461 t + 0.498 t^2 - 0.359 t^3 (1 past 2.18) and _Glow = 0 up to clip time 1.47,
    /// rising to 1.22 at 1.65, back to 1 at 1.7333; width 0.08 -> 0.18 over clip times 1.47..2.18.
    ///
    /// Colours: the watched player's gun colours (MpPresence.GetGunColors) on copies of the
    /// target's hand material and of the telegraph's circle and centre materials (_Color). The
    /// telegraph's cloud is a hue-shifted texture natively (TargetColorSetter makes mTelegraphLeft
    /// / Right / SustainLeft / SustainRight from a red source, which is why the base materials are
    /// red, and the colour only arrives with the texture put on each telegraph's own copy): it
    /// takes a copy of TargetColorSetter's material for that hand (telegraph*Mat, telegraphSlot*Mat,
    /// telegraphSustain*Mat) with the hand's texture: the game's own (GetTelegraphTexture) when the
    /// watched player's colour is this player's, else one shifted to their colour the same way
    /// (ShiftTextureColors).
    ///
    /// From the UnityExplorer console: ExScoringMod.SpectatorTargets.DebugDump();
    /// </summary>
    internal static class SpectatorTargets
    {
        // ── Native numbers (findings 2026-11-13 / 14) ────────────────────────
        private static readonly Vector3 DomePosition = new Vector3(0f, -1.5f, 6f);
        private const float DomeRadius = 15f;
        private const float RowBaseDegrees = -16.5f, RowStepDegrees = 11f;
        private const float ColBaseDegrees = 86.5f, ColStepDegrees = 17f;
        private const float HolderScale = 2f;
        private const float TelegraphScale = 1.5f;
        private const float HitClipTime = 2.18f;        // where the clip is at the cue's time
        private const float ShowClipTime = 1.083333f;   // Target.mAnimationShowTime
        private const float TelegraphMaxTime = 2.17f;   // just inside the telegraph clip (2.183 s)
        private const float CompleteClipTime = 2.72f;   // just inside Target.mAnimationCompleteTime (2.733)
        private const float MeleeCompleteClipTime = 2.44f; // melee: 2.45

        private const double ChainMaxMs = 30000;        // a chain's nodes are looked for this far after its start
        private const double HoldGraceMs = 400;         // a held sustain whose end result never came goes this long after its end
        private const double AimWindowMs = 250;         // a shot and a hit this close together are the same shot
        private const double ResultKeepMs = 2000;       // applied results are kept this long (for aiming beams)
        private const float GunLookupSeconds = 2f;

        private const float MeleeLookaheadSeconds = 2.18f;  // KataConfig.secondsLookaheadMelee
        private const float MeleeShowClipTime = 0.55f;
        private const float MeleeSpawnerScale = 0.5f;
        private const float MeleeTelegraphScale = 0.75f;
        private const float MeleeX = 0.35f, MeleeLowY = 0.371f, MeleeHighY = 0.871f, MeleeZ = 0.75f;
        private const float MeleeLeftYaw = 141.38f, MeleeRightYaw = 218.62f;
        private const float DefaultHeightInches = 69f;
        private const float MetresPerInch = 0.0254f;

        private const float LineWidthStartTime = 1.47f, LineWidthMin = 0.08f, LineWidthMax = 0.18f;

        private const string LeftClip = "target_left";
        private const string RightClip = "target_right";
        private const string MeleeLeftClip = "target_left_melee_soccer";
        private const string MeleeRightClip = "target_right_melee_soccer";
        private const string TelegraphClip = "telegraph";
        private const string TimingName = "timing_shootable";
        private const string TrailsName = "trails";
        private const string CircleName = "circle_mesh";
        private const string CenterName = "center";
        private const string CloudPrefix = "cloud";
        private const string LineName = "simultaneous_line";
        private const string ChainLineName = "chain_line";
        private const string ChainArrowName = "ExChainArrow";
        private const float ChainNodeScale = 0.5f;      // Target.InitFromSpawner: a chain node is half size
        private const float ChainLineForward = 0.15f;   // both ends of a chain line are this far toward the player
        private const float ChainLineWidth = 0.35f;
        private const float ChainLineColor = 0.502f;    // the line's colour: the hand's times this (alpha too)
        private const float ChainTravelTicks = 240f;
        private const string MeleeLineName = "melee_connector";

        private const double JumpBackMs = 250;          // the position went back this much: start over
        private const double JumpAheadMs = 3000;        // or ahead this much in one frame
        private const float SameColor = 0.004f;

        // ── Templates ────────────────────────────────────────────────────────
        private class Template
        {
            public GameObject Target;
            public GameObject Telegraph;                // null when the prefab has none
            public Material Left, Right, Either;        // Target.leftHandMat / rightHandMat / eitherHandMat
        }

        private static GameObject templateRoot;
        private static readonly Dictionary<int, Template> templates = new Dictionary<int, Template>();
        private static bool templatesFailed;

        // ── Live targets ─────────────────────────────────────────────────────
        private class Live
        {
            public int Index;
            public SongCues.Cue Cue;
            public double CueMs, EndMs;
            public bool Melee;
            public float Speed, ShowTime;
            public GameObject Spawner;
            public Transform TargetTransform;
            public Animation TargetAnim;
            public AnimationState TargetState;
            public MeshRenderer Mesh;
            public Renderer Timing;
            public GameObject Telegraph;
            public Animation TelegraphAnim;
            public AnimationState TelegraphState;
            public float CompleteTime;      // the clip time it is gone by when nothing hit it
            public bool Done;               // hit (or a sustain finished): goes now
            public bool Holding;            // a sustain that was hit and is being held
            public int HoldHand;            // the hand holding it (Target.TargetHandType)
            public double HeldSinceMs;      // the song position it was hit at
            public bool Broken;             // a sustain that was let go
            public int Pair = -1;           // the cue landing with this one, or -1
            public double AnimMs;           // the song time its clip is at the hit pose: its cue's, a chain node's start's
            public int Head = -1;           // a chain node: its chain start's place in the chart
            public int Prev = -1;           // a chain node: the node (or start) before it
            public LineRenderer ChainLine;  // a chain node's line to the one before it
            public MaterialPropertyBlock ChainBlock;
            public LineRenderer Line;       // set on the later one of a pair only
            public MaterialPropertyBlock LineBlock;
            public Transform Holder;
        }

        private static GameObject root;
        private static Transform dome;
        private static Transform meleeRoot;
        private static readonly List<Live> live = new List<Live>();
        private static readonly Dictionary<int, Live> liveByIndex = new Dictionary<int, Live>();
        private static readonly Dictionary<string, Material> tinted = new Dictionary<string, Material>();
        private static readonly Dictionary<string, RenderTexture[]> cloudTextures = new Dictionary<string, RenderTexture[]>();

        // The chart the arrays below are for.
        private static SongList.SongData chartSong;
        private static KataConfig.Difficulty chartDifficulty;
        private static int chartCueCount;
        private static float chartTargetSpeed, chartMeleeSpeed, chartHeightInches;
        private static double[] cueMs;
        private static double[] endMs;
        private static int[] pairOf;
        private static int[] headOf;                   // a chain node's start (place in the chart), else -1
        private static int[] prevOf;                   // a chain node's previous node or start, else -1
        private static bool[] wasHit;                  // a hit result was applied for that cue
        private static float domeSpeed, meleeSpeed;   // clip seconds per song second
        private static double spawnAheadMs;            // how long before its cue a target is made
        private static int nextIndex;
        private static double lastNowMs;

        // Results (SpectatorEvents) for this chart.
        private class Result
        {
            public MpSpectateEvent E;
            public double Ms;               // E.tick on this client's copy of the song
            public bool Applied;
            public bool BeamUsed;
        }

        private static readonly List<Result> results = new List<Result>();
        private static readonly List<MpSpectateEvent> taken = new List<MpSpectateEvent>();
        // Where a cue is in this client's list, by its tick and pitch (results name their cue that way).
        private static readonly Dictionary<long, int> positionOfCue = new Dictionary<long, int>();
        private static int resultsWithoutCue, hitsApplied, missesApplied;
        private static bool loggedNoCue;
        private static bool hasResults;
        private static int resultsApplied;
        // The sustain beams, one per hand (0 = left, 1 = right).
        private class HoldBeam
        {
            public GameObject Root;
            public SustainBeam Beam;
            public string ColorKey;
        }

        private static readonly HoldBeam[] holdBeams = new HoldBeam[2];
        private static readonly SustainAudioTracker[] ownTrackers = new SustainAudioTracker[2];
        private static readonly bool[] trackerTried = new bool[2];
        private const int TrackerBufferLength = 15000, TrackerSampleDelay = 4320, TrackerTapLeft = 2, TrackerTapRight = 3;
        private static bool holdBeamFailed;

        private static bool useSample = true;
        private static bool lineWidthFailed;
        private static bool lineBlockFailed;
        private static bool chainLineFailed;
        private static bool cloudFailed;
        private static string lastError;
        private static int spawnedTotal;
        private static int skippedTotal;

        private static void Log(string message) => DebugLog.Log("[SpectatorTargets] " + message);

        // ── Tick ─────────────────────────────────────────────────────────────

        /// <summary>Once per frame from ExScoring.OnUpdate, after SpectatorSong.Tick.</summary>
        public static void Tick()
        {
            try
            {
                List<SongCues.Cue> cues = SpectatorSong.Cues;
                SongList.SongData song = SpectatorSong.Song;
                if (song == null || cues.Count == 0)
                {
                    Clear("no chart to show");
                    return;
                }

                Transform space = SpectatorWindow.ShownWatchedRoot;
                if (space == null || !SpectatorSong.TryGetPosition(out double nowMs, out double tick))
                    return; // between poses, or the avatar is being rebuilt: hold what is there

                if (!EnsureTemplates()) return;
                EnsureRoot();
                root.transform.position = space.position;
                root.transform.rotation = space.rotation;
                // The root's own scale is always 1: under the avatar (diorama) it takes the avatar's
                // scale, and on its own it is full size. A stray scale here shrank the targets and
                // moved them off the player after a switch.
                if (root.transform.localScale != Vector3.one) root.transform.localScale = Vector3.one;

                ReadSettings(out float targetSpeed, out float meleeSpeedSetting, out float heightInches);
                if (song != chartSong || SpectatorSong.Difficulty != chartDifficulty || cues.Count != chartCueCount
                    || Mathf.Abs(targetSpeed - chartTargetSpeed) > 0.001f || Mathf.Abs(meleeSpeedSetting - chartMeleeSpeed) > 0.001f
                    || Mathf.Abs(heightInches - chartHeightInches) > 0.01f)
                    SetUpChart(song, SpectatorSong.Difficulty, cues, targetSpeed, meleeSpeedSetting, heightInches);

                if (nowMs < lastNowMs - JumpBackMs || nowMs > lastNowMs + JumpAheadMs)
                {
                    // Restart, loop or seek: everything on screen belongs to another moment.
                    DestroyLive();
                    results.Clear();
                    SpectatorPopups.Clear();
                    if (wasHit != null) Array.Clear(wasHit, 0, wasHit.Length);
                    nextIndex = 0;
                }
                lastNowMs = nowMs;

                while (nextIndex < cueMs.Length && cueMs[nextIndex] - nowMs <= spawnAheadMs)
                {
                    // A chain's nodes are already there: made with their start.
                    if (endMs[nextIndex] > nowMs && !liveByIndex.ContainsKey(nextIndex)) Spawn(cues[nextIndex], nextIndex);
                    nextIndex++;
                }

                PullResults();
                ApplyResults(nowMs, tick);

                Transform head = SpectatorWindow.ShownWatchedHead;
                for (int i = live.Count - 1; i >= 0; i--)
                {
                    Live target = live[i];
                    if (target.Spawner == null || IsOver(target, nowMs))
                    {
                        if (target.Spawner != null) GameObject.Destroy(target.Spawner);
                        liveByIndex.Remove(target.Index);
                        live.RemoveAt(i);
                        continue;
                    }
                    UpdateTarget(target, nowMs, tick, head);
                }

                UpdateHoldBeams();

                // After every target is posed, so both ends of a line are this frame's.
                for (int i = 0; i < live.Count; i++)
                {
                    if (live[i].Line != null) UpdateLine(live[i], nowMs);
                    if (live[i].ChainLine != null) UpdateChainLine(live[i], nowMs, tick);
                }

                // The watched player's text popups, in the same space as the targets.
                SpectatorPopups.Update(nowMs, root != null ? root.transform : null);
            }
            catch (Exception ex)
            {
                // Logged once per distinct error: this runs every frame.
                if (ex.Message != lastError)
                {
                    lastError = ex.Message;
                    MelonLogger.Log("[SpectatorTargets] tick failed: " + ex);
                }
            }
        }

        /// <summary>The watched player's settings, each falling back to this player's own when they didn't say.</summary>
        private static void ReadSettings(out float targetSpeed, out float meleeSpeedSetting, out float heightInches)
        {
            targetSpeed = 1f;
            meleeSpeedSetting = 1f;
            heightInches = DefaultHeightInches;
            try
            {
                PlayerPreferences prefs = PlayerPreferences.I;
                if (prefs != null)
                {
                    targetSpeed = prefs.TargetSpeedMultiplier.mVal;
                    meleeSpeedSetting = prefs.MeleeSpeedMultiplier.mVal;
                    heightInches = prefs.PlayerHeightInches.mVal;
                }
            }
            catch
            {
                // keep the defaults
            }

            MpSpectateSettings settings = SpectatorSong.Settings;
            if (settings != null)
            {
                if (settings.targetSpeed.HasValue) targetSpeed = settings.targetSpeed.Value;
                if (settings.meleeSpeed.HasValue) meleeSpeedSetting = settings.meleeSpeed.Value;
                if (settings.playerHeight.HasValue) heightInches = settings.playerHeight.Value;
            }

            if (targetSpeed < 0.05f) targetSpeed = 1f;
            if (meleeSpeedSetting < 0.05f) meleeSpeedSetting = 1f;
            if (heightInches < 20f || heightInches > 120f) heightInches = DefaultHeightInches;
        }

        private static float LookaheadSeconds(KataConfig.Difficulty difficulty)
        {
            switch (difficulty)
            {
                case KataConfig.Difficulty.Easy: return 3.1f;
                case KataConfig.Difficulty.Normal: return 2.5f;
                case KataConfig.Difficulty.Hard: return 2.18f;
                default: return 1.9f;
            }
        }

        private static bool IsMelee(SongCues.Cue cue) => cue.behavior == Target.TargetBehavior.Melee;

        private static void SetUpChart(SongList.SongData song, KataConfig.Difficulty difficulty, List<SongCues.Cue> cues,
            float targetSpeed, float meleeSpeedSetting, float heightInches)
        {
            DestroyLive();
            chartSong = song;
            chartDifficulty = difficulty;
            chartCueCount = cues.Count;
            chartTargetSpeed = targetSpeed;
            chartMeleeSpeed = meleeSpeedSetting;
            chartHeightInches = heightInches;

            cueMs = new double[cues.Count];
            endMs = new double[cues.Count];
            pairOf = new int[cues.Count];
            headOf = new int[cues.Count];
            prevOf = new int[cues.Count];
            wasHit = new bool[cues.Count];
            int[] lastOfHand = { -1, -1, -1, -1 };  // by Target.TargetHandType: the last chain cue seen
            int[] headOfHand = { -1, -1, -1, -1 };
            positionOfCue.Clear();
            results.Clear();
            hasResults = false;
            resultsApplied = 0;
            resultsWithoutCue = 0;
            hitsApplied = 0;
            missesApplied = 0;
            loggedNoCue = false;
            for (int i = 0; i < cues.Count; i++)
            {
                SongCues.Cue cue = cues[i];
                cueMs[i] = AudioDriver.TickSpanToMs(song, 0f, cue.tick);
                // A sustain stays for its length; everything else goes at its cue's time.
                endMs[i] = cue.behavior == Target.TargetBehavior.Hold
                    ? AudioDriver.TickSpanToMs(song, 0f, cue.tick + cue.tickLength)
                    : cueMs[i];
                pairOf[i] = -1;
                positionOfCue[CueKey(cue.tick, cue.pitch)] = i;

                // Chains, by hand and order (cues are in time order): a start opens one, every
                // node after it of that hand belongs to it and follows the one before.
                headOf[i] = -1;
                prevOf[i] = -1;
                int handSlot = (int)cue.handType & 3;
                if (cue.behavior == Target.TargetBehavior.ChainStart)
                {
                    headOfHand[handSlot] = i;
                    lastOfHand[handSlot] = i;
                }
                else if (cue.behavior == Target.TargetBehavior.Chain && headOfHand[handSlot] >= 0)
                {
                    headOf[i] = headOfHand[handSlot];
                    prevOf[i] = lastOfHand[handSlot];
                    lastOfHand[handSlot] = i;
                }
            }

            // Pairs: the next cue on the same tick that can pair with this one.
            int pairs = 0;
            for (int i = 0; i < cues.Count; i++)
            {
                if (pairOf[i] >= 0) continue;
                for (int j = i + 1; j < cues.Count && cues[j].tick == cues[i].tick; j++)
                {
                    if (pairOf[j] >= 0 || !CanPair(cues[i], cues[j])) continue;
                    pairOf[i] = j;
                    pairOf[j] = i;
                    pairs++;
                    break;
                }
            }

            float domeLookahead = LookaheadSeconds(difficulty) / targetSpeed;
            float meleeLookahead = MeleeLookaheadSeconds / (targetSpeed * meleeSpeedSetting * meleeSpeedSetting);
            domeSpeed = HitClipTime / domeLookahead;
            meleeSpeed = HitClipTime / meleeLookahead;
            spawnAheadMs = Math.Max(domeLookahead, meleeLookahead) * 1000.0;

            if (meleeRoot != null) meleeRoot.localPosition = new Vector3(0f, heightInches * MetresPerInch, 0f);

            nextIndex = 0;
            lastNowMs = double.MinValue;
            spawnedTotal = 0;
            skippedTotal = 0;
            Log($"chart set up: {song.songID} [{difficulty}] {cues.Count} cues, {pairs} pairs, target speed {targetSpeed:0.##} (lookahead {domeLookahead:0.00} s, clip speed {domeSpeed:0.000}), " +
                $"melee speed {meleeSpeedSetting:0.##} (lookahead {meleeLookahead:0.00} s, clip speed {meleeSpeed:0.000}), height {heightInches:0.#} in");
        }

        /// <summary>Target.InitFromSpawner's rules for a simultaneous pair.</summary>
        private static bool CanPair(SongCues.Cue a, SongCues.Cue b)
        {
            if (a.behavior == Target.TargetBehavior.Chain || b.behavior == Target.TargetBehavior.Chain) return false;
            if (IsMelee(a) != IsMelee(b)) return false;
            if ((a.behavior == Target.TargetBehavior.Dodge) != (b.behavior == Target.TargetBehavior.Dodge)) return false;

            Target.TargetHandType ha = a.handType, hb = b.handType;
            if (ha == Target.TargetHandType.Left) return hb == Target.TargetHandType.Right;
            if (ha == Target.TargetHandType.Right) return hb == Target.TargetHandType.Left;
            return hb == ha; // either with either
        }

        // ── Templates ────────────────────────────────────────────────────────

        private static bool EnsureTemplates()
        {
            if (templatesFailed) return false;
            if (templateRoot != null && templates.Count > 0) return true; // Unity-null: rebuilt if destroyed

            KataConfig config = KataConfig.I;
            if (config == null) return false;

            try
            {
                templates.Clear();
                if (templateRoot != null) GameObject.Destroy(templateRoot);
                templateRoot = new GameObject("ExScoring_SpectatorTargetTemplates");
                // Inactive before anything is copied in: the copies' Target / Telegraph never wake up.
                templateRoot.SetActive(false);
                GameObject.DontDestroyOnLoad(templateRoot);

                AddTemplate(Target.TargetBehavior.Standard, config.targetStandard != null ? config.targetStandard.gameObject : null);
                AddTemplate(Target.TargetBehavior.Vertical, config.targetVertical != null ? config.targetVertical.gameObject : null);
                AddTemplate(Target.TargetBehavior.Horizontal, config.targetHorizontal != null ? config.targetHorizontal.gameObject : null);
                AddTemplate(Target.TargetBehavior.Hold, config.targetSustain != null ? config.targetSustain.gameObject : null);
                AddTemplate(Target.TargetBehavior.Melee, config.targetMelee != null ? config.targetMelee.gameObject : null);
                AddTemplate(Target.TargetBehavior.ChainStart, config.targetChainStart != null ? config.targetChainStart.gameObject : null);
                AddTemplate(Target.TargetBehavior.Chain, config.targetChainNode != null ? config.targetChainNode.gameObject : null);

                if (templates.Count == 0)
                {
                    templatesFailed = true;
                    MelonLogger.Log("[SpectatorTargets] no target prefab could be copied: no targets in the spectator window");
                    return false;
                }
                Log($"templates built: {templates.Count} target types");
                return true;
            }
            catch (Exception ex)
            {
                templatesFailed = true;
                MelonLogger.Log("[SpectatorTargets] building the templates failed: " + ex);
                return false;
            }
        }

        private static void AddTemplate(Target.TargetBehavior behavior, GameObject prefab)
        {
            if (prefab == null)
            {
                Log($"no prefab for {behavior}");
                return;
            }

            Target prefabTarget = prefab.GetComponent<Target>();
            Template template = new Template();
            if (prefabTarget != null)
            {
                template.Left = prefabTarget.leftHandMat;
                template.Right = prefabTarget.rightHandMat;
                template.Either = prefabTarget.eitherHandMat;
            }

            template.Target = GameObject.Instantiate(prefab, templateRoot.transform);
            template.Target.name = "Tpl_" + behavior;
            Strip(template.Target);
            Transform trails = template.Target.transform.Find(TrailsName);
            if (trails != null) trails.gameObject.SetActive(false); // the sustain's hold particles
            Transform arrow = template.Target.transform.Find(ChainArrowName);
            if (arrow != null) arrow.gameObject.SetActive(false);   // this mod's own chain arrow, if the prefab carries one

            Telegraph telegraphPrefab = prefabTarget != null ? prefabTarget.telegraphPrefab : null;
            if (telegraphPrefab != null)
            {
                template.Telegraph = GameObject.Instantiate(telegraphPrefab.gameObject, templateRoot.transform);
                template.Telegraph.name = "Tpl_" + behavior + "_Telegraph";
                Strip(template.Telegraph);
            }

            templates[(int)behavior] = template;
            Log($"template {behavior}: materials L={Name(template.Left)} R={Name(template.Right)} E={Name(template.Either)} telegraph={(template.Telegraph != null ? telegraphPrefab.name : "none")}");
        }

        private static string Name(Material material) => material != null ? material.name : "null";

        /// <summary>
        /// Makes a prefab copy inert, while it is still inactive: no Target / Telegraph logic, no
        /// colliders, no clip starting by itself, everything on the song-only layer.
        /// </summary>
        private static void Strip(GameObject copy)
        {
            var targets = copy.GetComponentsInChildren<Target>(true);
            for (int i = 0; i < targets.Length; i++)
                if (targets[i] != null) UnityEngine.Object.DestroyImmediate(targets[i]);

            var telegraphs = copy.GetComponentsInChildren<Telegraph>(true);
            for (int i = 0; i < telegraphs.Length; i++)
                if (telegraphs[i] != null) UnityEngine.Object.DestroyImmediate(telegraphs[i]);

            var colliders = copy.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
                if (colliders[i] != null) colliders[i].enabled = false;

            var animations = copy.GetComponentsInChildren<Animation>(true);
            for (int i = 0; i < animations.Length; i++)
                if (animations[i] != null) animations[i].playAutomatically = false;

            SetLayerRecursive(copy.transform, MpPresence.SongOnlyLayer);
        }

        private static void SetLayerRecursive(Transform t, int layer)
        {
            t.gameObject.layer = layer;
            for (int i = 0; i < t.childCount; i++) SetLayerRecursive(t.GetChild(i), layer);
        }

        // ── Spawning ─────────────────────────────────────────────────────────

        private static void EnsureRoot()
        {
            if (root != null && dome != null && meleeRoot != null) return; // Unity-null: rebuilt if destroyed
            DestroyLive();
            if (root != null) GameObject.Destroy(root);

            root = new GameObject("ExScoring_SpectatorTargets");
            root.layer = MpPresence.SongOnlyLayer;
            GameObject.DontDestroyOnLoad(root);

            dome = NewChild("SpawnerDome", root.transform);
            dome.localPosition = DomePosition;

            meleeRoot = NewChild("MeleeHeightAdjust", root.transform);
            meleeRoot.localPosition = new Vector3(0f, (chartHeightInches > 0f ? chartHeightInches : DefaultHeightInches) * MetresPerInch, 0f);
        }

        private static Transform NewChild(string name, Transform parent)
        {
            GameObject child = new GameObject(name);
            child.layer = MpPresence.SongOnlyLayer;
            child.transform.SetParent(parent, false);
            return child.transform;
        }

        private static void Spawn(SongCues.Cue cue, int index)
        {
            if (cue == null || !templates.TryGetValue((int)cue.behavior, out Template template))
            {
                skippedTotal++; // dodge: not drawn yet
                return;
            }

            bool melee = IsMelee(cue);
            Live target = new Live
            {
                Index = index,
                Cue = cue,
                CueMs = cueMs[index],
                EndMs = endMs[index],
                AnimMs = headOf[index] >= 0 ? cueMs[headOf[index]] : cueMs[index],
                Head = headOf[index],
                Prev = prevOf[index],
                Melee = melee,
                Speed = melee ? meleeSpeed : domeSpeed,
                ShowTime = melee ? MeleeShowClipTime : ShowClipTime,
                CompleteTime = melee ? MeleeCompleteClipTime : CompleteClipTime,
                Pair = pairOf[index]
            };

            // Spawner and holder (TargetSpawner.SpawnCue).
            Transform holder;
            bool leftSide;
            if (melee)
            {
                // 98 low left, 99 low right, 100 high left, 101 high right.
                leftSide = cue.pitch % 2 == 0;
                bool high = cue.pitch >= 100;
                Transform spawner = NewChild("Cue " + index + " Melee " + cue.pitch, meleeRoot);
                spawner.localPosition = new Vector3(leftSide ? -MeleeX : MeleeX, high ? MeleeHighY : MeleeLowY, MeleeZ);
                spawner.localRotation = Quaternion.Euler(0f, leftSide ? MeleeLeftYaw : MeleeRightYaw, 0f);
                spawner.localScale = new Vector3(MeleeSpawnerScale, MeleeSpawnerScale, MeleeSpawnerScale);
                target.Spawner = spawner.gameObject;

                holder = NewChild("targetholder", spawner);
                holder.localPosition = new Vector3(2f * cue.gridOffset.x, 2f * cue.gridOffset.y, cue.zOffset);
            }
            else
            {
                leftSide = cue.handType == Target.TargetHandType.Left;
                float col = cue.pitch % 12 + cue.gridOffset.x;
                float row = cue.pitch / 12 + cue.gridOffset.y;
                Quaternion rotation = Quaternion.Euler(RowBaseDegrees + RowStepDegrees * row, ColBaseDegrees + ColStepDegrees * col, 0f);

                Transform spawner = NewChild("Cue " + index + " " + cue.behavior + " " + cue.handType, dome);
                spawner.localRotation = rotation;
                spawner.localPosition = rotation * Vector3.back * DomeRadius * (1f + cue.zOffset);
                target.Spawner = spawner.gameObject;

                holder = NewChild("targetholder", spawner);
                holder.localScale = new Vector3(HolderScale, HolderScale, HolderScale);
            }

            target.Holder = holder;
            MpPresence.GetGunColors(SpectatorSong.WatchedUserId, out Color leftColor, out Color rightColor);
            Color color = HandColor(cue.handType, leftColor, rightColor);

            // The target.
            GameObject targetObject = GameObject.Instantiate(template.Target, holder);
            targetObject.name = template.Target.name.Substring(4);
            targetObject.transform.localRotation = Quaternion.identity;
            bool chainNode = cue.behavior == Target.TargetBehavior.Chain;
            targetObject.transform.localScale = chainNode ? new Vector3(ChainNodeScale, ChainNodeScale, ChainNodeScale) : Vector3.one;
            target.TargetTransform = targetObject.transform;

            target.Mesh = targetObject.GetComponent<MeshRenderer>();
            if (target.Mesh != null)
            {
                Material source = cue.handType == Target.TargetHandType.Left ? template.Left
                    : cue.handType == Target.TargetHandType.Right ? template.Right : template.Either;
                if (source == null) source = target.Mesh.sharedMaterial;
                if (source != null)
                {
                    // An either-hand melee ball keeps its own grey material.
                    bool keep = melee && cue.handType != Target.TargetHandType.Left && cue.handType != Target.TargetHandType.Right;
                    target.Mesh.sharedMaterial = keep ? source : Tinted(source, color);
                }
                target.Mesh.enabled = false; // shown from ShowTime
            }
            Transform timing = targetObject.transform.Find(TimingName);
            target.Timing = timing != null ? timing.GetComponent<Renderer>() : null;
            if (target.Timing != null) target.Timing.enabled = false;

            target.TargetAnim = targetObject.GetComponent<Animation>();
            target.TargetState = StateOf(target.TargetAnim, melee ? (leftSide ? MeleeLeftClip : MeleeRightClip) : (leftSide ? LeftClip : RightClip));

            // Its telegraph: beside the target on the holder, or on the melee ball itself.
            if (template.Telegraph != null && !chainNode && target.CueMs > lastNowMs)
            {
                target.Telegraph = GameObject.Instantiate(template.Telegraph, melee ? targetObject.transform : holder);
                target.Telegraph.name = template.Telegraph.name.Substring(4);
                target.Telegraph.transform.localPosition = Vector3.zero;
                target.Telegraph.transform.localRotation = Quaternion.identity;
                float scale = melee ? MeleeTelegraphScale : TelegraphScale;
                target.Telegraph.transform.localScale = new Vector3(scale, scale, scale);
                ColorTelegraph(target.Telegraph.transform, cue, color);

                target.TelegraphAnim = target.Telegraph.GetComponent<Animation>();
                target.TelegraphState = StateOf(target.TelegraphAnim, TelegraphClip);
            }

            // The line to the target landing with it: on the later one of the two.
            if (target.Pair >= 0 && target.Pair < index)
            {
                Transform line = targetObject.transform.Find(melee ? MeleeLineName : LineName);
                target.Line = line != null ? line.GetComponent<LineRenderer>() : null;
                if (target.Line != null && !lineBlockFailed)
                {
                    try
                    {
                        // Target.InitFromSpawner: this hand's colour and the other hand's.
                        Target.TargetHandType otherHand = cue.handType == Target.TargetHandType.Left ? Target.TargetHandType.Right
                            : cue.handType == Target.TargetHandType.Right ? Target.TargetHandType.Left : cue.handType;
                        target.LineBlock = new MaterialPropertyBlock();
                        target.LineBlock.SetColor("_ColorA", color);
                        target.LineBlock.SetColor("_ColorB", HandColor(otherHand, leftColor, rightColor));
                        target.Line.SetPropertyBlock(target.LineBlock);
                    }
                    catch (Exception ex)
                    {
                        lineBlockFailed = true;
                        target.LineBlock = null;
                        Log("line colours can't be set (" + ex.Message + "): the lines keep the material's own");
                    }
                }
            }

            // A chain node's line to the one before it; on any other target that has one, off.
            Transform chainLine = targetObject.transform.Find(ChainLineName);
            if (chainLine != null)
            {
                LineRenderer renderer = chainNode && target.Prev >= 0 && !chainLineFailed ? chainLine.GetComponent<LineRenderer>() : null;
                if (renderer == null) chainLine.gameObject.SetActive(false);
                else
                {
                    try
                    {
                        Color lineColor = color * ChainLineColor;
                        renderer.useWorldSpace = true;
                        renderer.positionCount = 2;
                        renderer.widthMultiplier = ChainLineWidth;
                        renderer.startColor = lineColor;
                        renderer.endColor = lineColor;
                        target.ChainBlock = new MaterialPropertyBlock();
                        target.ChainLine = renderer;
                        chainLine.gameObject.SetActive(false); // shown by UpdateChainLine
                    }
                    catch (Exception ex)
                    {
                        chainLineFailed = true;
                        target.ChainLine = null;
                        chainLine.gameObject.SetActive(false);
                        Log("chain lines can't be set up (" + ex.Message + "): chains are drawn without them");
                    }
                }
            }

            if (!targetObject.activeSelf) targetObject.SetActive(true);
            target.Spawner.SetActive(false); // until its own lookahead is reached (UpdateTarget)
            live.Add(target);
            liveByIndex[index] = target;
            spawnedTotal++;

            // TargetSpawner.SpawnCue: a chain's nodes are made with its start.
            if (cue.behavior == Target.TargetBehavior.ChainStart)
            {
                List<SongCues.Cue> cues = SpectatorSong.Cues;
                for (int j = index + 1; j < cues.Count && j < headOf.Length && cueMs[j] - cueMs[index] < ChainMaxMs; j++)
                {
                    if (headOf[j] == index && endMs[j] > lastNowMs && !liveByIndex.ContainsKey(j)) Spawn(cues[j], j);
                }
            }
        }

        private static Color HandColor(Target.TargetHandType hand, Color left, Color right)
        {
            if (hand == Target.TargetHandType.Left) return left;
            if (hand == Target.TargetHandType.Right) return right;
            try
            {
                KataConfig config = KataConfig.I;
                if (config != null) return config.eitherHandColor;
            }
            catch
            {
                // fall through
            }
            return Color.white;
        }

        /// <summary>The clip's state on this Animation, or its default clip's when that name isn't there.</summary>
        private static AnimationState StateOf(Animation animation, string clipName)
        {
            if (animation == null) return null;
            animation.Stop();
            AnimationState state = animation[clipName];
            if (state == null && animation.clip != null) state = animation[animation.clip.name];
            return state;
        }

        private static string ColorKey(Color color)
        {
            return color.r.ToString("F3") + "," + color.g.ToString("F3") + "," + color.b.ToString("F3");
        }

        /// <summary>A copy of source with _Color set, shared by every target of that colour.</summary>
        private static Material Tinted(Material source, Color color)
        {
            string key = source.GetInstanceID() + "|" + ColorKey(color);
            if (tinted.TryGetValue(key, out Material material) && material != null) return material;

            material = new Material(source);
            material.name = source.name + " (spectator)";
            material.hideFlags = HideFlags.DontUnloadUnusedAsset;
            material.SetColor("_Color", color);
            tinted[key] = material;
            return material;
        }

        /// <summary>The circle and centre take the hand's colour; the cloud takes that hand's hue-shifted material.</summary>
        private static void ColorTelegraph(Transform telegraph, SongCues.Cue cue, Color color)
        {
            var renderers = telegraph.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || renderer.sharedMaterial == null) continue;
                string name = renderer.gameObject.name;

                if (name == CircleName || name == CenterName)
                {
                    renderer.sharedMaterial = Tinted(renderer.sharedMaterial, color);
                }
                else if (name.StartsWith(CloudPrefix))
                {
                    Material cloud = CloudMaterial(cue.behavior, cue.handType, color);
                    if (cloud != null) renderer.sharedMaterial = cloud;
                }
            }
        }

        /// <summary>
        /// The telegraph cloud's material for a target type and hand: TargetColorSetter's own
        /// (in this player's colours), or a copy with a texture shifted to the watched player's
        /// colour when that differs. Null when it can't be had (the prefab's stays).
        /// </summary>
        private static Material CloudMaterial(Target.TargetBehavior behavior, Target.TargetHandType hand, Color color)
        {
            if (cloudFailed) return null;
            try
            {
                TargetColorSetter setter = TargetColorSetter.I;
                KataConfig config = KataConfig.I;
                if (setter == null) return null;

                bool left = hand == Target.TargetHandType.Left;
                bool right = hand == Target.TargetHandType.Right;
                Material source;
                if (behavior == Target.TargetBehavior.Hold || behavior == Target.TargetBehavior.ChainStart)
                    source = left ? setter.telegraphSustainLeftMat : right ? setter.telegraphSustainRightMat : setter.telegraphSustainEitherMat;
                else if (behavior == Target.TargetBehavior.Horizontal || behavior == Target.TargetBehavior.Vertical)
                    source = left ? setter.telegraphSlotLeftMat : right ? setter.telegraphSlotRightMat : setter.telegraphSlotEitherMat;
                else
                    source = left ? setter.telegraphLeftMat : right ? setter.telegraphRightMat : setter.telegraphEitherMat;
                if (source == null) return null;

                // An either-hand target: the native material as is.
                if (!left && !right) return source;

                string key = source.GetInstanceID() + "|cloud|" + ColorKey(color);
                if (tinted.TryGetValue(key, out Material material) && material != null) return material;

                // The material itself is red: the hand's colour is in the texture the game puts on
                // each telegraph's own copy (TargetColorSetter.GetTelegraphTexture). This player's
                // own colour for that hand: the game's texture; another colour: one shifted to it.
                bool sustain = behavior == Target.TargetBehavior.Hold || behavior == Target.TargetBehavior.ChainStart;
                Texture texture = null;
                Color own = config != null ? (left ? config.leftHandColor : config.rightHandColor) : Color.clear;
                bool ownColor = config != null && Mathf.Abs(own.r - color.r) < SameColor && Mathf.Abs(own.g - color.g) < SameColor
                    && Mathf.Abs(own.b - color.b) < SameColor;
                if (!ownColor)
                {
                    RenderTexture[] textures = CloudTextures(setter, color);
                    if (textures != null) texture = sustain ? textures[1] : textures[0];
                }
                if (texture == null) texture = setter.GetTelegraphTexture(hand, sustain);
                if (texture == null) return source;

                material = new Material(source);
                material.name = source.name + " (spectator)";
                material.hideFlags = HideFlags.DontUnloadUnusedAsset;
                material.mainTexture = texture;
                tinted[key] = material;
                Log($"cloud material {source.name} for {hand}: texture {(ownColor ? "the game's own" : "shifted to " + ColorKey(color))}");
                return material;
            }
            catch (Exception ex)
            {
                cloudFailed = true;
                Log("telegraph cloud materials failed (the prefab's stay): " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// [normal, sustain] telegraph textures in one colour, made the way TargetColorSetter makes
        /// its own: a copy of its texture, repainted from the red source with ShiftTextureColors.
        /// </summary>
        private static RenderTexture[] CloudTextures(TargetColorSetter setter, Color color)
        {
            string key = ColorKey(color);
            if (cloudTextures.TryGetValue(key, out RenderTexture[] textures) && textures[0] != null && textures[1] != null) return textures;

            RenderTexture normalSource = setter.mTelegraphRight != null ? setter.mTelegraphRight : setter.mTelegraphLeft;
            RenderTexture sustainSource = setter.mTelegraphSustainRight != null ? setter.mTelegraphSustainRight : setter.mTelegraphSustainLeft;
            if (normalSource == null || sustainSource == null) return null;

            Texture2D red = TargetColorSetter.ShouldUseBlueTelegraphSource(color) && setter.telegraphRedForBlue != null
                ? setter.telegraphRedForBlue : setter.telegraphRed;
            if (red == null || setter.telegraphSustainRed == null) return null;

            RenderTexture normal = UnityEngine.Object.Instantiate<RenderTexture>(normalSource);
            normal.name = "SpectatorTelegraph " + key;
            normal.hideFlags = HideFlags.DontUnloadUnusedAsset;
            setter.ShiftTextureColors(color, red, normal);

            RenderTexture sustain = UnityEngine.Object.Instantiate<RenderTexture>(sustainSource);
            sustain.name = "SpectatorTelegraphSustain " + key;
            sustain.hideFlags = HideFlags.DontUnloadUnusedAsset;
            setter.ShiftTextureColors(color, setter.telegraphSustainRed, sustain);

            textures = new[] { normal, sustain };
            cloudTextures[key] = textures;
            Log("telegraph textures made for colour " + key);
            return textures;
        }

        // ── Results ──────────────────────────────────────────────────────────

        /// <summary>Takes what SpectatorEvents received for the watched player since last time.</summary>
        private static void PullResults()
        {
            SpectatorEvents.EnsureInit();
            if (chartSong == null) return;
            taken.Clear();
            if (SpectatorEvents.TakeFor(SpectatorSong.WatchedUserId, taken) == 0) return;
            bool anyResult = false;

            for (int i = 0; i < taken.Count; i++)
            {
                MpSpectateEvent e = taken[i];
                if (double.IsNaN(e.tick) || double.IsInfinity(e.tick) || e.tick < 0) continue;
                double eventMs = AudioDriver.TickSpanToMs(chartSong, 0f, (float)e.tick);
                if (e.k == SpectatorEvents.Popup)
                {
                    SpectatorPopups.Add(e, eventMs); // a text popup: not a target's result
                    continue;
                }
                results.Add(new Result { E = e, Ms = eventMs });
                anyResult = true;
                if (e.k == SpectatorEvents.Hit) TellSounds(e);
            }
            if (!hasResults && anyResult)
            {
                hasResults = true;
                Log("results are coming in: targets now follow the watched player's hits and misses");
            }
        }

        /// <summary>
        /// A hit, as soon as it is received (ahead of the window's song position by the render
        /// delay): SpectatorSounds makes the cue's note audible, from its start when the cue's
        /// time hasn't come yet. The sound is the map's, on the cue's time; the target itself
        /// goes when the result is applied.
        /// </summary>
        private static void TellSounds(MpSpectateEvent e)
        {
            try
            {
                int place = -1;
                if (positionOfCue.TryGetValue(CueKey(e.ct, e.cp), out int position)) place = position;
                else if (e.ct == 0 && e.cp == 0 && e.i >= 0 && e.i < chartCueCount) place = e.i;
                if (place < 0) return;

                liveByIndex.TryGetValue(place, out Live target);
                List<SongCues.Cue> cues = SpectatorSong.Cues;
                SongCues.Cue hitCue = target != null ? target.Cue : (place < cues.Count ? cues[place] : null);
                bool paired = pairOf != null && place < pairOf.Length && pairOf[place] >= 0;
                bool placed = target != null && target.Holder != null;
                SpectatorSounds.HitKnown(hitCue, placed, placed ? target.Holder.position : Vector3.zero, paired);
            }
            catch (Exception ex)
            {
                Log("telling the sounds about a hit failed: " + ex.Message);
            }
        }

        /// <summary>Applies every result whose tick the song position has reached, and drops old ones.</summary>
        private static void ApplyResults(double nowMs, double tick)
        {
            for (int i = results.Count - 1; i >= 0; i--)
            {
                Result result = results[i];
                if (!result.Applied && result.E.tick <= tick)
                {
                    result.Applied = true;
                    resultsApplied++;
                    Apply(result.E);
                }
                if (result.Applied && result.Ms < nowMs - ResultKeepMs) results.RemoveAt(i);
            }
        }

        private static void Apply(MpSpectateEvent e)
        {
            Live target = null;
            int place = -1; // the cue's place in this client's chart
            if (positionOfCue.TryGetValue(CueKey(e.ct, e.cp), out int position))
            {
                place = position;
            }
            else if (e.ct == 0 && e.cp == 0 && e.i >= 0 && e.i < chartCueCount)
            {
                // A recording from before results carried the cue's tick and pitch: the game's
                // index, taken as the cue's place in the chart.
                place = e.i;
            }
            if (place >= 0)
            {
                liveByIndex.TryGetValue(place, out target);
            }
            else
            {
                resultsWithoutCue++;
                if (!loggedNoCue)
                {
                    loggedNoCue = true;
                    Log($"a result names a cue this chart doesn't have (tick {e.ct}, pitch {e.cp}, {e.k}): another version of the map? (logged once)");
                }
            }
            if (e.k == SpectatorEvents.Hit)
            {
                hitsApplied++;
                if (place >= 0 && wasHit != null && place < wasHit.Length) wasHit[place] = true;
            }
            else if (e.k == SpectatorEvents.Miss)
            {
                missesApplied++;
                // Target.Update: when a chain breaks after its start was hit, the nodes left go at once.
                if (target != null && target.Head >= 0 && wasHit != null && wasHit[target.Head]) target.Done = true;
            }

            switch (e.k)
            {
                case SpectatorEvents.Hit:
                    // The target's sound: SpectatorSounds, told when the result was received (TellSounds).
                    if (target != null)
                    {
                        if (target.Cue.behavior == Target.TargetBehavior.Hold)
                        {
                            target.Holding = true;
                            target.HoldHand = e.hand;
                            target.HeldSinceMs = lastNowMs;
                        }
                        else target.Done = true;
                    }
                    break;

                case SpectatorEvents.HoldDone:
                    if (target != null) target.Done = true;
                    break;

                case SpectatorEvents.HoldBroken:
                    if (target != null && !target.Broken)
                    {
                        target.Holding = false;
                        target.Broken = true;
                        // Target.Update: a failed sustain's colour goes halfway to black.
                        if (target.Mesh != null && target.Mesh.sharedMaterial != null)
                        {
                            Material current = target.Mesh.sharedMaterial;
                            Color dark = Color.Lerp(current.GetColor("_Color"), Color.black, 0.5f);
                            target.Mesh.sharedMaterial = Tinted(current, dark);
                        }
                    }
                    break;

                    // SpectatorEvents.Miss: the target flies on past its spot (a broken chain's node: gone, above).
            }
        }

        private static long CueKey(int tick, int pitch) => (long)tick * 1000 + pitch;

        /// <summary>The watched player is holding a sustain with that hand (0 = left, 1 = right) right now. For SpectatorSounds.</summary>
        internal static bool IsHolding(int hand)
        {
            int handType = hand == 0 ? (int)Target.TargetHandType.Left : (int)Target.TargetHandType.Right;
            for (int i = 0; i < live.Count; i++)
            {
                if (live[i].Holding && live[i].HoldHand == handType) return true;
            }
            return false;
        }

        /// <summary>A chain node whose start was hit, from the start's time on: it sits on its spot.</summary>
        private static bool Pinned(Live target, double nowMs)
        {
            return target.Head >= 0 && hasResults && wasHit != null && wasHit[target.Head] && nowMs >= target.AnimMs;
        }

        /// <summary>Whether a target is to be removed now.</summary>
        private static bool IsOver(Live target, double nowMs)
        {
            if (target.Done) return true;
            if (!hasResults) return nowMs >= target.EndMs;                       // no results at all: at its time
            if (target.Broken) return nowMs >= target.EndMs;                     // a let-go sustain: at its end
            if (target.Holding) return nowMs >= target.EndMs + HoldGraceMs;      // its end result never came
            if (Pinned(target, nowMs)) return nowMs >= target.CueMs + HoldGraceMs; // a chain node whose result never came
            float clipTime = HitClipTime - (float)((target.AnimMs - nowMs) / 1000.0) * target.Speed;
            return clipTime >= target.CompleteTime;                              // went by without being hit
        }

        /// <summary>
        /// For MpPresence, when it draws a watched in-song player's shot: where that shot hit a
        /// target, in world space, when one of their recent or imminent hit results is by that
        /// hand (0 = left, 1 = right) and not used for a beam yet. False = it hit nothing known.
        /// </summary>
        internal static bool TryGetShotAim(string userId, int hand, out Vector3 point)
        {
            point = Vector3.zero;
            try
            {
                if (userId == null || userId != SpectatorSong.WatchedUserId || root == null || chartSong == null) return false;
                PullResults();

                int wantedHand = hand == 0 ? (int)Target.TargetHandType.Left : (int)Target.TargetHandType.Right;
                Result best = null;
                double bestGap = AimWindowMs;
                for (int i = 0; i < results.Count; i++)
                {
                    Result result = results[i];
                    MpSpectateEvent e = result.E;
                    if (result.BeamUsed || e.k != SpectatorEvents.Hit || !e.shot || e.hand != wantedHand || e.p == null || e.p.Length != 3) continue;
                    double gap = Math.Abs(result.Ms - lastNowMs);
                    if (gap <= bestGap) { best = result; bestGap = gap; }
                }
                if (best == null) return false;

                best.BeamUsed = true;
                point = root.transform.TransformPoint(new Vector3(best.E.p[0], best.E.p[1], best.E.p[2]));
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ── Sustain beam ─────────────────────────────────────────────────────

        /// <summary>Each hand's beam runs from the watched player's gun to the sustain that hand holds, else it is hidden.</summary>
        private static void UpdateHoldBeams()
        {
            if (holdBeamFailed) return;
            try
            {
                for (int h = 0; h < 2; h++)
                {
                    int handType = h == 0 ? (int)Target.TargetHandType.Left : (int)Target.TargetHandType.Right;
                    Live held = null;
                    for (int i = 0; i < live.Count; i++)
                    {
                        if (live[i].Holding && live[i].HoldHand == handType && live[i].TargetTransform != null) { held = live[i]; break; }
                    }

                    HoldBeam beam = holdBeams[h];
                    bool show = held != null
                        && MpPresence.TryGetRemoteFirepoint(SpectatorSong.WatchedUserId, h, out Vector3 firepoint, out Vector3 forward);
                    if (!show)
                    {
                        if (beam != null && beam.Root != null && beam.Root.activeSelf) beam.Root.SetActive(false);
                        continue;
                    }

                    MpPresence.TryGetRemoteFirepoint(SpectatorSong.WatchedUserId, h, out Vector3 from, out Vector3 aim);
                    beam = EnsureHoldBeam(h, handType);
                    if (beam == null) continue;

                    // SustainBeam.Update's four points.
                    Vector3 to = held.TargetTransform.position;
                    SustainBeam native = beam.Beam;
                    if (native.start != null) native.start.position = from;
                    // 2 m ahead of the gun, natively; shrunk with a spectator diorama, or the curve
                    // overshoots the (nearer) target and folds back to it.
                    float ahead = 2f * SpectatorDiorama.ScaleOf(SpectatorWindow.ShownWatchedRoot);
                    if (native.startForward != null) native.startForward.position = from + aim * ahead;
                    if (native.middle != null) native.middle.position = from + (to - from) * 0.4f;
                    if (native.end != null) native.end.position = to;
                    if (!beam.Root.activeSelf) beam.Root.SetActive(true);
                }
            }
            catch (Exception ex)
            {
                holdBeamFailed = true;
                MelonLogger.Log("[SpectatorTargets] the sustain beam failed (no beams from now on): " + ex);
                for (int h = 0; h < 2; h++)
                {
                    if (holdBeams[h] != null && holdBeams[h].Root != null) GameObject.Destroy(holdBeams[h].Root);
                    holdBeams[h] = null;
                }
            }
        }

        /// <summary>
        /// The sustain audio tracker for a hand (0 = left, 1 = right): the game's own while a song
        /// is loaded, else one made here with the same settings. Null when it can't be had.
        /// </summary>
        private static SustainAudioTracker AudioTracker(int h)
        {
            try
            {
                AudioDriver driver = AudioDriver.I;
                if (driver != null)
                {
                    SustainAudioTracker native = h == 0 ? driver.sustainAudioTrackerLeft : driver.sustainAudioTrackerRight;
                    if (native != null) return native;
                }

                if (ownTrackers[h] != null) return ownTrackers[h]; // Unity-null: remade if destroyed
                if (trackerTried[h]) return null;
                trackerTried[h] = true;

                // Inactive while the component is added and set: Awake runs on activation and sees the values.
                GameObject holder = new GameObject("ExScoring_SustainTracker" + (h == 0 ? "Left" : "Right"));
                holder.SetActive(false);
                GameObject.DontDestroyOnLoad(holder);
                SustainAudioTracker tracker = holder.AddComponent<SustainAudioTracker>();
                tracker.bufferLength = TrackerBufferLength;
                tracker.sampleDelay = TrackerSampleDelay;
                tracker.signalTapId = h == 0 ? TrackerTapLeft : TrackerTapRight;
                holder.SetActive(true);

                // Awake failed (nothing to read from): a line with such a tracker would throw every frame.
                if (tracker.mAudioRingBuffer == null || tracker.mTap == null)
                {
                    Log($"the {(h == 0 ? "left" : "right")} sustain audio tracker didn't start (ring buffer {(tracker.mAudioRingBuffer != null ? "set" : "null")}, tap {(tracker.mTap != null ? "set" : "null")}): the beam stays straight");
                    GameObject.Destroy(holder);
                    return null;
                }
                ownTrackers[h] = tracker;
                Log($"sustain audio tracker made for the {(h == 0 ? "left" : "right")} hand (tap {tracker.signalTapId}, buffer {tracker.mAudioRingBuffer.Length})");
                return tracker;
            }
            catch (Exception ex)
            {
                Log("the sustain audio tracker can't be made (" + ex.Message + "): the beam stays straight");
                return null;
            }
        }

        private static HoldBeam EnsureHoldBeam(int h, int handType)
        {
            HoldBeam beam = holdBeams[h];
            if (beam == null || beam.Root == null || beam.Beam == null) // Unity-null: rebuilt if destroyed
            {
                KataConfig config = KataConfig.I;
                if (config == null || config.sustainBeamPrefab == null) return null;

                beam = new HoldBeam();
                beam.Root = GameObject.Instantiate(config.sustainBeamPrefab.gameObject, root.transform);
                beam.Root.name = "SustainBeam " + (h == 0 ? "L" : "R");
                beam.Beam = beam.Root.GetComponent<SustainBeam>();
                if (beam.Beam == null)
                {
                    GameObject.Destroy(beam.Root);
                    throw new Exception("the sustain beam prefab has no SustainBeam");
                }
                var colliders = beam.Root.GetComponentsInChildren<Collider>(true);
                for (int i = 0; i < colliders.Length; i++)
                    if (colliders[i] != null) colliders[i].enabled = false;
                SetLayerRecursive(beam.Root.transform, MpPresence.SongOnlyLayer);

                // SustainBeam.Init without a gun: each line finds its LineRenderer and width curve,
                // and takes the audio tracker its wobble comes from.
                SustainAudioTracker tracker = AudioTracker(h);
                var lines = beam.Beam.lines;
                for (int i = 0; lines != null && i < lines.Length; i++)
                {
                    if (lines[i] != null) lines[i].Init((Target.TargetHandType)handType, tracker);
                }
                holdBeams[h] = beam;
                Log($"sustain beam built for the {(h == 0 ? "left" : "right")} hand ({(lines != null ? lines.Length : 0)} lines, audio tracker: {(tracker != null ? "yes" : "none, straight lines")})");
            }

            // The watched player's colour, the way SustainBeamLine.Init colours a line.
            MpPresence.GetGunColors(SpectatorSong.WatchedUserId, out Color leftColor, out Color rightColor);
            Color color = h == 0 ? leftColor : rightColor;
            string key = ColorKey(color);
            if (beam.ColorKey != key)
            {
                beam.ColorKey = key;
                var lines = beam.Beam.lines;
                for (int i = 0; lines != null && i < lines.Length; i++)
                {
                    SustainBeamLine line = lines[i];
                    if (line == null || line.mLine == null) continue;
                    float toWhite = Mathf.Clamp01(line.bandIndex * 0.5f);
                    Color start = Color.Lerp(color, Color.white, toWhite);
                    start.a = 0f;
                    Color end = start;
                    end.a = Mathf.Lerp(color.a, 0.25f, toWhite);
                    line.mLine.startColor = start;
                    line.mLine.endColor = end;
                }
            }
            return beam;
        }

        // ── Per frame ────────────────────────────────────────────────────────

        private static void UpdateTarget(Live target, double nowMs, double tick, Transform head)
        {
            float clipTime = HitClipTime - (float)((target.AnimMs - nowMs) / 1000.0) * target.Speed;

            // Made a little early (one spawn window for every type): shown from its own lookahead.
            bool started = clipTime >= 0f;
            if (target.Spawner.activeSelf != started) target.Spawner.SetActive(started);
            if (!started) return;

            // A held (or let go) sustain sits on its spot. With results, a target that wasn't hit
            // flies on past its spot; without any, everything stops there (and goes at its time).
            float poseTime = target.Holding || target.Broken || !hasResults
                ? Mathf.Clamp(clipTime, 0f, HitClipTime)
                : Mathf.Clamp(clipTime, 0f, target.CompleteTime);
            bool pinned = Pinned(target, nowMs);
            Pose(target.TargetAnim, target.TargetState, pinned ? HitClipTime : poseTime);
            if (pinned && target.TargetTransform != null) target.TargetTransform.localPosition = Vector3.zero; // Target.Update

            if (target.Holding && target.TargetTransform != null)
            {
                // Target.LateUpdate: a held sustain spins faster and faster.
                float divisor = Mathf.Clamp(target.Cue.tickLength / 3840f, 1f, 4f);
                float held = Mathf.Max(0f, (float)((nowMs - target.HeldSinceMs) / 1000.0)) / divisor;
                float sign = target.Cue.handType == Target.TargetHandType.Left ? 1f : -1f;
                target.TargetTransform.localRotation = Quaternion.Euler(0f, 0f, (held * held + held) * 300f * sign);
            }

            if (target.Mesh != null)
            {
                bool show = clipTime >= target.ShowTime;
                if (target.Mesh.enabled != show) target.Mesh.enabled = show;
            }

            if (target.Timing != null)
            {
                SongCues.Cue cue = target.Cue;
                bool inSlop = tick >= cue.tick - cue.slopBeforeTicks && tick <= cue.tick + cue.slopAfterTicks;
                if (target.Timing.enabled != inSlop) target.Timing.enabled = inSlop;
            }

            if (target.Telegraph != null)
            {
                if (nowMs >= target.CueMs)
                {
                    GameObject.Destroy(target.Telegraph); // natively it destroys itself once the cue's tick has passed
                    target.Telegraph = null;
                    target.TelegraphAnim = null;
                    target.TelegraphState = null;
                }
                else
                {
                    Pose(target.TelegraphAnim, target.TelegraphState, Mathf.Clamp(clipTime, 0f, TelegraphMaxTime));
                    if (target.Melee && head != null)
                    {
                        // Telegraph.Update: a melee telegraph faces the player's head.
                        Vector3 toHead = head.position - target.Telegraph.transform.position;
                        if (toHead.sqrMagnitude > 0.0001f) target.Telegraph.transform.rotation = Quaternion.LookRotation(toHead);
                    }
                }
            }
        }

        /// <summary>The line between the spots where this target and the one landing with it will land.</summary>
        private static void UpdateLine(Live target, double nowMs)
        {
            bool show = nowMs < target.CueMs
                && target.Spawner != null && target.Spawner.activeSelf
                && liveByIndex.TryGetValue(target.Pair, out Live other)
                && other.Spawner != null && other.Spawner.activeSelf && other.Holder != null;

            GameObject lineObject = target.Line.gameObject;
            if (lineObject.activeSelf != show) lineObject.SetActive(show);
            if (!show) return;

            liveByIndex.TryGetValue(target.Pair, out Live pair);
            // Dome targets: holder to holder. A melee pair: ball to ball.
            target.Line.SetPosition(0, target.Melee ? target.TargetTransform.position : target.Holder.position);
            target.Line.SetPosition(1, target.Melee ? pair.TargetTransform.position : pair.Holder.position);

            float t = HitClipTime - (float)((target.CueMs - nowMs) / 1000.0) * target.Speed;

            if (!lineWidthFailed)
            {
                try
                {
                    target.Line.widthMultiplier = Mathf.Lerp(LineWidthMin, LineWidthMax, Mathf.InverseLerp(LineWidthStartTime, HitClipTime, t));
                }
                catch (Exception ex)
                {
                    lineWidthFailed = true;
                    Log("line width can't be set (" + ex.Message + "): the prefab's width stays");
                }
            }

            if (target.LineBlock != null && !lineBlockFailed)
            {
                try
                {
                    // Target.UpdateSimultaneousLineAnim.
                    float border = t > HitClipTime ? 1f : 1f + 0.461039f * t + 0.4982684f * t * t - 0.3593074f * t * t * t;
                    float glow;
                    if (t <= 1.47f) glow = 0f;
                    else if (t <= 1.65f) glow = Mathf.Lerp(0f, 1.22f, Mathf.InverseLerp(1.47f, 1.65f, t));
                    else if (t <= 1.7333f) glow = Mathf.Lerp(1.22f, 1f, Mathf.InverseLerp(1.65f, 1.7333f, t));
                    else glow = 1f;
                    target.LineBlock.SetFloat("_BorderAmount", border);
                    target.LineBlock.SetFloat("_Glow", glow);
                    target.Line.SetPropertyBlock(target.LineBlock);
                }
                catch (Exception ex)
                {
                    lineBlockFailed = true;
                    Log("line border / glow can't be set (" + ex.Message + ")");
                }
            }
        }

        /// <summary>A chain node's line from the node before it (Target.Update, UpdateChainLineAnim).</summary>
        private static void UpdateChainLine(Live target, double nowMs, double tick)
        {
            GameObject lineObject = target.ChainLine.gameObject;
            bool show = !chainLineFailed
                && target.Spawner != null && target.Spawner.activeSelf && target.TargetTransform != null
                && liveByIndex.TryGetValue(target.Prev, out Live previous)
                && previous.Spawner != null && previous.Spawner.activeSelf && previous.TargetTransform != null
                && target.Mesh != null && target.Mesh.enabled;
            if (lineObject.activeSelf != show) lineObject.SetActive(show);
            if (!show) return;

            try
            {
                liveByIndex.TryGetValue(target.Prev, out Live before);
                // Natively 0.15 m down the world's z, toward the player: here in the watched player's space.
                Vector3 forward = root.transform.rotation * new Vector3(0f, 0f, -ChainLineForward);
                target.ChainLine.SetPosition(0, before.TargetTransform.position + forward);
                target.ChainLine.SetPosition(1, target.TargetTransform.position + forward);

                float clipTime = HitClipTime - (float)((target.AnimMs - nowMs) / 1000.0) * target.Speed;
                float travel = 1f - Mathf.Clamp01((float)(target.Cue.tick - tick) / ChainTravelTicks);
                float glow = Mathf.Clamp01(Mathf.InverseLerp(1.47f, 1.6f, clipTime));
                target.ChainBlock.SetFloat("_Travel", travel);
                target.ChainBlock.SetFloat("_Glow", glow);
                target.ChainLine.SetPropertyBlock(target.ChainBlock);
            }
            catch (Exception ex)
            {
                chainLineFailed = true;
                lineObject.SetActive(false);
                Log("chain lines can't be drawn (" + ex.Message + "): chains go on without them");
            }
        }

        /// <summary>Puts an Animation at a clip time without playing it.</summary>
        private static void Pose(Animation animation, AnimationState state, float time)
        {
            if (animation == null || state == null) return;

            if (useSample)
            {
                try
                {
                    state.enabled = true;
                    state.weight = 1f;
                    state.time = time;
                    animation.Sample();
                    state.enabled = false;
                    return;
                }
                catch (Exception ex)
                {
                    useSample = false;
                    Log("Animation.Sample isn't usable (" + ex.Message + "): posing through a playing state at speed 0 instead");
                }
            }

            if (!animation.isPlaying) animation.Play(state.name);
            state.speed = 0f;
            state.time = time;
        }

        // ── Cleanup / debug ──────────────────────────────────────────────────

        private static void DestroyLive()
        {
            for (int i = 0; i < live.Count; i++)
            {
                if (live[i].Spawner != null) GameObject.Destroy(live[i].Spawner);
            }
            live.Clear();
            liveByIndex.Clear();
            for (int h = 0; h < 2; h++)
            {
                if (holdBeams[h] != null && holdBeams[h].Root != null && holdBeams[h].Root.activeSelf) holdBeams[h].Root.SetActive(false);
            }
        }

        /// <summary>Removes every target and forgets the chart (the templates stay).</summary>
        private static void Clear(string reason)
        {
            if (chartSong == null && live.Count == 0) return;
            DestroyLive();
            SpectatorPopups.Clear();
            foreach (Material material in tinted.Values)
            {
                if (material != null) UnityEngine.Object.Destroy(material);
            }
            tinted.Clear();
            foreach (RenderTexture[] textures in cloudTextures.Values)
            {
                for (int i = 0; i < textures.Length; i++)
                    if (textures[i] != null) UnityEngine.Object.Destroy(textures[i]);
            }
            cloudTextures.Clear();
            if (chartSong != null) Log($"cleared ({reason}): {spawnedTotal} targets shown, {skippedTotal} cues of types not drawn yet (dodge), results applied {resultsApplied} (hits {hitsApplied}, misses {missesApplied}, without a cue {resultsWithoutCue})");
            chartSong = null;
            chartCueCount = 0;
            cueMs = null;
            endMs = null;
            pairOf = null;
            headOf = null;
            prevOf = null;
            wasHit = null;
        }

        public static void DebugDump()
        {
            MelonLogger.Log("[SpectatorTargets] ── dump ──");
            MelonLogger.Log($"  templates={templates.Count} failed={templatesFailed} root={(root != null ? "built" : "null")} sampleMode={useSample} lineWidthFailed={lineWidthFailed} lineBlockFailed={lineBlockFailed} cloudFailed={cloudFailed}");
            MelonLogger.Log($"  chart={(chartSong != null ? chartSong.songID : "none")} [{chartDifficulty}] cues={chartCueCount} targetSpeed={chartTargetSpeed:0.##} meleeSpeed={chartMeleeSpeed:0.##} height={chartHeightInches:0.#} in " +
                $"domeClipSpeed={domeSpeed:0.000} meleeClipSpeed={meleeSpeed:0.000} spawnAheadMs={spawnAheadMs:0}");
            MelonLogger.Log($"  results: any={hasResults} kept={results.Count} applied={resultsApplied} (hits {hitsApplied}, misses {missesApplied}, without a cue {resultsWithoutCue}) holdBeamFailed={holdBeamFailed}");
            MelonLogger.Log($"  nowMs={lastNowMs:0} nextIndex={nextIndex} live={live.Count} spawned={spawnedTotal} skipped={skippedTotal} tintedMaterials={tinted.Count} cloudTextureSets={cloudTextures.Count}");
            if (root != null && !root.WasCollected)
                MelonLogger.Log($"  root pos={root.transform.position} rot={root.transform.eulerAngles}");
            for (int h = 0; h < 2; h++)
            {
                SustainAudioTracker tracker = ownTrackers[h];
                string state = "none";
                try
                {
                    if (tracker != null)
                    {
                        float peak = 0f;
                        var buffer = tracker.mAudioRingBuffer;
                        for (int i = 0; buffer != null && i < buffer.Length; i += 16) peak = Mathf.Max(peak, Mathf.Abs(buffer[i]));
                        state = $"tap {tracker.signalTapId} bufferPos={tracker.mBufferPos} dspLength={tracker.mDspBufferLength} peak={peak:0.0000}";
                    }
                }
                catch (Exception ex) { state = "unreadable: " + ex.Message; }
                MelonLogger.Log($"  sustain audio tracker {(h == 0 ? "L" : "R")}: tried={trackerTried[h]} {state}");
            }
            MelonLogger.Log($"  chainLineFailed={chainLineFailed}");
            for (int i = 0; i < live.Count && i < 8; i++)
            {
                Live target = live[i];
                if (target.Spawner == null) continue;
                MelonLogger.Log($"  [{target.Index}] {target.Cue.behavior}/{target.Cue.handType} pitch={target.Cue.pitch} in {(target.CueMs - lastNowMs):0} ms pair={target.Pair} line={(target.Line != null)} " +
                    $"spawner={target.Spawner.transform.position} active={target.Spawner.activeSelf} target local={(target.TargetTransform != null ? target.TargetTransform.localPosition.ToString() : "-")} " +
                    $"mesh={(target.Mesh != null && target.Mesh.enabled)} telegraph={(target.Telegraph != null)} state={(target.TargetState != null ? target.TargetState.name + "@" + target.TargetState.time.ToString("0.00") : "null")}");
            }
        }
    }
}