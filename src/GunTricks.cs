using Harmony;
using MelonLoader;
using System.Collections.Generic;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// Options > Gun Tricks > Throwing.
    ///
    /// How the game's throw works (Gun.Update, Ghidra + UnityExplorer measurements):
    ///  - Trick 2 (throw) launches the gun once: rigidBody.velocity = controller velocity * kThrowBoost (2.0),
    ///    no gravity, no drag, so it flies in a straight line.
    ///  - Every frame while on physics, a hardcoded leash keeps it within
    ///    Mathf.Lerp(5, 0, mRigidBodyReelIn) metres of the return point
    ///    (mOriginalRigidBodyParent.TransformPoint(mOriginalRigidBodyPosition)).
    ///  - mRigidBodyReelIn stays 0 while the throw is held; once released it climbs to 1 at
    ///    mReelInSpeed/s, the leash shrinks with it, and EndThrowTrick fires at 1.
    ///
    /// The 5 m is a constant, so distance is overridden instead of patched: the prefix saves the
    /// rigidbody position (physics has already run this frame), the postfix restores it — undoing the
    /// game's leash, the only position change Update makes while thrown — and applies our own:
    ///  - held:     radius = MaxThrowDistance, or none when uncapped
    ///  - released: radius = releaseDistance * (1 - mRigidBodyReelIn), so the gun comes back smoothly
    ///              from any distance instead of snapping to the game's 5 m on the first return frame.
    /// At default settings (5 m, not uncapped) none of this runs and the game's leash is untouched.
    /// </summary>
    public partial class ExScoring : MelonMod
    {
        private const int ThrowTrick = 2;
        private const float VanillaThrowDistance = 5f;

        private class GunThrowState
        {
            public float originalThrowBoost = -1f;
            public bool hasSavedPosition;
            public Vector3 savedPosition;
            public bool wasHolding;
            public float releaseDistance;
        }

        private static readonly Dictionary<int, GunThrowState> gunThrowStates = new Dictionary<int, GunThrowState>();

        private static GunThrowState GetGunThrowState(Gun gun)
        {
            int id = gun.GetInstanceID();
            if (!gunThrowStates.TryGetValue(id, out GunThrowState state))
            {
                state = new GunThrowState();
                gunThrowStates[id] = state;
            }
            return state;
        }

        private static bool OverridesThrowDistance =>
            Config.UncapThrowDistance || !Mathf.Approximately(Config.MaxThrowDistance, VanillaThrowDistance);

        private static bool TryGetReturnPoint(Gun gun, out Vector3 point)
        {
            point = Vector3.zero;
            Transform parent = gun.mOriginalRigidBodyParent;
            if (parent == null) return false;
            point = parent.TransformPoint(gun.mOriginalRigidBodyPosition);
            return true;
        }

        /// <summary>Keeps the gun within radius of the return point, dropping any outward velocity at the edge.</summary>
        private static void ClampToRadius(Rigidbody rb, Vector3 center, float radius)
        {
            Vector3 offset = rb.transform.position - center;
            float dist = offset.magnitude;
            if (dist <= radius || dist < 0.0001f) return;

            Vector3 dir = offset / dist;
            rb.transform.position = center + dir * radius;

            Vector3 vel = rb.velocity;
            float outward = Vector3.Dot(vel, dir);
            if (outward > 0f) rb.velocity = vel - dir * outward;
        }

        // Disable Throwing: the throw trick never reports as active, so the launch branch never runs.
        [HarmonyPatch(typeof(Gun), "GetCurrentTrick")]
        private static class GunGetCurrentTrick_DisableThrowing
        {
            private static void Postfix(ref Gun.Trick __result)
            {
                if (Config.DisableThrowing && (int)__result == ThrowTrick)
                    __result = (Gun.Trick)0;
            }
        }

        [HarmonyPatch(typeof(Gun), "Update")]
        private static class GunUpdate_ThrowControl
        {
            private static void Prefix(Gun __instance)
            {
                GunThrowState state = GetGunThrowState(__instance);

                // Throw Speed: kThrowBoost is read by the launch code inside this Update.
                if (state.originalThrowBoost < 0f) state.originalThrowBoost = __instance.kThrowBoost;
                __instance.kThrowBoost = state.originalThrowBoost * (Config.ThrowSpeed / 100f);

                state.hasSavedPosition = false;
                if (!OverridesThrowDistance || !__instance.IsUsingPhysics()) return;

                Rigidbody rb = __instance.rigidBody;
                if (rb == null) return;
                state.savedPosition = rb.transform.position;
                state.hasSavedPosition = true;
            }

            private static void Postfix(Gun __instance)
            {
                GunThrowState state = GetGunThrowState(__instance);

                bool holding = (int)__instance.mLastFrameTrick == ThrowTrick;
                bool onPhysics = __instance.IsUsingPhysics();

                if (!onPhysics) state.releaseDistance = 0f;

                if (!onPhysics || !state.hasSavedPosition)
                {
                    state.wasHolding = onPhysics && holding;
                    return;
                }

                Rigidbody rb = __instance.rigidBody;
                if (rb == null || !TryGetReturnPoint(__instance, out Vector3 center))
                {
                    state.wasHolding = holding;
                    return;
                }

                // Undo the game's 5 m leash for this frame.
                rb.transform.position = state.savedPosition;

                if (holding)
                {
                    if (!Config.UncapThrowDistance)
                        ClampToRadius(rb, center, Config.MaxThrowDistance);
                }
                else
                {
                    // First frame after the throw is released: remember how far out the gun is,
                    // then shrink from there as mRigidBodyReelIn goes 0 -> 1. The <= 0 check covers a
                    // setting changed mid-return, where we never saw the release frame.
                    if (state.wasHolding || state.releaseDistance <= 0f)
                        state.releaseDistance = Vector3.Distance(rb.transform.position, center);

                    float reelIn = Mathf.Clamp01(__instance.mRigidBodyReelIn);
                    ClampToRadius(rb, center, state.releaseDistance * (1f - reelIn));
                }

                state.wasHolding = holding;
            }
        }
    }
}