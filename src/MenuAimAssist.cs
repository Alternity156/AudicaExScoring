using Harmony;
using System.Collections.Generic;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// Options > Menu > Menu Aim Assist: temporal aim assist for menu buttons (GunButton).
    ///
    /// How the game handles a menu shot (Gun.Update, Ghidra dump 2026-11-10; field offsets from the
    /// ILSpy field order, checked against firepoint 0x18, mGrabBeam 0x1B0, mRaycastHitInfo 0x220,
    /// mFirepointHistory 0x250, isSetup 0x2B0):
    ///  1. The press is worked out inline (UpdateTrigger isn't called there): unless mfadeStopFire,
    ///     pressed = IsTriggerHeld() && !mWasTriggerHeld && |mLastFireTime - realtimeSinceStartup| > 0.05.
    ///     On a press mLastFireTime = now; mWasTriggerHeld = IsTriggerHeld() every frame.
    ///  2. One FirepointHistory entry (position, forward, autoaimForward, time = realtimeSinceStartup)
    ///     is written into the mFirepointHistory ring buffer, every frame, menus included.
    ///  3. GetRewindFirepointIndex(false), every frame. Outside active gameplay it returns the newest
    ///     entry at least 30 ms old.
    ///  4. While the gun isn't thrown and |GetFingerSpinSpeed| &lt; 1000, ONE ray from that entry
    ///     (position, forward) with mColliderRaycastMask: an interactable GunButton is highlighted;
    ///     on the press frame a GunButton gets OnHit (the shot is then successful), otherwise the
    ///     hit's ShootableToy (if any) is the shotToy.
    ///  5. On the press frame, Fire(target, intersection, that index, successful, false, shotToy).
    ///
    /// So the assist steers step 3 on press frames only: a Gun.Update prefix records the press with
    /// step 1's own test (before the game updates those fields), and the GetRewindFirepointIndex
    /// postfix, when the game's own sample hits no GunButton and no ShootableToy, returns the
    /// closest-in-age sample inside the configured window whose ray hits an interactable GunButton.
    /// The game's own raycast then hits that button and the game does OnHit and the successful Fire
    /// itself. A real hit is never overridden, hover highlighting stays on the game's sample, and
    /// nothing is fired twice.
    ///
    /// Gun.Fire is deliberately NOT patched: under MelonLoader 0.5.7 any Harmony patch on it (even an
    /// empty prefix) breaks the no-target branch: rebounds off the floor disc, menu glass and thrown
    /// guns, and the misfire health penalty (PreciseTiming.cs, confirmed in game 2026-10-20).
    /// </summary>
    public partial class ExScoring
    {
        private const float NativeMenuRewindSeconds = 0.03f;
        private const float NativePressDebounceSeconds = 0.05f; // |mLastFireTime - now| > 0.05
        private const float NativeMaxRaycastSpinSpeed = 1000f;  // |GetFingerSpinSpeed| < 1000

        // The gun that presses this frame (step 1), consumed by the first GetRewindFirepointIndex call.
        private static System.IntPtr menuAssistPressGun = System.IntPtr.Zero;
        private static int menuAssistPressFrame = -1;

        /// <summary>Step 1's own press test, before the game changes mWasTriggerHeld / mLastFireTime.</summary>
        [HarmonyPatch(typeof(Gun), "Update")]
        private static class GunUpdate_MenuAimAssistPress
        {
            private static void Prefix(Gun __instance)
            {
                if (!Config.MenuAimAssist || __instance == null) return;
                try
                {
                    if (!__instance.isSetup || __instance.mfadeStopFire) return;
                    if (!__instance.IsTriggerHeld() || __instance.mWasTriggerHeld) return;
                    if (Mathf.Abs(__instance.mLastFireTime - Time.realtimeSinceStartup) <= NativePressDebounceSeconds) return;
                    menuAssistPressGun = __instance.Pointer;
                    menuAssistPressFrame = Time.frameCount;
                }
                catch (System.Exception ex)
                {
                    DebugLog.Log($"[MenuAimAssist] press check failed: {ex.Message}");
                }
            }
        }

        /// <summary>Step 3 on a press frame: point the game's one raycast at a button you just were on.</summary>
        [HarmonyPatch(typeof(Gun), "GetRewindFirepointIndex", new System.Type[] { typeof(bool) })]
        private static class GunGetRewindFirepointIndex_MenuAimAssist
        {
            private static void Postfix(Gun __instance, bool forceLatest, ref int __result)
            {
                if (__instance == null || forceLatest) return;
                if (menuAssistPressFrame != Time.frameCount || menuAssistPressGun != __instance.Pointer) return;
                menuAssistPressGun = System.IntPtr.Zero; // once per press
                menuAssistPressFrame = -1;

                if (!Config.MenuAimAssist) return;
                try
                {
                    // Never during active gameplay. Pause and other InGameUI states still get the assist.
                    InGameUI ui = InGameUI.I;
                    if (ui != null && (int)ui.GetState() == 0) return;

                    // Thrown or physics guns: the game recalls the gun (or drops the press) and the
                    // firepoint isn't the hand's; spinning guns skip the raycast altogether.
                    if (__instance.IsUsingPhysics()) return;
                    Rigidbody body = __instance.rigidBody;
                    if (body == null || body.transform.parent == null) return;
                    bool spinning = __instance.GetCurrentTrick() == Gun.Trick.Spin;
                    if (Mathf.Abs(__instance.GetFingerSpinSpeed(spinning)) >= NativeMaxRaycastSpinSpeed) return;

                    int rescued = FindRescueSample(__instance, __result);
                    if (rescued >= 0) __result = rescued;
                }
                catch (System.Exception ex)
                {
                    DebugLog.Log($"[MenuAimAssist] rescue failed: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// The history index to use instead of nativeIndex, or -1 to keep the game's own: -1 when the
        /// game's sample already hits a GunButton or a ShootableToy, or when no other sample inside the
        /// window hits an interactable GunButton.
        /// </summary>
        private static int FindRescueSample(Gun gun, int nativeIndex)
        {
            var history = gun.mFirepointHistory;
            if (history == null) return -1;

            int len = history.Length;
            if (len <= 0 || nativeIndex < 0 || nativeIndex >= len) return -1;

            int mask = gun.mColliderRaycastMask.value;
            RaycastHit hit;

            // The game's own sample: a button (even a non-interactable one, the game still calls its
            // OnHit) or a thrown gun is a real hit and stays.
            Gun.FirepointHistory native = history[nativeIndex];
            if (native == null) return -1;
            if (Physics.Raycast(native.position, native.forward, out hit, float.PositiveInfinity, mask))
            {
                Collider nativeCol = hit.collider;
                if (nativeCol != null &&
                    (nativeCol.GetComponent<GunButton>() != null || nativeCol.GetComponent<ShootableToy>() != null))
                    return -1;
            }

            int latest = gun.mLatestFirepointHistoryIndex;
            float now = Time.realtimeSinceStartup;
            float window = Config.MenuAimAssistWindowMs / 1000f;

            // Every sample inside the window, newest to oldest.
            var candidates = new List<KeyValuePair<int, float>>(len);
            for (int i = 0; i < len; i++)
            {
                int idx = ((latest - i) % len + len) % len;
                Gun.FirepointHistory entry = history[idx];
                if (entry == null) continue;

                float age = now - entry.time;
                if (age > window) break; // ring buffer: everything past this is older
                if (idx == nativeIndex) continue; // checked above

                // Closest to the game's own 30 ms sample first, so the result stays as close as
                // possible to what the game would have picked.
                candidates.Add(new KeyValuePair<int, float>(idx, Mathf.Abs(age - NativeMenuRewindSeconds)));
            }

            if (candidates.Count == 0) return -1;
            candidates.Sort((a, b) => a.Value.CompareTo(b.Value));

            foreach (var candidate in candidates)
            {
                Gun.FirepointHistory entry = history[candidate.Key];
                if (entry == null) continue;
                if (!Physics.Raycast(entry.position, entry.forward, out hit, float.PositiveInfinity, mask)) continue;

                Collider col = hit.collider;
                if (col == null) continue;

                GunButton button = col.GetComponent<GunButton>();
                if (button == null || !button.IsInteractable()) continue;

                DebugLog.Log($"[MenuAimAssist] Rescued miss -> '{button.gameObject.name}' " +
                             $"(sample {candidate.Key}, {(now - entry.time) * 1000f:N0} ms old)");
                return candidate.Key;
            }
            return -1;
        }
    }
}