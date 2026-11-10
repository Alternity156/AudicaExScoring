using System;
using UnhollowerBaseLib;      // TryCast
using UnhollowerRuntimeLib;   // Il2CppType (ML 0.5.7)
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// A window to the left of the native Settings panel that shows the player's own avatar, seen
    /// from a camera standing in front of them, while that panel is on its Customization page.
    ///
    /// When: only the game's own settings OptionsMenu (menu/ShellPage_Settings/page/ShellPanel_Center)
    /// counts. The song-list Options panel (OptionsMenuClone, "ExScoringOptionsPanel") and the
    /// other clones are separate instances, so opening Customize from them never shows the window.
    /// Tick() checks every frame that the native menu is active in the hierarchy and its mPage is
    /// Customization; no Harmony patch is involved. Leaving the page, leaving Settings or the menu
    /// being destroyed hides it.
    ///
    /// Camera: our own, so the game's SpectatorCam is never touched. Its clear flags, background,
    /// field of view and clip planes are copied once from SpectatorCam.cam (found even when
    /// inactive); without one, from Camera.main with a 60° field of view. Culling mask = the
    /// source camera's mask + layer 15 ThirdPersonAvatar (own head and body, which the headset
    /// never renders) − layer 20 HeadsetOnly (headset-only objects). stereoTargetEye is None so it never draws into the headset. It
    /// renders into a TextureWidth × TextureHeight RenderTexture, and only while the window is shown.
    ///
    /// Placement: the window is fixed at WindowPosition / WindowRotation / WindowScale (tuned in
    /// game 2026-11-10; the root sits at the origin, so these are world values). It acts as a
    /// mirror: the camera sits at the window's center (nudged CameraForwardNudge toward the
    /// player so the quads are behind it) and, each time the window opens, is aimed at a point
    /// CameraLookDrop below the head, with a vertical field of view that frames FramedHeight
    /// metres at the head's distance. It stays put until the window closes (not following the
    /// head). The picture's UVs are flipped left-right, so it reads like a mirror, not a photo.
    ///
    /// Picture: a quad with Sprites/Default (the shader LobbyPanel already uses) and the
    /// RenderTexture as mainTexture, in front of a slightly larger dark frame (LobbyPanel's colour).
    /// Both are on the native settings panel's layer (fallback WindowFallbackLayer), so the
    /// headset and the spectator camera show them like the panel. The preview camera's mask
    /// still includes that layer; it never sees the window because it faces away from it.
    /// No colliders, so it never blocks shots. No post-processing is copied from SpectatorCam.
    ///
    /// From the UnityExplorer console: ExScoringMod.AvatarPreview.DebugDump();
    /// </summary>
    internal static class AvatarPreview
    {
        // ── Tunables ─────────────────────────────────────────────────────────
        private const string NativeSettingsPath = "ShellPage_Settings/page/ShellPanel_Center";

        private const int TextureWidth = 512;
        private const int TextureHeight = 640;
        private const int TextureAntiAliasing = 2;

        private const float WindowWidth = 0.55f;                     // metres
        private const float WindowHeight = WindowWidth * TextureHeight / TextureWidth;
        private const float FrameBorder = 0.015f;                    // metres around the picture
        private static readonly Vector3 WindowPosition = new Vector3(-13f, 2f, 15f);
        private static readonly Vector3 WindowRotation = new Vector3(0f, 315f, 0f);
        private const float WindowScale = 20f;

        private const float CameraForwardNudge = 0.1f;               // metres toward the player
        private const float CameraLookDrop = 0.5f;                   // aim this far below the head
        private const float FramedHeight = 2.5f;                     // metres shown top to bottom at the head
        private const float MinFov = 2f;
        private const float FallbackFov = 60f;
        private const float FallbackNear = 0.05f;
        private const float FallbackFar = 1000f;

        private const int ThirdPersonAvatarLayer = 15;
        private const int HeadsetOnlyLayer = 20;
        private const int WindowFallbackLayer = 0;                   // Default, if the panel isn't found

        private static readonly Color FrameColor = new Color(0.16f, 0.16f, 0.22f, 0.85f);
        private const int FrameRenderQueue = 2990;
        private const int PictureRenderQueue = 3000;

        private const float NativeLookupSeconds = 2f;

        // ── State ────────────────────────────────────────────────────────────
        private static OptionsMenu nativeMenu;
        private static float nextNativeLookup;

        private static GameObject root;
        private static GameObject window;
        private static Camera previewCamera;
        private static RenderTexture texture;
        private static Material pictureMaterial;
        private static Material frameMaterial;
        private static bool cameraConfigured;
        private static string cameraSource = "none";

        private static bool shown;
        private static bool buildFailed;

        private static void Log(string message) => DebugLog.Log("[AvatarPreview] " + message);

        // ── Tick ─────────────────────────────────────────────────────────────

        /// <summary>Called every frame from ExScoring.OnUpdate.</summary>
        public static void Tick()
        {
            bool want = false;
            try
            {
                OptionsMenu menu = FindNativeMenu();
                want = menu != null
                       && menu.gameObject.activeInHierarchy
                       && menu.mPage == OptionsMenu.Page.Customization;
            }
            catch (Exception ex)
            {
                Log($"native menu check failed: {ex.Message}");
                nativeMenu = null;
            }

            if (want && !shown) Show();
            else if (!want && shown) Hide();
        }

        private static OptionsMenu FindNativeMenu()
        {
            if (nativeMenu != null && !nativeMenu.WasCollected) return nativeMenu;
            nativeMenu = null;

            if (Time.unscaledTime < nextNativeLookup) return null;
            nextNativeLookup = Time.unscaledTime + NativeLookupSeconds;

            GameObject menuRoot = GameObject.Find("menu");
            if (menuRoot == null) return null;
            Transform panel = menuRoot.transform.Find(NativeSettingsPath);
            if (panel == null) return null;

            nativeMenu = panel.gameObject.GetComponentInChildren<OptionsMenu>(true);
            if (nativeMenu != null) Log("native settings OptionsMenu found");
            return nativeMenu;
        }

        // ── Show / hide ──────────────────────────────────────────────────────

        private static void Show()
        {
            shown = true;
            if (buildFailed) return;

            try
            {
                if (!EnsureBuilt()) return;
                if (!cameraConfigured) ConfigureCamera();
                Place();
                root.SetActive(true);
                Log("shown");
            }
            catch (Exception ex)
            {
                MelonLoader.MelonLogger.LogError("[AvatarPreview] show failed: " + ex);
                if (root != null) root.SetActive(false);
            }
        }

        private static void Hide()
        {
            shown = false;
            try
            {
                if (root != null && !root.WasCollected) root.SetActive(false);
                Log("hidden");
            }
            catch (Exception ex)
            {
                Log($"hide failed: {ex.Message}");
            }
        }

        // ── Building ─────────────────────────────────────────────────────────

        private static bool EnsureBuilt()
        {
            if (root != null && !root.WasCollected && previewCamera != null && !previewCamera.WasCollected)
                return true;

            Destroy();

            try
            {
                root = new GameObject("ExScoring_AvatarPreview");
                GameObject.DontDestroyOnLoad(root);
                root.SetActive(false);

                // Render target
                texture = new RenderTexture(TextureWidth, TextureHeight, 24);
                texture.name = "ExScoring_AvatarPreviewRT";
                texture.antiAliasing = TextureAntiAliasing;
                texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
                texture.Create();

                // Camera
                GameObject camObject = new GameObject("Camera");
                camObject.transform.SetParent(root.transform, false);
                previewCamera = camObject.AddComponent<Camera>();
                previewCamera.stereoTargetEye = StereoTargetEyeMask.None;
                previewCamera.targetTexture = texture;
                previewCamera.depth = -10f;
                cameraConfigured = false;

                // Window: frame behind, picture in front (toward the viewer, local -Z)
                window = new GameObject("Window");
                window.transform.SetParent(root.transform, false);

                Shader shader = Shader.Find("Sprites/Default");
                if (shader == null)
                {
                    MelonLoader.MelonLogger.LogError("[AvatarPreview] Sprites/Default shader not found; window not built");
                    buildFailed = true;
                    Destroy();
                    return false;
                }

                frameMaterial = new Material(shader) { name = "ExScoring_AvatarPreviewFrame", color = FrameColor };
                frameMaterial.renderQueue = FrameRenderQueue;
                frameMaterial.hideFlags = HideFlags.DontUnloadUnusedAsset;
                CreateQuad("Frame", window.transform, WindowWidth + FrameBorder * 2f, WindowHeight + FrameBorder * 2f,
                    0.002f, frameMaterial, false);

                pictureMaterial = new Material(shader) { name = "ExScoring_AvatarPreviewPicture", color = Color.white };
                pictureMaterial.mainTexture = texture;
                pictureMaterial.renderQueue = PictureRenderQueue;
                pictureMaterial.hideFlags = HideFlags.DontUnloadUnusedAsset;
                CreateQuad("Picture", window.transform, WindowWidth, WindowHeight, 0f, pictureMaterial, true);

                Log("built");
                return true;
            }
            catch (Exception ex)
            {
                MelonLoader.MelonLogger.LogError("[AvatarPreview] build failed: " + ex);
                buildFailed = true;
                Destroy();
                return false;
            }
        }

        /// <summary>A quad facing local -Z with UVs, no collider. z pushes it back; mirrorU flips it left-right.</summary>
        private static void CreateQuad(string name, Transform parent, float width, float height, float z, Material material, bool mirrorU)
        {
            GameObject quad = new GameObject(name);
            quad.transform.SetParent(parent, false);
            quad.transform.localPosition = new Vector3(0f, 0f, z);
            quad.transform.localScale = new Vector3(width, height, 1f);

            Mesh mesh = new Mesh();
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f)
            };
            float u0 = mirrorU ? 1f : 0f, u1 = mirrorU ? 0f : 1f;
            mesh.uv = new[]
            {
                new Vector2(u0, 0f), new Vector2(u1, 0f),
                new Vector2(u1, 1f), new Vector2(u0, 1f)
            };
            // Both windings, so it shows from either side.
            mesh.triangles = new[] { 0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateNormals();

            quad.AddComponent<MeshFilter>().mesh = mesh;
            MeshRenderer renderer = quad.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
        }

        /// <summary>Copies the spectator camera's look (or the headset's, without one).</summary>
        private static void ConfigureCamera()
        {
            Camera source = null;
            SpectatorCam spectator = null;
            try
            {
                foreach (UnityEngine.Object o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<SpectatorCam>()))
                {
                    SpectatorCam candidate = o.TryCast<SpectatorCam>();
                    if (candidate == null || !candidate.gameObject.scene.IsValid()) continue; // skip prefabs
                    spectator = candidate;
                    break;
                }
                if (spectator != null) source = spectator.cam;
            }
            catch (Exception ex)
            {
                Log($"SpectatorCam lookup failed: {ex.Message}");
            }

            int mask;
            if (source != null)
            {
                previewCamera.clearFlags = source.clearFlags;
                previewCamera.backgroundColor = source.backgroundColor;
                previewCamera.fieldOfView = source.fieldOfView > 1f ? source.fieldOfView : FallbackFov;
                previewCamera.nearClipPlane = source.nearClipPlane;
                previewCamera.farClipPlane = source.farClipPlane;
                mask = source.cullingMask;
                cameraSource = "SpectatorCam.cam";
            }
            else
            {
                Camera main = Camera.main;
                if (main == null) return; // try again on the next Show
                previewCamera.clearFlags = main.clearFlags;
                previewCamera.backgroundColor = main.backgroundColor;
                previewCamera.fieldOfView = FallbackFov;
                previewCamera.nearClipPlane = FallbackNear;
                previewCamera.farClipPlane = FallbackFar;
                mask = main.cullingMask;
                cameraSource = "Camera.main";
            }

            mask |= 1 << ThirdPersonAvatarLayer;
            mask &= ~(1 << HeadsetOnlyLayer);
            previewCamera.cullingMask = mask;
            cameraConfigured = true;

            Log($"camera from {cameraSource}: clear={previewCamera.clearFlags} fov={previewCamera.fieldOfView:0.#} " +
                $"near={previewCamera.nearClipPlane} far={previewCamera.farClipPlane} mask=0x{mask:X8}");

            if (spectator != null) LogPreviewDisplay(spectator);
        }

        /// <summary>Logs what the native preview window is, for later reference.</summary>
        private static void LogPreviewDisplay(SpectatorCam spectator)
        {
            try
            {
                GameObject display = spectator.previewCamDisplay;
                Camera nativePreview = spectator.previewCam;
                if (display == null) { Log("previewCamDisplay: null"); return; }

                Renderer r = display.GetComponent<Renderer>();
                string material = r != null && r.sharedMaterial != null
                    ? $"{r.sharedMaterial.name} ({(r.sharedMaterial.shader != null ? r.sharedMaterial.shader.name : "no shader")})"
                    : "no renderer";
                string rt = nativePreview != null && nativePreview.targetTexture != null
                    ? $"{nativePreview.targetTexture.width}x{nativePreview.targetTexture.height}"
                    : "none";
                Log($"previewCamDisplay: '{display.name}' active={display.activeInHierarchy} layer={display.layer} " +
                    $"material={material}; previewCam RT={rt}");
            }
            catch (Exception ex)
            {
                Log($"previewCamDisplay log failed: {ex.Message}");
            }
        }

        // ── Placement ────────────────────────────────────────────────────────

        private static void Place()
        {
            // Window: fixed, on the settings panel's layer so every camera that shows the panel shows it.
            window.transform.localPosition = WindowPosition;
            window.transform.localEulerAngles = WindowRotation;
            window.transform.localScale = Vector3.one * WindowScale;
            int layer = nativeMenu != null && !nativeMenu.WasCollected ? nativeMenu.gameObject.layer : WindowFallbackLayer;
            for (int i = 0; i < window.transform.childCount; i++)
                window.transform.GetChild(i).gameObject.layer = layer;

            // Camera: at the window's center, facing out of its front (local -Z) toward the player.
            Camera head = Camera.main;
            Vector3 headPos = head != null ? head.transform.position : new Vector3(0f, 1.6f, 0f);
            Vector3 camPos = window.transform.position - window.transform.forward * CameraForwardNudge;
            Vector3 lookAt = headPos - Vector3.up * CameraLookDrop;
            Vector3 toTarget = lookAt - camPos;
            if (toTarget.sqrMagnitude < 1e-4f) toTarget = -window.transform.forward;

            previewCamera.transform.position = camPos;
            previewCamera.transform.rotation = Quaternion.LookRotation(toTarget, Vector3.up);

            float distance = toTarget.magnitude;
            float fov = 2f * Mathf.Atan(FramedHeight * 0.5f / Mathf.Max(distance, 0.01f)) * 57.29578f; // rad -> deg (Mathf.Rad2Deg and Mathf.PI are constants the unhollowed Mathf leaves out)
            previewCamera.fieldOfView = Mathf.Clamp(fov, MinFov, 120f);

            Log($"placed: window layer={layer}, camera {camPos} -> head {headPos}, distance={distance:0.##} m, fov={previewCamera.fieldOfView:0.##}");
        }

        // ── Cleanup / debug ──────────────────────────────────────────────────

        private static void Destroy()
        {
            try
            {
                if (previewCamera != null && !previewCamera.WasCollected) previewCamera.targetTexture = null;
                if (root != null && !root.WasCollected) GameObject.Destroy(root);
                if (texture != null && !texture.WasCollected) { texture.Release(); UnityEngine.Object.Destroy(texture); }
                if (pictureMaterial != null && !pictureMaterial.WasCollected) UnityEngine.Object.Destroy(pictureMaterial);
                if (frameMaterial != null && !frameMaterial.WasCollected) UnityEngine.Object.Destroy(frameMaterial);
            }
            catch (Exception ex)
            {
                Log($"destroy failed: {ex.Message}");
            }
            root = null; window = null; previewCamera = null; texture = null;
            pictureMaterial = null; frameMaterial = null; cameraConfigured = false;
        }

        public static void DebugDump()
        {
            MelonLoader.MelonLogger.Log("[AvatarPreview] ── dump ──");
            MelonLoader.MelonLogger.Log($"  shown={shown} buildFailed={buildFailed} nativeMenu={(nativeMenu != null ? "found" : "null")}");
            if (nativeMenu != null && !nativeMenu.WasCollected)
                MelonLoader.MelonLogger.Log($"  nativeMenu active={nativeMenu.gameObject.activeInHierarchy} page={nativeMenu.mPage} pos={nativeMenu.transform.position}");
            MelonLoader.MelonLogger.Log($"  root={(root != null ? (root.activeSelf ? "active" : "inactive") : "null")}");
            if (previewCamera != null && !previewCamera.WasCollected)
                MelonLoader.MelonLogger.Log($"  camera source={cameraSource} pos={previewCamera.transform.position} rot={previewCamera.transform.eulerAngles} " +
                    $"fov={previewCamera.fieldOfView} mask=0x{previewCamera.cullingMask:X8} clear={previewCamera.clearFlags}");
            if (window != null && !window.WasCollected)
                MelonLoader.MelonLogger.Log($"  window pos={window.transform.position} rot={window.transform.eulerAngles} size={WindowWidth}x{WindowHeight:0.###}");
        }
    }
}