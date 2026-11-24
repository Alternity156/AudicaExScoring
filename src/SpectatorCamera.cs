using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Harmony;
using MelonLoader;
using UnhollowerBaseLib;      // TryCast
using UnhollowerRuntimeLib;   // Il2CppType (ML 0.5.7)
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// Spectator camera overrides, on Options > Spectator Camera. Findings behind it:
    /// claude/Spectator-Camera-Findings.md (2026-11-23).
    ///
    /// Camera Override:
    ///  - Off: the game's own spectator camera, untouched.
    ///  - Follow: AudicaFollowCamera by Continuum, ported. It sits behind and beside the headset,
    ///    follows its yaw at a fixed height, and is smoothed over time (frame-rate independent).
    ///    Written in a SpectatorCam.Update Postfix from our own smoothed pose, so the native
    ///    static-camera easing never leaks in.
    ///  - Static: the game's own 3rd person static camera, which already reads SpectatorCamX/Y/Z/
    ///    Pitch/Yaw/Roll every frame (Y + 1). This page edits those prefs: sliders, a grip-grab of
    ///    the game's camera model (thirdPersonCameraVisualization, it has a collider), and 3 preset
    ///    slots. While grabbed, the camera is snapped to the hand (no track-speed lag).
    ///
    /// Picking Follow or Static switches the game to 3rd person static (SpectatorCamMode 1 +
    /// UpdateSettings, as the game's own mode button does) and turns the spectator cam on. Picking
    /// Off restores the mode from before.
    ///
    /// While this page is open, the game's spectator preview (previewCam + previewCamDisplay) is
    /// forced on and the display is moved onto our cloned options panel, at the same spot relative
    /// to it as on the native settings panel. The game only shows it on its own Spectator page,
    /// which our clone never is. It's put back as soon as the page closes. While shown, it's shaped
    /// like the game window (its render texture is swapped for a matching one if needed) and uses
    /// the spectator camera's field of view, so it matches the desktop view.
    ///
    /// AudicaFollowCamera (FollowCamera.dll) must be removed: its Update Prefix turns the native
    /// update off whenever it isn't active. Its Mods/Config/FollowCamera.json is imported once.
    /// </summary>
    internal static class SpectatorCamera
    {
        public const string CategoryId = "opt_spectatorcam";

        public const int ModeOff = 0, ModeFollow = 1, ModeStatic = 2;
        public static readonly string[] ModeOptions = { "Off", "Follow", "Static" };

        public const int PresetCount = 3;

        // Native CamType values (SpectatorCam.CamType / SpectatorCamMode as a float).
        private const float NativeFirstPerson = 0f;
        private const float NativeThirdPerson = 1f;

        // The native static camera adds 1 m to SpectatorCamY (Ghidra SpectatorCam.Update).
        private const float NativeYOffset = 1f;

        private const string SettingsCenterPath = "ShellPage_Settings/page/ShellPanel_Center"; // = OptionsMenuClone's
        private const float GripAxisThreshold = 0.5f;
        private const float GrabRayLength = 30f;
        private const float GrabTouchDistance = 0.35f;
        private const float GunLookupSeconds = 1f;

        // ── Prefs ────────────────────────────────────────────────────────────
        private const string P = "SpectatorCam"; // key prefix (all hidden: empty description)

        public static int Mode;
        private static float prevNativeMode;      // SpectatorCamMode before an override was picked
        private static bool prevNativeOn;         // SpectatorCam (on/off) before an override was picked

        // Follow, as in AudicaFollowCamera, but speeds are per second (1 - e^(-speed x dt) a frame).
        public static float FollowPositionSpeed;
        public static float FollowRotationSpeed;
        public static float FollowOffset;   // m, to the right of the headset
        public static float FollowHeight;   // m, absolute
        public static float FollowDistance; // m, behind the headset
        public static float FollowTilt;     // degrees, pitch

        public const float DefaultFollowPositionSpeed = 0.45f; // ≈ AudicaFollowCamera's 0.005 a frame at 90 Hz
        public const float DefaultFollowRotationSpeed = 0.45f;
        public const float DefaultFollowOffset = 5f;
        public const float DefaultFollowHeight = 1f;
        public const float DefaultFollowDistance = 5f;
        public const float DefaultFollowTilt = 0f;

        private static readonly string[] PresetFields = { "X", "Y", "Z", "Pitch", "Yaw", "Roll", "Fov" };

        public static void Register()
        {
            string c = Config.Category;
            MelonPrefs.RegisterInt(c, P + "Mode", ModeOff, "");
            MelonPrefs.RegisterFloat(c, P + "PrevNativeMode", NativeThirdPerson, "");
            MelonPrefs.RegisterBool(c, P + "PrevNativeOn", false, "");

            MelonPrefs.RegisterFloat(c, P + "FollowPositionSpeed", DefaultFollowPositionSpeed, "");
            MelonPrefs.RegisterFloat(c, P + "FollowRotationSpeed", DefaultFollowRotationSpeed, "");
            MelonPrefs.RegisterFloat(c, P + "FollowOffset", DefaultFollowOffset, "");
            MelonPrefs.RegisterFloat(c, P + "FollowHeight", DefaultFollowHeight, "");
            MelonPrefs.RegisterFloat(c, P + "FollowDistance", DefaultFollowDistance, "");
            MelonPrefs.RegisterFloat(c, P + "FollowTilt", DefaultFollowTilt, "");
            MelonPrefs.RegisterBool(c, P + "FollowCameraImported", false, "");

            for (int i = 0; i < PresetCount; i++)
            {
                MelonPrefs.RegisterBool(c, PresetKey(i, "Saved"), false, "");
                foreach (string f in PresetFields)
                    MelonPrefs.RegisterFloat(c, PresetKey(i, f), 0f, "");
            }
        }

        public static void Load()
        {
            string c = Config.Category;
            Mode = Mathf.Clamp(MelonPrefs.GetInt(c, P + "Mode"), ModeOff, ModeStatic);
            prevNativeMode = MelonPrefs.GetFloat(c, P + "PrevNativeMode");
            prevNativeOn = MelonPrefs.GetBool(c, P + "PrevNativeOn");

            FollowPositionSpeed = MelonPrefs.GetFloat(c, P + "FollowPositionSpeed");
            FollowRotationSpeed = MelonPrefs.GetFloat(c, P + "FollowRotationSpeed");
            FollowOffset = MelonPrefs.GetFloat(c, P + "FollowOffset");
            FollowHeight = MelonPrefs.GetFloat(c, P + "FollowHeight");
            FollowDistance = MelonPrefs.GetFloat(c, P + "FollowDistance");
            FollowTilt = MelonPrefs.GetFloat(c, P + "FollowTilt");

            if (!MelonPrefs.GetBool(c, P + "FollowCameraImported"))
                ImportFollowCameraConfig();
        }

        private static string PresetKey(int slot, string field) => P + "Preset" + (slot + 1) + field;

        private static void SaveFollow()
        {
            string c = Config.Category;
            MelonPrefs.SetFloat(c, P + "FollowPositionSpeed", FollowPositionSpeed);
            MelonPrefs.SetFloat(c, P + "FollowRotationSpeed", FollowRotationSpeed);
            MelonPrefs.SetFloat(c, P + "FollowOffset", FollowOffset);
            MelonPrefs.SetFloat(c, P + "FollowHeight", FollowHeight);
            MelonPrefs.SetFloat(c, P + "FollowDistance", FollowDistance);
            MelonPrefs.SetFloat(c, P + "FollowTilt", FollowTilt);
            MelonPrefs.SaveConfig();
        }

        /// <summary>
        /// Mods/Config/FollowCamera.json from AudicaFollowCamera, once. Its smoothing was a fraction
        /// a frame (Slerp factor); converted to a per-second speed at 90 Hz: -ln(1 - f) x 90.
        /// </summary>
        private static void ImportFollowCameraConfig()
        {
            string c = Config.Category;
            MelonPrefs.SetBool(c, P + "FollowCameraImported", true);
            try
            {
                string path = Path.Combine(Application.dataPath, "../Mods/Config/FollowCamera.json");
                if (!File.Exists(path)) { MelonPrefs.SaveConfig(); return; }
                string json = File.ReadAllText(path);

                if (TryJsonFloat(json, "positionSmoothing", out float ps)) FollowPositionSpeed = SmoothingToSpeed(ps);
                if (TryJsonFloat(json, "rotationSmoothing", out float rs)) FollowRotationSpeed = SmoothingToSpeed(rs);
                if (TryJsonFloat(json, "camOffset", out float off)) FollowOffset = off;
                if (TryJsonFloat(json, "camHeight", out float h)) FollowHeight = h;
                if (TryJsonFloat(json, "camDistance", out float d)) FollowDistance = d;
                if (TryJsonFloat(json, "camRotation", out float t)) FollowTilt = t;
                SaveFollow();
                MelonLogger.Log("[SpectatorCamera] imported FollowCamera.json settings");
            }
            catch (Exception ex)
            {
                MelonLogger.Log("[SpectatorCamera] FollowCamera.json import failed: " + ex.Message);
                MelonPrefs.SaveConfig();
            }
        }

        private static bool TryJsonFloat(string json, string key, out float value)
        {
            value = 0f;
            Match m = Regex.Match(json, "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"?(-?[0-9]*\\.?[0-9]+(?:[eE][-+]?[0-9]+)?)");
            return m.Success && float.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static float SmoothingToSpeed(float perFrame)
        {
            perFrame = Mathf.Clamp(perFrame, 0.0001f, 0.99f);
            return Mathf.Clamp((float)(-Math.Log(1.0 - perFrame) * 90.0), 0.05f, 20f);
        }

        // ── State ────────────────────────────────────────────────────────────

        private static SpectatorCam spectator;
        private static float nextSpectatorLookup;

        private static bool followInitialized;
        private static Vector3 followPos;
        private static Quaternion followRot = Quaternion.identity;

        private static Gun gunL, gunR;
        private static float nextGunLookup;
        private static readonly bool[] grabWasDown = new bool[2];
        private static int grabHand = -1;
        private static Vector3 grabLocalPos;
        private static Quaternion grabLocalRot = Quaternion.identity;

        // Preview display moved onto our panel (restored when the page closes).
        private static bool previewMoved;
        private static Transform previewOrigParent;
        private static Vector3 previewOrigLocalPos, previewOrigLocalScale;
        private static Quaternion previewOrigLocalRot;
        private static Vector3 previewBaseScale;

        private static PlayerPreferences Prefs => PlayerPreferences.I;

        /// <summary>True while Options > Spectator Camera is drawn on our clone.</summary>
        private static bool PageShowing =>
            GlobalOptions.IsShowing(CategoryId)
            && OptionsMenuClone.Menu != null
            && OptionsMenuClone.Menu.gameObject.activeInHierarchy;

        private static SpectatorCam FindSpectator()
        {
            if (spectator != null) return spectator;
            if (Time.unscaledTime < nextSpectatorLookup) return null;
            nextSpectatorLookup = Time.unscaledTime + 1f;
            try
            {
                foreach (UnityEngine.Object o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<SpectatorCam>()))
                {
                    SpectatorCam candidate = o.TryCast<SpectatorCam>();
                    if (candidate == null || !candidate.gameObject.scene.IsValid()) continue; // skip prefabs
                    spectator = candidate;
                    break;
                }
            }
            catch (Exception ex)
            {
                DebugLog.Log("[SpectatorCamera] SpectatorCam lookup failed: " + ex.Message);
            }
            return spectator;
        }

        // ── Mode ─────────────────────────────────────────────────────────────

        public static void SetMode(int mode)
        {
            mode = Mathf.Clamp(mode, ModeOff, ModeStatic);
            if (mode == Mode) return;
            int old = Mode;
            string c = Config.Category;

            EndGrab(false);
            followInitialized = false;

            try
            {
                PlayerPreferences prefs = Prefs;
                if (prefs != null)
                {
                    if (old == ModeOff)
                    {
                        // Remember the game's own setup, to put it back on Off.
                        prevNativeMode = prefs.SpectatorCamMode.mVal;
                        prevNativeOn = prefs.SpectatorCam.mVal;
                        MelonPrefs.SetFloat(c, P + "PrevNativeMode", prevNativeMode);
                        MelonPrefs.SetBool(c, P + "PrevNativeOn", prevNativeOn);
                    }

                    if (mode != ModeOff)
                    {
                        if (!prefs.SpectatorCam.mVal) prefs.SpectatorCam.Set(true);
                        prefs.SpectatorCamMode.Set(NativeThirdPerson);
                    }
                    else
                    {
                        prefs.SpectatorCamMode.Set(prevNativeMode);
                        if (prefs.SpectatorCam.mVal != prevNativeOn) prefs.SpectatorCam.Set(prevNativeOn);
                    }

                    SpectatorCam cam = FindSpectator();
                    if (cam != null)
                    {
                        if (mode != ModeOff && !cam.gameObject.activeSelf) cam.gameObject.SetActive(true);
                        cam.UpdateSettings();
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Log("[SpectatorCamera] switching the native mode failed: " + ex.Message);
            }

            Mode = mode;
            MelonPrefs.SetInt(c, P + "Mode", Mode);
            MelonPrefs.SaveConfig();
            DebugLog.Log($"[SpectatorCamera] mode {ModeOptions[old]} -> {ModeOptions[Mode]}");

            GlobalOptions.RedrawIfShowing(CategoryId); // the rows depend on the mode
        }

        // ── SpectatorCam.Update Postfix ──────────────────────────────────────

        [HarmonyPatch(typeof(SpectatorCam), "Update")]
        private static class SpectatorCamUpdatePatch
        {
            private static void Postfix(SpectatorCam __instance)
            {
                try
                {
                    OnSpectatorUpdate(__instance);
                }
                catch (Exception ex)
                {
                    DebugLog.Log("[SpectatorCamera] update failed: " + ex.Message);
                }
            }
        }

        private static void OnSpectatorUpdate(SpectatorCam cam)
        {
            spectator = cam;
            bool page = PageShowing;
            Transform t = cam.transform;

            // The native code already wrote this frame's pose; overrides go on top. Not in mirror
            // mode, nor when the game's mode button switched to first person.
            bool overridable = !cam.mMirrorMode && cam.mCamType != SpectatorCam.CamType.FirstPerson;

            if (Mode == ModeFollow && overridable) ApplyFollow(t);
            else followInitialized = false;

            if (Mode == ModeStatic && page && overridable)
            {
                ShowCameraModel(cam);
                UpdateGrab(cam, t);
            }
            else if (grabHand >= 0)
            {
                EndGrab(true);
            }

            if (page) ForcePreview(cam);
            else RestorePreview(cam);
        }

        // ── Follow ───────────────────────────────────────────────────────────

        private static Transform Headset()
        {
            Camera main = Camera.main;
            return main != null ? main.transform : null;
        }

        private static void ApplyFollow(Transform t)
        {
            Transform head = Headset();
            if (head == null) return;

            Vector3 forward = head.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f) forward = head.up * -1f; // looking straight up/down
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.0001f) forward = Vector3.forward;
            forward.Normalize();
            Vector3 right = Vector3.Cross(Vector3.up, forward);

            Vector3 target = head.position + right * FollowOffset - forward * FollowDistance;
            target.y = FollowHeight;
            Quaternion targetRot = Quaternion.Euler(FollowTilt, Quaternion.LookRotation(forward).eulerAngles.y, 0f);

            if (!followInitialized)
            {
                followPos = target;
                followRot = targetRot;
                followInitialized = true;
            }
            else
            {
                float dt = Time.deltaTime;
                followPos = Vector3.Lerp(followPos, target, 1f - Mathf.Exp(-FollowPositionSpeed * dt));
                followRot = Quaternion.Slerp(followRot, targetRot, 1f - Mathf.Exp(-FollowRotationSpeed * dt));
            }

            t.position = followPos;
            t.rotation = followRot;
        }

        // ── Static: native pose ──────────────────────────────────────────────

        /// <summary>The native static camera's target pose (world), from its prefs.</summary>
        private static bool TryGetNativePose(out Vector3 position, out Quaternion rotation)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;
            PlayerPreferences prefs = Prefs;
            if (prefs == null) return false;
            position = new Vector3(prefs.SpectatorCamX.mVal, prefs.SpectatorCamY.mVal + NativeYOffset, prefs.SpectatorCamZ.mVal);
            rotation = Quaternion.Euler(prefs.SpectatorCamPitch.mVal, prefs.SpectatorCamYaw.mVal, prefs.SpectatorCamRoll.mVal);
            return true;
        }

        /// <summary>
        /// Writes a world pose to the native static camera prefs. commit: through FloatPref.Set (the
        /// game's own save path); otherwise mVal only (every frame while grabbed), clamped the same way.
        /// </summary>
        private static void SetNativePose(Vector3 position, Quaternion rotation, bool commit)
        {
            PlayerPreferences prefs = Prefs;
            if (prefs == null) return;
            Vector3 e = rotation.eulerAngles;
            WritePref(prefs.SpectatorCamX, position.x, false, commit);
            WritePref(prefs.SpectatorCamY, position.y - NativeYOffset, false, commit);
            WritePref(prefs.SpectatorCamZ, position.z, false, commit);
            WritePref(prefs.SpectatorCamPitch, e.x, true, commit);
            WritePref(prefs.SpectatorCamYaw, e.y, true, commit);
            WritePref(prefs.SpectatorCamRoll, e.z, true, commit);
        }

        private static void WritePref(PlayerPreferences.FloatPref pref, float value, bool angle, bool commit)
        {
            if (pref == null) return;
            float min = pref.mMin, max = pref.mMax;
            bool ranged = max > min;
            if (angle)
            {
                value = Mathf.DeltaAngle(0f, value); // -180..180
                if (ranged && max - min >= 359f)
                {
                    while (value < min) value += 360f;
                    while (value > max) value -= 360f;
                }
            }
            if (ranged) value = Mathf.Clamp(value, min, max);
            if (commit) pref.Set(value);
            else pref.mVal = value;
        }

        // ── Static: grabbing the camera model ────────────────────────────────

        private static void ShowCameraModel(SpectatorCam cam)
        {
            GameObject model = cam.thirdPersonCameraVisualization;
            if (model != null && !model.activeSelf) model.SetActive(true);
        }

        private static void FindGuns()
        {
            if (gunL != null && gunR != null) return;
            if (Time.unscaledTime < nextGunLookup) return;
            nextGunLookup = Time.unscaledTime + GunLookupSeconds;

            var guns = UnityEngine.Object.FindObjectsOfType<Gun>();
            for (int i = 0; i < guns.Length; i++)
            {
                Gun gun = guns[i];
                if (gun == null) continue;
                if (gun.name == "GunLeft") gunL = gun;
                else if (gun.name == "GunRight") gunR = gun;
            }
        }

        /// <summary>The grip, same check as SpectatorWindow.</summary>
        private static bool GrabDown(Gun gun)
        {
            try
            {
                OVRInput.Controller controller = gun.GetOVRInputControllerType();
                if (KataInput.GripButtonHeld(controller)) return true;
                return KataInput.GetAxis(KataInput.Axis.Grip, controller) >= GripAxisThreshold;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>The gun's firepoint ray hits the camera model's colliders, or the gun touches it.</summary>
        private static bool PointsAtCamera(Gun gun, SpectatorCam cam, Transform t)
        {
            GameObject model = cam.thirdPersonCameraVisualization;
            Vector3 centre = model != null ? model.transform.position : t.position;
            if ((gun.transform.position - centre).sqrMagnitude < GrabTouchDistance * GrabTouchDistance) return true;
            if (model == null) return false;

            Transform firepoint = gun.firepoint;
            if (firepoint == null) firepoint = gun.transform;
            Ray ray = new Ray(firepoint.position, firepoint.forward);

            var colliders = model.GetComponentsInChildren<Collider>(false);
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];
                if (collider == null || !collider.enabled) continue;
                if (collider.Raycast(ray, out RaycastHit hit, GrabRayLength)) return true;
            }
            return false;
        }

        /// <summary>
        /// Grip while pointing at (or touching) the camera model grabs it. It keeps its pose relative
        /// to the gun until the grip is released; the prefs follow every frame and are committed on release.
        /// </summary>
        private static void UpdateGrab(SpectatorCam cam, Transform t)
        {
            FindGuns();
            for (int h = 0; h < 2; h++)
            {
                Gun gun = h == 0 ? gunL : gunR;
                if (gun == null)
                {
                    grabWasDown[h] = false;
                    if (grabHand == h) EndGrab(true);
                    continue;
                }

                bool down = GrabDown(gun);
                bool pressed = down && !grabWasDown[h];
                grabWasDown[h] = down;

                if (grabHand == h)
                {
                    if (!down) { EndGrab(true); continue; }
                    Transform g = gun.transform;
                    Vector3 pos = g.position + g.rotation * grabLocalPos;
                    Quaternion rot = g.rotation * grabLocalRot;
                    SetNativePose(pos, rot, false);
                    // Snap (no track-speed lag), to the clamped pose the native code will aim for.
                    if (TryGetNativePose(out Vector3 clampedPos, out Quaternion clampedRot))
                    {
                        t.position = clampedPos;
                        t.rotation = clampedRot;
                    }
                }
                else if (grabHand < 0 && pressed && PointsAtCamera(gun, cam, t))
                {
                    Transform g = gun.transform;
                    Quaternion inverse = Quaternion.Inverse(g.rotation);
                    grabLocalPos = inverse * (t.position - g.position);
                    grabLocalRot = inverse * t.rotation;
                    grabHand = h;
                    DebugLog.Log("[SpectatorCamera] grabbed with " + (h == 0 ? "left" : "right"));
                }
            }
        }

        private static void EndGrab(bool commit)
        {
            if (grabHand < 0) return;
            grabHand = -1;
            if (!commit) return;
            if (TryGetNativePose(out Vector3 pos, out Quaternion rot))
                SetNativePose(pos, rot, true);
            DebugLog.Log("[SpectatorCamera] released, pose saved");
            GlobalOptions.RedrawIfShowing(CategoryId); // slider values changed
        }

        // ── Static: buttons ──────────────────────────────────────────────────

        private static void LevelHorizon()
        {
            PlayerPreferences prefs = Prefs;
            if (prefs == null) return;
            prefs.SpectatorCamRoll.Set(0f);
            GlobalOptions.RedrawIfShowing(CategoryId);
        }

        private static void LookAtPlayer()
        {
            Transform head = Headset();
            if (head == null || !TryGetNativePose(out Vector3 pos, out Quaternion _)) return;
            Vector3 dir = head.position - pos;
            if (dir.sqrMagnitude < 0.0001f) return;
            SetNativePose(pos, Quaternion.LookRotation(dir, Vector3.up), true);
            LevelHorizon(); // LookRotation already has no roll; keeps the pref exactly 0
        }

        private static bool PresetSaved(int slot) => MelonPrefs.GetBool(Config.Category, PresetKey(slot, "Saved"));

        private static void SavePreset(int slot)
        {
            PlayerPreferences prefs = Prefs;
            if (prefs == null) return;
            string c = Config.Category;
            float[] values =
            {
                prefs.SpectatorCamX.mVal, prefs.SpectatorCamY.mVal, prefs.SpectatorCamZ.mVal,
                prefs.SpectatorCamPitch.mVal, prefs.SpectatorCamYaw.mVal, prefs.SpectatorCamRoll.mVal,
                prefs.SpectatorCamFOV.mVal
            };
            for (int i = 0; i < PresetFields.Length; i++)
                MelonPrefs.SetFloat(c, PresetKey(slot, PresetFields[i]), values[i]);
            MelonPrefs.SetBool(c, PresetKey(slot, "Saved"), true);
            MelonPrefs.SaveConfig();
            DebugLog.Log($"[SpectatorCamera] preset {slot + 1} saved");
            GlobalOptions.RedrawIfShowing(CategoryId);
        }

        private static void LoadPreset(int slot)
        {
            PlayerPreferences prefs = Prefs;
            if (prefs == null || !PresetSaved(slot)) return;
            string c = Config.Category;
            Func<string, float> get = f => MelonPrefs.GetFloat(c, PresetKey(slot, f));
            prefs.SpectatorCamX.Set(get("X"));
            prefs.SpectatorCamY.Set(get("Y"));
            prefs.SpectatorCamZ.Set(get("Z"));
            prefs.SpectatorCamPitch.Set(get("Pitch"));
            prefs.SpectatorCamYaw.Set(get("Yaw"));
            prefs.SpectatorCamRoll.Set(get("Roll"));
            prefs.SpectatorCamFOV.Set(get("Fov"));
            DebugLog.Log($"[SpectatorCamera] preset {slot + 1} loaded");
            if (Mode != ModeStatic) SetMode(ModeStatic); // redraws
            else GlobalOptions.RedrawIfShowing(CategoryId);
        }

        // ── Preview ──────────────────────────────────────────────────────────

        /// <summary>
        /// Every frame while the page is open (the native code turns it off every frame, since its
        /// condition is the native options page). The display is moved under our clone once.
        /// It's shaped like the game window and uses the spectator camera's field of view, so it
        /// shows what's on the desktop (re-checked every frame, so resizing the window follows).
        /// </summary>
        private static void ForcePreview(SpectatorCam cam)
        {
            Camera preview = cam.previewCam;
            GameObject display = cam.previewCamDisplay;
            if (preview == null || display == null) return;

            if (!previewMoved) MovePreviewOntoClone(display);

            if (!preview.gameObject.activeSelf) preview.gameObject.SetActive(true);
            if (!display.activeSelf) display.SetActive(true);

            Camera main = cam.cam;
            float aspect = WindowAspect(main);
            if (aspect <= 0.01f) return;

            if (main != null) preview.fieldOfView = main.fieldOfView;

            // Shape first: a failed texture swap mustn't skip it.
            ApplyPreviewShape(display, aspect);

            try
            {
                MatchPreviewTexture(preview, display, aspect);
            }
            catch (Exception ex)
            {
                DebugLog.Log("[SpectatorCamera] preview texture swap failed: " + ex.Message);
            }
            preview.aspect = aspect; // after any texture swap (that resets the aspect)
        }

        // Which of the display's local axes the screen's width and height lie along, and the
        // screen's unscaled size along each (from its mesh bounds). Found once per page opening.
        private static bool shapeKnown;
        private static int shapeWidthAxis, shapeHeightAxis;
        private static float shapeWidthLength, shapeHeightLength;
        private static Vector3 shapeBaseScale;

        /// <summary>
        /// Scales the display so its world width / height equals the window's. The screen mesh may be
        /// a quad (height along local Y) or a plane (local Z), possibly on a rotated child, so the
        /// axes are read from the mesh rather than assumed. Width keeps its scale; height is set.
        /// </summary>
        private static void ApplyPreviewShape(GameObject display, float aspect)
        {
            Transform d = display.transform;
            if (!shapeKnown && !FindPreviewShape(display)) return;

            Vector3 scale = shapeBaseScale;
            float width = scale[shapeWidthAxis] * shapeWidthLength;
            if (shapeHeightLength <= 0f || width <= 0f) return;
            scale[shapeHeightAxis] = width / (shapeHeightLength * aspect);
            d.localScale = scale;
        }

        private static bool FindPreviewShape(GameObject display)
        {
            Transform d = display.transform;
            MeshFilter filter = display.GetComponentInChildren<MeshFilter>(true);
            Mesh mesh = filter != null ? filter.sharedMesh : null;
            if (mesh == null)
            {
                DebugLog.Log("[SpectatorCamera] preview display has no mesh, shape not matched");
                return false;
            }

            shapeBaseScale = previewMoved ? previewBaseScale : d.localScale;
            Vector3 size = mesh.bounds.size;
            Transform m = filter.transform;

            // Each mesh axis, as a vector in the display's local units (the display's own scale taken
            // out by InverseTransformVector, so world length = local length x localScale on that axis).
            // A flat screen has two: height is the one closest to vertical in the world, width the other.
            int count = 0;
            int[] along = new int[3];
            float[] length = new float[3], up = new float[3];
            for (int i = 0; i < 3; i++)
            {
                Vector3 axis = Vector3.zero;
                axis[i] = size[i];
                Vector3 world = m.TransformVector(axis);
                if (world.sqrMagnitude < 0.00000001f) continue; // the flat side
                Vector3 local = d.InverseTransformVector(world);
                Vector3 abs = new Vector3(Mathf.Abs(local.x), Mathf.Abs(local.y), Mathf.Abs(local.z));
                along[count] = abs.x >= abs.y && abs.x >= abs.z ? 0 : (abs.y >= abs.z ? 1 : 2);
                length[count] = local.magnitude;
                up[count] = Mathf.Abs(Vector3.Dot(world.normalized, Vector3.up));
                count++;
            }

            int wAxis = 0, hAxis = 0;
            float wLen = 0f, hLen = 0f;
            if (count >= 2)
            {
                // The two longest mesh axes are the screen; if there are three, drop the shortest.
                int drop = -1;
                if (count == 3)
                    drop = length[0] <= length[1] && length[0] <= length[2] ? 0 : (length[1] <= length[2] ? 1 : 2);
                int first = -1, second = -1;
                for (int i = 0; i < count; i++)
                {
                    if (i == drop) continue;
                    if (first < 0) first = i; else second = i;
                }
                int h = up[first] >= up[second] ? first : second;
                int w = h == first ? second : first;
                hAxis = along[h]; hLen = length[h];
                wAxis = along[w]; wLen = length[w];
            }

            if (wAxis == hAxis || wLen <= 0f || hLen <= 0f)
            {
                DebugLog.Log($"[SpectatorCamera] preview shape not found (mesh {size}, width axis {wAxis}, height axis {hAxis})");
                return false;
            }

            shapeWidthAxis = wAxis;
            shapeHeightAxis = hAxis;
            shapeWidthLength = wLen;
            shapeHeightLength = hLen;
            shapeKnown = true;
            const string axes = "XYZ";
            char wName = axes[wAxis], hName = axes[hAxis];
            DebugLog.Log($"[SpectatorCamera] preview shape: mesh size {size.x:F3}x{size.y:F3}x{size.z:F3}, width along local {wName} ({wLen:F3}), height along local {hName} ({hLen:F3}), base scale {shapeBaseScale.x:F3},{shapeBaseScale.y:F3},{shapeBaseScale.z:F3}");
            return true;
        }

        /// <summary>The game window's width / height (what the desktop shows).</summary>
        private static float WindowAspect(Camera main)
        {
            if (Screen.width > 0 && Screen.height > 0) return (float)Screen.width / Screen.height;
            return main != null ? main.aspect : 0f;
        }

        // Preview render texture swapped for one shaped like the window (restored when the page closes).
        private static bool textureSwapped;
        private static RenderTexture originalTexture;
        private static RenderTexture matchedTexture;
        private static Renderer displayRenderer;
        private static Material originalDisplayMaterial;
        private static Material matchedDisplayMaterial;

        /// <summary>
        /// The preview camera draws into a fixed-size render texture; if it isn't the window's
        /// shape, the picture is squashed. Swaps in one of the same height and the window's shape,
        /// shown on a copy of the display's material.
        /// </summary>
        private static void MatchPreviewTexture(Camera preview, GameObject display, float aspect)
        {
            RenderTexture source = textureSwapped ? originalTexture : preview.targetTexture;
            if (source == null || source.height <= 0) return;

            int height = source.height;
            int width = Mathf.Max(1, Mathf.RoundToInt(height * aspect));

            if (!textureSwapped)
            {
                float sourceAspect = (float)source.width / source.height;
                if (Mathf.Abs(sourceAspect - aspect) < 0.01f) return; // already the right shape

                Renderer renderer = display.GetComponentInChildren<Renderer>(true);
                if (renderer == null)
                {
                    DebugLog.Log("[SpectatorCamera] preview display has no renderer, texture not swapped");
                    return;
                }

                originalTexture = source;
                displayRenderer = renderer;
                originalDisplayMaterial = renderer.sharedMaterial;
                if (originalDisplayMaterial == null) return;
                if (originalDisplayMaterial.mainTexture != source)
                    DebugLog.Log("[SpectatorCamera] preview material's main texture isn't the camera's texture, swapping it anyway");

                matchedDisplayMaterial = new Material(originalDisplayMaterial);
                matchedDisplayMaterial.hideFlags |= HideFlags.DontUnloadUnusedAsset;
                renderer.sharedMaterial = matchedDisplayMaterial;
                textureSwapped = true;
            }

            if (matchedTexture != null && matchedTexture.width == width && matchedTexture.height == height) return;

            // New or resized window: (re)build the texture.
            RenderTexture old = matchedTexture;
            matchedTexture = new RenderTexture(width, height, originalTexture.depth, originalTexture.format);
            matchedTexture.antiAliasing = Mathf.Max(1, originalTexture.antiAliasing);
            matchedTexture.filterMode = originalTexture.filterMode;
            matchedTexture.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            matchedTexture.Create();

            preview.targetTexture = matchedTexture;
            if (matchedDisplayMaterial != null) matchedDisplayMaterial.mainTexture = matchedTexture;

            if (old != null) { old.Release(); UnityEngine.Object.Destroy(old); }
            DebugLog.Log($"[SpectatorCamera] preview texture {width}x{height} (game's was {originalTexture.width}x{originalTexture.height})");
        }

        private static void RestorePreviewTexture(SpectatorCam cam)
        {
            if (!textureSwapped) return;
            textureSwapped = false;

            Camera preview = cam.previewCam;
            if (preview != null) preview.targetTexture = originalTexture;
            if (displayRenderer != null && originalDisplayMaterial != null) displayRenderer.sharedMaterial = originalDisplayMaterial;

            if (matchedDisplayMaterial != null) UnityEngine.Object.Destroy(matchedDisplayMaterial);
            if (matchedTexture != null) { matchedTexture.Release(); UnityEngine.Object.Destroy(matchedTexture); }
            matchedDisplayMaterial = null;
            matchedTexture = null;
            originalTexture = null;
            originalDisplayMaterial = null;
            displayRenderer = null;
            DebugLog.Log("[SpectatorCamera] preview texture put back");
        }

        /// <summary>Where the preview sits on our cloned options panel (local to its OptionsMenu).</summary>
        private static readonly Vector3 PreviewLocalPosition = new Vector3(-32.5f, -6.27f, -7.5f);

        private static readonly System.Collections.Generic.List<Collider> disabledPreviewColliders =
            new System.Collections.Generic.List<Collider>();

        private static void MovePreviewOntoClone(GameObject display)
        {
            OptionsMenu clone = OptionsMenuClone.Menu;
            OptionsMenu native = null;
            GameObject menuRoot = GameObject.Find("menu");
            Transform nativeCenter = menuRoot != null ? menuRoot.transform.Find(SettingsCenterPath) : null;
            if (nativeCenter != null) native = nativeCenter.gameObject.GetComponentInChildren<OptionsMenu>(true);
            if (clone == null || native == null)
            {
                DebugLog.Log($"[SpectatorCamera] preview not moved (clone={(clone != null)}, native={(native != null)})");
                return;
            }

            Transform d = display.transform;
            Transform n = native.transform;

            // Pose relative to the native options menu, then the same relative pose on the clone.
            Vector3 localPos = n.InverseTransformPoint(d.position);
            Quaternion localRot = Quaternion.Inverse(n.rotation) * d.rotation;
            Vector3 ns = n.lossyScale, ds = d.lossyScale;
            Vector3 localScale = new Vector3(
                ns.x != 0f ? ds.x / ns.x : ds.x,
                ns.y != 0f ? ds.y / ns.y : ds.y,
                ns.z != 0f ? ds.z / ns.z : ds.z);

            previewOrigParent = d.parent;
            previewOrigLocalPos = d.localPosition;
            previewOrigLocalRot = d.localRotation;
            previewOrigLocalScale = d.localScale;

            d.SetParent(clone.transform, false);
            d.localPosition = PreviewLocalPosition; // fixed spot on the clone (relative pose was localPos)
            d.localRotation = localRot;
            d.localScale = localScale;
            previewBaseScale = localScale;
            previewMoved = true;

            // Not shootable while it's on our panel: its colliders are switched off (back on when it's put back).
            disabledPreviewColliders.Clear();
            var colliders = display.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                Collider collider = colliders[i];
                if (collider == null || !collider.enabled) continue;
                collider.enabled = false;
                disabledPreviewColliders.Add(collider);
            }
            DebugLog.Log($"[SpectatorCamera] preview moved onto the options clone ({disabledPreviewColliders.Count} colliders off, relative pose was {localPos.x:F3},{localPos.y:F3},{localPos.z:F3}, from {(previewOrigParent != null ? previewOrigParent.name : "root")})");
        }

        private static void RestorePreview(SpectatorCam cam)
        {
            RestorePreviewTexture(cam);
            if (!previewMoved) return;
            previewMoved = false;
            GameObject display = cam.previewCamDisplay;
            if (display == null) return;
            Transform d = display.transform;
            d.SetParent(previewOrigParent, false);
            d.localPosition = previewOrigLocalPos;
            d.localRotation = previewOrigLocalRot;
            d.localScale = previewOrigLocalScale;
            previewOrigParent = null;
            shapeKnown = false;

            foreach (Collider collider in disabledPreviewColliders)
                if (collider != null) collider.enabled = true;
            disabledPreviewColliders.Clear();
            DebugLog.Log("[SpectatorCamera] preview put back");
        }

        // ── Page ─────────────────────────────────────────────────────────────

        /// <summary>A slider on a native FloatPref (offset added for display, e.g. +1 on Y).</summary>
        private static GameObject NativeSlider(int col, string label, PlayerPreferences.FloatPref pref,
            float offset, float step, float fallbackMin, float fallbackMax, string unit, string format, string hover)
        {
            bool ranged = pref != null && pref.mMax > pref.mMin;
            float min = (ranged ? pref.mMin : fallbackMin) + offset;
            float max = (ranged ? pref.mMax : fallbackMax) + offset;
            float def = (pref != null ? pref.mDefault : 0f) + offset;
            return OptionsMenuClone.CreateSlider(col, label,
                () => pref != null ? pref.mVal + offset : 0f,
                v => { if (pref != null) pref.Set(v - offset); },
                min, max, step, def,
                v => v.ToString(format, CultureInfo.InvariantCulture) + unit,
                hover);
        }

        private static GameObject FollowSlider(int col, string label, Func<float> get, Action<float> set,
            float min, float max, float step, float def, string unit, string format, string hover)
        {
            return OptionsMenuClone.CreateSlider(col, label, get,
                v => { set(v); followInitialized = false; SaveFollow(); },
                min, max, step, def,
                v => v.ToString(format, CultureInfo.InvariantCulture) + unit,
                hover);
        }

        /// <summary>Options > Spectator Camera.</summary>
        public static void BuildPage()
        {
            PlayerPreferences prefs = Prefs;
            SpectatorCam cam = FindSpectator();

            OptionsMenuClone.AddRow(OptionsMenuClone.CreateHeader(0, "Spectator Camera"));

            var modeCycle = OptionsMenuClone.CreateCycle(0, "Camera Override", ModeOptions,
                () => Mode,
                v => MelonCoroutines.Start(SetModeNextFrame(v)), // SetMode redraws: not inside the slider's own event
                ModeOff,
                "Off: the game's own spectator camera. Follow: follows you from behind and beside. Static: a fixed camera you place. Follow and Static switch the game to 3rd person static; Off puts back what you had");
            GameObject fovSlider = prefs != null
                ? NativeSlider(1, "Field Of View", prefs.SpectatorCamFOV, 0f, 1f, 20f, 120f, "", "N0",
                    "The spectator camera's field of view (the game's own setting)")
                : null;
            OptionsMenuClone.AddRow(modeCycle, fovSlider);

            if (prefs != null && !prefs.SpectatorCam.mVal)
                OptionsMenuClone.AddRow(OptionsMenuClone.CreateHeader(0, "<size=60%>The spectator camera is off in the game's settings</size>"));
            else if (Mode != ModeOff && cam != null && cam.mCamType == SpectatorCam.CamType.FirstPerson)
                OptionsMenuClone.AddRow(OptionsMenuClone.CreateHeader(0, "<size=60%>Switched to first person with the controller button: pick the override again</size>"));

            if (Mode == ModeFollow) BuildFollowRows();
            else if (Mode == ModeStatic && prefs != null) BuildStaticRows(prefs);
        }

        private static System.Collections.IEnumerator SetModeNextFrame(int mode)
        {
            yield return null;
            SetMode(mode);
        }

        private static void BuildFollowRows()
        {
            OptionsMenuClone.AddRow(OptionsMenuClone.CreateHeader(0, "Follow Camera <size=60%>by Continuum</size>"));

            OptionsMenuClone.AddRow(
                FollowSlider(0, "Position Speed", () => FollowPositionSpeed, v => FollowPositionSpeed = v,
                    0.05f, 10f, 0.05f, DefaultFollowPositionSpeed, "", "0.00",
                    "How quickly the camera catches up with where it should be. Lower is smoother"),
                FollowSlider(1, "Rotation Speed", () => FollowRotationSpeed, v => FollowRotationSpeed = v,
                    0.05f, 10f, 0.05f, DefaultFollowRotationSpeed, "", "0.00",
                    "How quickly the camera turns with you. Lower is smoother"));

            OptionsMenuClone.AddRow(
                FollowSlider(0, "Horizontal Offset", () => FollowOffset, v => FollowOffset = v,
                    -15f, 15f, 0.1f, DefaultFollowOffset, " m", "0.0",
                    "How far to your right the camera sits. Negative is to your left"),
                FollowSlider(1, "Height", () => FollowHeight, v => FollowHeight = v,
                    -2f, 10f, 0.1f, DefaultFollowHeight, " m", "0.0",
                    "The camera's height above the floor"));

            OptionsMenuClone.AddRow(
                FollowSlider(0, "Distance", () => FollowDistance, v => FollowDistance = v,
                    -15f, 20f, 0.1f, DefaultFollowDistance, " m", "0.0",
                    "How far behind you the camera sits. Negative is in front"),
                FollowSlider(1, "Tilt", () => FollowTilt, v => FollowTilt = v,
                    -90f, 90f, 1f, DefaultFollowTilt, "", "0",
                    "Tilts the camera down (positive) or up (negative)"));

            OptionsMenuClone.AddRow(OptionsMenuClone.CreateButton(0, "Reset Follow", () =>
            {
                FollowPositionSpeed = DefaultFollowPositionSpeed;
                FollowRotationSpeed = DefaultFollowRotationSpeed;
                FollowOffset = DefaultFollowOffset;
                FollowHeight = DefaultFollowHeight;
                FollowDistance = DefaultFollowDistance;
                FollowTilt = DefaultFollowTilt;
                followInitialized = false;
                SaveFollow();
                GlobalOptions.RedrawIfShowing(CategoryId);
            }, "Restores the follow camera's default settings"));
        }

        private static void BuildStaticRows(PlayerPreferences prefs)
        {
            OptionsMenuClone.AddRow(OptionsMenuClone.CreateHeader(0, "Static Camera <size=60%>point at the camera and hold grip to move it</size>"));

            OptionsMenuClone.AddRow(
                NativeSlider(0, "X", prefs.SpectatorCamX, 0f, 0.05f, -20f, 20f, " m", "0.00", "Left / right"),
                NativeSlider(1, "Height", prefs.SpectatorCamY, NativeYOffset, 0.05f, -20f, 20f, " m", "0.00", "Height of the camera"));
            OptionsMenuClone.AddRow(
                NativeSlider(0, "Z", prefs.SpectatorCamZ, 0f, 0.05f, -20f, 20f, " m", "0.00", "Forward / back"),
                NativeSlider(1, "Pitch", prefs.SpectatorCamPitch, 0f, 1f, -180f, 180f, "", "0", "Tilt down (positive) or up (negative)"));
            OptionsMenuClone.AddRow(
                NativeSlider(0, "Yaw", prefs.SpectatorCamYaw, 0f, 1f, -180f, 180f, "", "0", "Turn left / right"),
                NativeSlider(1, "Roll", prefs.SpectatorCamRoll, 0f, 1f, -180f, 180f, "", "0", "Tilt the horizon"));

            OptionsMenuClone.AddRow(
                OptionsMenuClone.CreateButton(0, "Level Horizon", LevelHorizon, "Sets roll to 0"),
                OptionsMenuClone.CreateButton(1, "Look At Me", LookAtPlayer, "Turns the camera to face your headset, horizon level"));

            OptionsMenuClone.AddRow(
                NativeSlider(0, "Move Smoothing", prefs.SpectatorCamPositionTrackSpeed, 0f, 0.5f, 0f, 30f, "", "0.0",
                    "The game's position track speed: how fast the camera glides to a new spot. Also used by first person"),
                NativeSlider(1, "Turn Smoothing", prefs.SpectatorCamRotationTrackSpeed, 0f, 0.5f, 0f, 30f, "", "0.0",
                    "The game's rotation track speed. Also used by first person"));

            OptionsMenuClone.AddRow(OptionsMenuClone.CreateHeader(0, "Presets"));
            for (int i = 0; i < PresetCount; i++)
            {
                int slot = i;
                bool saved = PresetSaved(slot);
                OptionsMenuClone.AddRow(
                    OptionsMenuClone.CreateButton(0, "Save Slot " + (slot + 1), () => SavePreset(slot),
                        "Saves the current position, rotation and field of view to slot " + (slot + 1) + (saved ? " (replaces it)" : "")),
                    OptionsMenuClone.CreateButton(1, saved ? "Load Slot " + (slot + 1) : "Slot " + (slot + 1) + " (empty)",
                        () => LoadPreset(slot),
                        saved ? "Moves the camera to slot " + (slot + 1) : "Nothing saved in slot " + (slot + 1) + " yet"));
            }
        }
    }
}