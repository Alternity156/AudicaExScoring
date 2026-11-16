using System;
using System.Globalization;
using MelonLoader;
using TMPro;
using UnhollowerBaseLib;      // TryCast
using UnhollowerRuntimeLib;   // Il2CppType (ML 0.5.7)
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// Spectator window, phase 1: a window the player can drag around in the menus that shows the
    /// lobby through another player's eyes, with this player's own spectator camera smoothing.
    /// Findings behind it: claude/Spectator-Window-Findings.md (2026-11-13).
    ///
    /// Opening: a "Spectate" toggle under the lobby panel's buttons (its own root at LobbyPanel's
    /// pose), shown in a lobby outside songs. The window closes on leaving the lobby and is hidden
    /// during songs. On the window, "Next Player" cycles through the other lobby members.
    ///
    /// What the window shows: our own camera, at the watched player's avatar head
    /// (MpPresenceAvatars/MpAvatar {userId}/Head, which MpPresence places from their interpolated
    /// pose). Near plane 1 cm, so guns held by the face aren't cut. While the watched player is in
    /// a song it renders only layer 31 (MpPresence.SongOnlyLayer: their hands, guns and platform)
    /// and the sky: what they see is their gameplay, not this player's lobby. While they are in the
    /// menus it renders the lobby as they see it, these layers:
    ///   11 Player              every gun (remote copies and this player's own)
    ///   12 MixedRealityIgnore  every hand
    ///   15 ThirdPersonAvatar   this player's own head and body (as the watched player sees them)
    ///   17 MixedRealityForegroundIgnore  platforms (own and disc copies), beams
    ///   19 LitParticles        explosions, dust
    /// and the sky (clear flags copied from SpectatorCam.cam). Not layer 0 or 5, so no menus and no
    /// scenery: the watched player's menus aren't ours, and scenery shares layer 0 with ours. Other
    /// remote players' heads and bodies are on layer 0 too, so with three or more players only
    /// their hands and guns show (phase 1 limit). The watched player's own head copy is on layer 0,
    /// so the camera sitting inside it never sees it. Nothing in MpPresence is changed; with Show
    /// Other Players off there are no avatars and the window says so.
    ///
    /// Smoothing, exactly SpectatorCam.Update's first-person path (Ghidra 2026-11-13), from this
    /// player's own preferences:
    ///   position = Lerp(position, head, clamp01(dt x SpectatorCamPositionTrackSpeed))
    ///   rotation = Slerp(rotation, head, dt x SpectatorCamRotationTrackSpeed)
    ///   field of view = SpectatorCamFOV
    /// It snaps on the first frame, on a new target, and when the head is more than SnapDistance away.
    ///
    /// Window: a frame and a picture quad, a title above and the Next Player button below, on layer 0.
    /// The frame (Unlit/Color) and picture (Unlit/Texture, the RenderTexture as mainTexture) are
    /// opaque and drawn in the geometry queue, so they write depth: menu text behind the window (the
    /// difficulty buttons' labels, drawn later in the transparent queue without writing depth) fails
    /// the depth test instead of being drawn over the picture. Falls back to Sprites/Default (no depth)
    /// if those shaders are missing. A thin BoxCollider (layer 0) covers it so a shot at the window
    /// doesn't press a menu button behind it.
    ///
    /// Dragging: hold a gun's grip button (KataInput.GripButtonHeld, or the Grip axis past half)
    /// while it points at the window (ray from Gun.firepoint against the
    /// window's rectangle, our own math) and the window follows that gun, keeping its pose relative
    /// to it, until the grip is released. The pose is then saved (MelonPrefs category
    /// PrefsCategory, one string) and restored next time. The trigger doesn't move the window, and a
    /// trigger grab aimed at the window doesn't grab-scroll a list behind it (BlocksGrabScroll, used by
    /// OptionsMenuFunctions' GrabScroll.OnGrab patch).
    ///
    /// Watching during a song (v2.53, "spectate"): the window tells the server who it shows
    /// (MpSession.SetWatch, re-sent by MpSession when needed; nobody while the window is closed or
    /// this player is in a song). The server then keeps listing that player in poses while they
    /// play, and MpPresence draws them on SongOnlyLayer, which only this camera renders: hands,
    /// guns and their platform, at their lobby spot. No beams or targets yet.
    ///
    /// The watched player's song (SpectatorSong, which reads ShownWatchedId): while they are in a
    /// song, its audio plays here in time with them once the song is installed (downloaded from
    /// Maudica when missing). The title's second line says what is going on (SpectatorSong.StatusText:
    /// the song's title, the download's progress, or why it can't be spectated). Until the song is
    /// installed here the view is withheld (SpectatorSong.ViewAllowed): the camera renders no layer,
    /// only the sky.
    ///
    /// Their targets (SpectatorTargets, which reads ShownWatchedRoot) are drawn on layer 31 too,
    /// in the watched player's play space.
    ///
    /// Testing alone: SpectatorDebug's replay adds a fake in-song player to the list (no server needed).
    ///
    /// Not in this phase: targets and beams during a song, scenery, post-processing, other
    /// players' heads and bodies.
    ///
    /// From the UnityExplorer console: ExScoringMod.SpectatorWindow.DebugDump();
    /// </summary>
    internal static class SpectatorWindow
    {
        // ── Tunables ─────────────────────────────────────────────────────────
        private const int TextureWidth = 960;
        private const int TextureHeight = 540;
        private const int TextureAntiAliasing = 2;

        private const float WindowWidth = 0.6f;                      // metres
        private const float WindowHeight = WindowWidth * TextureHeight / TextureWidth;
        private const float FrameBorder = 0.012f;                    // metres around the picture
        private static readonly Vector3 DefaultPosition = new Vector3(0.75f, 1.55f, 1.2f);
        private static readonly Vector3 DefaultRotation = new Vector3(0f, 32f, 0f); // facing a player at the origin

        private static readonly Color FrameColor = new Color(0.16f, 0.16f, 0.22f, 0.85f);
        // Opaque, depth-writing: before every transparent menu text, so text behind the window is hidden.
        private const int FrameRenderQueue = 2000;
        private const int PictureRenderQueue = 2001;
        // Fallback (Sprites/Default, no depth write), as before.
        private const int FallbackFrameRenderQueue = 2990;
        private const int FallbackPictureRenderQueue = 3000;
        private const int TextRenderQueue = 3010;
        private const int WindowLayer = 0;                           // Default: headset + spectator camera

        private const float TitleFontSize = 0.3f;                    // as LobbyPanel
        private const float TitleOutlineWidth = 0.2f;
        private const float TitleGap = 0.008f;                       // above the frame

        // Buttons: options-menu rows at LobbyPanel's scale.
        private static readonly Vector3 ButtonScale = new Vector3(0.05f, 0.05f, 0.05f);
        private static readonly Vector3 NextButtonPosition = new Vector3(-0.125f, -(WindowHeight * 0.5f) - 0.06f, -0.005f);

        // The opener sits under LobbyPanel's Start Song button (same 0.081 spacing), in LobbyPanel's frame.
        private static readonly Vector3 LobbyPanelPosition = new Vector3(-0.549f, 1.03f, 1.33f);
        private static readonly Vector3 LobbyPanelRotation = new Vector3(37.74f, 0f, 0f);
        private static readonly Vector3 OpenerPosition = new Vector3(-0.125f, -0.206f, -0.005f);

        // Camera: what the window shows (see the summary).
        // A player in the menus: the lobby as they see it. A player in a song: only their own gameplay
        // (their avatar is on SongOnlyLayer), nothing of this player's world.
        private const int LobbyMask = (1 << 11) | (1 << 12) | (1 << 15) | (1 << 17) | (1 << 19);
        private const int SongMask = 1 << MpPresence.SongOnlyLayer;
        // Our own clip planes: guns held near the face must not be cut (SpectatorCam's near plane is
        // 0.1 m), and nothing we draw is far away, so a short far plane keeps the depth precision.
        private const float NearClip = 0.01f;
        private const float FarClip = 1000f;
        private const float FallbackFov = 120f;
        private const float FallbackPositionSpeed = 10f;
        private const float FallbackRotationSpeed = 5f;
        private const float SnapDistance = 2f;                       // a jump this big isn't smoothed (new seat, new target)

        private const string AvatarHolderName = "MpPresenceAvatars"; // MpPresence.BuildAvatar
        private const string AvatarPrefix = "MpAvatar ";
        private const string AvatarHeadName = "Head";

        private const float GunLookupSeconds = 2f;
        private const float GripAxisThreshold = 0.5f;                // Grip axis fallback (0..1)
        private const float StateRefreshSeconds = 0.25f;

        private const string PrefsCategory = "ExScoringSpectatorWindow";
        private const string PrefsPose = "WindowPose";

        // ── State ────────────────────────────────────────────────────────────
        private static bool open;            // the player wants the window
        private static string watchedId;

        private static GameObject root;
        private static Camera windowCamera;
        private static RenderTexture texture;
        private static Material pictureMaterial;
        private static Material frameMaterial;
        private static TextMeshPro title;
        private static string lastTitle;
        private static bool cameraConfigured;
        private static bool buildFailed;

        private static GameObject nextObject;
        private static GunButton nextButton;

        private static GameObject openerRoot;
        private static GameObject openerObject;
        private static GunButton openerButton;

        private static Transform watchedRoot, watchedHead;
        private static string resolvedId;
        private static bool hasCameraPose;
        private static bool targetVisible;
        private static float nextStateRefresh;
        private static float nextOpenerRefresh;
        private static float nextWatchSync;
        private static readonly System.Collections.Generic.List<string> candidates = new System.Collections.Generic.List<string>();

        private static Gun gunL, gunR;
        private static float nextGunLookup;
        private static readonly bool[] grabWasDown = new bool[2];
        private static int grabHand = -1;
        private static Vector3 grabLocalPos;
        private static Quaternion grabLocalRot = Quaternion.identity;

        private static bool prefsRegistered;

        private static void Log(string message) => DebugLog.Log("[SpectatorWindow] " + message);

        /// <summary>The player the window is showing right now, or null while it is closed or hidden (for SpectatorSong).</summary>
        internal static string ShownWatchedId => root != null && root.activeSelf ? watchedId : null;

        /// <summary>
        /// The shown player's avatar root while their avatar is drawn (their play space: MpPresence
        /// puts it at their seat and poses the parts inside it), else null. For SpectatorTargets.
        /// </summary>
        /// <summary>The shown player's avatar head while it is drawn, else null (melee telegraphs face it).</summary>
        internal static Transform ShownWatchedHead
        {
            get
            {
                if (root == null || !root.activeSelf || watchedId == null) return null;
                return HeadOf(watchedId);
            }
        }

        internal static Transform ShownWatchedRoot
        {
            get
            {
                if (root == null || !root.activeSelf || watchedId == null) return null;
                return HeadOf(watchedId) != null ? watchedRoot : null;
            }
        }

        // ── Tick ─────────────────────────────────────────────────────────────

        /// <summary>Once per frame from ExScoring.OnUpdate, after MpPresence.Tick (so the head is this frame's).</summary>
        public static void Tick()
        {
            try
            {
                bool available = Config.EnableOnlinePlay && MpSession.InLobby && !InSongScene();
                if (!MpSession.InLobby && open)
                {
                    open = false;
                    watchedId = null;
                    Log("closed (left the lobby)");
                }

                UpdateOpener(available);

                // v2.53: who the server should keep sending while they play. Only changes go out.
                if (Time.unscaledTime >= nextWatchSync)
                {
                    nextWatchSync = Time.unscaledTime + StateRefreshSeconds;
                    // The debug replay's fake player isn't someone the server knows.
                    bool real = watchedId != null && watchedId != SpectatorDebug.FakeUserId;
                    MpSession.SetWatch(available && open && real ? watchedId : null);
                }

                bool show = available && open && !buildFailed;
                if (!show)
                {
                    if (root != null && root.activeSelf)
                    {
                        EndGrab(true);
                        root.SetActive(false);
                    }
                    return;
                }

                if (!EnsureBuilt()) return;
                if (!root.activeSelf)
                {
                    root.SetActive(true);
                    hasCameraPose = false;
                    lastTitle = null;
                    nextStateRefresh = 0f;
                }
                if (!cameraConfigured) ConfigureCamera();

                if (Time.unscaledTime >= nextStateRefresh)
                {
                    nextStateRefresh = Time.unscaledTime + StateRefreshSeconds;
                    PickTarget();
                    UpdateButtons();
                    UpdateTitle();
                }

                UpdateCamera();
                UpdateGrab();
            }
            catch (Exception ex)
            {
                MelonLogger.Log("[SpectatorWindow] tick failed: " + ex);
            }
        }

        private static bool InSongScene()
        {
            MenuState.State state = ExScoring.menuState;
            return state == MenuState.State.Launching || state == MenuState.State.Launched;
        }

        // ── Opener (the Spectate toggle by the lobby panel) ──────────────────

        private static void UpdateOpener(bool available)
        {
            if (!available)
            {
                if (openerRoot != null && openerRoot.activeSelf) openerRoot.SetActive(false);
                return;
            }

            if (!EnsureOpener()) return;
            if (!openerRoot.activeSelf) openerRoot.SetActive(true);

            if (Time.unscaledTime < nextOpenerRefresh) return;
            nextOpenerRefresh = Time.unscaledTime + StateRefreshSeconds;
            // Tie the button to whichever menu page is showing, like the page's own buttons.
            ShellPage page = LobbyPanel.CurrentShellPage();
            if (page != null) openerButton.mShellPage = page;
            openerButton.SetInteractable(true);
        }

        private static bool EnsureOpener()
        {
            if (openerRoot != null && openerObject != null && openerButton != null) return true; // Unity-null: rebuilt if destroyed

            if (!OptionsMenuClone.EnsureClone() || OptionsMenuClone.Menu == null) return false;

            if (openerRoot == null)
            {
                openerRoot = new GameObject("ExScoring_SpectatorWindowOpener");
                GameObject.DontDestroyOnLoad(openerRoot);
                openerRoot.transform.position = LobbyPanelPosition;
                openerRoot.transform.eulerAngles = LobbyPanelRotation;
                openerRoot.transform.localScale = Vector3.one;
            }

            OptionsMenuButton toggle = OptionsMenuClone.Menu.AddButton(0, "Spectate",
                new Action(OnOpenerShot),
                new Func<bool>(() => open),
                "Opens a window showing another player's view");
            if (toggle == null || toggle.button == null)
            {
                Log("couldn't make the Spectate toggle");
                if (toggle != null) GameObject.Destroy(toggle.gameObject);
                return false;
            }

            openerObject = toggle.gameObject;
            // No "(Clone)" in the name, and off the options menu, so its Wipe() never touches it.
            openerObject.name = "SpectateButton";
            openerObject.transform.SetParent(openerRoot.transform, false);
            openerObject.transform.localPosition = OpenerPosition;
            openerObject.transform.localRotation = Quaternion.identity;
            openerObject.transform.localScale = ButtonScale;

            openerButton = toggle.button;
            openerButton.disableOnShot = false;
            openerButton.destroyOnShot = false;
            openerButton.SetInteractable(true);
            if (toggle.help != null) toggle.help.forceDisable = true; // no hover help bubble here

            Log("Spectate toggle created");
            return true;
        }

        private static void OnOpenerShot()
        {
            open = !open;
            nextStateRefresh = 0f;
            Log(open ? "opened" : "closed");
        }

        // ── Building the window ──────────────────────────────────────────────

        private static bool EnsureBuilt()
        {
            if (root != null && windowCamera != null && title != null) return true; // Unity-null: rebuilt if destroyed

            Destroy();

            try
            {
                Shader shader = Shader.Find("Sprites/Default");
                if (shader == null)
                {
                    MelonLogger.LogError("[SpectatorWindow] Sprites/Default shader not found; window not built");
                    buildFailed = true;
                    return false;
                }

                root = new GameObject("ExScoring_SpectatorWindow");
                GameObject.DontDestroyOnLoad(root);
                // Left active while it is built: a TextMeshPro added under an inactive object never
                // runs Awake, and its font / fontMaterial then throw (log 2026-11-13). Tick shows it anyway.
                root.layer = WindowLayer;
                root.transform.position = DefaultPosition;
                root.transform.eulerAngles = DefaultRotation;
                root.transform.localScale = Vector3.one;
                LoadPose();

                // Render target
                texture = new RenderTexture(TextureWidth, TextureHeight, 24);
                texture.name = "ExScoring_SpectatorWindowRT";
                texture.antiAliasing = TextureAntiAliasing;
                texture.hideFlags = HideFlags.DontUnloadUnusedAsset;
                texture.Create();

                // Camera: a child only so it is switched off with the window; it is placed in world space every frame.
                GameObject camObject = new GameObject("Camera");
                camObject.transform.SetParent(root.transform, false);
                windowCamera = camObject.AddComponent<Camera>();
                windowCamera.stereoTargetEye = StereoTargetEyeMask.None;
                windowCamera.targetTexture = texture;
                windowCamera.depth = -10f;
                windowCamera.cullingMask = LobbyMask;
                windowCamera.nearClipPlane = NearClip;
                windowCamera.farClipPlane = FarClip;
                cameraConfigured = false;
                hasCameraPose = false;

                // Opaque shaders write depth, so menu text behind the window is hidden by it (see the summary).
                Shader frameShader = Shader.Find("Unlit/Color");
                Shader pictureShader = Shader.Find("Unlit/Texture");
                bool opaque = frameShader != null && pictureShader != null;
                if (!opaque)
                {
                    MelonLogger.Log("[SpectatorWindow] Unlit/Color or Unlit/Texture shader missing; using Sprites/Default (menu text may show over the window)");
                    frameShader = shader;
                    pictureShader = shader;
                }

                // Frame behind, picture in front (toward the viewer, local -Z).
                frameMaterial = new Material(frameShader) { name = "ExScoring_SpectatorWindowFrame", color = FrameColor };
                frameMaterial.renderQueue = opaque ? FrameRenderQueue : FallbackFrameRenderQueue;
                frameMaterial.hideFlags = HideFlags.DontUnloadUnusedAsset;
                CreateQuad("Frame", root.transform, WindowWidth + FrameBorder * 2f, WindowHeight + FrameBorder * 2f, 0.002f, frameMaterial);

                pictureMaterial = new Material(pictureShader) { name = "ExScoring_SpectatorWindowPicture", color = Color.white };
                pictureMaterial.mainTexture = texture;
                pictureMaterial.renderQueue = opaque ? PictureRenderQueue : FallbackPictureRenderQueue;
                pictureMaterial.hideFlags = HideFlags.DontUnloadUnusedAsset;
                CreateQuad("Picture", root.transform, WindowWidth, WindowHeight, 0f, pictureMaterial);

                // Stops shots, so a menu button behind the window isn't pressed through it.
                GameObject blocker = new GameObject("ShotBlocker");
                blocker.layer = WindowLayer;
                blocker.transform.SetParent(root.transform, false);
                blocker.transform.localPosition = new Vector3(0f, 0f, 0.002f);
                BoxCollider box = blocker.AddComponent<BoxCollider>();
                box.size = new Vector3(WindowWidth + FrameBorder * 2f, WindowHeight + FrameBorder * 2f, 0.004f);

                // Title, growing upward from the frame's top edge.
                GameObject titleObject = new GameObject("Title");
                titleObject.layer = WindowLayer;
                titleObject.transform.SetParent(root.transform, false);
                title = titleObject.AddComponent<TextMeshPro>();
                title.fontSize = TitleFontSize;
                title.alignment = TextAlignmentOptions.Bottom;
                title.enableWordWrapping = false;
                title.richText = true;
                title.color = Color.white;
                LobbyPanel.ApplyGameFont(title);
                title.fontMaterial.renderQueue = TextRenderQueue; // this text's own material instance
                title.outlineWidth = TitleOutlineWidth;
                title.outlineColor = Color.black;
                title.rectTransform.pivot = new Vector2(0.5f, 0f);
                title.rectTransform.sizeDelta = new Vector2(WindowWidth, 0.1f);
                titleObject.transform.localPosition = new Vector3(0f, WindowHeight * 0.5f + FrameBorder + TitleGap, -0.002f);
                lastTitle = null;

                Log("built");
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.LogError("[SpectatorWindow] build failed: " + ex);
                buildFailed = true;
                Destroy();
                return false;
            }
        }

        /// <summary>A quad facing local -Z with UVs (not mirrored when seen from -Z), no collider. z pushes it back.</summary>
        private static void CreateQuad(string name, Transform parent, float width, float height, float z, Material material)
        {
            GameObject quad = new GameObject(name);
            quad.layer = WindowLayer;
            quad.transform.SetParent(parent, false);
            quad.transform.localPosition = new Vector3(0f, 0f, z);
            quad.transform.localScale = new Vector3(width, height, 1f);

            Mesh mesh = new Mesh();
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f)
            };
            mesh.uv = new[]
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f),
                new Vector2(1f, 1f), new Vector2(0f, 1f)
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

        /// <summary>Sky, background and clip planes from the spectator camera (or the headset's, without one). The mask is our own.</summary>
        private static void ConfigureCamera()
        {
            Camera source = null;
            string from = "fallbacks";
            try
            {
                foreach (UnityEngine.Object o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<SpectatorCam>()))
                {
                    SpectatorCam candidate = o.TryCast<SpectatorCam>();
                    if (candidate == null || !candidate.gameObject.scene.IsValid()) continue; // skip prefabs
                    source = candidate.cam;
                    from = "SpectatorCam.cam";
                    break;
                }
            }
            catch (Exception ex)
            {
                Log($"SpectatorCam lookup failed: {ex.Message}");
            }
            if (source == null)
            {
                source = Camera.main;
                if (source != null) from = "Camera.main";
            }

            if (source != null)
            {
                windowCamera.clearFlags = source.clearFlags;
                windowCamera.backgroundColor = source.backgroundColor;
            }
            windowCamera.nearClipPlane = NearClip;
            windowCamera.farClipPlane = FarClip;
            windowCamera.fieldOfView = FallbackFov;
            cameraConfigured = source != null; // without any camera yet, try again next frame

            Log($"camera from {from}: clear={windowCamera.clearFlags} near={windowCamera.nearClipPlane} far={windowCamera.farClipPlane}");
        }

        // ── Who is watched ───────────────────────────────────────────────────

        /// <summary>Everyone the window can show, in seat order: the other lobby members, then the debug replay's fake player.</summary>
        private static void RefreshCandidates()
        {
            candidates.Clear();
            MpLobbyState lobby = MpSession.Lobby;
            if (lobby != null && lobby.players != null)
            {
                foreach (MpPlayerState player in lobby.players)
                {
                    if (player == null || player.userId == null || player.userId == MpSession.MyUserId) continue;
                    candidates.Add(player.userId);
                }
            }
            if (SpectatorDebug.ReplayActive) candidates.Add(SpectatorDebug.FakeUserId);
        }

        /// <summary>Keeps the watched player while they can still be picked; otherwise the first one whose avatar is shown, else the first.</summary>
        private static void PickTarget()
        {
            RefreshCandidates();
            if (watchedId != null && candidates.Contains(watchedId)) return;

            string firstVisible = null;
            foreach (string id in candidates)
            {
                if (HeadOf(id) != null) { firstVisible = id; break; }
            }
            string pick = firstVisible ?? (candidates.Count > 0 ? candidates[0] : null);
            if (pick != watchedId)
            {
                watchedId = pick;
                hasCameraPose = false;
                Log("watching " + (pick ?? "nobody"));
            }
        }

        private static void OnNextShot()
        {
            RefreshCandidates();
            if (candidates.Count == 0) return;

            // The one after the watched player, wrapping around.
            int index = watchedId != null ? candidates.IndexOf(watchedId) : -1;
            string pick = candidates[(index + 1) % candidates.Count];
            if (pick != watchedId)
            {
                watchedId = pick;
                hasCameraPose = false;
                Log("watching " + pick);
            }
            nextStateRefresh = 0f;
        }

        /// <summary>
        /// The watched avatar's head copy while it is shown with a pose, else null. Found by name
        /// under MpPresence's avatar holder; looked up again when the avatar was rebuilt or the
        /// watched player changed.
        /// </summary>
        private static Transform HeadOf(string userId)
        {
            if (userId == null) return null;

            if (resolvedId != userId || watchedRoot == null || watchedHead == null)
            {
                resolvedId = userId;
                watchedRoot = null;
                watchedHead = null;
                GameObject holder = GameObject.Find(AvatarHolderName);
                if (holder == null) return null;
                watchedRoot = holder.transform.Find(AvatarPrefix + userId);
                if (watchedRoot == null) return null;
                watchedHead = watchedRoot.Find(AvatarHeadName);
                if (watchedHead == null) return null;
            }

            if (!watchedRoot.gameObject.activeSelf || !watchedHead.gameObject.activeSelf) return null;
            return watchedHead;
        }

        // ── Camera ───────────────────────────────────────────────────────────

        private static void UpdateCamera()
        {
            float fov = FallbackFov, positionSpeed = FallbackPositionSpeed, rotationSpeed = FallbackRotationSpeed;
            try
            {
                PlayerPreferences prefs = PlayerPreferences.I;
                if (prefs != null)
                {
                    fov = prefs.SpectatorCamFOV.mVal;
                    positionSpeed = prefs.SpectatorCamPositionTrackSpeed.mVal;
                    rotationSpeed = prefs.SpectatorCamRotationTrackSpeed.mVal;
                }
            }
            catch
            {
                // keep the fallbacks
            }
            if (fov < 1f || fov > 179f) fov = FallbackFov;
            if (windowCamera.fieldOfView != fov) windowCamera.fieldOfView = fov;

            Transform head = HeadOf(watchedId);
            targetVisible = head != null;
            if (head == null) return; // holds the last view

            // In a song (MpPresence put their avatar on SongOnlyLayer): only their gameplay, and
            // nothing at all until their song is installed here (SpectatorSong).
            int mask = head.gameObject.layer == MpPresence.SongOnlyLayer ? SongMask : LobbyMask;
            if (!SpectatorSong.ViewAllowed(watchedId)) mask = 0;
            if (windowCamera.cullingMask != mask) windowCamera.cullingMask = mask;

            Vector3 targetPos = head.position;
            Quaternion targetRot = head.rotation;
            Transform cam = windowCamera.transform;

            if (!hasCameraPose || (cam.position - targetPos).sqrMagnitude > SnapDistance * SnapDistance)
            {
                cam.position = targetPos;
                cam.rotation = targetRot;
                hasCameraPose = true;
                return;
            }

            // SpectatorCam.Update (Ghidra 2026-11-13).
            float dt = Time.deltaTime;
            cam.position = Vector3.Lerp(cam.position, targetPos, Mathf.Clamp01(dt * positionSpeed));
            cam.rotation = Quaternion.Slerp(cam.rotation, targetRot, dt * rotationSpeed);
        }

        // ── Buttons and title ────────────────────────────────────────────────

        private static void UpdateButtons()
        {
            if (!EnsureNextButton()) return;
            ShellPage page = LobbyPanel.CurrentShellPage();
            if (page != null) nextButton.mShellPage = page;
            nextButton.SetInteractable(candidates.Count > 1);
        }

        private static bool EnsureNextButton()
        {
            if (nextObject != null && nextButton != null) return true; // Unity-null: rebuilt if destroyed

            if (!OptionsMenuClone.EnsureClone() || OptionsMenuClone.Menu == null) return false;

            // A null isChecked makes a plain button, with no checkbox square.
            OptionsMenuButton button = OptionsMenuClone.Menu.AddButton(0, "Next Player",
                new Action(OnNextShot),
                (OptionsMenuButton.IsCheckedDelegate)null,
                "Watches the next player in the lobby");
            if (button == null || button.button == null)
            {
                Log("couldn't make the Next Player button");
                if (button != null) GameObject.Destroy(button.gameObject);
                return false;
            }

            nextObject = button.gameObject;
            nextObject.name = "NextPlayerButton";
            nextObject.transform.SetParent(root.transform, false);
            nextObject.transform.localPosition = NextButtonPosition;
            nextObject.transform.localRotation = Quaternion.identity;
            nextObject.transform.localScale = ButtonScale;

            nextButton = button.button;
            nextButton.disableOnShot = false;
            nextButton.destroyOnShot = false;
            if (button.help != null) button.help.forceDisable = true;

            Log("Next Player button created");
            return true;
        }

        private static void UpdateTitle()
        {
            string content;
            MpLobbyState lobby = MpSession.Lobby;
            MpPlayerState player = lobby != null && watchedId != null ? lobby.FindPlayer(watchedId) : null;
            if (watchedId == SpectatorDebug.FakeUserId && SpectatorDebug.ReplayActive)
                content = SpectatorDebug.FakeName + (targetVisible ? "" : " <color=#c0c0c0>(not visible right now)</color>");
            else if (player == null) content = "Nobody to watch";
            else
            {
                string name = "<noparse>" + (player.nickname ?? "").Replace("</noparse>", "") + "</noparse>";
                if (targetVisible) content = name;
                else if (!Config.ShowOtherPlayers) content = name + " <color=#c0c0c0>(turn on Show Other Players)</color>";
                else if (player.location == "InSong" && !MpSession.SpectateEnabled) content = name + " <color=#c0c0c0>(in a song: this server can't show it)</color>";
                else if (player.location == "InSong") content = name + " <color=#c0c0c0>(in a song, not sharing)</color>";
                else content = name + " <color=#c0c0c0>(not visible right now)</color>";
            }

            // Second line: the watched player's song (title, download progress, or why it can't be spectated).
            string songStatus = SpectatorSong.StatusText(watchedId);
            if (songStatus != null) content += "\n<size=70%>" + songStatus + "</size>";

            if (content == lastTitle) return;
            lastTitle = content;
            title.text = content;
        }

        // ── Dragging ─────────────────────────────────────────────────────────

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

        /// <summary>
        /// The grip button of this gun's controller. Not HoldAction.GrabScroll: that is the trigger
        /// (the song list is dragged with the trigger; found in game 2026-11-15). KataInput.GripButtonHeld
        /// is the game's own grip check; the Grip axis is a fallback in case it reads false on a runtime.
        /// </summary>
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

        /// <summary>
        /// For OptionsMenuFunctions' GrabScroll.OnGrab prefix: true while the window is shown and this
        /// gun is dragging it or points at it, so a trigger grab on the window doesn't scroll a list behind it.
        /// </summary>
        internal static bool BlocksGrabScroll(Gun gun)
        {
            try
            {
                if (gun == null || root == null || !root.activeSelf) return false;
                if (grabHand >= 0 && (grabHand == 0 ? gunL : gunR) == gun) return true;
                return PointsAtWindow(gun);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// The grip pressed while that gun points at the window grabs it; it then keeps its pose
        /// relative to the gun until the grip is released, and the new pose is saved.
        /// </summary>
        private static void UpdateGrab()
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
                    if (!down)
                    {
                        EndGrab(true);
                        continue;
                    }
                    Transform g = gun.transform;
                    root.transform.position = g.position + g.rotation * grabLocalPos;
                    root.transform.rotation = g.rotation * grabLocalRot;
                }
                else if (grabHand < 0 && pressed && PointsAtWindow(gun))
                {
                    Transform g = gun.transform;
                    Quaternion inverse = Quaternion.Inverse(g.rotation);
                    grabLocalPos = inverse * (root.transform.position - g.position);
                    grabLocalRot = inverse * root.transform.rotation;
                    grabHand = h;
                }
            }
        }

        private static void EndGrab(bool save)
        {
            if (grabHand < 0) return;
            grabHand = -1;
            if (save) SavePose();
        }

        /// <summary>The gun's firepoint ray against the window's rectangle (frame included), from either side.</summary>
        private static bool PointsAtWindow(Gun gun)
        {
            Transform firepoint = gun.firepoint;
            if (firepoint == null) firepoint = gun.transform;

            Quaternion inverse = Quaternion.Inverse(root.transform.rotation);
            Vector3 origin = inverse * (firepoint.position - root.transform.position);
            Vector3 direction = inverse * firepoint.forward;
            if (Mathf.Abs(direction.z) < 0.0001f) return false;

            float t = -origin.z / direction.z;
            if (t <= 0f) return false;

            float x = origin.x + direction.x * t;
            float y = origin.y + direction.y * t;
            return Mathf.Abs(x) <= WindowWidth * 0.5f + FrameBorder && Mathf.Abs(y) <= WindowHeight * 0.5f + FrameBorder;
        }

        // ── Saved pose ───────────────────────────────────────────────────────

        private static void EnsurePrefs()
        {
            if (prefsRegistered) return;
            prefsRegistered = true;
            MelonPrefs.RegisterString(PrefsCategory, PrefsPose, "", "Spectator window position (set by dragging it in game)");
        }

        /// <summary>Read on every build: the saved string is the single source for where the window goes.</summary>
        private static void LoadPose()
        {
            try
            {
                EnsurePrefs();
                string saved = MelonPrefs.GetString(PrefsCategory, PrefsPose);
                if (string.IsNullOrEmpty(saved)) return;

                string[] parts = saved.Split(',');
                if (parts.Length != 7) return;
                float[] v = new float[7];
                for (int i = 0; i < 7; i++)
                {
                    if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i])) return;
                    if (float.IsNaN(v[i]) || float.IsInfinity(v[i])) return;
                }
                Quaternion rotation = new Quaternion(v[3], v[4], v[5], v[6]);
                if (Mathf.Abs(v[3]) + Mathf.Abs(v[4]) + Mathf.Abs(v[5]) + Mathf.Abs(v[6]) < 0.5f) return;

                root.transform.position = new Vector3(v[0], v[1], v[2]);
                root.transform.rotation = rotation;
                Log("pose restored");
            }
            catch (Exception ex)
            {
                Log($"pose load failed: {ex.Message}");
            }
        }

        private static void SavePose()
        {
            try
            {
                EnsurePrefs();
                Vector3 p = root.transform.position;
                Quaternion q = root.transform.rotation;
                string value = string.Join(",", new[]
                {
                    p.x.ToString("0.####", CultureInfo.InvariantCulture),
                    p.y.ToString("0.####", CultureInfo.InvariantCulture),
                    p.z.ToString("0.####", CultureInfo.InvariantCulture),
                    q.x.ToString("0.#####", CultureInfo.InvariantCulture),
                    q.y.ToString("0.#####", CultureInfo.InvariantCulture),
                    q.z.ToString("0.#####", CultureInfo.InvariantCulture),
                    q.w.ToString("0.#####", CultureInfo.InvariantCulture)
                });
                MelonPrefs.SetString(PrefsCategory, PrefsPose, value);
                MelonPrefs.SaveConfig();
                Log("pose saved: " + value);
            }
            catch (Exception ex)
            {
                Log($"pose save failed: {ex.Message}");
            }
        }

        /// <summary>Puts the window back at its default place (from the UnityExplorer console) and saves that.</summary>
        public static void ResetPose()
        {
            if (root == null) return;
            EndGrab(false);
            root.transform.position = DefaultPosition;
            root.transform.eulerAngles = DefaultRotation;
            SavePose();
        }

        // ── Cleanup / debug ──────────────────────────────────────────────────

        private static void Destroy()
        {
            try
            {
                if (windowCamera != null && !windowCamera.WasCollected) windowCamera.targetTexture = null;
                if (root != null && !root.WasCollected) GameObject.Destroy(root); // the Next Player button goes with it
                if (texture != null && !texture.WasCollected) { texture.Release(); UnityEngine.Object.Destroy(texture); }
                if (pictureMaterial != null && !pictureMaterial.WasCollected) UnityEngine.Object.Destroy(pictureMaterial);
                if (frameMaterial != null && !frameMaterial.WasCollected) UnityEngine.Object.Destroy(frameMaterial);
            }
            catch (Exception ex)
            {
                Log($"destroy failed: {ex.Message}");
            }
            root = null; windowCamera = null; texture = null; title = null;
            pictureMaterial = null; frameMaterial = null;
            nextObject = null; nextButton = null;
            cameraConfigured = false; hasCameraPose = false; grabHand = -1; lastTitle = null;
        }

        public static void DebugDump()
        {
            MelonLogger.Log("[SpectatorWindow] ── dump ──");
            MelonLogger.Log($"  open={open} buildFailed={buildFailed} inLobby={MpSession.InLobby} inSongScene={InSongScene()} showOtherPlayers={Config.ShowOtherPlayers}");
            MelonLogger.Log($"  spectate connection={MpSession.SpectateEnabled} watchersOfMe={MpSession.WatcherCount}");
            MelonLogger.Log($"  watched={watchedId ?? "none"} targetVisible={targetVisible} avatarRoot={(watchedRoot != null ? "found" : "null")} head={(watchedHead != null ? "found" : "null")}");
            MelonLogger.Log($"  root={(root != null ? (root.activeSelf ? "active" : "inactive") : "null")} opener={(openerObject != null ? "built" : "null")} next={(nextObject != null ? "built" : "null")}");
            if (root != null && !root.WasCollected)
                MelonLogger.Log($"  window pos={root.transform.position} rot={root.transform.eulerAngles} size={WindowWidth}x{WindowHeight:0.###} grabHand={grabHand}");
            if (windowCamera != null && !windowCamera.WasCollected)
                MelonLogger.Log($"  camera pos={windowCamera.transform.position} rot={windowCamera.transform.eulerAngles} fov={windowCamera.fieldOfView} " +
                    $"mask=0x{windowCamera.cullingMask:X8} clear={windowCamera.clearFlags} configured={cameraConfigured} hasPose={hasCameraPose}");
            if (watchedHead != null && !watchedHead.WasCollected)
                MelonLogger.Log($"  head pos={watchedHead.position} rot={watchedHead.eulerAngles} active={watchedHead.gameObject.activeInHierarchy}");
            MelonLogger.Log($"  guns: left={(gunL != null ? "found" : "null")} right={(gunR != null ? "found" : "null")}");
            MelonLogger.Log($"  song: stage={SpectatorSong.CurrentStage} viewAllowed={SpectatorSong.ViewAllowed(watchedId)} (more: SpectatorSong.DebugDump())");
        }
    }
}