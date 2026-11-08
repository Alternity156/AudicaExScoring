using Harmony;
using System.Collections.Generic;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// Options > Menu > Menu Aim Assist: temporal aim assist for menu buttons (GunButton).
    ///
    /// How the game handles a menu shot (Gun.Update / Gun.Fire / Gun.GetRewindFirepointIndex, Ghidra):
    ///  - Gun.Update writes one FirepointHistory entry (position, forward, autoaimForward, time =
    ///    realtimeSinceStartup) into the mFirepointHistory ring buffer EVERY frame, menus included.
    ///  - Outside active gameplay, GetRewindFirepointIndex(false) returns the newest entry that is at
    ///    least 30 ms old (during gameplay it returns the latest; FindBestIntersection handles targets).
    ///  - Update casts ONE ray from that entry with mColliderRaycastMask. If it hits an interactable
    ///    GunButton on the trigger-pull frame it calls GunButton.OnHit and then
    ///    Fire(..., forceSuccessful: true, ...). Otherwise Fire runs with target == null,
    ///    forceSuccessful == false and (no toy hit) shotToy == null -> miss beam.
    ///  - With target == null, Fire ignores `intersection`; the beam starts at
    ///    history[firepointHistoryIndex].position and points along its autoaimForward.
    ///
    /// So this Prefix on Fire only acts on shots the game already resolved as "hit nothing": it
    /// searches the other history samples inside the configured window (closest to the game's own
    /// 30 ms sample first), re-casts each with the same mask, and on the first interactable GunButton
    /// it calls OnHit, flips the shot to successful and points the beam at that sample. A real hit is
    /// never overridden and a button can't be fired twice, because the game only reaches this state
    /// when its own raycast found no button.
    /// </summary>
    public partial class ExScoring
    {
        private const float NativeMenuRewindSeconds = 0.03f;

        [HarmonyPatch(typeof(Gun), "Fire")]
        private static class GunFire_MenuAimAssist
        {
            private static void Prefix(
                Gun __instance,
                Target target,
                ref int firepointHistoryIndex,
                ref bool forceSuccessful,
                ShootableToy shotToy)
            {
                if (!Config.MenuAimAssist) return;
                if (__instance == null) return;

                // Only shots the game resolved as "hit nothing".
                if (target != null || forceSuccessful || shotToy != null) return;

                // Never during active gameplay (same test GetRewindFirepointIndex uses). Pause and
                // other InGameUI states still get the assist.
                InGameUI ui = InGameUI.I;
                if (ui != null && (int)ui.GetState() == 0) return;

                // Mirror the cases where Update skips the button raycast / rewind entirely.
                if (__instance.mFingerSpinSpeed > 0f || __instance.IsUsingPhysics()) return;

                // Only real trigger pulls (the trigger is still held on the pull frame).
                if (!__instance.IsTriggerHeld()) return;

                TryRescueMenuShot(__instance, ref firepointHistoryIndex, ref forceSuccessful);
            }
        }

        private static void TryRescueMenuShot(Gun gun, ref int firepointHistoryIndex, ref bool forceSuccessful)
        {
            var history = gun.mFirepointHistory;
            if (history == null) return;

            int len = history.Length;
            if (len <= 0) return;

            int latest = gun.mLatestFirepointHistoryIndex;
            int nativeIndex = firepointHistoryIndex == -1 ? latest : firepointHistoryIndex;
            float now = Time.realtimeSinceStartup;
            float window = Config.MenuAimAssistWindowMs / 1000f;
            int mask = gun.mColliderRaycastMask.value;

            // Gather every sample inside the window, newest to oldest.
            var candidates = new List<KeyValuePair<int, float>>(len);
            for (int i = 0; i < len; i++)
            {
                int idx = ((latest - i) % len + len) % len;
                Gun.FirepointHistory entry = history[idx];
                if (entry == null) continue;

                float age = now - entry.time;
                if (age > window) break; // ring buffer: everything past this is older
                if (idx == nativeIndex) continue; // the game already tried this one

                // Rank by distance from the game's own 30 ms sample, so the result stays as close
                // as possible to what the game would have picked.
                candidates.Add(new KeyValuePair<int, float>(idx, Mathf.Abs(age - NativeMenuRewindSeconds)));
            }

            if (candidates.Count == 0) return;
            candidates.Sort((a, b) => a.Value.CompareTo(b.Value));

            foreach (var candidate in candidates)
            {
                Gun.FirepointHistory entry = history[candidate.Key];
                if (entry == null) continue;

                RaycastHit hit;
                if (!Physics.Raycast(entry.position, entry.forward, out hit, float.PositiveInfinity, mask)) continue;

                Collider col = hit.collider;
                if (col == null) continue;

                GunButton button = col.GetComponent<GunButton>();
                if (button == null || !button.IsInteractable()) continue;

                DebugLog.Log($"[MenuAimAssist] Rescued miss -> '{button.gameObject.name}' " +
                             $"(sample {candidate.Key}, {(now - entry.time) * 1000f:N0} ms old)");

                button.OnHit(hit.point, gun);
                forceSuccessful = true;
                firepointHistoryIndex = candidate.Key; // success beam comes from the rescued aim
                return;
            }
        }
    }
}