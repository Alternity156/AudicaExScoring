using System;
using System.Collections.Generic;
using System.IO;
using MelonLoader;
using TMPro;
using UnhollowerBaseLib;      // TryCast
using UnhollowerRuntimeLib;   // Il2CppType (ML 0.5.7)
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// Spectator diorama: the watched player's song as a small copy in front of this player, looked
    /// down on, instead of in the spectator window. Driven by SpectatorWindow's diorama mode (its 3D /
    /// 2D button), which owns the spot, the scale, grabbing and the saved settings, and calls Show
    /// every frame while the watched player is in a song and Hide otherwise. Findings:
    /// claude/Spectator-Diorama-Findings.md.
    ///
    /// From the UnityExplorer console:
    ///   ExScoringMod.SpectatorDiorama.Probe();   report, changes nothing
    ///
    /// Probe writes UserData/ExScoring_DioramaProbe.txt line by line (flushed after each line, so after
    /// a crash the last line says where it stopped) and the same lines to the MelonLoader log. It reports:
    /// every camera's near clip and culling mask; the watched avatar root and ExScoring_SpectatorTargets
    /// (positions, the visible mesh bounds relative to the root); per component type, aggregated
    /// (identical items counted once): LineRenderer / TrailRenderer widths and world space,
    /// ParticleSystemRenderer mode and min/max screen size (the unhollowed ParticleSystem.MainModule
    /// has no scalingMode and a set-only simulationSpace, so those two aren't read),
    /// TextMeshPro size, mesh shaders, lights, enabled colliders, Animation / Animator culling; the
    /// fields of SustainBeamLine and SustainBeam; then every other active object on layer 31, one line
    /// per root.
    ///
    /// Show: the watched player's play space, shrunk, with the centre of their target sphere (the
    /// SpawnerDome, local (0, -1.5, 6) from their root) at the given centre, facing +z, upright.
    ///   MpPresence.Tick asks Place for the avatar root's world pose every frame instead of its seat
    ///   (the only hook in MpPresence), so everything drawn from that root this frame is drawn in the
    ///   diorama: head, hands and guns (localPosition under the root, scaled with it), the targets
    ///   (SpectatorTargets puts its root on the avatar root's pose every frame; Show parents it under
    ///   the avatar root, so it also takes the scale), score popups (under the avatar root), and the
    ///   world-space lines worked out from those positions (chain and simultaneous lines, sustain
    ///   beam points, shot beams).
    ///   Widths: LateTick (from ExScoring.OnLateUpdate) multiplies every LineRenderer under the avatar
    ///   root by the scale each frame, after their owners have set this frame's width (see ScaledLine);
    ///   MpPresence scales the watched player's shot beams when it fires them (ScaleOf), since they
    ///   live under KataConfig. The sustain beam's sideways swing (SustainBeamLine.displacementAmount,
    ///   metres) is scaled the same way (ScaledBeamLine), and its bezier's forward point
    ///   (SpectatorTargets: 2 m ahead of the gun) shrinks with ScaleOf.
    ///   Cameras: layer 31 (MpPresence.SongOnlyLayer) is added to the headset camera and to every
    ///   camera of SpectatorCam (the desktop view and its preview), kept there every frame, and their
    ///   near clip goes to 0.01 m (probe 2026-11-16: CenterEyeAnchor and SpectatorCam near 0.1) so
    ///   leaning in doesn't cut it. Hide puts them back.
    /// Not moved or scaled (probe 2026-11-16): what lives under the scene's "Instantiated" root in
    /// world space, i.e. the target trails (TrailTarget / TrailMelee particles) and the muzzle flashes
    /// and shot dust (MpMuzzleFlash ...). They are emitted from the diorama's positions but at full size.
    /// If the avatar is rebuilt, the targets root goes with it; SpectatorTargets makes a new one, which
    /// Show picks up again.
    /// </summary>
    internal static class SpectatorDiorama
    {
        private const string ProbeFile = "UserData/ExScoring_DioramaProbe.txt";
        private const string TargetsRootName = "ExScoring_SpectatorTargets";

        // SpawnerDome in the watched player's play space (SpectatorTargets.DomePosition).
        private static readonly Vector3 DomeLocal = new Vector3(0f, -1.5f, 6f);

        // Show / Hide state.
        private static Vector3 placedPosition;
        private static Quaternion placedRotation = Quaternion.identity;
        private static float placedScale = 1f;
        private static Transform triedAvatar;
        private static Transform triedTargets;
        private static Vector3 avatarScaleBefore = Vector3.one;
        private const float DioramaNearClip = 0.01f; // the headset's 0.1 m would cut into a 0.75 m diorama up close
        private static bool tried;

        /// <summary>A camera that shows the diorama while it's up: the headset, SpectatorCam's desktop view and its preview.</summary>
        private sealed class ShownCamera
        {
            public Camera Camera;
            public string Name;
            public bool Had31;       // already rendered layer 31 before Show (left as it was by Hide)
            public float NearBefore;
        }
        private static readonly List<ShownCamera> shownCameras = new List<ShownCamera>();

        /// <summary>
        /// A LineRenderer under the diorama, whose width is scaled every frame. Base is the width the
        /// mod or the game gave it; Written is what this class last set. A value that is no longer
        /// Written was set again by its owner (SpectatorTargets animates the simultaneous lines, the
        /// sustain beam's lines pulse), so it becomes the new base.
        /// </summary>
        private struct ScaledLine
        {
            public LineRenderer Line;
            public float Base;
            public float Written;
        }
        private static readonly Dictionary<IntPtr, ScaledLine> scaledLines = new Dictionary<IntPtr, ScaledLine>();

        /// <summary>
        /// A sustain beam line under the diorama, whose SustainBeamLine.displacementAmount (how far the
        /// line swings sideways with the sustain track, in metres) is scaled. Same Base / Written idea
        /// as ScaledLine. A setting, not animated: Base normally stays the prefab's.
        /// </summary>
        private struct ScaledBeamLine
        {
            public SustainBeamLine Line;
            public float Base;
            public float Written;
        }
        private static readonly Dictionary<IntPtr, ScaledBeamLine> scaledBeamLines = new Dictionary<IntPtr, ScaledBeamLine>();

        private static StreamWriter writer;

        // ── Probe ────────────────────────────────────────────────────────────

        public static void Probe()
        {
            try
            {
                writer = new StreamWriter(ProbeFile, false) { AutoFlush = true };
            }
            catch (Exception ex)
            {
                MelonLogger.Log("[SpectatorDiorama] can't open " + ProbeFile + ": " + ex.Message);
                writer = null;
            }

            try
            {
                P("=== Diorama probe " + DateTime.Now + " ===");

                // Cameras.
                P("reading cameras");
                Camera main = Camera.main;
                Vector3 eye = main != null ? main.transform.position : Vector3.zero;
                foreach (Camera cam in UnityEngine.Object.FindObjectsOfType<Camera>())
                {
                    if (cam == null) continue;
                    P($"camera {PathOf(cam.transform)} enabled={cam.enabled} near={cam.nearClipPlane} far={cam.farClipPlane} fov={cam.fieldOfView} " +
                      $"mask=0x{cam.cullingMask:X8} has31={(cam.cullingMask & (1 << MpPresence.SongOnlyLayer)) != 0} " +
                      $"target={(cam.targetTexture != null ? cam.targetTexture.name : "screen")} clear={cam.clearFlags} main={cam == main}");
                }
                P($"headset pos={V(eye)}");

                GameObject window = GameObject.Find("ExScoring_SpectatorWindow");
                if (window != null) P($"window pos={V(window.transform.position)} rot={V(window.transform.eulerAngles)} lossy={V(window.transform.lossyScale)}");

                // Watched avatar.
                Transform avatar = SpectatorWindow.ShownWatchedRoot;
                if (avatar == null) P("no watched avatar shown (window closed, or the watched player isn't drawn)");
                else
                {
                    Transform head = avatar.Find("Head");
                    P($"watched avatar '{avatar.name}' pos={V(avatar.position)} localScale={V(avatar.localScale)} " +
                      $"headLocal={(head != null ? V(head.localPosition) : "-")} headLayer={(head != null ? head.gameObject.layer : -1)}");
                    Inventory(avatar, eye);
                }

                // Targets root.
                GameObject targets = GameObject.Find(TargetsRootName);
                if (targets == null) P("no " + TargetsRootName + " (window not showing a song?)");
                else
                {
                    Transform dome = targets.transform.Find("SpawnerDome");
                    Transform melee = targets.transform.Find("MeleeHeightAdjust");
                    P($"targets parent={(targets.transform.parent != null ? targets.transform.parent.name : "-")} " +
                      $"dome local={(dome != null ? V(dome.localPosition) : "-")} meleeRoot local={(melee != null ? V(melee.localPosition) : "-")}");
                    if (targets.transform.parent == null || targets.transform.parent != avatar)
                        Inventory(targets.transform, eye);
                    else
                        P("  (under the watched avatar: already counted above)");
                }

                // The sustain beam's own settings (a hand's beam exists once that hand held a sustain).
                if (targets != null) DumpFields(targets.transform, "SustainBeamLine");
                if (targets != null) DumpFields(targets.transform, "SustainBeam");

                // Everything else on layer 31.
                P("reading other active objects on layer 31");
                var roots = new Dictionary<string, int>();
                var rootTransforms = new Dictionary<string, Transform>();
                foreach (Transform t in UnityEngine.Object.FindObjectsOfType<Transform>())
                {
                    if (t == null || t.gameObject.layer != MpPresence.SongOnlyLayer) continue;
                    Transform r = t.root;
                    if (avatar != null && r == avatar.root) continue;
                    if (r.name == TargetsRootName) continue;
                    roots.TryGetValue(r.name, out int n);
                    roots[r.name] = n + 1;
                    if (n == 0)
                    {
                        rootTransforms[r.name] = r;
                        P($"layer31 elsewhere: {PathOf(t)} pos={V(t.position)}");
                    }
                }
                foreach (var kv in roots) P($"layer31 root '{kv.Key}': {kv.Value} active objects");
                foreach (var kv in rootTransforms) Inventory(kv.Value, eye);

                P("=== done ===");
            }
            catch (Exception ex)
            {
                P("probe failed: " + ex);
            }
            finally
            {
                if (writer != null)
                {
                    try { writer.Dispose(); } catch { }
                    writer = null;
                }
                MelonLogger.Log("[SpectatorDiorama] probe written to " + ProbeFile);
            }
        }

        private static void Inventory(Transform r, Vector3 eye)
        {
            P($"== {PathOf(r)} pos={V(r.position)} rot={V(r.eulerAngles)} lossy={V(r.lossyScale)} layer={r.gameObject.layer} " +
              $"active={r.gameObject.activeInHierarchy} children={r.childCount} distFromHead={(r.position - eye).magnitude:F2}");
            var agg = new Dictionary<string, int>();
            void A(string key)
            {
                agg.TryGetValue(key, out int n);
                agg[key] = n + 1;
            }

            P("  reading LineRenderers");
            foreach (LineRenderer lr in r.GetComponentsInChildren<LineRenderer>(true))
                A($"LineRenderer '{lr.name}' on={lr.enabled && lr.gameObject.activeInHierarchy} world={lr.useWorldSpace} widthMul={lr.widthMultiplier:F3} " +
                  $"start={lr.startWidth:F3} end={lr.endWidth:F3} align={lr.alignment} layer={lr.gameObject.layer} shader={ShaderOf(lr.sharedMaterial)}");

            // The TrailRenderer type this project compiles against isn't a usable unhollowed type (no
            // Renderer members, no pointer constructor), so it is never named: trails are found as
            // Renderers by Il2Cpp type name, and their own properties are read through Il2Cpp
            // reflection (as MpPresence.TintedParticles does) and printed as text.
            P("  reading TrailRenderers");
            foreach (Renderer rd in r.GetComponentsInChildren<Renderer>(true))
            {
                if (rd == null || rd.GetIl2CppType().Name != "TrailRenderer") continue;
                A($"TrailRenderer '{rd.name}' on={rd.enabled && rd.gameObject.activeInHierarchy} time={Prop(rd, "time")} " +
                  $"widthMul={Prop(rd, "widthMultiplier")} minVert={Prop(rd, "minVertexDistance")} shader={ShaderOf(rd.sharedMaterial)}");
            }

            P("  reading ParticleSystemRenderers");
            foreach (ParticleSystemRenderer pr in r.GetComponentsInChildren<ParticleSystemRenderer>(true))
                A($"ParticleSystemRenderer '{pr.name}' parent='{(pr.transform.parent != null ? pr.transform.parent.name : "-")}' active={pr.gameObject.activeInHierarchy} " +
                  $"mode={pr.renderMode} minScreen={pr.minParticleSize:F3} maxScreen={pr.maxParticleSize:F3} shader={ShaderOf(pr.sharedMaterial)}");

            P("  reading TextMeshPro");
            foreach (TextMeshPro tmp in r.GetComponentsInChildren<TextMeshPro>(true))
                A($"TextMeshPro '{tmp.name}' size={tmp.fontSize} lossy={tmp.transform.lossyScale.x:F3}");

            P("  reading MeshRenderers / SkinnedMeshRenderers");
            foreach (MeshRenderer rd in r.GetComponentsInChildren<MeshRenderer>(true))
                A($"MeshRenderer '{rd.name}' shader={ShaderOf(rd.sharedMaterial)}");
            foreach (SkinnedMeshRenderer rd in r.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                A($"SkinnedMeshRenderer '{rd.name}' shader={ShaderOf(rd.sharedMaterial)}");

            P("  reading Lights / Colliders / Animation / Animator");
            foreach (Light li in r.GetComponentsInChildren<Light>(true)) A($"Light '{li.name}' type={li.type} range={li.range}");
            foreach (Collider c in r.GetComponentsInChildren<Collider>(true)) if (c.enabled) A($"Collider ENABLED '{c.name}'");
            foreach (Animation an in r.GetComponentsInChildren<Animation>(true)) A($"Animation '{an.name}' cullingType={an.cullingType}");
            foreach (Animator an in r.GetComponentsInChildren<Animator>(true)) A($"Animator '{an.name}' cull={an.cullingMode}");

            foreach (var kv in agg) P($"  x{kv.Value}  {kv.Key}");

            P("  reading mesh bounds");
            bool any = false;
            Bounds b = new Bounds();
            foreach (MeshRenderer rd in r.GetComponentsInChildren<MeshRenderer>(false))
            {
                if (!rd.enabled) continue;
                if (!any) { b = rd.bounds; any = true; }
                else b.Encapsulate(rd.bounds);
            }
            if (any) P($"  mesh bounds (world, minus root pos): min={V(b.min - r.position)} max={V(b.max - r.position)} size={V(b.size)}");
        }

        // ── Show / Hide (driven by SpectatorWindow's diorama mode) ────────────

        /// <summary>
        /// Every frame from SpectatorWindow while its diorama mode shows a player in a song: their
        /// play space, shrunk to scale, with the centre of their target sphere at centre, facing +z.
        /// A new avatar hides the old one's first. lookupTargets: look for SpectatorTargets' root if it
        /// isn't under the avatar yet (a GameObject.Find, so the window asks only a few times a second).
        /// </summary>
        internal static void Show(Transform avatar, Vector3 centre, float scale, bool lookupTargets)
        {
            if (avatar == null || scale <= 0f)
            {
                Hide();
                return;
            }
            if (tried && triedAvatar != avatar) Hide();
            if (!tried)
            {
                avatarScaleBefore = avatar.localScale;
                triedAvatar = avatar;
                triedTargets = null;
                tried = true;
                CollectCameras(Camera.main);
                var names = new List<string>();
                foreach (ShownCamera c in shownCameras) names.Add(c.Name);
                Log($"shown: '{avatar.name}' at {V(centre)}, scale {scale:0.###}, on {string.Join(", ", names)}");
            }

            placedRotation = Quaternion.identity;
            placedScale = scale;
            placedPosition = centre - placedRotation * (DomeLocal * scale);

            if ((triedTargets == null || triedTargets.parent != avatar) && lookupTargets)
            {
                GameObject targets = GameObject.Find(TargetsRootName);
                if (targets != null)
                {
                    Transform t = targets.transform;
                    if (t.parent != avatar) t.SetParent(avatar, false);
                    // On the avatar's pose and scale only: a scale left on the root itself (from an
                    // earlier diorama) would shrink the targets twice and pull the dome towards the
                    // player.
                    t.localPosition = Vector3.zero;
                    t.localRotation = Quaternion.identity;
                    t.localScale = Vector3.one;
                    triedTargets = t;
                }
            }

            if (avatar.localScale.x != scale) avatar.localScale = Vector3.one * scale;
            avatar.position = placedPosition; // MpPresence keeps it there from the next frame (Place)
            avatar.rotation = placedRotation;
        }

        /// <summary>
        /// From MpPresence.Tick, every frame, for every drawn avatar, just before it sets the root's
        /// pose: replaces the seat pose with the diorama's while one is shown for this root.
        /// </summary>
        internal static void Place(Transform root, ref Vector3 position, ref Quaternion rotation)
        {
            if (!tried || root == null || root != triedAvatar) return;
            position = placedPosition;
            rotation = placedRotation;
            if (root.localScale.x != placedScale) root.localScale = Vector3.one * placedScale;
        }

        /// <summary>
        /// The diorama's scale for this avatar root (1 when none is shown for it). MpPresence uses it
        /// for the watched player's shot beams, which it makes under KataConfig, outside the root.
        /// </summary>
        internal static float ScaleOf(Transform root)
        {
            return tried && root != null && root == triedAvatar ? placedScale : 1f;
        }

        /// <summary>
        /// From ExScoring.OnLateUpdate, after MpPresence.LateTick: after everything has set this frame's
        /// line widths (SpectatorTargets in OnUpdate, the game's SustainBeamLine in its Update), the
        /// diorama's lines are made as thin as its scale; and layer 31 is kept on the cameras
        /// (SpectatorCam.UpdateSettings rewrites its masks when the spectator settings change).
        /// </summary>
        internal static void LateTick()
        {
            if (!tried) return;
            if (triedAvatar == null)
            {
                Log("the diorama's avatar is gone: hidden");
                Hide();
                return;
            }
            try
            {
                ScaleLines();
                ScaleBeamLines();
                ShowOnCameras();
            }
            catch (Exception ex)
            {
                Log("late tick failed, hidden: " + ex.Message);
                Hide();
            }
        }

        private static void ScaleLines()
        {
            var seen = new HashSet<IntPtr>();
            foreach (LineRenderer line in triedAvatar.GetComponentsInChildren<LineRenderer>(true))
            {
                if (line == null) continue;
                IntPtr key = line.Pointer;
                seen.Add(key);
                float current = line.widthMultiplier;
                if (!scaledLines.TryGetValue(key, out ScaledLine entry) || Mathf.Abs(current - entry.Written) > 1e-6f)
                    entry = new ScaledLine { Line = line, Base = current };
                entry.Written = entry.Base * placedScale;
                line.widthMultiplier = entry.Written;
                scaledLines[key] = entry;
            }
            if (seen.Count == scaledLines.Count) return;
            var gone = new List<IntPtr>();
            foreach (IntPtr key in scaledLines.Keys) if (!seen.Contains(key)) gone.Add(key);
            foreach (IntPtr key in gone) scaledLines.Remove(key);
        }

        private static void ScaleBeamLines()
        {
            foreach (SustainBeamLine beamLine in triedAvatar.GetComponentsInChildren<SustainBeamLine>(true))
            {
                if (beamLine == null) continue;
                IntPtr key = beamLine.Pointer;
                float current = beamLine.displacementAmount;
                if (!scaledBeamLines.TryGetValue(key, out ScaledBeamLine entry) || Mathf.Abs(current - entry.Written) > 1e-6f)
                {
                    entry = new ScaledBeamLine { Line = beamLine, Base = current };
                    Log($"sustain beam line '{beamLine.name}': displacement {current:0.####} m, shown as {current * placedScale:0.####} m");
                }
                entry.Written = entry.Base * placedScale;
                if (Mathf.Abs(current - entry.Written) > 1e-6f) beamLine.displacementAmount = entry.Written;
                scaledBeamLines[key] = entry;
            }
        }

        private static void RestoreBeamLines()
        {
            foreach (ScaledBeamLine entry in scaledBeamLines.Values)
            {
                SustainBeamLine beamLine = entry.Line;
                if (beamLine == null) continue;
                try
                {
                    if (Mathf.Abs(beamLine.displacementAmount - entry.Written) <= 1e-6f) beamLine.displacementAmount = entry.Base;
                }
                catch { }
            }
            scaledBeamLines.Clear();
        }

        private static void RestoreLines()
        {
            foreach (ScaledLine entry in scaledLines.Values)
            {
                LineRenderer line = entry.Line;
                if (line == null) continue;
                try
                {
                    if (Mathf.Abs(line.widthMultiplier - entry.Written) <= 1e-6f) line.widthMultiplier = entry.Base;
                }
                catch { }
            }
            scaledLines.Clear();
        }

        private static void CollectCameras(Camera headset)
        {
            shownCameras.Clear();
            AddCamera(headset, "headset");
            try
            {
                foreach (UnityEngine.Object o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<SpectatorCam>()))
                {
                    SpectatorCam spectator = o.TryCast<SpectatorCam>();
                    if (spectator == null || !spectator.gameObject.scene.IsValid()) continue; // skip prefabs
                    foreach (Camera c in spectator.GetComponentsInChildren<Camera>(true))
                        AddCamera(c, c.gameObject == spectator.gameObject ? "SpectatorCam" : "SpectatorCam/" + c.name);
                    break;
                }
            }
            catch (Exception ex)
            {
                Log("SpectatorCam lookup failed: " + ex.Message);
            }
        }

        private static void AddCamera(Camera camera, string name)
        {
            if (camera == null) return;
            foreach (ShownCamera c in shownCameras) if (c.Camera == camera) return;
            shownCameras.Add(new ShownCamera
            {
                Camera = camera,
                Name = name,
                Had31 = (camera.cullingMask & (1 << MpPresence.SongOnlyLayer)) != 0,
                NearBefore = camera.nearClipPlane
            });
        }

        private static void ShowOnCameras()
        {
            if (shownCameras.Count == 0) CollectCameras(Camera.main); // Camera.main wasn't there yet when shown
            int bit = 1 << MpPresence.SongOnlyLayer;
            foreach (ShownCamera c in shownCameras)
            {
                if (c.Camera == null) continue;
                if ((c.Camera.cullingMask & bit) == 0) c.Camera.cullingMask |= bit;
                if (c.Camera.nearClipPlane > DioramaNearClip) c.Camera.nearClipPlane = DioramaNearClip;
            }
        }

        /// <summary>Puts everything back (no-op when nothing is shown).</summary>
        internal static void Hide()
        {
            if (!tried) return;
            tried = false; // first, so Place / ScaleOf / LateTick stop at once
            RestoreLines();
            RestoreBeamLines();
            if (triedTargets != null && triedTargets.parent != null)
            {
                // worldPositionStays false: with true, the diorama's scale would be baked into the
                // root's own localScale, and the next player's targets would be drawn at scale²
                // (smaller, and with the dome off the player). SpectatorTargets puts the root back on
                // the watched player's pose next frame.
                Vector3 pos = triedTargets.position;
                Quaternion rot = triedTargets.rotation;
                triedTargets.SetParent(null, false);
                triedTargets.position = pos;
                triedTargets.rotation = rot;
                triedTargets.localScale = Vector3.one;
                UnityEngine.Object.DontDestroyOnLoad(triedTargets.gameObject); // as SpectatorTargets made it
            }
            if (triedAvatar != null) triedAvatar.localScale = avatarScaleBefore;
            int bit = 1 << MpPresence.SongOnlyLayer;
            foreach (ShownCamera c in shownCameras)
            {
                if (c.Camera == null) continue;
                if (!c.Had31) c.Camera.cullingMask &= ~bit;
                c.Camera.nearClipPlane = c.NearBefore;
            }
            shownCameras.Clear();
            Log("hidden");
            triedAvatar = null;
            triedTargets = null;
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private static void P(string line)
        {
            MelonLogger.Log(line);
            if (writer == null) return;
            try { writer.WriteLine(line); } catch { }
        }

        private static void Log(string message) => DebugLog.Log("[SpectatorDiorama] " + message);

        private static string PathOf(Transform t)
        {
            string path = t.name;
            for (Transform p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
            return path;
        }

        /// <summary>
        /// Every instance field of the first component of this Il2Cpp type under root, through Il2Cpp
        /// reflection, as text. Names and references are right; value-type values are not (probe
        /// 2026-11-16: bandIndex -1112716128, displacementAmount -0.234, widthRange (-3631, 0)):
        /// read those through the typed class.
        /// </summary>
        private static void DumpFields(Transform root, string typeName)
        {
            P($"reading the fields of {typeName}");
            try
            {
                foreach (Component c in root.GetComponentsInChildren<Component>(true))
                {
                    if (c == null || c.GetIl2CppType().Name != typeName) continue;
                    P($"  {typeName} on {PathOf(c.transform)}");
                    var fields = c.GetIl2CppType().GetFields(Il2CppSystem.Reflection.BindingFlags.Public
                        | Il2CppSystem.Reflection.BindingFlags.NonPublic | Il2CppSystem.Reflection.BindingFlags.Instance);
                    foreach (var field in fields)
                    {
                        string value;
                        try
                        {
                            Il2CppSystem.Object v = field.GetValue(c);
                            value = v != null ? v.ToString() : "null";
                        }
                        catch (Exception ex) { value = "? (" + ex.Message + ")"; }
                        P($"    {field.FieldType.Name} {field.Name} = {value}");
                    }
                    return;
                }
                P($"  no {typeName} under {root.name} (hold a sustain in the replay first)");
            }
            catch (Exception ex)
            {
                P($"  {typeName} fields unreadable: {ex.Message}");
            }
        }

        /// <summary>An instance property read through Il2Cpp reflection, as text; "?" if it can't be read.</summary>
        private static string Prop(Component c, string name)
        {
            try
            {
                var prop = c.GetIl2CppType().GetProperty(name,
                    Il2CppSystem.Reflection.BindingFlags.Public | Il2CppSystem.Reflection.BindingFlags.Instance);
                Il2CppSystem.Object value = prop != null ? prop.GetValue(c, null) : null;
                return value != null ? value.ToString() : "?";
            }
            catch
            {
                return "?";
            }
        }

        /// <summary>
        /// A Vector3 as text. Interpolating an Il2Cpp Vector3 directly prints "UnityEngine.Vector3"
        /// (probe 2026-11-16), and its own ToString uses the system's decimal comma.
        /// </summary>
        private static string V(Vector3 v) =>
            string.Format(System.Globalization.CultureInfo.InvariantCulture, "({0:F2}, {1:F2}, {2:F2})", v.x, v.y, v.z);

        private static string ShaderOf(Material m)
        {
            if (m == null) return "-";
            return (m.shader != null ? m.shader.name : "?") + " q" + m.renderQueue;
        }
    }
}