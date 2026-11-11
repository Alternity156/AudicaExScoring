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
    ///  2. Hit: Target.Update's chain branch calls Gun.Fire(useChainBeam: true). Its prefix switches
    ///     the cue to Hold (3) so Fire and Target.OnHit run their sustain paths (gun "fire_sustain"
    ///     trigger, SustainBeam, sustain animation, sustain flare). Fire then measures aim like a
    ///     shot, so the OnHit prefix puts the chain aim back (Gun.CalculateChainAimScore — it never
    ///     reads the behavior). If the hit fails, Fire's postfix switches the cue back to Chain.
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
        //  2. Hit: Gun.Fire switches to Hold, OnHit restores the chain aim
        // ══════════════════════════════════════════════════════════════════

        [HarmonyPatch(typeof(Gun), "Fire", new Type[]
        {
            typeof(Target), typeof(Vector3), typeof(int), typeof(bool), typeof(bool), typeof(ShootableToy)
        })]
        public static class GunFirePatch
        {
            public static void Prefix(Target target, bool useChainBeam, out bool __state)
            {
                __state = false;
                try
                {
                    if (!useChainBeam || target == null) return;
                    SongCues.Cue cue = target.mCue;
                    if (cue == null || cue.success || cue.behavior != Target.TargetBehavior.Chain) return;
                    if (!IsChainSustain(cue)) return;

                    cue.behavior = Target.TargetBehavior.Hold;
                    __state = true;
                }
                catch (Exception e)
                {
                    DebugLog.Log(Tag + "Gun.Fire prefix failed: " + e);
                }
            }

            public static void Postfix(Target target, bool __state)
            {
                if (!__state) return;
                try
                {
                    SongCues.Cue cue = target != null ? target.mCue : null;
                    if (cue != null && !cue.success) cue.behavior = Target.TargetBehavior.Chain;
                }
                catch (Exception e)
                {
                    DebugLog.Log(Tag + "Gun.Fire postfix failed: " + e);
                }
            }
        }

        [HarmonyPatch(typeof(Target), "OnHit", new Type[]
        {
            typeof(Gun), typeof(Gun.AttackType), typeof(float), typeof(Vector2), typeof(Vector3), typeof(float), typeof(bool)
        })]
        public static class TargetOnHitPatch
        {
            public static void Prefix(Target __instance, Gun gun, Gun.AttackType attackType, ref float aim, out bool __state)
            {
                __state = false;
                try
                {
                    if (__instance == null || gun == null || (int)attackType != 0) return;
                    SongCues.Cue cue = __instance.mCue;
                    if (cue == null || cue.success || cue.behavior != Target.TargetBehavior.Hold) return;
                    if (!IsChainSustain(cue)) return;

                    // Fire measured this like a shot (behavior was already Hold); score it as the chain
                    // node it is. CalculateChainAimScore only reads chainPrevious/chainHitPoint/positions.
                    aim = gun.CalculateChainAimScore(__instance);
                    __state = true;
                }
                catch (Exception e)
                {
                    DebugLog.Log(Tag + "Target.OnHit prefix failed: " + e);
                }
            }

            public static void Postfix(Target __instance, bool __result, bool __state)
            {
                if (!__state || !__result) return;
                try
                {
                    // The chain branch of Target.Update no longer runs for this target (it's a Hold
                    // now), so the chain line would stay frozen where it was: hide it and its arrow.
                    LineRenderer line = __instance.chainLine;
                    if (line != null) line.gameObject.SetActive(false);
                    ChainArrow.Hide(__instance);

                    // Chain nodes get their Animation disabled once the chain head is hit (Target.Update);
                    // OnHit's Hold branch plays the sustain clip on it, which needs it enabled.
                    Animation anim = __instance.mAnimation;
                    if (anim != null) anim.enabled = true;
                }
                catch (Exception e)
                {
                    DebugLog.Log(Tag + "Target.OnHit postfix failed: " + e);
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
