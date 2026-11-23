using Harmony;
using MelonLoader;
using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// Diagnostic for the "player teleports in front of the platform in menus" bug.
    ///
    /// PlayerCorral.Recenter (Ghidra, 2026-11-22):
    ///     cameraRig.position = (rig.x - head.x, rig.y, rig.z - head.z)
    /// i.e. it puts the head at world X/Z 0. Nothing but PlayerCorral.Update calls it, and nothing
    /// in the game calls InputTracking.Recenter. A joystick click fixes the bug, so the problem is
    /// an X/Z offset between the head and world 0 that appears by itself.
    ///
    /// Every frame (from a PlayerCorral.Update postfix) this samples every transform from
    /// PlayerCorral.head up to the scene root, plus cameraRig's own parents if it isn't one of
    /// them, and the environment's Static/Ground. It logs:
    ///  - "MOVED": a transform whose LOCAL position/rotation changed. The head's local pose is the
    ///    tracking data and only counts as a jump past HeadJumpMeters in one frame; every other
    ///    transform should never move in menus, so any change past StaticMoveMeters/StaticTurnDegrees
    ///    is logged when it starts and again with the total when it stops (catches lerps too).
    ///    The transform whose own local values changed is the one something wrote to.
    ///  - "GROUND": the environment platform moved in the world (the environment moved, not you).
    ///  - "RECENTER": every PlayerCorral.Recenter call, with the rig and head before and after,
    ///    and whether it came from inside PlayerCorral.Update.
    ///  - F7: dumps the current chain on demand.
    ///
    /// Logs through MelonLogger.Log directly, prefixed [RigJump], so it shows even with
    /// Options > Debug > Enable Logs off. It only writes on events, never every frame.
    /// Runs everywhere (songs included); every line carries the menu state.
    /// </summary>
    internal static class RigJumpWatcher
    {
        private const float HeadJumpMeters = 0.25f;    // head tracking pose change in one frame
        private const float StaticMoveMeters = 0.01f;  // any other transform's local position
        private const float StaticTurnDegrees = 1f;    // any other transform's local rotation
        private const float GroundMoveMeters = 0.01f;
        private const float GroundLookupSeconds = 2f;

        private sealed class Node
        {
            public Transform T;
            public IntPtr Ptr;
            public string Name;
            public bool IsHead;
            public bool IsRig;
            public Vector3 WorldPos;
            public Vector3 LocalPos;
            public Quaternion LocalRot;

            // An ongoing move of a non-head transform (logged at start and when it stops).
            public bool Moving;
            public int MoveStartFrame;
            public Vector3 MoveStartLocalPos;
            public Quaternion MoveStartLocalRot;
            public Vector3 MoveStartWorldPos;
            public Vector3 HeadWorldAtMoveStart;
        }

        private static IntPtr corralPtr = IntPtr.Zero;
        private static readonly List<Node> chain = new List<Node>();
        private static readonly List<IntPtr> scratchPtrs = new List<IntPtr>();
        private static int headPathLength; // chain[0..headPathLength-1] = head up to the scene root
        private static bool haveSample;
        private static int lastSampleFrame = -1;
        private static int eventCount;

        private static Transform ground;
        private static Transform groundRoot;
        private static Vector3 groundPos;
        private static Vector3 groundRootPos;
        private static float nextGroundLookup;

        private static bool inCorralUpdate;
        private static int lastRecenterFrame = -1;
        private static Vector3 recenterRigBefore;
        private static Vector3 recenterHeadBefore;

        // ── Hooks ────────────────────────────────────────────────────────────

        [HarmonyPatch(typeof(PlayerCorral), "Update")]
        private static class CorralUpdatePatch
        {
            private static void Prefix()
            {
                inCorralUpdate = true;
            }

            private static void Postfix(PlayerCorral __instance)
            {
                inCorralUpdate = false;
                try
                {
                    Sample(__instance);
                }
                catch (Exception ex)
                {
                    MelonLogger.Log($"[RigJump] sample failed: {ex}");
                }
            }
        }

        [HarmonyPatch(typeof(PlayerCorral), "Recenter")]
        private static class CorralRecenterPatch
        {
            private static void Prefix(PlayerCorral __instance)
            {
                try
                {
                    if (__instance == null) return;
                    Transform rig = __instance.cameraRig;
                    Transform head = __instance.head;
                    recenterRigBefore = rig != null ? rig.position : Vector3.zero;
                    recenterHeadBefore = head != null ? head.position : Vector3.zero;
                }
                catch (Exception ex)
                {
                    MelonLogger.Log($"[RigJump] recenter prefix failed: {ex.Message}");
                }
            }

            private static void Postfix(PlayerCorral __instance)
            {
                try
                {
                    if (__instance == null) return;
                    lastRecenterFrame = Time.frameCount;
                    Transform rig = __instance.cameraRig;
                    Transform head = __instance.head;
                    Vector3 rigAfter = rig != null ? rig.position : Vector3.zero;
                    Vector3 headAfter = head != null ? head.position : Vector3.zero;
                    eventCount++;
                    MelonLogger.Log($"[RigJump] #{eventCount} RECENTER {Header()} from {(inCorralUpdate ? "PlayerCorral.Update" : "OUTSIDE PlayerCorral.Update")}" +
                                    $" | rig {F(recenterRigBefore)} -> {F(rigAfter)} (moved {F(rigAfter - recenterRigBefore)})" +
                                    $" | head {F(recenterHeadBefore)} -> {F(headAfter)}" +
                                    $" | ranges gameplay {__instance.rangeGameplay} shell {__instance.rangeShell}");
                }
                catch (Exception ex)
                {
                    MelonLogger.Log($"[RigJump] recenter postfix failed: {ex.Message}");
                }
            }
        }

        // ── Sampling ─────────────────────────────────────────────────────────

        private static void Sample(PlayerCorral corral)
        {
            if (corral == null) return;
            int frame = Time.frameCount;
            if (frame == lastSampleFrame) return; // two corrals, or Update called twice
            lastSampleFrame = frame;

            Transform head = corral.head;
            Transform rig = corral.cameraRig;
            if (head == null || rig == null) return;

            if (corral.Pointer != corralPtr || !ChainStillValid(head, rig))
            {
                bool first = corralPtr == IntPtr.Zero;
                corralPtr = corral.Pointer;
                BuildChain(head, rig);
                haveSample = false;
                MelonLogger.Log($"[RigJump] {(first ? "watching" : "hierarchy changed, now watching")} {Header()}" +
                                $" | ranges gameplay {corral.rangeGameplay} shell {corral.rangeShell}\n{DumpChain()}");
            }

            SampleGround();

            Node headNode = chain[0];
            Vector3 headWorldBefore = headNode.WorldPos;

            for (int i = 0; i < chain.Count; i++)
            {
                Node n = chain[i];
                Vector3 world = n.T.position;
                Vector3 local = n.T.localPosition;
                Quaternion rot = n.T.localRotation;

                if (haveSample)
                {
                    float moved = (local - n.LocalPos).magnitude;
                    float turned = Quaternion.Angle(rot, n.LocalRot);

                    if (n.IsHead)
                    {
                        if (moved > HeadJumpMeters)
                        {
                            eventCount++;
                            MelonLogger.Log($"[RigJump] #{eventCount} MOVED head tracking pose jumped {moved:F3} m in one frame {Header()}" +
                                            $" | local {F(n.LocalPos)} -> {F(local)} (delta {F(local - n.LocalPos)}, turned {turned:F1} deg)" +
                                            $" | head world {F(n.WorldPos)} -> {F(world)}, XZ from 0: {XZ(n.WorldPos):F3} -> {XZ(world):F3}\n{DumpChain()}");
                        }
                    }
                    else
                    {
                        bool movingNow = moved > StaticMoveMeters || turned > StaticTurnDegrees;
                        // Keep a lerp going even if one frame's step is below the threshold.
                        if (!movingNow && n.Moving && ((local - n.LocalPos).sqrMagnitude > 1e-10f || turned > 0.001f))
                            movingNow = true;

                        if (movingNow && !n.Moving)
                        {
                            n.Moving = true;
                            n.MoveStartFrame = frame;
                            n.MoveStartLocalPos = n.LocalPos;
                            n.MoveStartLocalRot = n.LocalRot;
                            n.MoveStartWorldPos = n.WorldPos;
                            n.HeadWorldAtMoveStart = headWorldBefore;
                            eventCount++;
                            MelonLogger.Log($"[RigJump] #{eventCount} MOVED '{n.Name}'{Tags(n)} started moving {Header()}{RecenterNote(frame)}" +
                                            $" | local {F(n.LocalPos)} -> {F(local)} (delta {F(local - n.LocalPos)}, turned {turned:F1} deg)" +
                                            $" | world {F(n.WorldPos)} -> {F(world)}\n{DumpChain()}");
                        }
                        else if (!movingNow && n.Moving)
                        {
                            n.Moving = false;
                            Vector3 headNow = headNode.T.position;
                            MelonLogger.Log($"[RigJump] MOVED '{n.Name}'{Tags(n)} stopped after {frame - n.MoveStartFrame} frames {Header()}" +
                                            $" | local total {F(n.MoveStartLocalPos)} -> {F(n.LocalPos)} (delta {F(n.LocalPos - n.MoveStartLocalPos)}, turned {Quaternion.Angle(n.MoveStartLocalRot, n.LocalRot):F1} deg)" +
                                            $" | world {F(n.MoveStartWorldPos)} -> {F(n.WorldPos)}" +
                                            $" | head XZ from 0: {XZ(n.HeadWorldAtMoveStart):F3} -> {XZ(headNow):F3}");
                        }
                    }
                }

                n.WorldPos = world;
                n.LocalPos = local;
                n.LocalRot = rot;
            }

            haveSample = true;

            if (Input.GetKeyDown(KeyCode.F7))
            {
                MelonLogger.Log($"[RigJump] F7 dump {Header()} | head XZ from 0: {XZ(headNode.WorldPos):F3}" +
                                $" | ranges gameplay {corral.rangeGameplay} shell {corral.rangeShell}" +
                                $" | ground {(ground != null ? F(ground.position) : "none")}\n{DumpChain()}");
            }
        }

        private static void SampleGround()
        {
            if (ground == null)
            {
                groundRoot = null;
                if (Time.unscaledTime < nextGroundLookup) return;
                nextGroundLookup = Time.unscaledTime + GroundLookupSeconds;
                GameObject go = GameObject.Find("Static/Ground");
                if (go == null) return;
                ground = go.transform;
                groundRoot = ground.root;
                groundPos = ground.position;
                groundRootPos = groundRoot != null ? groundRoot.position : Vector3.zero;
                MelonLogger.Log($"[RigJump] ground found {Header()} | Static/Ground world {F(groundPos)}" +
                                $" | root '{(groundRoot != null ? groundRoot.gameObject.name : "?")}' world {F(groundRootPos)}");
                return;
            }

            Vector3 pos = ground.position;
            Vector3 rootPos = groundRoot != null ? groundRoot.position : Vector3.zero;
            if ((pos - groundPos).magnitude > GroundMoveMeters)
            {
                eventCount++;
                MelonLogger.Log($"[RigJump] #{eventCount} GROUND Static/Ground moved {Header()}" +
                                $" | world {F(groundPos)} -> {F(pos)} (delta {F(pos - groundPos)})" +
                                $" | root '{(groundRoot != null ? groundRoot.gameObject.name : "?")}' {F(groundRootPos)} -> {F(rootPos)}");
            }
            groundPos = pos;
            groundRootPos = rootPos;
        }

        // ── Chain ────────────────────────────────────────────────────────────

        /// <summary>head, its parents up to the root, then cameraRig and its parents if not already in.</summary>
        private static void BuildChain(Transform head, Transform rig)
        {
            chain.Clear();
            IntPtr rigPtr = rig.Pointer;

            for (Transform t = head; t != null; t = t.parent)
                chain.Add(MakeNode(t, t.Pointer == head.Pointer, t.Pointer == rigPtr));
            headPathLength = chain.Count;

            bool rigInChain = false;
            for (int i = 0; i < chain.Count; i++)
                if (chain[i].IsRig) rigInChain = true;

            if (!rigInChain)
            {
                for (Transform t = rig; t != null; t = t.parent)
                {
                    bool known = false;
                    for (int i = 0; i < chain.Count; i++)
                        if (chain[i].Ptr == t.Pointer) { known = true; break; }
                    if (!known) chain.Add(MakeNode(t, false, t.Pointer == rigPtr));
                }
                MelonLogger.Log("[RigJump] NOTE: PlayerCorral.cameraRig is NOT a parent of PlayerCorral.head");
            }
        }

        private static Node MakeNode(Transform t, bool isHead, bool isRig)
        {
            return new Node
            {
                T = t,
                Ptr = t.Pointer,
                Name = t.gameObject != null ? t.gameObject.name : "?",
                IsHead = isHead,
                IsRig = isRig,
                WorldPos = t.position,
                LocalPos = t.localPosition,
                LocalRot = t.localRotation,
            };
        }

        /// <summary>Same objects, still alive, same parents as when the chain was built.</summary>
        private static bool ChainStillValid(Transform head, Transform rig)
        {
            if (chain.Count == 0) return false;
            if (chain[0].T == null || chain[0].Ptr != head.Pointer) return false;

            scratchPtrs.Clear();
            for (Transform t = head; t != null; t = t.parent) scratchPtrs.Add(t.Pointer);
            if (scratchPtrs.Count != headPathLength) return false;

            bool rigSeen = false;
            for (int i = 0; i < chain.Count; i++)
            {
                Node n = chain[i];
                if (n.T == null) return false;
                if (i < headPathLength && n.Ptr != scratchPtrs[i]) return false; // head reparented somewhere
                if (n.IsRig)
                {
                    rigSeen = true;
                    if (n.Ptr != rig.Pointer) return false;
                }
            }
            return rigSeen;
        }

        // ── Formatting ───────────────────────────────────────────────────────

        private static string Header()
        {
            return $"(frame {Time.frameCount}, t {Time.unscaledTime:F2}s, dt {Time.unscaledDeltaTime * 1000f:F1} ms, state {ExScoring.menuState}, DisableAutoRecenter {Config.DisableAutoRecenter})";
        }

        private static string RecenterNote(int frame)
        {
            return lastRecenterFrame == frame ? " [PlayerCorral.Recenter ran this frame]" : "";
        }

        private static string Tags(Node n)
        {
            if (n.IsHead) return " [head]";
            if (n.IsRig) return " [cameraRig]";
            return "";
        }

        private static string DumpChain()
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < chain.Count; i++)
            {
                Node n = chain[i];
                if (n.T == null) { sb.Append($"    [{i}] '{n.Name}' destroyed\n"); continue; }
                Vector3 euler = n.T.localRotation.eulerAngles;
                sb.Append($"    [{i}] '{n.Name}'{Tags(n)} world {F(n.T.position)} local {F(n.T.localPosition)} localRot {F(euler)} scale {F(n.T.localScale)}\n");
            }
            return sb.ToString().TrimEnd('\n');
        }

        private static string F(Vector3 v)
        {
            return $"({v.x:F3}, {v.y:F3}, {v.z:F3})";
        }

        private static float XZ(Vector3 v)
        {
            return new Vector2(v.x, v.z).magnitude;
        }
    }
}