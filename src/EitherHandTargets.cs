using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using Harmony;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// Either-hand (handType 0) targets on non-melee cues: fixes for the native bugs, plus a
    /// diagnostic log (DebugLog, tag [EitherHand]) to find whatever is left.
    ///
    /// Native findings (Ghidra 2026-11-27, see claude/Either-Hand-Findings.md):
    ///  - IsValidTarget and OnHit skip the hand check for handType 0, and the sustain hold reads
    ///    mShotByGun (the gun that hit), so plain Either shots and sustain holds already work.
    ///  - Bug 1: OnHit calls SustainBeam.Init(gun, transform, cue.handType, ...), and Init passes
    ///    that hand to every SustainBeamLine.Init. For Either it is 0, not the gun's hand, so the
    ///    beam lines get the wrong colour. Fix: a SustainBeam.Init prefix sets hand = gun.hand.
    ///  - Bug 2: Target.Update's chain branch (behavior 5) takes guns[0] for Either nodes, for the
    ///    trigger-held hit check, IsValidTarget, Fire(useChainBeam) and the break check. A chain
    ///    whose head was shot with the other gun breaks on its first node. Fix: during that one
    ///    Target.Update call, put the gun that hit the chain head at guns[0] (Priority.First, so it
    ///    also runs before ChainSustain's prefix, whose GetGunFor reads guns[0] for Either), and
    ///    swap back in the postfix.
    ///  - Bug 3: the chain-break particles use the left pool only for handType 2, so Either always
    ///    gets the right-hand colour. Fix: during the same call, when the owner is the left gun,
    ///    swap ParticlePoolManager's left/right failure pools.
    ///
    /// Gun.Fire and Target.OnHit are NOT patched here (see ChainSustain.cs). The log works out
    /// each shot's result from what it can see around them instead:
    ///  - Gun.UpdateTrigger postfix (a press, before Fire runs): the Either targets near that
    ///    tick, with InSlopWindow, IsValidTarget(gun, shot, false) and the gun's orientation (the
    ///    same checks OnHit makes), and the GameplayStats miss counters.
    ///  - Gun.FindBestIntersection postfix: which targets the gun's aim found that frame.
    ///  - Next frame (Gun.Update prefix): which target was hit (cue.success flipped and
    ///    gun.mLastShotTarget is that target), how the counters changed, and a verdict.
    ///  - Target.Update postfix / OnDestroy prefix: each Either target's spawn, hit, miss,
    ///    sustain break and end. Any target hit by a gun of the wrong hand is also logged
    ///    (that would be the old OnHit-trampoline bug).
    /// </summary>
    internal static class EitherHandTargets
    {
        private const string Tag = "[EitherHand] ";

        /// <summary>The diagnostic log. The fixes don't depend on it. Needs Debug logs on.</summary>
        private static bool LogOn => Config.DebugLogs && ExScoring.menuState == MenuState.State.Launched;

        /// <summary>Either targets within this many ticks of a press are listed for that shot (2 beats).</summary>
        private const float NearbyTicks = 960f;

        private const Target.TargetHandType Either = (Target.TargetHandType)0;

        // ══════════════════════════════════════════════════════════════════
        //  Fix 1: sustain beam colour (SustainBeam.Init hand 0 → gun.hand)
        // ══════════════════════════════════════════════════════════════════

        private static bool initSearched;
        private static MethodInfo initMethod;

        /// <summary>SustainBeam.Init(gun, target, hand, sustainAudioTracker, beamParticles).</summary>
        private static MethodInfo FindSustainBeamInit()
        {
            if (initSearched) return initMethod;
            initSearched = true;
            try
            {
                foreach (MethodInfo m in typeof(SustainBeam).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (m.Name != "Init") continue;
                    ParameterInfo[] p = m.GetParameters();
                    if (p.Length == 5 && p[0].ParameterType == typeof(Gun)) { initMethod = m; break; }
                }
                if (initMethod == null)
                    MelonLoader.MelonLogger.Log(Tag + "SustainBeam.Init(gun, target, hand, tracker, particles) not found: the sustain colour fix is off");
                else
                {
                    Type handType = initMethod.GetParameters()[2].ParameterType;
                    if (handType != typeof(int) && handType != typeof(Target.TargetHandType))
                        MelonLoader.MelonLogger.Log(Tag + "SustainBeam.Init hand parameter is " + handType.FullName + ": the sustain colour fix is off");
                }
            }
            catch (Exception e)
            {
                MelonLoader.MelonLogger.Log(Tag + "SustainBeam.Init lookup failed: " + e.Message);
            }
            return initMethod;
        }

        private static bool InitHandIs(Type t)
        {
            MethodInfo m = FindSustainBeamInit();
            return m != null && m.GetParameters()[2].ParameterType == t;
        }

        private static void LogSustainSwap(Gun gun)
        {
            if (LogOn) DebugLog.Log(Tag + "sustain beam: hand Either -> " + gun.hand + " (the gun that hit)");
        }

        // Only one of these two applies (Prepare), depending on how the unhollowed Init declares hand.
        [HarmonyPatch]
        private static class SustainBeamInitIntPatch
        {
            private static bool Prepare() => InitHandIs(typeof(int));
            private static MethodBase TargetMethod() => FindSustainBeamInit();

            private static void Prefix(Gun gun, ref int hand)
            {
                try
                {
                    if (hand != 0 || gun == null) return;
                    hand = (int)gun.hand;
                    LogSustainSwap(gun);
                }
                catch { }
            }
        }

        [HarmonyPatch]
        private static class SustainBeamInitEnumPatch
        {
            private static bool Prepare() => InitHandIs(typeof(Target.TargetHandType));
            private static MethodBase TargetMethod() => FindSustainBeamInit();

            private static void Prefix(Gun gun, ref Target.TargetHandType hand)
            {
                try
                {
                    if (hand != Either || gun == null) return;
                    hand = gun.hand;
                    LogSustainSwap(gun);
                }
                catch { }
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  Fixes 2 + 3: Either chain nodes use the gun that hit the chain head
        // ══════════════════════════════════════════════════════════════════

        /// <summary>Chain head cue (pointer) → the gun that hit it.</summary>
        private static readonly Dictionary<IntPtr, Gun> ChainOwners = new Dictionary<IntPtr, Gun>();

        /// <summary>Nodes whose owner swap was already logged.</summary>
        private static readonly HashSet<IntPtr> LoggedNodeSwaps = new HashSet<IntPtr>();

        // Swap state, live only during one Target.Update call.
        private static bool gunsSwapped;
        private static int swappedIndex;
        private static Gun originalGun0;
        private static Gun originalGunAtIndex;

        private static bool poolsSwapped;
        private static ParticlePoolManager swappedPoolManager;
        private static ParticlePool originalLeftPool;
        private static ParticlePool originalRightPool;

        /// <summary>
        /// The gun an Either chain node belongs to: the gun that hit its chain head, or null while
        /// the head isn't hit (native's chain branch only needs a gun after that).
        /// </summary>
        internal static Gun GetChainOwnerGun(Target node)
        {
            SongCues.Cue head = node.mChainHead;
            if (head == null || !head.success) return null;

            IntPtr key = head.Pointer;
            if (ChainOwners.TryGetValue(key, out Gun cached) && cached != null) return cached;

            Gun owner = null;
            Target headTarget = head.target;
            if (headTarget != null) owner = headTarget.mShotByGun;

            // Head target already gone: an earlier node of this chain that was hit was fired at
            // by the owner (with this fix in place).
            if (owner == null)
            {
                SongCues.Cue c = node.mCue != null ? node.mCue.chainPrevious : null;
                while (c != null && owner == null && c.Pointer != key)
                {
                    if (c.success && c.target != null) owner = c.target.mShotByGun;
                    c = c.chainPrevious;
                }
            }

            if (owner != null)
            {
                if (ChainOwners.Count > 256) ChainOwners.Clear();
                ChainOwners[key] = owner;
            }
            return owner;
        }

        private static void ApplyChainSwap(Target target, SongCues.Cue cue)
        {
            if (cue.behavior != Target.TargetBehavior.Chain || cue.handType != Either) return;

            Gun owner = GetChainOwnerGun(target);
            if (owner == null) return;

            KataConfig kc = KataConfig.I;
            if (kc == null || kc.guns == null || kc.guns.Length < 2) return;

            // Guns: native takes guns[0] for Either.
            if (kc.guns[0] == null || kc.guns[0].Pointer != owner.Pointer)
            {
                int idx = -1;
                for (int i = 1; i < kc.guns.Length; i++)
                {
                    Gun g = kc.guns[i];
                    if (g != null && g.Pointer == owner.Pointer) { idx = i; break; }
                }
                if (idx > 0)
                {
                    originalGun0 = kc.guns[0];
                    originalGunAtIndex = kc.guns[idx];
                    swappedIndex = idx;
                    kc.guns[0] = originalGunAtIndex;
                    kc.guns[idx] = originalGun0;
                    gunsSwapped = true;
                }
            }

            // Failure particles: native takes the left pool only for handType 2.
            if (owner.hand == Target.TargetHandType.Left)
            {
                ParticlePoolManager ppm = ParticlePoolManager.I;
                if (ppm != null)
                {
                    swappedPoolManager = ppm;
                    originalLeftPool = ppm.explodeFailureParticleLeftPool;
                    originalRightPool = ppm.explodeFailureParticleRightPool;
                    ppm.explodeFailureParticleLeftPool = originalRightPool;
                    ppm.explodeFailureParticleRightPool = originalLeftPool;
                    poolsSwapped = true;
                }
            }

            if ((gunsSwapped || poolsSwapped) && LogOn && LoggedNodeSwaps.Add(cue.Pointer))
            {
                if (LoggedNodeSwaps.Count > 1024) LoggedNodeSwaps.Clear();
                DebugLog.Log(Tag + $"chain node cue {cue.index}: owner gun {owner.hand}" +
                    (gunsSwapped ? $" (native would have used guns[0] = {originalGunAtIndex?.hand})" : "") +
                    (poolsSwapped ? ", left failure particles" : ""));
            }
        }

        /// <summary>Puts guns[] and the failure pools back. Safe to call any time.</summary>
        private static void RestoreSwap()
        {
            if (gunsSwapped)
            {
                gunsSwapped = false;
                try
                {
                    KataConfig kc = KataConfig.I;
                    if (kc != null && kc.guns != null && kc.guns.Length > swappedIndex)
                    {
                        kc.guns[0] = originalGun0;
                        kc.guns[swappedIndex] = originalGunAtIndex;
                    }
                }
                catch (Exception e) { DebugLog.Log(Tag + "restoring guns failed: " + e.Message); }
                originalGun0 = null;
                originalGunAtIndex = null;
            }

            if (poolsSwapped)
            {
                poolsSwapped = false;
                try
                {
                    if (swappedPoolManager != null)
                    {
                        swappedPoolManager.explodeFailureParticleLeftPool = originalLeftPool;
                        swappedPoolManager.explodeFailureParticleRightPool = originalRightPool;
                    }
                }
                catch (Exception e) { DebugLog.Log(Tag + "restoring failure pools failed: " + e.Message); }
                swappedPoolManager = null;
                originalLeftPool = null;
                originalRightPool = null;
            }
        }

        // Prefix and postfix are separate classes so each gets its own class-level priority (the
        // form ChainSustain uses). The prefix must run before ChainSustain.TargetUpdatePatch.
        [HarmonyPatch(typeof(Target), "Update", new Type[0])]
        [HarmonyPriority(Priority.First)]
        private static class TargetUpdateSwapPatch
        {
            private static void Prefix(Target __instance)
            {
                RestoreSwap(); // in case a previous call threw before its postfix
                try
                {
                    if (__instance == null) return;
                    SongCues.Cue cue = __instance.mCue;
                    if (cue == null) return;
                    ApplyChainSwap(__instance, cue);
                }
                catch (Exception e)
                {
                    RestoreSwap();
                    DebugLog.Log(Tag + "chain owner swap failed: " + e);
                }
            }
        }

        [HarmonyPatch(typeof(Target), "Update", new Type[0])]
        [HarmonyPriority(Priority.Last)]
        private static class TargetUpdateRestorePatch
        {
            private static void Postfix(Target __instance)
            {
                RestoreSwap();
                if (!LogOn) return;
                try { TrackTarget(__instance); }
                catch (Exception e) { LogErrorOnce("track", e); }
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  Log: target lifecycle
        // ══════════════════════════════════════════════════════════════════

        private class TrackedTarget
        {
            public Target target;
            public SongCues.Cue cue;
            public bool success;
            public bool passed;
            public bool sustainFailed;
        }

        private static readonly Dictionary<IntPtr, TrackedTarget> Tracked = new Dictionary<IntPtr, TrackedTarget>();
        private static readonly HashSet<IntPtr> ReportedWrongHand = new HashSet<IntPtr>();

        private static bool IsLoggedEither(SongCues.Cue cue) =>
            cue.handType == Either &&
            cue.behavior != Target.TargetBehavior.Melee &&
            cue.behavior != Target.TargetBehavior.Dodge;

        private static string CueName(SongCues.Cue cue) =>
            ChainSustain.IsChainSustain(cue) ? "ChainSustain" : cue.behavior.ToString();

        private static string GunName(Gun g) => g == null ? "none" : g.hand.ToString();

        private static void TrackTarget(Target t)
        {
            if (t == null) return;
            SongCues.Cue cue = t.mCue;
            if (cue == null) return;

            // Any target: a hit by the wrong hand's gun must never happen.
            if (cue.success && cue.handType != Either && cue.behavior != Target.TargetBehavior.Dodge)
            {
                Gun shot = t.mShotByGun;
                if (shot != null && shot.hand != cue.handType && ReportedWrongHand.Add(cue.Pointer))
                {
                    if (ReportedWrongHand.Count > 1024) ReportedWrongHand.Clear();
                    DebugLog.Log(Tag + $"!! WRONG-HAND HIT ACCEPTED: cue {cue.index} {CueName(cue)} {cue.handType} hit by the {shot.hand} gun (tick {cue.tick})");
                }
            }

            if (!IsLoggedEither(cue)) return;

            IntPtr key = t.Pointer;
            if (!Tracked.TryGetValue(key, out TrackedTarget tr))
            {
                tr = new TrackedTarget { target = t, cue = cue, success = cue.success, passed = cue.passed, sustainFailed = t.mSustainFailed };
                Tracked[key] = tr;
                string chain = cue.behavior == Target.TargetBehavior.Chain && t.mChainHead != null ? $" head cue {t.mChainHead.index}" : "";
                DebugLog.Log(Tag + $"spawn cue {cue.index} {CueName(cue)} Either tick {cue.tick}" +
                    (cue.behavior == Target.TargetBehavior.Hold ? $" length {cue.tickLength}" : "") + chain);
                return;
            }

            if (!tr.success && cue.success)
            {
                tr.success = true;
                Gun g = t.mShotByGun;
                DebugLog.Log(Tag + $"HIT cue {cue.index} {CueName(cue)} by the {GunName(g)} gun, dt {cue.successTick - cue.tick:+0;-0} ticks, aim {cue.aim:0.00}");
            }

            if (!tr.passed && cue.passed)
            {
                tr.passed = true;
                if (!cue.success)
                {
                    string chain = "";
                    if (cue.behavior == Target.TargetBehavior.Chain)
                    {
                        SongCues.Cue head = t.mChainHead;
                        Gun owner = head != null && head.success ? GetChainOwnerGun(t) : null;
                        chain = head == null ? " (no head)" :
                            $" (head cue {head.index} {(head.success ? "hit by " + GunName(owner) : "not hit")}" +
                            $", owner trigger {(owner != null && owner.IsTriggerHeld() ? "held" : "released")})";
                    }
                    DebugLog.Log(Tag + $"MISS cue {cue.index} {CueName(cue)} (passed without a hit){chain}");
                }
            }

            if (!tr.sustainFailed && t.mSustainFailed)
            {
                tr.sustainFailed = true;
                DebugLog.Log(Tag + $"SUSTAIN BROKEN cue {cue.index} at {cue.sustainPercent:P0}, gun {GunName(t.mShotByGun)}, trigger {(t.mShotByGun != null && t.mShotByGun.IsTriggerHeld() ? "held" : "released")}");
            }
        }

        [HarmonyPatch(typeof(Target), "OnDestroy", new Type[0])]
        private static class TargetOnDestroyPatch
        {
            private static void Prefix(Target __instance)
            {
                try
                {
                    if (__instance == null) return;
                    IntPtr key = __instance.Pointer;
                    if (!Tracked.TryGetValue(key, out TrackedTarget tr)) return;
                    Tracked.Remove(key);
                    if (!LogOn) return;

                    SongCues.Cue cue = tr.cue;
                    if (cue == null) return;
                    string result = cue.success ? "hit" : cue.passed ? "missed" : "neither hit nor passed";
                    string sustain = cue.behavior == Target.TargetBehavior.Hold ? $", sustain {cue.sustainPercent:P0}" : "";
                    DebugLog.Log(Tag + $"end cue {cue.index} {CueName(cue)}: {result}{sustain}");
                }
                catch (Exception e) { LogErrorOnce("destroy", e); }
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  Log: shots
        // ══════════════════════════════════════════════════════════════════

        private struct Counters
        {
            public int wrongHand, wrongOrientation, shotNothing, aimMiss, shotMelee;

            public static Counters Read()
            {
                Counters c = new Counters();
                try
                {
                    GameplayStats s = InGameUI.I != null ? InGameUI.I.GetStats() : null;
                    if (s == null) return c;
                    c.wrongHand = s.mWrongHandCount;
                    c.wrongOrientation = s.mWrongOrientationCount;
                    c.shotNothing = s.mShotNothingCount;
                    c.aimMiss = s.mAimMissCount;
                    c.shotMelee = s.mShotMeleeCount;
                }
                catch { }
                return c;
            }

            public string DeltaFrom(Counters before)
            {
                var sb = new StringBuilder();
                Add(sb, "wrongHand", wrongHand - before.wrongHand);
                Add(sb, "wrongOrientation", wrongOrientation - before.wrongOrientation);
                Add(sb, "shotNothing", shotNothing - before.shotNothing);
                Add(sb, "aimMiss(near)", aimMiss - before.aimMiss);
                Add(sb, "shotMelee", shotMelee - before.shotMelee);
                return sb.Length == 0 ? "none" : sb.ToString();
            }

            private static void Add(StringBuilder sb, string name, int d)
            {
                if (d == 0) return;
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(name).Append(d > 0 ? " +" : " ").Append(d);
            }
        }

        private class Candidate
        {
            public IntPtr targetPtr;
            public SongCues.Cue cue;
            public float dTick;
            public bool slop, valid, success, passed;
        }

        private struct AimEntry
        {
            public IntPtr targetPtr;
            public int index;
            public Target.TargetBehavior behavior;
            public Target.TargetHandType hand;
            public bool result;
        }

        private class AimBuffer
        {
            public int frame = -1;
            public readonly List<AimEntry> entries = new List<AimEntry>();
            public int othersFound; // non-Either hits beyond the cap
        }

        private class PendingShot
        {
            public Gun gun;
            public int frame;
            public float tick;
            public Counters before;
            public bool horizontal, vertical;
            public string validTarget;
            public List<Candidate> candidates = new List<Candidate>();
            public AimBuffer aim;
        }

        private const int AimCap = 24;
        private static readonly Dictionary<IntPtr, AimBuffer> AimByGun = new Dictionary<IntPtr, AimBuffer>();
        private static readonly List<PendingShot> Pending = new List<PendingShot>();

        private static AimBuffer CurrentAim(Gun gun)
        {
            IntPtr key = gun.Pointer;
            int frame = Time.frameCount;
            if (!AimByGun.TryGetValue(key, out AimBuffer buf) || buf.frame != frame)
            {
                // A pending shot keeps its own buffer, so this frame gets a fresh one.
                buf = new AimBuffer { frame = frame };
                AimByGun[key] = buf;
            }
            return buf;
        }

        [HarmonyPatch(typeof(Gun), "FindBestIntersection")]
        private static class FindBestIntersectionPatch
        {
            private static void Postfix(Gun __instance, Target target, bool __result)
            {
                if (!LogOn || __instance == null || target == null) return;
                try
                {
                    SongCues.Cue cue = target.mCue;
                    if (cue == null) return;
                    bool either = cue.handType == Either;
                    if (!either && !__result) return;

                    AimBuffer buf = CurrentAim(__instance);
                    if (buf.entries.Count >= AimCap) { if (!either) buf.othersFound++; return; }
                    buf.entries.Add(new AimEntry
                    {
                        targetPtr = target.Pointer,
                        index = cue.index,
                        behavior = cue.behavior,
                        hand = cue.handType,
                        result = __result
                    });
                }
                catch (Exception e) { LogErrorOnce("aim", e); }
            }
        }

        /// <summary>A registered press: snapshot everything OnHit will look at, before Fire runs.</summary>
        [HarmonyPatch(typeof(Gun), "UpdateTrigger")]
        private static class GunUpdateTriggerPatch
        {
            private static void Postfix(Gun __instance, bool __result)
            {
                if (!__result || !LogOn || __instance == null) return;
                try
                {
                    AudioDriver audio = AudioDriver.I;
                    if (audio == null) return;

                    var shot = new PendingShot
                    {
                        gun = __instance,
                        frame = Time.frameCount,
                        tick = audio.GetTick((AudioDriver.TickContext)2),
                        before = Counters.Read(),
                        horizontal = __instance.IsHorizontal(),
                        vertical = __instance.IsVertical(),
                        aim = CurrentAim(__instance)
                    };

                    Target vt = __instance.mValidTarget;
                    shot.validTarget = vt != null && vt.mCue != null ? $"cue {vt.mCue.index} {vt.mCue.handType}" : "none";

                    foreach (TrackedTarget tr in Tracked.Values)
                    {
                        if (tr.target == null || tr.cue == null) continue; // Unity-null: destroyed
                        float d = shot.tick - tr.cue.tick;
                        if (Mathf.Abs(d) > NearbyTicks) continue;
                        shot.candidates.Add(new Candidate
                        {
                            targetPtr = tr.target.Pointer,
                            cue = tr.cue,
                            dTick = d,
                            slop = tr.target.InSlopWindow(),
                            valid = tr.target.IsValidTarget(__instance, 0, false),
                            success = tr.cue.success,
                            passed = tr.cue.passed
                        });
                    }

                    if (Pending.Count > 16) Pending.RemoveAt(0);
                    Pending.Add(shot);
                }
                catch (Exception e) { LogErrorOnce("press", e); }
            }
        }

        [HarmonyPatch(typeof(Gun), "Update")]
        private static class GunUpdatePatch
        {
            private static void Prefix()
            {
                RestoreSwap(); // belt and braces: never leave guns[] swapped across frames
                if (Pending.Count == 0) return;
                try { EvaluatePending(); }
                catch (Exception e) { Pending.Clear(); LogErrorOnce("evaluate", e); }
            }
        }

        /// <summary>Shots from earlier frames: everything their Fire did is done by now.</summary>
        private static void EvaluatePending()
        {
            int frame = Time.frameCount;
            for (int i = 0; i < Pending.Count;)
            {
                PendingShot s = Pending[i];
                if (s.frame >= frame) { i++; continue; }
                Pending.RemoveAt(i);
                if (LogOn) Evaluate(s);
            }
        }

        private static void Evaluate(PendingShot s)
        {
            bool aimFoundEither = false;
            foreach (AimEntry a in s.aim.entries) if (a.hand == Either) { aimFoundEither = true; break; }
            if (s.candidates.Count == 0 && !aimFoundEither) return; // nothing Either about this shot

            Gun gun = s.gun;
            IntPtr lastShot = IntPtr.Zero;
            try { if (gun != null && gun.mLastShotTarget != null) lastShot = gun.mLastShotTarget.Pointer; } catch { }

            Candidate hit = null;
            foreach (Candidate c in s.candidates)
                if (!c.success && c.cue.success && c.targetPtr == lastShot) { hit = c; break; }

            string head = Tag + $"SHOT {GunName(gun)} gun at tick {s.tick:0}: ";
            string stats = Counters.Read().DeltaFrom(s.before);

            if (hit != null)
            {
                DebugLog.Log(head + $"hit Either cue {hit.cue.index} {CueName(hit.cue)} (dt {hit.dTick:+0;-0} ticks)" +
                    (stats != "none" ? $" | stats {stats}" : ""));
                return;
            }

            // Did this shot hit something else?
            string other = "nothing";
            if (lastShot != IntPtr.Zero)
            {
                foreach (AimEntry a in s.aim.entries)
                    if (a.targetPtr == lastShot) { other = $"cue {a.index} {a.behavior} {a.hand}"; break; }
                if (other == "nothing") other = "a target not in the aim list";
            }

            // The Either target the player most likely meant: the closest in time.
            Candidate meant = null;
            foreach (Candidate c in s.candidates)
                if (meant == null || Mathf.Abs(c.dTick) < Mathf.Abs(meant.dTick)) meant = c;

            var sb = new StringBuilder(head);
            sb.Append("no Either target hit");
            if (meant != null)
                sb.Append($". Closest Either: cue {meant.cue.index} {CueName(meant.cue)} dt {meant.dTick:+0;-0} ticks -> {Verdict(meant, s)}");
            sb.Append($". Hit instead: {other}. Stats: {stats}.");
            DebugLog.Log(sb.ToString());

            // Details, for the cases that aren't simply early/late/already done.
            if (meant != null && meant.slop && !meant.success && !meant.passed)
            {
                var d = new StringBuilder(Tag + "  detail: ");
                d.Append($"gun horizontal {s.horizontal}, vertical {s.vertical}, mValidTarget {s.validTarget}; aim found: ");
                if (s.aim.entries.Count == 0) d.Append("nothing");
                for (int i = 0; i < s.aim.entries.Count; i++)
                {
                    AimEntry a = s.aim.entries[i];
                    if (i > 0) d.Append(", ");
                    d.Append($"cue {a.index} {a.behavior} {a.hand} {(a.result ? "in aim" : "not in aim")}");
                }
                if (s.aim.othersFound > 0) d.Append($" (+{s.aim.othersFound} more)");
                d.Append("; nearby Either: ");
                for (int i = 0; i < s.candidates.Count; i++)
                {
                    Candidate c = s.candidates[i];
                    if (i > 0) d.Append(", ");
                    d.Append($"cue {c.cue.index} {CueName(c.cue)} dt {c.dTick:+0;-0} slop {c.slop} valid {c.valid}");
                }
                DebugLog.Log(d.ToString());
            }
        }

        /// <summary>Why OnHit would refuse this target, from the press-time snapshot.</summary>
        private static string Verdict(Candidate c, PendingShot s)
        {
            if (c.success) return "already hit";
            if (c.passed) return "already passed";
            if (!c.slop) return c.dTick < 0 ? "early, outside the timing window" : "late, outside the timing window";
            if (!c.valid)
            {
                SongCues.Cue cue = c.cue;
                if (cue.behavior == Target.TargetBehavior.Horizontal && !s.horizontal) return "not valid: gun not horizontal";
                if (cue.behavior == Target.TargetBehavior.Vertical && !s.vertical) return "not valid: gun not vertical";
                if (cue.behavior == Target.TargetBehavior.Chain && cue.chainPrevious != null && !cue.chainPrevious.success) return "not valid: previous chain link not hit";
                return "not valid (IsValidTarget false, reason unknown)";
            }
            bool inAim = false;
            foreach (AimEntry a in s.aim.entries)
                if (a.targetPtr == c.targetPtr && a.result) { inAim = true; break; }
            if (!inAim) return "in the window and valid, but the gun's aim didn't find it";
            return "!! in the window, valid and in aim, but not hit (unexpected)";
        }

        // ══════════════════════════════════════════════════════════════════

        private static readonly HashSet<string> LoggedErrors = new HashSet<string>();

        private static void LogErrorOnce(string where, Exception e)
        {
            if (LoggedErrors.Add(where)) DebugLog.Log(Tag + where + " log failed: " + e);
        }
    }
}