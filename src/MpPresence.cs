using System;
using System.Collections.Generic;
using System.Text;
using MelonLoader;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// Player presence (ApiContract.md v2.39): the other lobby members drawn as the game's own
    /// default avatar (head, body, hands, guns), and this player's pose sent so they can draw us.
    /// Nothing runs during songs: the server neither sends nor accepts poses there. A match's end
    /// screen doesn't count as in the song (MpGameplay.OnResultsScreen, location "ResultsPage"), so
    /// presence resumes there; gun colours still wait until the menus.
    ///
    /// Sending (in a lobby, presence on, not in a song): ~15 poses a second, every part as its
    /// world pose [px,py,pz, qx,qy,qz,qw]:
    ///  - head  = OVRCameraRig.centerEyeAnchor
    ///  - hands = the KataHand objects (HandLeft / HandRight)
    ///  - guns  = GunLeft/GunRight's tricks/mesh, falling back to the Gun object itself. Gun tricks
    ///            spin "tricks" (a pivot under the Gun); "mesh" puts the model back on the Gun's
    ///            origin, so at rest this pose equals the Gun's (what older builds send) and during
    ///            a trick it follows the spin. A throw takes "tricks" out from under the Gun, so the
    ///            last mesh found is kept and sent wherever it is until it is back under the Gun
    ///  - triggers = Gun.GetTriggerPullAmount() >= Gun.GetTriggerThreshold()
    /// plus the gun colours (read from the prefs twice a second, sent by MpSession only on change).
    ///
    /// Drawing: each remote player's samples are buffered and drawn RenderDelayMs behind server
    /// time, interpolating on each sample's t (lerp / slerp, holding the last pose if nothing newer
    /// came). Players missing from the latest poses message are hidden; every avatar is hidden on
    /// entering a song (the server just stops sending, it never sends an empty list there).
    ///
    /// Avatars are copies of templates built once from the local rig, always the DEFAULT models
    /// (avatars[0] head/body, gun model "Gun1"), whatever anyone has selected:
    ///  - head  = 3rdPersonHead with only the default mask kept      (under CenterEyeAnchor)
    ///  - body  = the default torso, 0.39 m under the body point       (under 3rdPersonBody)
    ///  - hands = KataHand.normalHand (*_hand_skeletal_lowres), static (under HandLeft/Right)
    ///  - guns  = tricks/mesh/Gun1 with its offset and 0.021 scale     (under tricks/mesh)
    /// The copies are made under an inactive holder (so no Awake runs and the game's singletons
    /// stay untouched) and stripped of every component but Transform / MeshFilter / MeshRenderer /
    /// SkinnedMeshRenderer; muzzle, form indicator and multiplier display objects are removed.
    /// Head and body go to layer 0 (the local ones sit on ThirdPersonAvatar, which the headset
    /// never renders); hands and guns keep their layers. All are visible to the spectator camera.
    ///
    /// Gun colours: the remote gun copies get their own materials; the ones the game tints
    /// (GunRight/GunLeft metal, GunGlowRight/GunGlowLeft glow) get _Color = that player's colour.
    ///
    /// Body: TorsoTrack.Update's math on the remote head and hands (Ghidra):
    ///   dir = (lHand - head) + (rHand - head) + head.forward, flattened, normalised
    ///   pos = Lerp(pos, head, clamp01(dt * 10)); rot = Slerp(rot, LookRotation(dir), dt * 10)
    /// A missing hand is left out of the sum.
    ///
    /// Layout: a fixed grid of spots, the same for every client, numbered seat + 1, front row toward
    /// the targets (+Z), left = -X:
    ///      1   2   3
    ///      5   4   6
    ///        7   8          (7 between 5 and 4, 8 between 4 and 6)
    /// Spots are SpotSpacing apart. Each client shifts the grid so its own spot is where it stands:
    /// a remote player is drawn at spot(theirSeat) - spot(mySeat), plus their own pose. So seat 3
    /// (spot 4) is in the middle for everyone, seat 0 (spot 1) front-left for everyone. Everyone
    /// faces forward (no rotation).
    ///
    /// Discs: every remote player also gets a copy of the environment's platform (Static/Ground:
    /// the platform mesh, PlatformTops, PlatformEdges, platform_lower, terrain_shard), at the local
    /// platform's world pose plus the same grid offset, so it stays under their spot rather than
    /// following their head. The copies share the scene's materials, so the music-reactive shaders
    /// keep reacting. The source is looked up again every DiscCheckSeconds outside songs; when the
    /// environment swaps it for another object, the template and every copy are rebuilt from the new
    /// one, and when there is none, nobody gets a disc.
    ///
    /// Collisions: this player's thrown guns bounce off the other players' discs, guns, heads and
    /// bodies, as they do off the local platform and each other. Discs and guns carry a copy of the
    /// game's own collider:
    ///  - gun  = the solid BoxCollider on GunX/tricks (Player layer), placed relative to tricks/mesh
    ///           as on the local gun, on a kinematic Rigidbody at the gun copy's root. A gun in hand
    ///           is kinematic too and kinematic bodies never touch, so, as with the local guns, only a
    ///           thrown gun hits anything
    ///  - disc = the BoxCollider on Static/GroundCollider (a sibling of Static/Ground), static
    /// Head and body have no game collider to copy (the local ShootableHead is on the shootable-toy
    /// layer, so shots would hit it). AddMeshColliders builds them from whatever is drawn, which
    /// works for any shape (custom parts later): every enabled renderer that is switched on in the
    /// template gets a "Collider" child on the Player layer, a convex MeshCollider of its mesh when
    /// the mesh is readable, otherwise a BoxCollider of that mesh's own bounds. Disabled renderers
    /// (3rdPersonHead's own hidden Cylinder) are skipped. The part's root gets a kinematic Rigidbody
    /// like the guns, so all its colliders move as one. Hands get none.
    /// Each client only simulates its own guns, so a bounce seen here is what this player sees, and
    /// the others see it through this player's poses.
    ///
    /// From the UnityExplorer console: ExScoringMod.MpPresence.DebugDump();
    /// </summary>
    public static class MpPresence
    {
        private const long SendIntervalMs = 67;          // ~15 Hz; the server accepts up to 20/s
        private const long RenderDelayMs = 100;          // contract: buffer ~100 ms behind serverTime
        private const long SampleKeepMs = 1000;
        private const float GunColorPollSeconds = 0.5f;
        private const float RigLookupRetrySeconds = 2f;
        private const float TemplateRetrySeconds = 2f;
        private const float SpotSpacing = 2.5f;          // the platform is 2.26 m wide
        private const float DiscCheckSeconds = 2f;
        private const float TorsoFollowRate = 10f;       // TorsoTrack: deltaTime * 10
        private const int AvatarLayer = 0;               // Default: headset + spectator camera
        private const string DefaultGunModelName = "Gun1";
        private const string GunPosePath = "tricks/mesh";
        private const string DiscPath = "Static/Ground";
        private const string DiscColliderPath = "Static/GroundCollider";
        private const string GunColliderPath = "tricks";
        private const int PartColliderLayer = 11;        // Player: what the local guns are on

        // Grid spot of each seat (index = seat, spot number = seat + 1), in units of SpotSpacing:
        // x = right, y = forward.
        private static readonly Vector2[] Spots =
        {
            new Vector2(-1f, 1f),   // 1: front left
            new Vector2(0f, 1f),    // 2: front centre
            new Vector2(1f, 1f),    // 3: front right
            new Vector2(0f, 0f),    // 4: centre
            new Vector2(-1f, 0f),   // 5: middle left
            new Vector2(1f, 0f),    // 6: middle right
            new Vector2(-0.5f, -1f),// 7: back, between 5 and 4
            new Vector2(0.5f, -1f)  // 8: back, between 4 and 6
        };

        // Fallback when a player hasn't sent colours yet.
        private static readonly Color DefaultLeftColor = new Color(0f, 0.665f, 1f, 1f);
        private static readonly Color DefaultRightColor = new Color(1f, 0.467f, 0f, 1f);

        // Material names the game tints with the gun colour (MpColorDump, 2026-10-23). Copies get
        // " (Instance)" appended, hence StartsWith.
        private static readonly string[] TintedMaterialPrefixes = { "GunGlowRight", "GunGlowLeft", "GunRight", "GunLeft" };

        // An object carrying any of these is removed from a template entirely (gameplay visuals
        // that would show stale state once their script is gone).
        private static readonly HashSet<string> RemoveObjectTypes = new HashSet<string>
        {
            "TextMeshPro", "TextMeshProUGUI", "MultiplierDisplay", "Muzzle", "FormIndicator",
            "SpriteRenderer", "LineRenderer", "TrailRenderer", "ParticleSystem", "ParticleSystemRenderer",
            "Canvas", "Camera", "AudioSource"
        };

        // The only components a template keeps.
        private static readonly HashSet<string> KeepComponentTypes = new HashSet<string>
        {
            "Transform", "RectTransform", "MeshFilter", "MeshRenderer", "SkinnedMeshRenderer"
        };

        private const int PartHead = 0, PartLHand = 1, PartRHand = 2, PartLGun = 3, PartRGun = 4, PartCount = 5;

        private class Sample
        {
            public long T;
            public readonly Vector3[] Pos = new Vector3[PartCount];
            public readonly Quaternion[] Rot = new Quaternion[PartCount];
            public readonly bool[] Has = new bool[PartCount];
            public bool LTrigger;
            public bool RTrigger;
        }

        private class Remote
        {
            public string UserId;
            public readonly List<Sample> Samples = new List<Sample>();
            /// <summary>In the latest poses message.</summary>
            public bool Present;

            // Built lazily, once templates exist.
            public GameObject Root;
            public Transform Head, Body, LHand, RHand, LGun, RGun;
            /// <summary>Platform copy, or null when the environment has none.</summary>
            public Transform Disc;
            public readonly List<Material> TintLeft = new List<Material>();
            public readonly List<Material> TintRight = new List<Material>();
            public readonly List<Material> OwnedMaterials = new List<Material>();
            public string AppliedColorKey;

            // TorsoTrack state, in the sender's space.
            public bool BodyInit;
            public Vector3 BodyPos;
            public Quaternion BodyRot = Quaternion.identity;
        }

        private static readonly Dictionary<string, Remote> remotes = new Dictionary<string, Remote>();

        private static bool subscribed;
        private static bool wasInSong;
        private static long lastPoseSentMs;
        private static float nextGunColorPoll;

        // Local rig (for sending, and as the template source).
        private static Transform localHead;
        private static KataHand localKataL, localKataR;
        private static Gun localGunL, localGunR;
        private static Transform localGunPoseL, localGunPoseR; // tricks/mesh, kept while thrown
        private static float nextRigLookup;
        private static bool loggedRigMissing;

        // Templates, under an inactive holder that survives scene loads.
        private static GameObject templateHolder;
        private static GameObject tplHead, tplBody, tplHandL, tplHandR, tplGunL, tplGunR;
        private static bool templatesReady;
        private static bool gunCollidersL, gunCollidersR; // the gun templates got their colliders
        private static int headColliders, bodyColliders;  // colliders AddMeshColliders gave head and body
        private static float nextTemplateTry;

        // Disc template, under its own inactive holder so it can be rebuilt on its own when the
        // environment changes. discSource is the scene object it was copied from.
        private static GameObject discHolder;
        private static GameObject tplDisc;
        private static IntPtr discSource = IntPtr.Zero;
        private static bool discCollider; // the disc template got a collider
        private static float nextDiscCheck;

        private static GameObject avatarHolder;

        // Interpolation output, reused every frame.
        private static readonly Vector3[] curPos = new Vector3[PartCount];
        private static readonly Quaternion[] curRot = new Quaternion[PartCount];
        private static readonly bool[] curHas = new bool[PartCount];

        // ── Per frame ──

        /// <summary>Once per frame from ExScoring.OnUpdate, after MpSession.Tick.</summary>
        public static void Tick()
        {
            if (!subscribed)
            {
                subscribed = true;
                MpSession.OnPoses += HandlePoses;
            }

            if (!MpSession.InLobby)
            {
                if (remotes.Count > 0) DestroyAll("not in a lobby");
                wasInSong = false;
                return;
            }

            if (InSong())
            {
                if (!wasInSong)
                {
                    wasInSong = true;
                    HideAndClearAll();
                }
                return;
            }
            wasInSong = false;

            if (!MpSession.PresenceEnabled)
            {
                // Reconnected to a server without the feature, or still handshaking.
                if (remotes.Count > 0) DestroyAll("presence off on this connection");
                return;
            }

            RemoveRemotesNotInLobby();
            CheckDisc();
            SendPoseIfDue();
            // Not on the end screen either: another mod's temporary in-song colours must never go out.
            if (!InSongScene()) PollGunColors();
            Render();
        }

        /// <summary>In a song, except on a match's end screen (presence resumes there).</summary>
        private static bool InSong()
        {
            return InSongScene() && !MpGameplay.OnResultsScreen;
        }

        /// <summary>Anywhere from launching a song until leaving it, end screen included.</summary>
        private static bool InSongScene()
        {
            MenuState.State state = ExScoring.menuState;
            return state == MenuState.State.Launching || state == MenuState.State.Launched;
        }

        // ── Sending ──

        private static void SendPoseIfDue()
        {
            long now = MpSession.LocalNowMs;
            if (now - lastPoseSentMs < SendIntervalMs) return;
            if (!MpSession.IsConnected) return;
            if (!FindLocalRig()) return;

            lastPoseSentMs = now;

            JObject pose = new JObject
            {
                ["t"] = MpSession.ServerNowMs,
                ["head"] = PartJson(localHead),
                ["lHand"] = PartJson(localKataL != null ? localKataL.transform : null),
                ["rHand"] = PartJson(localKataR != null ? localKataR.transform : null),
                ["lGun"] = PartJson(GunPoseTransform(localGunL, ref localGunPoseL)),
                ["rGun"] = PartJson(GunPoseTransform(localGunR, ref localGunPoseR)),
                ["lTrigger"] = TriggerDown(localGunL),
                ["rTrigger"] = TriggerDown(localGunR)
            };
            MpSession.SendPose(pose);
        }

        private static JToken PartJson(Transform t)
        {
            if (t == null || !t.gameObject.activeInHierarchy) return JValue.CreateNull();
            Vector3 p = t.position;
            Quaternion q = t.rotation;
            return new JArray(
                Math.Round((double)p.x, 3), Math.Round((double)p.y, 3), Math.Round((double)p.z, 3),
                Math.Round((double)q.x, 4), Math.Round((double)q.y, 4), Math.Round((double)q.z, 4), Math.Round((double)q.w, 4));
        }

        /// <summary>
        /// The transform a gun's pose is read from: tricks/mesh, which follows gun tricks and sits on
        /// the Gun's origin at rest. Looked up under the Gun on every call; while it isn't there (the
        /// gun is thrown, "tricks" is out from under the Gun) the last one found is used wherever it
        /// is. Falls back to the Gun itself if none was ever found or it was destroyed.
        /// </summary>
        private static Transform GunPoseTransform(Gun gun, ref Transform cached)
        {
            if (gun == null) return null;
            Transform found = gun.transform.Find(GunPosePath);
            if (found != null) cached = found;
            else if (!IsAlive(cached)) cached = null;
            return cached != null ? cached : gun.transform;
        }

        private static bool IsAlive(Transform t)
        {
            if (t == null) return false;
            try
            {
                return !t.WasCollected && t.gameObject != null;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>For the debug dump: where a gun's pose transform currently is.</summary>
        private static string GunPoseState(Gun gun, Transform cached)
        {
            if (gun == null) return "no gun";
            if (!IsAlive(cached)) return "root";
            return gun.transform.Find(GunPosePath) != null ? "mesh" : "thrown";
        }

        private static bool TriggerDown(Gun gun)
        {
            if (gun == null) return false;
            try
            {
                return gun.GetTriggerPullAmount() >= gun.GetTriggerThreshold();
            }
            catch
            {
                return false;
            }
        }

        private static void PollGunColors()
        {
            if (Time.unscaledTime < nextGunColorPoll) return;
            nextGunColorPoll = Time.unscaledTime + GunColorPollSeconds;

            PlayerPreferences prefs = PlayerPreferences.I;
            if (prefs == null || prefs.GunColorLeft == null || prefs.GunColorRight == null) return;

            Color left = prefs.GunColorLeft.mVal;
            Color right = prefs.GunColorRight.mVal;
            MpSession.SetGunColors(new[] { left.r, left.g, left.b }, new[] { right.r, right.g, right.b });
        }

        /// <summary>Finds (and caches) the local head, hands and guns. Retries every 2 s while incomplete.</summary>
        private static bool FindLocalRig()
        {
            if (localHead != null && localKataL != null && localKataR != null && localGunL != null && localGunR != null) return true;
            if (Time.unscaledTime < nextRigLookup) return localHead != null;
            nextRigLookup = Time.unscaledTime + RigLookupRetrySeconds;

            try
            {
                KataCameraRig rig = KataCameraRig.I;
                if (rig != null && rig.cameraRig != null) localHead = rig.cameraRig.centerEyeAnchor;

                var hands = UnityEngine.Object.FindObjectsOfType<KataHand>();
                for (int i = 0; i < hands.Length; i++)
                {
                    KataHand hand = hands[i];
                    if (hand == null) continue;
                    if (hand.name == "HandLeft") localKataL = hand;
                    else if (hand.name == "HandRight") localKataR = hand;
                }

                var guns = UnityEngine.Object.FindObjectsOfType<Gun>();
                for (int i = 0; i < guns.Length; i++)
                {
                    Gun gun = guns[i];
                    if (gun == null) continue;
                    Transform parent = gun.transform.parent;
                    bool underLeft = parent != null && localKataL != null && parent.Pointer == localKataL.transform.Pointer;
                    bool underRight = parent != null && localKataR != null && parent.Pointer == localKataR.transform.Pointer;
                    if (underLeft || (!underRight && gun.name == "GunLeft")) localGunL = gun;
                    else if (underRight || gun.name == "GunRight") localGunR = gun;
                }
            }
            catch (Exception ex)
            {
                Log($"local rig lookup failed: {ex.Message}");
            }

            bool complete = localHead != null && localKataL != null && localKataR != null && localGunL != null && localGunR != null;
            if (!complete && !loggedRigMissing)
            {
                loggedRigMissing = true;
                Log($"local rig incomplete: head={localHead != null} handL={localKataL != null} handR={localKataR != null} gunL={localGunL != null} gunR={localGunR != null}");
            }
            if (complete) loggedRigMissing = false;
            return localHead != null;
        }

        // ── Receiving ──

        private static void HandlePoses(MpPoses poses)
        {
            if (poses == null || !MpSession.InLobby || InSong()) return;

            HashSet<string> present = new HashSet<string>();
            if (poses.players != null)
            {
                foreach (MpPoseEntry entry in poses.players)
                {
                    if (entry == null || entry.userId == null || entry.userId == MpSession.MyUserId) continue;
                    present.Add(entry.userId);

                    if (!remotes.TryGetValue(entry.userId, out Remote remote))
                    {
                        remote = new Remote { UserId = entry.userId };
                        remotes[entry.userId] = remote;
                    }
                    remote.Present = true;
                    AddSample(remote, entry);
                }
            }

            foreach (Remote remote in remotes.Values)
            {
                if (present.Contains(remote.UserId)) continue;
                if (remote.Present) Log($"{remote.UserId} left the poses (song, disconnected or stale), hiding");
                remote.Present = false;
                remote.Samples.Clear();
                remote.BodyInit = false;
            }
        }

        private static void AddSample(Remote remote, MpPoseEntry entry)
        {
            List<Sample> samples = remote.Samples;
            if (samples.Count > 0 && entry.t <= samples[samples.Count - 1].T) return; // out of order or repeated

            Sample sample = new Sample { T = entry.t, LTrigger = entry.lTrigger, RTrigger = entry.rTrigger };
            ReadPart(entry.head, sample, PartHead);
            ReadPart(entry.lHand, sample, PartLHand);
            ReadPart(entry.rHand, sample, PartRHand);
            ReadPart(entry.lGun, sample, PartLGun);
            ReadPart(entry.rGun, sample, PartRGun);
            samples.Add(sample);

            // Keep about a second, never fewer than two.
            long cutoff = entry.t - SampleKeepMs;
            while (samples.Count > 2 && samples[0].T < cutoff) samples.RemoveAt(0);
        }

        private static void ReadPart(float[] a, Sample sample, int part)
        {
            sample.Has[part] = false;
            if (a == null || a.Length != 7) return;
            for (int i = 0; i < 7; i++)
            {
                if (float.IsNaN(a[i]) || float.IsInfinity(a[i])) return;
            }

            Quaternion q = new Quaternion(a[3], a[4], a[5], a[6]);
            float mag = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            if (mag < 0.0001f) return;

            sample.Pos[part] = new Vector3(a[0], a[1], a[2]);
            sample.Rot[part] = new Quaternion(q.x / mag, q.y / mag, q.z / mag, q.w / mag);
            sample.Has[part] = true;
        }

        private static void RemoveRemotesNotInLobby()
        {
            if (remotes.Count == 0) return;
            MpLobbyState lobby = MpSession.Lobby;

            List<string> gone = null;
            foreach (Remote remote in remotes.Values)
            {
                if (lobby != null && lobby.FindPlayer(remote.UserId) != null) continue;
                if (gone == null) gone = new List<string>();
                gone.Add(remote.UserId);
            }
            if (gone == null) return;

            foreach (string userId in gone)
            {
                DestroyAvatar(remotes[userId]);
                remotes.Remove(userId);
                Log($"{userId} left the lobby, avatar removed");
            }
        }

        // ── Drawing ──

        private static void Render()
        {
            if (remotes.Count == 0) return;

            if (!Config.ShowOtherPlayers)
            {
                foreach (Remote remote in remotes.Values) SetVisible(remote, false);
                return;
            }

            long renderTime = MpSession.ServerNowMs - RenderDelayMs;
            int mySeat = MpSession.MySeat;
            MpLobbyState lobby = MpSession.Lobby;
            float dt = Time.deltaTime;

            foreach (Remote remote in remotes.Values)
            {
                if (!remote.Present || remote.Samples.Count == 0)
                {
                    SetVisible(remote, false);
                    continue;
                }

                if (remote.Root == null && !BuildAvatar(remote)) continue;

                MpPlayerState player = lobby != null ? lobby.FindPlayer(remote.UserId) : null;
                int seat = player != null ? player.seat : 0;
                remote.Root.transform.position = LayoutOffset(seat, mySeat);
                remote.Root.transform.rotation = Quaternion.identity;

                ApplyColors(remote, player);

                Interpolate(remote.Samples, renderTime);
                UpdateBody(remote, dt);

                SetPart(remote.Head, PartHead);
                SetPart(remote.LHand, PartLHand);
                SetPart(remote.RHand, PartRHand);
                SetPart(remote.LGun, PartLGun);
                SetPart(remote.RGun, PartRGun);

                if (remote.Body != null)
                {
                    bool showBody = curHas[PartHead] && remote.BodyInit;
                    if (remote.Body.gameObject.activeSelf != showBody) remote.Body.gameObject.SetActive(showBody);
                    if (showBody)
                    {
                        remote.Body.localPosition = remote.BodyPos;
                        remote.Body.localRotation = remote.BodyRot;
                    }
                }

                SetVisible(remote, true);
            }
        }

        private static void SetPart(Transform part, int index)
        {
            if (part == null) return;
            bool has = curHas[index];
            if (part.gameObject.activeSelf != has) part.gameObject.SetActive(has);
            if (!has) return;
            part.localPosition = curPos[index];
            part.localRotation = curRot[index];
        }

        /// <summary>Fills curPos/curRot/curHas for renderTime from the samples around it.</summary>
        private static void Interpolate(List<Sample> samples, long renderTime)
        {
            Sample a = null, b = null;
            for (int i = 0; i < samples.Count; i++)
            {
                if (samples[i].T <= renderTime)
                {
                    a = samples[i];
                }
                else
                {
                    b = samples[i];
                    break;
                }
            }

            if (a == null) a = b;   // render time before the first sample: show the first
            if (b == null) b = a;   // after the last: hold the last

            float frac = 0f;
            if (a != b && b.T > a.T) frac = Mathf.Clamp01((float)(renderTime - a.T) / (b.T - a.T));

            for (int p = 0; p < PartCount; p++)
            {
                if (a.Has[p] && b.Has[p])
                {
                    curPos[p] = Vector3.Lerp(a.Pos[p], b.Pos[p], frac);
                    curRot[p] = Quaternion.Slerp(a.Rot[p], b.Rot[p], frac);
                    curHas[p] = true;
                }
                else
                {
                    Sample nearest = frac < 0.5f ? a : b;
                    curHas[p] = nearest.Has[p];
                    if (curHas[p])
                    {
                        curPos[p] = nearest.Pos[p];
                        curRot[p] = nearest.Rot[p];
                    }
                }
            }
        }

        /// <summary>TorsoTrack.Update on the interpolated remote head and hands.</summary>
        private static void UpdateBody(Remote remote, float dt)
        {
            if (!curHas[PartHead])
            {
                remote.BodyInit = false;
                return;
            }

            Vector3 head = curPos[PartHead];
            Vector3 dir = curRot[PartHead] * Vector3.forward;
            if (curHas[PartLHand]) dir += curPos[PartLHand] - head;
            if (curHas[PartRHand]) dir += curPos[PartRHand] - head;
            dir.y = 0f;

            Quaternion target = dir.sqrMagnitude > 0.000001f ? Quaternion.LookRotation(dir.normalized) : remote.BodyRot;

            if (!remote.BodyInit)
            {
                remote.BodyInit = true;
                remote.BodyPos = head;
                remote.BodyRot = target;
                return;
            }

            float t = Mathf.Clamp01(dt * TorsoFollowRate);
            remote.BodyPos = Vector3.Lerp(remote.BodyPos, head, t);
            remote.BodyRot = Quaternion.Slerp(remote.BodyRot, target, t);
        }

        private static void ApplyColors(Remote remote, MpPlayerState player)
        {
            Color left = DefaultLeftColor;
            Color right = DefaultRightColor;
            if (player != null && player.gunColors != null)
            {
                left = ToColor(player.gunColors.left, DefaultLeftColor);
                right = ToColor(player.gunColors.right, DefaultRightColor);
            }

            string key = $"{left.r:F3},{left.g:F3},{left.b:F3}|{right.r:F3},{right.g:F3},{right.b:F3}";
            if (key == remote.AppliedColorKey) return;
            remote.AppliedColorKey = key;

            foreach (Material mat in remote.TintLeft) if (mat != null) mat.SetColor("_Color", left);
            foreach (Material mat in remote.TintRight) if (mat != null) mat.SetColor("_Color", right);
        }

        private static Color ToColor(float[] c, Color fallback)
        {
            if (c == null || c.Length < 3) return fallback;
            return new Color(Mathf.Clamp01(c[0]), Mathf.Clamp01(c[1]), Mathf.Clamp01(c[2]), 1f);
        }

        private static void SetVisible(Remote remote, bool visible)
        {
            if (remote.Root != null && remote.Root.activeSelf != visible) remote.Root.SetActive(visible);
        }

        // ── Avatars ──

        private static bool BuildAvatar(Remote remote)
        {
            if (!EnsureTemplates()) return false;

            try
            {
                if (avatarHolder == null)
                {
                    avatarHolder = new GameObject("MpPresenceAvatars");
                    UnityEngine.Object.DontDestroyOnLoad(avatarHolder);
                }

                GameObject root = new GameObject("MpAvatar " + remote.UserId);
                root.transform.SetParent(avatarHolder.transform, false);
                remote.Root = root;

                remote.Head = CopyPart(tplHead, root, "Head");
                remote.Body = CopyPart(tplBody, root, "Body");
                remote.LHand = CopyPart(tplHandL, root, "HandL");
                remote.RHand = CopyPart(tplHandR, root, "HandR");
                remote.LGun = CopyPart(tplGunL, root, "GunL");
                remote.RGun = CopyPart(tplGunR, root, "GunR");
                remote.Disc = CopyDisc(root);

                CollectGunMaterials(remote.LGun, remote.TintLeft, remote.OwnedMaterials);
                CollectGunMaterials(remote.RGun, remote.TintRight, remote.OwnedMaterials);
                remote.AppliedColorKey = null;

                Log($"avatar built for {remote.UserId} ({remote.TintLeft.Count}+{remote.TintRight.Count} tinted materials)");
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Log($"[MpPresence] building the avatar for {remote.UserId} failed: {ex}");
                DestroyAvatar(remote);
                return false;
            }
        }

        private static Transform CopyPart(GameObject template, GameObject root, string name)
        {
            GameObject part = GameObject.Instantiate(template, root.transform);
            part.name = name;
            part.SetActive(false); // shown once it has a pose
            return part.transform;
        }

        /// <summary>Gives every gun renderer its own materials and keeps the tinted ones.</summary>
        private static void CollectGunMaterials(Transform gun, List<Material> tinted, List<Material> owned)
        {
            if (gun == null) return;
            var renderers = gun.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null) continue;
                var mats = renderer.materials; // instantiates this renderer's own copies
                for (int j = 0; j < mats.Length; j++)
                {
                    Material mat = mats[j];
                    if (mat == null) continue;
                    owned.Add(mat);
                    if (IsTintedMaterial(mat.name)) tinted.Add(mat);
                }
            }
        }

        private static bool IsTintedMaterial(string name)
        {
            if (name == null) return false;
            foreach (string prefix in TintedMaterialPrefixes)
            {
                if (name.StartsWith(prefix, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static void DestroyAvatar(Remote remote)
        {
            foreach (Material mat in remote.OwnedMaterials)
            {
                if (mat != null) UnityEngine.Object.Destroy(mat);
            }
            remote.OwnedMaterials.Clear();
            remote.TintLeft.Clear();
            remote.TintRight.Clear();
            remote.AppliedColorKey = null;

            if (remote.Root != null) UnityEngine.Object.Destroy(remote.Root);
            remote.Root = null;
            remote.Head = remote.Body = remote.LHand = remote.RHand = remote.LGun = remote.RGun = null;
            remote.Disc = null;
            remote.BodyInit = false;
        }

        private static void DestroyAll(string reason)
        {
            foreach (Remote remote in remotes.Values) DestroyAvatar(remote);
            remotes.Clear();
            Log($"all avatars removed ({reason})");
        }

        private static void HideAndClearAll()
        {
            foreach (Remote remote in remotes.Values)
            {
                SetVisible(remote, false);
                remote.Present = false;
                remote.Samples.Clear();
                remote.BodyInit = false;
            }
            if (remotes.Count > 0) Log("song started, avatars hidden");
        }

        // ── Layout ──

        private static Vector2 SpotOf(int seat)
        {
            return seat >= 0 && seat < Spots.Length ? Spots[seat] : Vector2.zero;
        }

        /// <summary>Spot number shown in the docs (seat + 1), or 0 for no seat.</summary>
        private static int SpotNumber(int seat)
        {
            return seat >= 0 && seat < Spots.Length ? seat + 1 : 0;
        }

        /// <summary>
        /// Where a remote player's whole avatar (and disc) is shifted to: their spot minus ours, so
        /// our own spot lands where we stand and the grid looks the same from every seat.
        /// </summary>
        private static Vector3 LayoutOffset(int seat, int mySeat)
        {
            Vector2 theirs = SpotOf(seat);
            Vector2 mine = SpotOf(mySeat);
            return new Vector3((theirs.x - mine.x) * SpotSpacing, 0f, (theirs.y - mine.y) * SpotSpacing);
        }

        // ── Discs ──

        /// <summary>
        /// Every DiscCheckSeconds (outside songs): finds the environment's platform and, when it is a
        /// different object from last time (or gone), rebuilds the disc template and every copy. A
        /// failed build isn't retried until the platform object changes.
        /// </summary>
        private static void CheckDisc()
        {
            if (Time.unscaledTime < nextDiscCheck) return;
            nextDiscCheck = Time.unscaledTime + DiscCheckSeconds;

            GameObject source = null;
            try
            {
                source = GameObject.Find(DiscPath);
            }
            catch (Exception ex)
            {
                Log($"disc lookup failed: {ex.Message}");
            }

            IntPtr pointer = source != null ? source.Pointer : IntPtr.Zero;
            if (pointer == discSource) return;
            discSource = pointer;

            DestroyDiscTemplate();
            if (source != null)
            {
                try
                {
                    BuildDiscTemplate(source);
                    Log($"disc template built from {DiscPath}");
                }
                catch (Exception ex)
                {
                    MelonLogger.Log($"[MpPresence] building the disc template failed: {ex}");
                    DestroyDiscTemplate();
                }
            }
            else
            {
                Log($"no {DiscPath} in this environment, players get no disc");
            }

            foreach (Remote remote in remotes.Values)
            {
                if (remote.Root == null) continue;
                if (remote.Disc != null) UnityEngine.Object.Destroy(remote.Disc.gameObject);
                remote.Disc = CopyDisc(remote.Root);
            }
        }

        /// <summary>
        /// A stripped copy of the platform holding its exact world pose (template root at the
        /// origin), so a copy under an avatar root lands at the local platform's place plus the
        /// grid offset. Materials stay shared with the scene so the reactive shaders keep working.
        /// </summary>
        private static void BuildDiscTemplate(GameObject source)
        {
            discHolder = new GameObject("MpPresenceDiscTemplate");
            discHolder.SetActive(false); // nothing copied under it ever runs Awake
            UnityEngine.Object.DontDestroyOnLoad(discHolder);

            tplDisc = new GameObject("TplDisc");
            tplDisc.transform.SetParent(discHolder.transform, false);

            Transform src = source.transform;
            GameObject copy = GameObject.Instantiate(source, tplDisc.transform);
            copy.name = source.name;
            copy.SetActive(true);
            copy.transform.localPosition = src.position;
            copy.transform.localRotation = src.rotation;
            copy.transform.localScale = src.lossyScale;

            Strip(copy);

            // The platform's collider is a sibling (Static/GroundCollider), copied at its world pose
            // like the mesh. No Rigidbody: the disc only moves when the seats change.
            discCollider = false;
            GameObject colliderSource = null;
            try
            {
                colliderSource = GameObject.Find(DiscColliderPath);
            }
            catch (Exception ex)
            {
                Log($"disc collider lookup failed: {ex.Message}");
            }
            BoxCollider box = colliderSource != null ? colliderSource.GetComponent<BoxCollider>() : null;
            discCollider = AddColliderCopy(tplDisc, null, box, "Collider");
            if (!discCollider) Log($"no {DiscColliderPath} BoxCollider, discs get no collider");
        }

        private static Transform CopyDisc(GameObject root)
        {
            if (tplDisc == null || root == null) return null;
            GameObject disc = GameObject.Instantiate(tplDisc, root.transform);
            disc.name = "Disc";
            return disc.transform;
        }

        private static void DestroyDiscTemplate()
        {
            if (discHolder != null) UnityEngine.Object.Destroy(discHolder);
            discHolder = null;
            tplDisc = null;
            discCollider = false;
        }

        // ── Templates ──

        private static bool EnsureTemplates()
        {
            if (templatesReady) return true;
            if (Time.unscaledTime < nextTemplateTry) return false;
            nextTemplateTry = Time.unscaledTime + TemplateRetrySeconds;

            try
            {
                return BuildTemplates();
            }
            catch (Exception ex)
            {
                MelonLogger.Log($"[MpPresence] building the avatar templates failed: {ex}");
                DestroyTemplates();
                return false;
            }
        }

        private static bool BuildTemplates()
        {
            FindLocalRig();
            if (localHead == null || localKataL == null || localKataR == null || localGunL == null || localGunR == null)
            {
                Log("templates: local rig not complete yet");
                return false;
            }

            AvatarSelector selector = AvatarSelector.I;
            if (selector == null || selector.avatars == null || selector.avatars.Count == 0)
            {
                Log("templates: AvatarSelector not ready yet");
                return false;
            }

            GameObject defaultMask = selector.avatars[0].head;
            GameObject defaultTorso = selector.avatars[0].body;
            if (defaultMask == null || defaultTorso == null || defaultMask.transform.parent == null || defaultTorso.transform.parent == null)
            {
                Log("templates: default head/body not found");
                return false;
            }

            GameObject handL = localKataL.normalHand;
            GameObject handR = localKataR.normalHand;
            Transform gunModelL = FindGunModel(localGunL.transform);
            Transform gunModelR = FindGunModel(localGunR.transform);
            if (handL == null || handR == null || gunModelL == null || gunModelR == null)
            {
                Log($"templates: hands or gun models missing (handL={handL != null} handR={handR != null} gunL={gunModelL != null} gunR={gunModelR != null})");
                return false;
            }

            DestroyTemplates();
            templateHolder = new GameObject("MpPresenceTemplates");
            templateHolder.SetActive(false); // nothing copied under it ever runs Awake
            UnityEngine.Object.DontDestroyOnLoad(templateHolder);

            // Head: 3rdPersonHead with only the default mask (customhead and the other masks go).
            Transform headSource = defaultMask.transform.parent;
            tplHead = MakeTemplate("TplHead", localHead, headSource);
            Transform headCopy = tplHead.transform.GetChild(0);
            for (int i = headCopy.childCount - 1; i >= 0; i--)
            {
                Transform child = headCopy.GetChild(i);
                if (child.name == defaultMask.name) child.gameObject.SetActive(true);
                else GameObject.DestroyImmediate(child.gameObject);
            }
            SetLayerRecursive(tplHead, AvatarLayer);

            // Body: the default torso under its body point (3rdPersonBody).
            tplBody = MakeTemplate("TplBody", defaultTorso.transform.parent, defaultTorso.transform);
            SetLayerRecursive(tplBody, AvatarLayer);

            // Head and body collisions, from the meshes actually drawn (added after the layer pass, so
            // they stay on the Player layer). A missing one only costs the collisions.
            headColliders = AddMeshColliders(tplHead);
            bodyColliders = AddMeshColliders(tplBody);
            if (headColliders == 0 || bodyColliders == 0)
                Log($"head/body colliders missing (head={headColliders} body={bodyColliders}), those parts won't collide");

            // Hands: normalHand under HandLeft/HandRight. Animator stripped, so they stay in the pose
            // they had when copied.
            tplHandL = MakeTemplate("TplHandL", localKataL.transform, handL.transform);
            tplHandR = MakeTemplate("TplHandR", localKataR.transform, handR.transform);

            // Guns: the default model, anchored on the same transform the pose is sent from
            // (tricks/mesh), so its offset doesn't include a trick that happens to be playing now.
            // Keeps its 0.021 scale; template-owned material copies so nothing points at the local
            // gun's instances.
            // (FindGunModel finds nothing mid-throw, so a build then fails above and is retried.)
            tplGunL = MakeTemplate("TplGunL", GunPoseTransform(localGunL, ref localGunPoseL), gunModelL);
            tplGunR = MakeTemplate("TplGunR", GunPoseTransform(localGunR, ref localGunPoseR), gunModelR);
            OwnMaterials(tplGunL);
            OwnMaterials(tplGunR);

            // Gun collisions: the solid collider on tricks, and a kinematic Rigidbody so the copy can
            // be moved every frame. A missing collider only costs the collisions, not the avatar.
            gunCollidersL = AddGunCollider(tplGunL, localGunL, localGunPoseL);
            gunCollidersR = AddGunCollider(tplGunR, localGunR, localGunPoseR);
            if (!gunCollidersL || !gunCollidersR)
                Log($"gun colliders missing (L={gunCollidersL} R={gunCollidersR}), those guns won't collide");

            templatesReady = true;
            Log("templates built (default mask, torso, hands, Gun1)");
            return true;
        }

        /// <summary>
        /// A template root standing where the anchor is (scale = the anchor's world scale), holding a
        /// stripped copy of source placed relative to the anchor exactly as it is in the game.
        /// </summary>
        private static GameObject MakeTemplate(string name, Transform anchor, Transform source)
        {
            GameObject root = new GameObject(name);
            root.transform.SetParent(templateHolder.transform, false);
            root.transform.localScale = anchor.lossyScale;

            GameObject copy = GameObject.Instantiate(source.gameObject, root.transform);
            copy.name = source.name;
            copy.SetActive(true); // the source may be inactive (another model selected locally)
            copy.transform.localPosition = anchor.InverseTransformPoint(source.position);
            copy.transform.localRotation = Quaternion.Inverse(anchor.rotation) * source.rotation;
            copy.transform.localScale = Divide(source.lossyScale, anchor.lossyScale);

            Strip(copy);
            return root;
        }

        /// <summary>Copies the gun's solid tricks collider into a gun template and makes its root kinematic.</summary>
        private static bool AddGunCollider(GameObject template, Gun gun, Transform anchor)
        {
            if (template == null || gun == null || anchor == null) return false;

            Transform tricks = gun.transform.Find(GunColliderPath);
            BoxCollider box = tricks != null ? tricks.GetComponent<BoxCollider>() : null;
            if (box == null || box.isTrigger) return false;
            if (!AddColliderCopy(template, anchor, box, "Collider")) return false;

            Rigidbody rb = template.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
            return true;
        }

        /// <summary>
        /// Gives a template colliders matching what it draws, whatever its shape: for every renderer
        /// that is enabled and switched on within the template, a "Collider" child (PartColliderLayer)
        /// holding a convex MeshCollider of its mesh when the mesh is readable, otherwise a BoxCollider
        /// of that mesh's own bounds (always available). The template root gets a kinematic Rigidbody
        /// so the copies can be moved every frame. Returns how many colliders were added.
        /// </summary>
        private static int AddMeshColliders(GameObject template)
        {
            if (template == null) return 0;
            Transform root = template.transform;
            int added = 0;

            var renderers = template.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || !renderer.enabled || !ActiveUnder(renderer.transform, root)) continue;

                Mesh mesh = RendererMesh(renderer);
                if (mesh == null) continue;

                GameObject go = new GameObject("Collider");
                go.transform.SetParent(renderer.transform, false);
                go.layer = PartColliderLayer;

                string kind = "box";
                if (IsReadable(mesh))
                {
                    try
                    {
                        MeshCollider meshCollider = go.AddComponent<MeshCollider>();
                        meshCollider.convex = true;
                        meshCollider.sharedMesh = mesh;
                        kind = "shape";
                    }
                    catch (Exception ex)
                    {
                        Log($"{template.name}: convex collider for {mesh.name} failed ({ex.Message}), using its box");
                        MeshCollider failed = go.GetComponent<MeshCollider>();
                        if (failed != null) UnityEngine.Object.DestroyImmediate(failed);
                    }
                }
                if (kind == "box")
                {
                    Bounds b = mesh.bounds;
                    BoxCollider box = go.AddComponent<BoxCollider>();
                    box.center = b.center;
                    box.size = b.size;
                }

                Vector3 size = Vector3.Scale(mesh.bounds.size, renderer.transform.lossyScale);
                Log($"{template.name} collider: {renderer.name} ({mesh.name}) {kind}, {size.x:F2} x {size.y:F2} x {size.z:F2} m");
                added++;
            }

            if (added > 0)
            {
                Rigidbody rb = template.AddComponent<Rigidbody>();
                rb.isKinematic = true;
                rb.useGravity = false;
            }
            return added;
        }

        /// <summary>The renderer's mesh: SkinnedMeshRenderer.sharedMesh or its MeshFilter's.</summary>
        private static Mesh RendererMesh(Renderer renderer)
        {
            SkinnedMeshRenderer skinned = renderer.TryCast<SkinnedMeshRenderer>();
            if (skinned != null) return skinned.sharedMesh;
            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            return filter != null ? filter.sharedMesh : null;
        }

        /// <summary>activeSelf on t and every parent up to (and including) root; the holder above is inactive on purpose.</summary>
        private static bool ActiveUnder(Transform t, Transform root)
        {
            while (t != null)
            {
                if (!t.gameObject.activeSelf) return false;
                if (t.Pointer == root.Pointer) return true;
                t = t.parent;
            }
            return true;
        }

        private static bool IsReadable(Mesh mesh)
        {
            try
            {
                return mesh.isReadable;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Adds a solid copy of source (size, center, layer, physics material) under a template root,
        /// placed relative to anchor as source is in the game (the same way MakeTemplate places the
        /// models). With no anchor the root stands for the world origin, as the disc template does.
        /// </summary>
        private static bool AddColliderCopy(GameObject root, Transform anchor, BoxCollider source, string name)
        {
            if (root == null || source == null) return false;

            Transform src = source.transform;
            GameObject go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            go.layer = source.gameObject.layer;
            if (anchor != null)
            {
                go.transform.localPosition = anchor.InverseTransformPoint(src.position);
                go.transform.localRotation = Quaternion.Inverse(anchor.rotation) * src.rotation;
                go.transform.localScale = Divide(src.lossyScale, anchor.lossyScale);
            }
            else
            {
                go.transform.localPosition = src.position;
                go.transform.localRotation = src.rotation;
                go.transform.localScale = src.lossyScale;
            }

            BoxCollider box = go.AddComponent<BoxCollider>();
            box.center = source.center;
            box.size = source.size;
            box.isTrigger = false;
            box.sharedMaterial = source.sharedMaterial;
            return true;
        }

        private static Transform FindGunModel(Transform gun)
        {
            Transform model = gun.Find("tricks/mesh/" + DefaultGunModelName);
            if (model != null) return model;

            var all = gun.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].name == DefaultGunModelName) return all[i];
            }
            return null;
        }

        /// <summary>Removes gameplay-only objects, then every component but the visual ones.</summary>
        private static void Strip(GameObject root)
        {
            var transforms = root.GetComponentsInChildren<Transform>(true);
            List<GameObject> doomed = new List<GameObject>();
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform t = transforms[i];
                if (t == null || t.Pointer == root.transform.Pointer) continue;
                var components = t.GetComponents<Component>();
                for (int j = 0; j < components.Length; j++)
                {
                    Component c = components[j];
                    if (c != null && RemoveObjectTypes.Contains(TypeName(c)))
                    {
                        doomed.Add(t.gameObject);
                        break;
                    }
                }
            }
            foreach (GameObject go in doomed)
            {
                if (go != null) GameObject.DestroyImmediate(go); // a doomed child of a doomed parent is already gone
            }

            // A component another one requires can't go first, so a few passes.
            for (int pass = 0; pass < 3; pass++)
            {
                bool removedAny = false;
                var components = root.GetComponentsInChildren<Component>(true);
                for (int i = 0; i < components.Length; i++)
                {
                    Component c = components[i];
                    if (c == null || KeepComponentTypes.Contains(TypeName(c))) continue;
                    UnityEngine.Object.DestroyImmediate(c);
                    removedAny = true;
                }
                if (!removedAny) break;
            }
        }

        private static void OwnMaterials(GameObject root)
        {
            var renderers = root.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] != null)
                {
                    // The getter swaps in this renderer's own copies; assigning them back makes the
                    // intent explicit (and keeps the compiler from flagging an unused read).
                    renderers[i].materials = renderers[i].materials;
                }
            }
        }

        private static void DestroyTemplates()
        {
            templatesReady = false;
            if (templateHolder != null) UnityEngine.Object.Destroy(templateHolder);
            templateHolder = null;
            tplHead = tplBody = tplHandL = tplHandR = tplGunL = tplGunR = null;
            gunCollidersL = gunCollidersR = false;
            headColliders = bodyColliders = 0;
        }

        private static void SetLayerRecursive(GameObject root, int layer)
        {
            var all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null) all[i].gameObject.layer = layer;
            }
        }

        private static string TypeName(Component c)
        {
            try
            {
                return c.GetIl2CppType().Name;
            }
            catch
            {
                return "";
            }
        }

        private static Vector3 Divide(Vector3 a, Vector3 b)
        {
            return new Vector3(
                Mathf.Abs(b.x) > 0.000001f ? a.x / b.x : 1f,
                Mathf.Abs(b.y) > 0.000001f ? a.y / b.y : 1f,
                Mathf.Abs(b.z) > 0.000001f ? a.z / b.z : 1f);
        }

        // ── Debug ──

        /// <summary>Logs the presence state through MelonLogger.</summary>
        public static void DebugDump()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"[MpPresence] presence={MpSession.PresenceEnabled} inLobby={MpSession.InLobby} inSong={InSong()} results={MpGameplay.OnResultsScreen} show={Config.ShowOtherPlayers} mySeat={MpSession.MySeat} templates={templatesReady}");
            sb.AppendLine($"[MpPresence] rig: head={localHead != null} handL={localKataL != null} handR={localKataR != null} gunL={localGunL != null} gunR={localGunR != null} gunPoseL={GunPoseState(localGunL, localGunPoseL)} gunPoseR={GunPoseState(localGunR, localGunPoseR)}");
            sb.AppendLine($"[MpPresence] disc: template={tplDisc != null} source={(discSource != IntPtr.Zero ? "found" : "none")} collider={discCollider} mySpot={SpotNumber(MpSession.MySeat)}");
            sb.AppendLine($"[MpPresence] colliders: gunL={gunCollidersL} gunR={gunCollidersR} head={headColliders} body={bodyColliders}");
            long renderTime = MpSession.ServerNowMs - RenderDelayMs;
            foreach (Remote remote in remotes.Values)
            {
                MpPlayerState player = MpSession.Lobby?.FindPlayer(remote.UserId);
                long newest = remote.Samples.Count > 0 ? remote.Samples[remote.Samples.Count - 1].T : 0;
                sb.AppendLine($"[MpPresence]   {remote.UserId} \"{player?.nickname}\" seat={player?.seat} present={remote.Present} samples={remote.Samples.Count} " +
                              $"newest={(newest > 0 ? (newest - renderTime) + " ms ahead of render" : "-")} built={remote.Root != null} visible={remote.Root != null && remote.Root.activeSelf} colors={player?.gunColors != null}");
            }
            foreach (string line in sb.ToString().TrimEnd().Split('\n')) MelonLogger.Log(line.TrimEnd('\r'));
        }

        private static void Log(string message)
        {
            DebugLog.Log("[MpPresence] " + message);
        }
    }
}