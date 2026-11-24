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
    /// which our clone never is. It's put back as soon as the page closes.
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
        /// </summary>
        private static void ForcePreview(SpectatorCam cam)
        {
            Camera preview = cam.previewCam;
            GameObject display = cam.previewCamDisplay;
            if (preview == null || display == null) return;

            if (!previewMoved) MovePreviewOntoClone(display);

            if (!preview.gameObject.activeSelf) preview.gameObject.SetActive(true);
            if (!display.activeSelf) display.SetActive(true);

            // As the native code does while it shows the preview.
            Camera main = cam.cam;
            if (main != null) preview.aspect = main.aspect;
            if (previewMoved && preview.aspect > 0.01f)
                display.transform.localScale = new Vector3(previewBaseScale.x, previewBaseScale.x / preview.aspect, previewBaseScale.z);
        }

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
            d.localPosition = localPos;
            d.localRotation = localRot;
            d.localScale = localScale;
            previewBaseScale = localScale;
            previewMoved = true;
            DebugLog.Log($"[SpectatorCamera] preview moved onto the options clone (local {localPos}, from {(previewOrigParent != null ? previewOrigParent.name : "root")})");
        }

        private static void RestorePreview(SpectatorCam cam)
        {
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