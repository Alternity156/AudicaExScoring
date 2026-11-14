using System;
using System.Collections.Generic;
using System.Reflection;
using Harmony;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// Chain sustain target (cue behavior 8): the last node of a chain is a sustain. It is traced like
    /// any chain node (trigger held since the ChainStart, aimed at when its tick arrives), then held
    /// like a sustain until tick + tickLength.
    ///
    /// Vanilla Audica has no behavior 8 (TargetBehavior ends at Dodge = 7), so charts that use it
    /// will not work in the unmodded game or in tools that don't know it.
    ///
    /// How it's done (all native paths confirmed via Ghidra):
    ///  1. Load: every JSON chart goes through SongCues.LoadCuesFromJson(string, out float). Its
    ///     postfix turns each 8 into a Chain (5) so HookUpChains links it (it only links 5s), the
    ///     chain line / trace detection / chain break all work natively, and the spawner takes a
    ///     chain node prefab (target_sustain has no chainLine, which InitFromSpawner/Update use).
    ///     The cue is marked by setting meleeVelocityAmount to <see cref="Marker"/>: it is runtime-
    ///     only (never in the chart JSON), only ever written by Target.OnHit for melee attacks and
    ///     only read natively for behavior 6, and it survives the behavior switch below and every
    ///     cold GetCues() call, so no side registry is needed.
    ///  2. Hit: native's chain branch of Target.Update calls Gun.Fire(useChainBeam: true). Gun.Fire
    ///     and Target.OnHit can't be patched (wrong-colour hits register as correct), so a
    ///     Target.Update prefix runs that same check itself for chain sustains, switches the cue to
    ///     Hold (3) and calls Fire, so Fire and OnHit run their sustain paths (gun "fire_sustain"
    ///     trigger, SustainBeam, sustain animation, sustain flare). Fire then measures aim like a
    ///     shot, so a CalculateAim postfix returns the chain aim (Gun.CalculateChainAimScore) during
    ///     that call. If the hit fails, the prefix switches the cue back to Chain.
    ///  3. Sustain: Target.Update's Hold branch runs as for any sustain (trigger check, 0.1 s grace,
    ///     sustainPercent, OnFailure on break, CompleteTarget at the end).
    ///  4. Score: Audica = native chain node score + native Hold sustain bonus (ScoreData postfix).
    ///     EX = chain tail (aim in the chain average + chain judgement) + its own sustain judgement
    ///     (Hooks.GetScoreForCuePatch / ExScoring.cs / Judgement.cs).
    ///
    /// Debug "Chain Sustain Test Mode" (Options > Debug, session only): every chain tail becomes a
    /// one-measure chain sustain at load, the game is forced into no-fail, and nothing is saved or
    /// uploaded (see TestModeActive and Hooks.SongEndOnSongOverPatch).
    /// </summary>
    internal static class ChainSustain
    {
        private const string Tag = "[ChainSustain] ";

        /// <summary>The chart / saved-run value of the new target type.</summary>
        public const Target.TargetBehavior Behavior = (Target.TargetBehavior)8;

        /// <summary>Name used where behaviors are sent as strings (map data upload).</summary>
        public const string BehaviorName = "ChainSustain";

        /// <summary>Written to Cue.meleeVelocityAmount to mark a chain sustain cue.</summary>
        public const float Marker = -8f;

        /// <summary>Test mode: 1 measure in 4/4 at Audica's 480 ticks per beat.</summary>
        public const int TestModeTickLength = 1920;

        // ══════════════════════════════════════════════════════════════════
        //  Identification
        // ══════════════════════════════════════════════════════════════════

        public static bool IsChainSustain(SongCues.Cue cue)
        {
            try { return cue != null && cue.meleeVelocityAmount == Marker; }
            catch { return false; }
        }

        /// <summary>The behavior an ExCue / saved run records for this cue (8 for chain sustains).</summary>
        public static Target.TargetBehavior GetExBehavior(SongCues.Cue cue)
        {
            return IsChainSustain(cue) ? Behavior : cue.behavior;
        }

        /// <summary>Behavior name for the map data upload ("ChainSustain" for chain sustains).</summary>
        public static string GetChartBehaviorName(SongCues.Cue cue)
        {
            return IsChainSustain(cue) ? BehaviorName : cue.behavior.ToString();
        }

        /// <summary>True while the debug test mode is on (no scoring, no fail, converted tails).</summary>
        public static bool TestModeActive => Config.ChainSustainTestMode;

        // ══════════════════════════════════════════════════════════════════
        //  1. Load: 8 -> Chain + marker (and test mode tail conversion)
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Patches SongCues.LoadCuesFromJson(string, out float) by hand: an out parameter can't be
        /// written in a [HarmonyPatch] attribute's Type[]. Called once from OnApplicationStart.
        /// </summary>
        public static void Init()
        {
            try
            {
                MethodInfo target = typeof(SongCues).GetMethod("LoadCuesFromJson",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new Type[] { typeof(string), typeof(float).MakeByRefType() }, null);
                if (target == null)
                {
                    MelonLoader.MelonLogger.LogError(Tag + "SongCues.LoadCuesFromJson(string, out float) not found; chain sustains disabled.");
                    return;
                }

                HarmonyInstance harmony = HarmonyInstance.Create("ExScoring.ChainSustain");
                harmony.Patch(target, null, new HarmonyMethod(typeof(ChainSustain)
                    .GetMethod(nameof(LoadCuesFromJsonPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            }
            catch (Exception e)
            {
                MelonLoader.MelonLogger.LogError(Tag + "patching LoadCuesFromJson failed; chain sustains disabled. " + e);
            }
        }

        private static void LoadCuesFromJsonPostfix(SongCues.SongCueContainer __result)
        {
            try
            {
                if (__result == null) return;
                var cues = __result.cues;
                if (cues == null || cues.Length == 0) return;
                ConvertCues(cues);
            }
            catch (Exception e)
            {
                DebugLog.Log(Tag + "cue conversion failed: " + e);
            }
        }

        private static void ConvertCues(UnhollowerBaseLib.Il2CppReferenceArray<SongCues.Cue> cues)
        {
            int n = cues.Length;
            SongCues.Cue[] all = new SongCues.Cue[n];
            for (int i = 0; i < n; i++) all[i] = cues[i];

            // Pass 1: chart behavior 8 -> Chain + marker.
            int converted = 0;
            for (int i = 0; i < n; i++)
            {
                SongCues.Cue c = all[i];
                if (c == null || (int)c.behavior != (int)Behavior) continue;
                c.behavior = Target.TargetBehavior.Chain;
                c.meleeVelocityAmount = Marker;
                converted++;
            }

            // Same-hand neighbours, in array order (the order HookUpChains links in).
            int[] prevSameHand = new int[n];
            int[] nextSameHand = new int[n];
            var lastByHand = new Dictionary<int, int>();
            for (int i = 0; i < n; i++)
            {
                prevSameHand[i] = -1;
                nextSameHand[i] = -1;
                if (all[i] == null) continue;
                int hand = (int)all[i].handType;
                if (lastByHand.TryGetValue(hand, out int p))
                {
                    prevSameHand[i] = p;
                    nextSameHand[p] = i;
                }
                lastByHand[hand] = i;
            }

            // Pass 2 (debug test mode): every chain tail becomes a one-measure chain sustain.
            int testConverted = 0;
            if (TestModeActive)
            {
                for (int i = 0; i < n; i++)
                {
                    SongCues.Cue c = all[i];
                    if (c == null || c.behavior != Target.TargetBehavior.Chain || IsChainSustain(c)) continue;
                    if (!IsChainLink(all, prevSameHand[i])) continue;
                    if (IsChain(all, nextSameHand[i])) continue; // not the tail

                    c.meleeVelocityAmount = Marker;
                    c.tickLength = TestModeTickLength;
                    testConverted++;
                }
            }

            // Pass 3: a chain sustain must follow a ChainStart/Chain of its hand (otherwise
            // InitFromSpawner would walk a null chainPrevious) and must be the chain's last node.
            int asHold = 0, asChain = 0;
            for (int i = 0; i < n; i++)
            {
                SongCues.Cue c = all[i];
                if (!IsChainSustain(c)) continue;

                if (!IsChainLink(all, prevSameHand[i]))
                {
                    c.behavior = Target.TargetBehavior.Hold;
                    c.meleeVelocityAmount = 0f;
                    asHold++;
                }
                else if (IsChain(all, nextSameHand[i]))
                {
                    c.meleeVelocityAmount = 0f;
                    asChain++;
                }
            }

            if (converted > 0 || testConverted > 0)
            {
                DebugLog.Log($"{Tag}Loaded {converted} chain sustain(s), {testConverted} from test mode" +
                             (asHold > 0 ? $", {asHold} not after a chain (played as sustains)" : "") +
                             (asChain > 0 ? $", {asChain} not at a chain's end (played as chain nodes)" : ""));
            }
        }

        private static bool IsChain(SongCues.Cue[] all, int i) =>
            i >= 0 && all[i] != null && all[i].behavior == Target.TargetBehavior.Chain;

        private static bool IsChainLink(SongCues.Cue[] all, int i) =>
            i >= 0 && all[i] != null &&
            (all[i].behavior == Target.TargetBehavior.Chain || all[i].behavior == Target.TargetBehavior.ChainStart);

        // ══════════════════════════════════════════════════════════════════
        //  2. Hit: Target.Update prefix fires the chain hit itself, as a Hold
        // ══════════════════════════════════════════════════════════════════

        // Gun.Fire and Target.OnHit are NOT patched. Fire passes its forceSuccessful bool straight
        // to OnHit, which is where the hand/colour check is; with a Harmony patch on either of them
        // under MelonLoader 0.5.7, wrong-colour hits registered as correct (and no-target shots
        // broke, see PreciseTiming.cs), even with empty patches. Calling Fire from here is fine.

        /// <summary>cue.index of the chain sustain whose hit is being fired by the prefix below, or -1.</summary>
        private static int firingCueIndex = -1;

        /// <summary>
        /// Runs the native chain branch's hit (Target.Update, Ghidra 2026-11-11) one step early for
        /// chain sustains, with the cue switched to Hold first so Gun.Fire and Target.OnHit take their
        /// sustain paths (gun "fire_sustain" trigger, sustain setup, sustain flare). Same condition as
        /// native: cue.tick &lt;= GetTick(1) + deltaTick / 2, chainPrevious hit, the hand's gun (guns[0]
        /// for Either) holding its trigger and IsValidTarget(gun, 0, false); then
        /// Fire(target, target position, -1, false, useChainBeam: true, null). On success the
        /// original Update then runs its Hold branch this same frame and skips the chain branch (the
        /// behavior is 3 now); on failure the cue goes back to Chain and native carries on as usual.
        /// </summary>
        [HarmonyPatch(typeof(Target), "Update", new Type[0])]
        public static class TargetUpdatePatch
        {
            public static void Prefix(Target __instance)
            {
                SongCues.Cue cue = null;
                bool switched = false;
                try
                {
                    if (__instance == null) return;
                    cue = __instance.mCue;
                    if (cue == null || cue.meleeVelocityAmount != Marker) return; // cheap IsChainSustain
                    if (cue.behavior != Target.TargetBehavior.Chain || cue.success || cue.passed) return;

                    SongCues.Cue prev = cue.chainPrevious;
                    if (prev == null || !prev.success) return;

                    AudioDriver audio = AudioDriver.I;
                    ScoreKeeper keeper = ScoreKeeper.I;
                    if (audio == null || keeper == null) return;
                    float tick = audio.GetTick((AudioDriver.TickContext)1);
                    if (cue.tick > keeper.GetDeltaTick() * 0.5f + tick) return;

                    Gun gun = GetGunFor(cue.handType);
                    if (gun == null || !gun.IsTriggerHeld()) return;
                    if (!__instance.IsValidTarget(gun, 0, false)) return;

                    // Chain nodes get their Animation disabled once the chain head is hit (chain
                    // branch of Target.Update); OnHit's Hold branch plays the sustain clip on it.
                    Animation anim = __instance.mAnimation;
                    if (anim != null) anim.enabled = true;

                    cue.behavior = Target.TargetBehavior.Hold;
                    switched = true;
                    firingCueIndex = cue.index;
                    try
                    {
                        gun.Fire(__instance, __instance.transform.position, -1, false, true, null);
                    }
                    finally
                    {
                        firingCueIndex = -1;
                    }

                    if (cue.success)
                    {
                        // The chain branch no longer runs for this target (it's a Hold now), so the
                        // chain line would stay frozen where it was: hide it and its arrow.
                        LineRenderer line = __instance.chainLine;
                        if (line != null) line.gameObject.SetActive(false);
                        ChainArrow.Hide(__instance);
                    }
                    else
                    {
                        cue.behavior = Target.TargetBehavior.Chain;
                    }
                }
                catch (Exception e)
                {
                    try { if (switched && cue != null && !cue.success) cue.behavior = Target.TargetBehavior.Chain; }
                    catch { }
                    DebugLog.Log(Tag + "chain sustain hit failed: " + e);
                }
            }
        }

        /// <summary>The gun native's chain branch picks: guns[0] for Either, else the one of that hand.</summary>
        private static Gun GetGunFor(Target.TargetHandType hand)
        {
            KataConfig kc = KataConfig.I;
            if (kc == null || kc.guns == null || kc.guns.Length == 0) return null;
            if ((int)hand == 0) return kc.guns[0];
            for (int i = 0; i < kc.guns.Length; i++)
            {
                Gun g = kc.guns[i];
                if (g != null && g.hand == hand) return g;
            }
            return null;
        }

        /// <summary>
        /// Fire takes a Hold cue's aim from CalculateAim (Fire, Ghidra 2026-11-11: only behavior 5
        /// uses CalculateChainAimScore). During the prefix's own Fire call, score this cue as the
        /// chain node it is. CalculateChainAimScore only reads chainPrevious/chainHitPoint/positions.
        /// Last priority so it runs after Hooks.CalculateAimPatch (which records the measured shot
        /// aim and may replace it with the linear score).
        /// </summary>
        [HarmonyPatch(typeof(Gun), "CalculateAim", new Type[] { typeof(Target), typeof(Vector3) })]
        [HarmonyPriority(Priority.Last)]
        public static class GunCalculateAimPatch
        {
            public static void Postfix(Gun __instance, Target target, ref float __result)
            {
                if (firingCueIndex < 0) return;
                try
                {
                    if (__instance == null || target == null) return;
                    SongCues.Cue cue = target.mCue;
                    if (cue == null || cue.index != firingCueIndex) return;
                    __result = __instance.CalculateChainAimScore(target);
                }
                catch (Exception e)
                {
                    DebugLog.Log(Tag + "chain aim override failed: " + e);
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  Visuals (Options > Chain Arrow > Chain Sustain Look)
        // ══════════════════════════════════════════════════════════════════

        private const string SustainShellName = "target_shell_sustain_base";

        [HarmonyPatch(typeof(Target), "InitFromSpawner", new Type[] { typeof(TargetSpawner.SpawnInfo), typeof(SongCues.Cue) })]
        public static class TargetInitFromSpawnerPatch
        {
            public static void Postfix(Target __instance, SongCues.Cue cue)
            {
                try
                {
                    if (__instance == null || !IsChainSustain(cue)) return;
                    ApplyLook(__instance, cue);
                }
                catch (Exception e)
                {
                    DebugLog.Log(Tag + "visual setup failed: " + e);
                }
            }
        }

        private static void ApplyLook(Target target, SongCues.Cue cue)
        {
            bool sustainLook = Config.ChainSustainLook == 0;

            if (sustainLook)
            {
                // InitFromSpawner scales chain nodes to 0.5; a sustain is full size.
                target.transform.localScale = Vector3.one;

                Material holdMat = GetHoldMaterial(cue.handType);
                MeshRenderer mr = target.mMeshRenderer;
                if (holdMat != null && mr != null) mr.sharedMaterial = holdMat;
            }

            // The sustain prefab's shell (the ring around a sustain's diamond).
            Transform shellSource = GetSustainShell();
            if (shellSource == null) return;

            GameObject shell = GameObject.Instantiate(shellSource.gameObject, target.transform, false);
            shell.name = SustainShellName;
            shell.SetActive(true);

            // "Chain Node + Ring": the node stays half size; keep the ring at sustain size around it.
            if (!sustainLook) shell.transform.localScale = shellSource.localScale * 2f;
        }

        private static Material GetHoldMaterial(Target.TargetHandType hand)
        {
            TargetColorSetter cs = TargetColorSetter.I;
            if (cs == null) return null;
            switch (hand)
            {
                case Target.TargetHandType.Left: return cs.mHoldTargetLeftMat;
                case Target.TargetHandType.Right: return cs.mHoldTargetRightMat;
                default: return cs.mHoldTargetEitherMat;
            }
        }

        private static Transform GetSustainShell()
        {
            KataConfig kc = KataConfig.I;
            if (kc == null || kc.targetSustain == null) return null;
            return kc.targetSustain.transform.Find(SustainShellName);
        }

        // ══════════════════════════════════════════════════════════════════
        //  4. Audica score: native chain node score + native sustain bonus
        // ══════════════════════════════════════════════════════════════════

        [HarmonyPatch(typeof(ScoreData), "GetScoreForCue", new Type[]
        {
            typeof(SongCues.Cue), typeof(float), typeof(float), typeof(float)
        })]
        public static class ScoreDataGetScoreForCuePatch
        {
            public static void Postfix(SongCues.Cue cue, float timing, float aim, float extra, ref int __result)
            {
                try
                {
                    if (!IsChainSustain(cue) || cue.finaleSequenceFinalNote) return;

                    // Both from the game's own per-behavior formulas: what this cue earns as a chain
                    // node, plus only the part of a Hold's score that its sustain adds.
                    int chainScore = ScoreData.GetScoreForCue(Target.TargetBehavior.Chain, timing, aim, 0f);
                    int sustainBonus = ScoreData.GetScoreForCue(Target.TargetBehavior.Hold, timing, aim, extra)
                                     - ScoreData.GetScoreForCue(Target.TargetBehavior.Hold, timing, aim, 0f);

                    __result = chainScore + Math.Max(0, sustainBonus);
                }
                catch (Exception e)
                {
                    DebugLog.Log(Tag + "Audica score override failed: " + e);
                }
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  Debug test mode: no fail (and native treats the run as no-fail)
        // ══════════════════════════════════════════════════════════════════

        [HarmonyPatch(typeof(KataConfig), "NoFail", new Type[0])]
        public static class KataConfigNoFailPatch
        {
            public static void Postfix(ref bool __result)
            {
                if (TestModeActive) __result = true;
            }
        }

        [HarmonyPatch(typeof(KataConfig), "AllowFailure", new Type[0])]
        public static class KataConfigAllowFailurePatch
        {
            public static void Postfix(ref bool __result)
            {
                if (TestModeActive) __result = false;
            }
        }
    }
}