using System;
using System.Collections.Generic;
using System.Text;
using Harmony;
using MelonLoader;
using Newtonsoft.Json.Linq;
using TMPro;
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
    /// Sending (in a lobby, presence on, not in a song): MpSession.PoseRateHz poses a second (the
    /// server's poseRateHz since v2.40, 15 without it), on a fixed schedule so the average rate
    /// holds even when frames don't line up with it. Every part as its world pose [px,py,pz, qx,qy,qz,qw]:
    ///  - head  = OVRCameraRig.centerEyeAnchor
    ///  - hands = the KataHand objects (HandLeft / HandRight)
    ///  - guns  = GunLeft/GunRight's tricks/mesh, falling back to the Gun object itself. Gun tricks
    ///            spin "tricks" (a pivot under the Gun); "mesh" puts the model back on the Gun's
    ///            origin, so at rest this pose equals the Gun's (what older builds send) and during
    ///            a trick it follows the spin. A throw takes "tricks" out from under the Gun, so the
    ///            last mesh found is kept and sent wherever it is until it is back under the Gun
    ///  - triggers = Gun.GetTriggerPullAmount() >= Gun.GetTriggerThreshold()
    ///  - pulls    = Gun.GetTriggerPullAmount(), clamped 0..1, 2 decimals (v2.40)
    ///  - shots    = running count per hand of registered presses (Gun.UpdateTrigger returning
    ///               true, the same moment the gun fires), reset on every welcome since the
    ///               contract counts "since the connection opened" (v2.40)
    ///  - handAnim = [point, thumbsUp, spin] per hand (v2.42, 19.15): KataHand's own blends on
    ///               Oculus-style controllers; on Knuckles (VRHardwareSetup hardware 4) worked out
    ///               from the curls (1 - index, 1 - thumb, trick eased at HandBlendRate)
    ///  - fingers  = [thumb, index, middle, ring, pinky] SteamVR curls, Knuckles only (v2.42)
    /// plus the gun colours (read from the prefs twice a second, sent by MpSession only on change).
    ///
    /// Shots from others (v2.40, 19.13.4): each hand's count is read as render time passes each
    /// sample. The first sample (or the first after the player was hidden) only sets the baseline;
    /// a count that goes down (they reconnected) resets it; a rise fires that many shots, at most
    /// MaxShotsPerJump. Without counts (a v2.39 sender or server) a false -> true trigger change
    /// is one shot. Trigger pull is interpolated like the parts (bool 0/1 without a pull) and
    /// feeds the hand animation.
    ///
    /// Hands from others (v2.42): each remote hand copy keeps the normal hand's Animator. With
    /// finger curls (a Knuckles sender) the Animator is off and every finger bone is slerped
    /// between that side's open and fist poses (sampled once from the Animator at Flex 0 and 1);
    /// otherwise the Animator gets KataHand.UpdateAnimStates' values from HandAnim and the pull.
    /// Neither: the rest pose. After each remote shot the gun copy's gun_slide replays the game's
    /// gun_fire clip movement (sampled once from the local gun).
    ///
    /// Each shot is shown the way the game shows its own (Gun.Fire, PlayShot): the success beam
    /// from the gun copy's firepoint (a marker placed from the local gun's firepoint), the muzzle
    /// flash and the beam dust. Per remote hand, 5 beams and a muzzle flash are built on the first
    /// shot from the local gun of the same hand (as Gun.SetUp / CreateMuzzleFlash do), living
    /// under KataConfig like the game's own, destroyed with the avatar. The beam carries that
    /// player's gun colour: a copy of the native beam texture repainted with TargetColorSetter's
    /// CreateTexturesBeam + ShiftTextureColors, on copies of the beam quads' materials. The muzzle
    /// flash and dust use the game's per-hand colour slot, so they show this player's own colour
    /// for that hand.
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
    ///  - hands = KataHand.normalHand (*_hand_skeletal_lowres), Animator kept (under HandLeft/Right)
    ///  - guns  = tricks/mesh/Gun1 with its offset and 0.021 scale     (under tricks/mesh)
    /// The copies are made under an inactive holder (so no Awake runs and the game's singletons
    /// stay untouched) and stripped of every component but Transform / MeshFilter / MeshRenderer /
    /// SkinnedMeshRenderer (plus the hands' Animator); muzzle, form indicator and multiplier display objects are removed.
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
    /// Name labels (Config.ShowPlayerNames, only while ShowOtherPlayers is on): the player's
    /// nickname (no guest/bot suffix) NameHeight above their interpolated head point, in world up,
    /// under the avatar root (so it hides and is destroyed with the avatar), hidden while the head
    /// part is missing. Each label is turned every frame to face one camera, upright, no roll.
    /// Every player gets two labels, one per camera, kept apart by layers (camera dump 2026-10-26):
    ///  - CenterEyeAnchor (headset) renders 20 HeadsetOnly and never 15 ThirdPersonAvatar
    ///  - SpectatorCam (and its PreviewCam child) never renders 20; it renders 15 in both
    ///    third-person modes (fixed and spinning) but not in first person
    /// So while the spectator camera is on and renders 15 (third person), the headset label is on
    /// 20 facing the headset and the spectator label on 15 facing SpectatorCam. Otherwise (first
    /// person, which sits at the headset anyway, or no spectator camera) only the headset label is
    /// shown, on layer 0, which both cameras render. The mode is checked every SpectatorCheckSeconds,
    /// so switching spectator modes follows. No camera's culling mask is ever changed. The font and
    /// material are copied from a native Main page label like LobbyPanel's; each label's material
    /// instance is destroyed with the avatar.
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
        // Send rate: MpSession.PoseRateHz (19.13.3). The server allows 1.5x that per second.
        private const long RenderDelayMs = 100;          // contract: buffer ~100 ms behind serverTime (3 samples at 30 Hz)
        private const int MaxShotsPerJump = 5;           // 19.13.4: a burst after a stall stays readable

        // Shot effects, the game's own numbers (Ghidra, 2026-10-29).
        private const int BeamPoolSize = 5;              // Gun.SetUp: 5 beams per pool
        private const int BeamDustBurst = 3500;          // Gun.DoBeamDust: burstNum += 0xdac
        private const int BeamDustEmitterShape = 6;      // Gun.DoBeamDust: SetBeamEmitterShape(6)
        private const string FirepointName = "Firepoint";

        // Hand animation (v2.42) and the gun slide, the game's own numbers (Ghidra / in-game dumps, 2026-10-30).
        private const float HandBlendRate = 20f;         // KataHand.INPUT_RATE_CHANGE: a blend moves 20 per second
        private const int KnucklesHardware = 4;          // VRHardwareSetup hardware 4 = Knuckles
        private const float FingerPoseMinAngle = 5f;     // open vs fist must differ at least this much to be usable
        private const string GunFireClipName = "gun_fire";
        private const string SlideName = "gun_slide";    // under the default model (tricks/mesh/Gun1/gun_slide)
        private const int SlideSamplesPerSecond = 90;
        private const int SampleSteps = 10;              // finger pose sampling: Animator steps per pose
        private const float SampleStepSeconds = 0.05f;   // 10 x 0.05 s = half a second per pose
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

        // Name labels.
        private const float NameHeight = 0.35f;          // above the head point (eyes), world up (m)
        private const float NameFontSize = 1.0f;         // TextMeshPro world size: ~0.12 m per line
        private const float NameOutlineWidth = 0.2f;
        private const int HeadsetNameLayer = 20;         // HeadsetOnly: the headset, never the spectator
        private const int SpectatorNameLayer = 15;       // ThirdPersonAvatar: the spectator in 3rd person, never the headset
        private const int SharedNameLayer = 0;           // Default: both, when only one label is shown
        private const string SpectatorCamName = "SpectatorCam";
        private const float SpectatorCheckSeconds = 0.5f;
        // A native label whose font and material render well in this game (as LobbyPanel).
        private const string NameFontSourcePath = "menu/ShellPage_Main/page/ShellPanel_Left";

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

            // v2.40, index 0 = left, 1 = right. Has* false when the sender sent null (v2.39 mod or server).
            public readonly float[] Pull = new float[2];
            public readonly bool[] HasPull = new bool[2];
            public readonly long[] Shots = new long[2];
            public readonly bool[] HasShots = new bool[2];

            // v2.42 (19.15): [point, thumbsUp, spin] and [thumb, index, middle, ring, pinky] curls.
            public readonly float[][] HandAnim = { new float[3], new float[3] };
            public readonly bool[] HasHandAnim = new bool[2];
            public readonly float[][] Fingers = { new float[5], new float[5] };
            public readonly bool[] HasFingers = new bool[2];

            public bool Trigger(int hand) => hand == 0 ? LTrigger : RTrigger;
        }

        /// <summary>
        /// One remote hand copy's animation (v2.42): its own Animator (kept from the normal hand,
        /// driven like KataHand.UpdateAnimStates) and its skinned bones, posed one finger at a time
        /// from finger curls when the sender has finger tracking.
        /// </summary>
        private class HandRig
        {
            public Animator Animator;
            /// <summary>-2 = not looked up yet (needs the Animator active), -1 = the controller has no such layer.</summary>
            public int PointLayer = -2, ThumbLayer = -2;
            /// <summary>SkinnedMeshRenderer.bones, the same order as fingerPoses.</summary>
            public Transform[] Bones;
            /// <summary>For the debug dump: "anim", "fingers" or "-".</summary>
            public string Mode = "-";
        }

        /// <summary>
        /// One hand side's finger poses, sampled once from the hand template's own Animator: per
        /// skinned bone, which finger it belongs to (0 thumb .. 4 pinky, -1 none) and its local
        /// rotation with the hand open (Flex 0) and as a fist (Flex 1).
        /// </summary>
        private class FingerPoses
        {
            public int[] Finger;
            public Quaternion[] Open;
            public Quaternion[] Fist;
            public int FingerBones;
            public float MaxAngle;
        }

        /// <summary>
        /// One remote hand's shot effects, built like the game builds a gun's own (Gun.SetUp,
        /// Gun.CreateMuzzleFlash) from the local gun of the same hand. The beams and muzzle flash
        /// live where the game keeps its own (under KataConfig and its instantiateParent), not
        /// under the avatar, so they are destroyed with the avatar by hand (DestroyShotFx).
        /// </summary>
        private class ShotFx
        {
            /// <summary>The local same-hand gun's successBeamPrefab: scale and speed are copied from it on every shot, as Gun.Fire does.</summary>
            public GunBeam Prefab;
            public GunBeam[] Beams;
            public int Cycle;
            public MuzzleFlash Muzzle;
            /// <summary>This player's beam texture: a copy of the native one, repainted in their gun colour.</summary>
            public RenderTexture Texture;
            public string TextureColorKey;
            /// <summary>The beam quads' material copies, one per prefab material, each using Texture.</summary>
            public readonly Dictionary<IntPtr, Material> Materials = new Dictionary<IntPtr, Material>();
        }

        /// <summary>Per hand of a remote player: what the shot detection has seen so far.</summary>
        private class ShotState
        {
            /// <summary>The last count read. Only meaningful while HasBaseline.</summary>
            public long Baseline;
            public bool HasBaseline;
            /// <summary>Trigger fallback (no counts): the last trigger value read.</summary>
            public bool LastTrigger;
            public bool HasLastTrigger;
            /// <summary>Shots fired for this hand since the avatar was created (debug dump).</summary>
            public long Fired;

            public void Reset()
            {
                HasBaseline = false;
                HasLastTrigger = false;
            }
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
            /// <summary>Firepoint markers on the gun copies (FirepointName), where shots leave from.</summary>
            public Transform LFire, RFire;
            /// <summary>Beams, muzzle flash and beam texture per hand (0 = left, 1 = right), made on the first shot.</summary>
            public readonly ShotFx[] Fx = new ShotFx[2];
            /// <summary>Platform copy, or null when the environment has none.</summary>
            public Transform Disc;
            public readonly List<Material> TintLeft = new List<Material>();
            public readonly List<Material> TintRight = new List<Material>();
            public readonly List<Material> OwnedMaterials = new List<Material>();
            public string AppliedColorKey;
            /// <summary>Faces the headset; on layer 20, or 0 when it is the only label.</summary>
            public TextMeshPro NameHeadset;
            /// <summary>Faces SpectatorCam, layer 15; only shown in its third-person modes.</summary>
            public TextMeshPro NameSpectator;
            public string AppliedName;

            // TorsoTrack state, in the sender's space.
            public bool BodyInit;
            public Vector3 BodyPos;
            public Quaternion BodyRot = Quaternion.identity;

            // v2.40 shots: the newest sample time already read, and each hand's state (0 = left, 1 = right).
            public long ShotsReadUpTo;
            public readonly ShotState[] Shot = { new ShotState(), new ShotState() };
            /// <summary>Interpolated trigger pull per hand, 0..1 (for the finger animation).</summary>
            public readonly float[] Pull = new float[2];

            // v2.42 hand animation, and the gun slide replayed on each shot (0 = left, 1 = right).
            public readonly HandRig[] Hands = new HandRig[2];
            public readonly Transform[] Slide = new Transform[2];
            public readonly Vector3[] SlideRestPos = new Vector3[2];
            public readonly Quaternion[] SlideRestRot = new Quaternion[2];
            /// <summary>Time.time of the last shot per hand, or -1 while the slide is at rest.</summary>
            public readonly float[] SlideStart = { -1f, -1f };

            /// <summary>Forgets what was read, so the next sample only sets a new baseline.</summary>
            public void ResetShots()
            {
                ShotsReadUpTo = 0;
                Shot[0].Reset();
                Shot[1].Reset();
            }
        }

        private static readonly Dictionary<string, Remote> remotes = new Dictionary<string, Remote>();

        private static bool subscribed;
        private static bool wasInSong;
        private static long nextPoseAtMs;
        private static float nextGunColorPoll;

        // This player's shots per hand (0 = left, 1 = right), counted by the Gun.UpdateTrigger
        // postfix below and reset on every welcome (v2.40: "since the connection opened").
        private static readonly long[] localShots = new long[2];

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

        // Spectator camera, for the name labels.
        private static Camera spectatorCam;
        private static float nextSpectatorCheck;
        private static bool splitNames; // two labels per player (spectator in third person)

        // Interpolation output, reused every frame.
        private static readonly Vector3[] curPos = new Vector3[PartCount];
        private static readonly Quaternion[] curRot = new Quaternion[PartCount];
        private static readonly bool[] curHas = new bool[PartCount];
        private static readonly float[] curPull = new float[2];
        private static readonly float[][] curHandAnim = { new float[3], new float[3] };
        private static readonly float[][] curFingers = { new float[5], new float[5] };
        private static readonly bool[] curHasFingers = new bool[2];

        // v2.42: finger poses per hand side (null until sampled, or when sampling found nothing usable).
        private static readonly FingerPoses[] fingerPoses = new FingerPoses[2];
        // The gun slide's movement in the game's gun_fire clip, sampled once (null when unavailable).
        private static Vector3[] slidePos;
        private static Quaternion[] slideRot;
        private static float slideLength;
        // Knuckles: this player's own eased "spin" per hand, since the game doesn't compute one there.
        private static readonly float[] localSpin = new float[2];
        // The hand Animator's parameter and layer names, read once from KataHand's statics.
        private static string animFlex, animPinch, animPointLayer, animThumbLayer;

        // ── Per frame ──

        /// <summary>Once per frame from ExScoring.OnUpdate, after MpSession.Tick.</summary>
        public static void Tick()
        {
            if (!subscribed)
            {
                subscribed = true;
                MpSession.OnPoses += HandlePoses;
                // A new connection counts shots from 0 (19.13.4); receivers re-baseline on the drop.
                MpSession.OnConnected += _ =>
                {
                    localShots[0] = 0;
                    localShots[1] = 0;
                };
                // A new rate starts its schedule now rather than waiting out the old interval.
                MpSession.OnPoseRateChanged += _ => nextPoseAtMs = 0;
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
            UpdateLocalSpin();
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

        /// <summary>
        /// One pose every 1000 / PoseRateHz ms. The next send time steps by exactly one interval, so
        /// the average rate is right even though sends can only happen on frames (at 30 Hz and
        /// 90 fps a plain "time since last send" check drifts down to ~22/s). After a stall it
        /// restarts from now instead of catching up in a burst, so the server's 1.5x limit is never
        /// approached.
        /// </summary>
        private static void SendPoseIfDue()
        {
            long now = MpSession.LocalNowMs;
            if (now < nextPoseAtMs) return;
            if (!MpSession.IsConnected) return;
            if (!FindLocalRig()) return;

            long interval = Math.Max(1, (long)Math.Round(1000.0 / Math.Max(1, MpSession.PoseRateHz)));
            nextPoseAtMs = nextPoseAtMs <= 0 || now - nextPoseAtMs >= interval ? now + interval : nextPoseAtMs + interval;

            JObject pose = new JObject
            {
                ["t"] = MpSession.ServerNowMs,
                ["head"] = PartJson(localHead),
                ["lHand"] = PartJson(localKataL != null ? localKataL.transform : null),
                ["rHand"] = PartJson(localKataR != null ? localKataR.transform : null),
                ["lGun"] = PartJson(GunPoseTransform(localGunL, ref localGunPoseL)),
                ["rGun"] = PartJson(GunPoseTransform(localGunR, ref localGunPoseR)),
                ["lTrigger"] = TriggerDown(localGunL),
                ["rTrigger"] = TriggerDown(localGunR),
                ["lPull"] = PullJson(localGunL),
                ["rPull"] = PullJson(localGunR),
                ["lShots"] = localShots[0],
                ["rShots"] = localShots[1]
            };

            // v2.42 (19.15.3): hand animation, and finger curls with finger tracking.
            bool knuckles = UsingKnuckles();
            pose["lHandAnim"] = HandAnimJson(localKataL, 0, knuckles);
            pose["rHandAnim"] = HandAnimJson(localKataR, 1, knuckles);
            pose["lFingers"] = FingersJson(localKataL, knuckles);
            pose["rFingers"] = FingersJson(localKataR, knuckles);
            MpSession.SendPose(pose);
        }

        /// <summary>The same check KataHand.Update makes: VRHardwareSetup hardware 4 poses the hands from the SteamVR skeleton.</summary>
        private static bool UsingKnuckles()
        {
            try
            {
                VRHardwareSetup setup = VRHardwareSetup.I;
                return setup != null && (int)setup.hardware == KnucklesHardware;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Knuckles: this player's own "spin" per hand, eased like KataHand's blends (InputValueRateChange:
        /// 20 per second toward 1 while the gun does a trick, toward 0 otherwise). The game only
        /// computes m_spinningBlend for the normal hand.
        /// </summary>
        private static void UpdateLocalSpin()
        {
            float step = HandBlendRate * Time.deltaTime;
            for (int h = 0; h < 2; h++)
            {
                Gun gun = h == 0 ? localGunL : localGunR;
                bool tricking = false;
                try
                {
                    tricking = gun != null && (int)gun.GetCurrentTrick() != 0;
                }
                catch
                {
                    // leave it at false
                }
                localSpin[h] = Mathf.MoveTowards(localSpin[h], tricking ? 1f : 0f, step);
            }
        }

        /// <summary>
        /// [point, thumbsUp, spin] for one hand (19.15.3). Normal hand: KataHand's own blends.
        /// Knuckles: worked out from the curls (point = 1 - index, thumbsUp = 1 - thumb) and the eased
        /// spin. Null for a missing or inactive hand.
        /// </summary>
        private static JToken HandAnimJson(KataHand hand, int h, bool knuckles)
        {
            if (hand == null || !hand.gameObject.activeInHierarchy) return JValue.CreateNull();
            try
            {
                if (knuckles)
                {
                    Valve.VR.SteamVR_Behaviour_Skeleton skeleton = hand.knucklesHand;
                    if (skeleton == null || !skeleton.gameObject.activeInHierarchy) return JValue.CreateNull();
                    return new JArray(Round2(1f - skeleton.indexCurl), Round2(1f - skeleton.thumbCurl), Round2(localSpin[h]));
                }
                return new JArray(Round2(hand.m_pointBlend), Round2(hand.m_thumbsUpBlend), Round2(hand.m_spinningBlend));
            }
            catch
            {
                return JValue.CreateNull();
            }
        }

        /// <summary>[thumb, index, middle, ring, pinky] curls from the SteamVR skeleton (Knuckles only), else null.</summary>
        private static JToken FingersJson(KataHand hand, bool knuckles)
        {
            if (!knuckles || hand == null || !hand.gameObject.activeInHierarchy) return JValue.CreateNull();
            try
            {
                Valve.VR.SteamVR_Behaviour_Skeleton skeleton = hand.knucklesHand;
                if (skeleton == null || !skeleton.gameObject.activeInHierarchy) return JValue.CreateNull();
                return new JArray(Round2(skeleton.thumbCurl), Round2(skeleton.indexCurl), Round2(skeleton.middleCurl),
                                  Round2(skeleton.ringCurl), Round2(skeleton.pinkyCurl));
            }
            catch
            {
                return JValue.CreateNull();
            }
        }

        private static double Round2(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) v = 0f;
            return Math.Round((double)Mathf.Clamp01(v), 2);
        }

        /// <summary>Trigger pull 0..1 with 2 decimals (what the server keeps), or null without a gun.</summary>
        private static JToken PullJson(Gun gun)
        {
            if (gun == null) return JValue.CreateNull();
            try
            {
                float pull = gun.GetTriggerPullAmount();
                if (float.IsNaN(pull) || float.IsInfinity(pull)) return JValue.CreateNull();
                return Math.Round((double)Mathf.Clamp01(pull), 2);
            }
            catch
            {
                return JValue.CreateNull();
            }
        }

        /// <summary>
        /// A registered press (the gun fires) on one of this player's guns: +1 to that hand's count.
        /// Called from the Gun.UpdateTrigger postfix, in menus and in songs alike; counts go out with
        /// the next pose, so shots in a song are never seen by anyone (receivers re-baseline after
        /// a song, as they hid this player).
        /// </summary>
        private static void CountLocalShot(Gun gun)
        {
            int hand = HandOf(gun);
            if (hand < 0) return;
            if (localShots[hand] < int.MaxValue) localShots[hand]++; // the contract caps counts at 2^31 - 1
        }

        /// <summary>
        /// 0 = left, 1 = right, -1 = not one of the player's two guns. Matched against the rig when
        /// it's known, else Gun.controller (1 = left, 2 = right, as in PreciseTiming).
        /// </summary>
        private static int HandOf(Gun gun)
        {
            if (gun == null) return -1;
            if (localGunL != null && gun.Pointer == localGunL.Pointer) return 0;
            if (localGunR != null && gun.Pointer == localGunR.Pointer) return 1;
            int controller = (int)gun.controller;
            if (controller == 1) return 0;
            if (controller == 2) return 1;
            return -1;
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

        /// <summary>Not null, not destroyed by Unity, and its managed wrapper not collected (a Component also needs its GameObject).</summary>
        private static bool IsAlive(UnityEngine.Object o)
        {
            if (o == null) return false;
            try
            {
                if (o.WasCollected) return false;
                Component component = o.TryCast<Component>();
                return component == null || component.gameObject != null;
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
                remote.ResetShots(); // coming back, their count only sets a new baseline
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
            ReadPull(entry.lPull, sample, 0);
            ReadPull(entry.rPull, sample, 1);
            ReadShots(entry.lShots, sample, 0);
            ReadShots(entry.rShots, sample, 1);
            sample.HasHandAnim[0] = ReadUnitValues(entry.lHandAnim, sample.HandAnim[0]);
            sample.HasHandAnim[1] = ReadUnitValues(entry.rHandAnim, sample.HandAnim[1]);
            sample.HasFingers[0] = ReadUnitValues(entry.lFingers, sample.Fingers[0]);
            sample.HasFingers[1] = ReadUnitValues(entry.rFingers, sample.Fingers[1]);
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

        private static void ReadPull(float? value, Sample sample, int hand)
        {
            sample.HasPull[hand] = value.HasValue && !float.IsNaN(value.Value) && !float.IsInfinity(value.Value);
            sample.Pull[hand] = sample.HasPull[hand] ? Mathf.Clamp01(value.Value) : 0f;
        }

        /// <summary>v2.42: an array of exactly dest.Length finite numbers, clamped to 0..1. False (dest untouched) otherwise.</summary>
        private static bool ReadUnitValues(float[] source, float[] dest)
        {
            if (source == null || source.Length != dest.Length) return false;
            for (int i = 0; i < source.Length; i++)
            {
                if (float.IsNaN(source[i]) || float.IsInfinity(source[i])) return false;
            }
            for (int i = 0; i < source.Length; i++) dest[i] = Mathf.Clamp01(source[i]);
            return true;
        }

        private static void ReadShots(long? value, Sample sample, int hand)
        {
            sample.HasShots[hand] = value.HasValue && value.Value >= 0;
            sample.Shots[hand] = sample.HasShots[hand] ? value.Value : 0;
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

            long renderTime = MpSession.ServerNowMs - RenderDelayMs;

            if (!Config.ShowOtherPlayers)
            {
                foreach (Remote remote in remotes.Values)
                {
                    // Shots are still read, so the baselines stay current and turning it back on
                    // doesn't replay a burst. Nothing fires while it's off.
                    if (remote.Present && remote.Samples.Count > 0) ReadShotsUpTo(remote, renderTime, false);
                    SetVisible(remote, false);
                }
                return;
            }
            int mySeat = MpSession.MySeat;
            MpLobbyState lobby = MpSession.Lobby;
            float dt = Time.deltaTime;

            CheckSpectator();
            Transform headsetEye = localHead;
            if (headsetEye == null && Camera.main != null) headsetEye = Camera.main.transform;
            Transform spectatorEye = splitNames && spectatorCam != null ? spectatorCam.transform : null;

            foreach (Remote remote in remotes.Values)
            {
                if (!remote.Present || remote.Samples.Count == 0)
                {
                    SetVisible(remote, false);
                    continue;
                }

                if (remote.Root == null && !BuildAvatar(remote))
                {
                    ReadShotsUpTo(remote, renderTime, false); // keep the baselines while there's nothing to draw
                    continue;
                }

                MpPlayerState player = lobby != null ? lobby.FindPlayer(remote.UserId) : null;
                int seat = player != null ? player.seat : 0;
                remote.Root.transform.position = LayoutOffset(seat, mySeat);
                remote.Root.transform.rotation = Quaternion.identity;

                ApplyColors(remote, player);

                Interpolate(remote.Samples, renderTime);
                remote.Pull[0] = curPull[0];
                remote.Pull[1] = curPull[1];
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

                UpdateNames(remote, player, headsetEye, spectatorEye);

                SetVisible(remote, true);

                // After the parts are placed and shown, so a beam leaves from this frame's gun.
                ReadShotsUpTo(remote, renderTime, true);

                for (int h = 0; h < 2; h++)
                {
                    UpdateHand(remote, h);
                    UpdateSlide(remote, h);
                }
            }
        }

        // ── Hand animation (v2.42) and gun slide ──

        /// <summary>
        /// Poses one remote hand copy from this frame's interpolated values (19.15.4):
        ///  - finger curls (a finger-tracking sender): the copy's Animator off, every finger bone set
        ///    between its sampled open and fist rotation by its finger's curl
        ///  - otherwise the copy's Animator, driven exactly like KataHand.UpdateAnimStates (Ghidra
        ///    2026-10-30) from HandAnim and the trigger pull, the grip axis taken as 0 (it isn't sent
        ///    and only moves Flex between 0.2 and 0.25):
        ///      Flex   = Lerp(Lerp(0.2, 0.25, grip), 0, spin)
        ///      Pinch  = Lerp(Lerp(0, 0.5, trigger), 0.3, spin)
        ///      Point Layer weight = Lerp(Lerp(0, 0.2, 1 - trigger) + Lerp(0, 0.1, point), 0, spin)
        ///      Thumb Layer weight = Lerp(Lerp(0, 0.2, thumbsUp), 1, spin)
        /// </summary>
        private static void UpdateHand(Remote remote, int h)
        {
            HandRig rig = remote.Hands[h];
            Transform part = h == 0 ? remote.LHand : remote.RHand;
            if (rig == null || part == null || !part.gameObject.activeInHierarchy) return;

            FingerPoses poses = fingerPoses[h];
            if (curHasFingers[h] && poses != null && rig.Bones != null && rig.Bones.Length == poses.Finger.Length)
            {
                if (rig.Animator != null && rig.Animator.enabled) rig.Animator.enabled = false;
                float[] curls = curFingers[h];
                for (int i = 0; i < rig.Bones.Length; i++)
                {
                    int finger = poses.Finger[i];
                    Transform bone = rig.Bones[i];
                    if (finger < 0 || bone == null) continue;
                    bone.localRotation = Quaternion.Slerp(poses.Open[i], poses.Fist[i], curls[finger]);
                }
                rig.Mode = "fingers";
                return;
            }

            Animator animator = rig.Animator;
            if (animator == null || !ReadAnimNames())
            {
                rig.Mode = "-";
                return;
            }
            if (!animator.enabled) animator.enabled = true;
            if (rig.PointLayer == -2)
            {
                rig.PointLayer = animator.GetLayerIndex(animPointLayer);
                rig.ThumbLayer = animator.GetLayerIndex(animThumbLayer);
            }

            float[] values = curHandAnim[h];
            float point = values[0], thumbsUp = values[1], spin = values[2];
            float trigger = remote.Pull[h];
            const float grip = 0f;

            animator.SetFloat(animFlex, Mathf.Lerp(Mathf.Lerp(0.2f, 0.25f, grip), 0f, spin));
            animator.SetFloat(animPinch, Mathf.Lerp(Mathf.Lerp(0f, 0.5f, trigger), 0.3f, spin));
            if (rig.PointLayer >= 0)
                animator.SetLayerWeight(rig.PointLayer, Mathf.Lerp(Mathf.Lerp(0f, 0.2f, 1f - trigger) + Mathf.Lerp(0f, 0.1f, point), 0f, spin));
            if (rig.ThumbLayer >= 0)
                animator.SetLayerWeight(rig.ThumbLayer, Mathf.Lerp(Mathf.Lerp(0f, 0.2f, thumbsUp), 1f, spin));
            rig.Mode = "anim";
        }

        /// <summary>The hand Animator's names from KataHand's statics ("Flex", "Pinch", "Point Layer", "Thumb Layer"), read once.</summary>
        private static bool ReadAnimNames()
        {
            if (animFlex != null) return true;
            try
            {
                animFlex = KataHand.ANIM_PARAM_NAME_FLEX;
                animPinch = KataHand.ANIM_PARAM_NAME_PINCH;
                animPointLayer = KataHand.ANIM_LAYER_NAME_POINT;
                animThumbLayer = KataHand.ANIM_LAYER_NAME_THUMB;
            }
            catch (Exception ex)
            {
                Log($"hand animator names unreadable: {ex.Message}");
                animFlex = null;
            }
            return animFlex != null;
        }

        /// <summary>
        /// Replays the game's gun_fire slide movement (sampled once, SampleGunSlide) on a remote gun
        /// copy's gun_slide after each of its shots, then puts it back at rest. The recoil kick
        /// itself (tricks/mesh) already arrives with the sender's gun pose.
        /// </summary>
        private static void UpdateSlide(Remote remote, int h)
        {
            Transform slide = remote.Slide[h];
            float start = remote.SlideStart[h];
            if (slide == null || start < 0f) return;

            float t = Time.time - start;
            if (slidePos == null || slidePos.Length < 2 || t >= slideLength)
            {
                slide.localPosition = remote.SlideRestPos[h];
                slide.localRotation = remote.SlideRestRot[h];
                remote.SlideStart[h] = -1f;
                return;
            }

            float at = Mathf.Clamp01(t / slideLength) * (slidePos.Length - 1);
            int i0 = Mathf.Min((int)at, slidePos.Length - 2);
            float f = at - i0;
            slide.localPosition = Vector3.Lerp(slidePos[i0], slidePos[i0 + 1], f);
            slide.localRotation = Quaternion.Slerp(slideRot[i0], slideRot[i0 + 1], f);
        }

        // ── Name labels ──

        /// <summary>
        /// Every SpectatorCheckSeconds: finds SpectatorCam and decides whether each player needs a
        /// second label for it (it is on and in third person: renders 15, never 20).
        /// </summary>
        private static void CheckSpectator()
        {
            if (Time.unscaledTime < nextSpectatorCheck) return;
            nextSpectatorCheck = Time.unscaledTime + SpectatorCheckSeconds;

            bool split = false;
            try
            {
                if (spectatorCam == null || spectatorCam.WasCollected)
                {
                    spectatorCam = null;
                    GameObject go = GameObject.Find(SpectatorCamName);
                    if (go != null) spectatorCam = go.GetComponent<Camera>();
                }

                if (spectatorCam != null && spectatorCam.enabled && spectatorCam.gameObject.activeInHierarchy)
                {
                    int mask = spectatorCam.cullingMask;
                    split = (mask & (1 << SpectatorNameLayer)) != 0 && (mask & (1 << HeadsetNameLayer)) == 0;
                }
            }
            catch (Exception ex)
            {
                Log($"spectator camera check failed: {ex.Message}");
                spectatorCam = null;
            }

            if (split != splitNames) Log(split ? "spectator in third person: one name label per camera" : "spectator off or in first person: one shared name label");
            splitNames = split;
        }

        /// <summary>
        /// Text, visibility, layer and facing of a player's labels. Called after the parts are
        /// placed, so the head's world position is current.
        /// </summary>
        private static void UpdateNames(Remote remote, MpPlayerState player, Transform headsetEye, Transform spectatorEye)
        {
            if (remote.NameHeadset == null) return;

            string name = player != null ? (player.nickname ?? "").Trim() : "";
            if (name != remote.AppliedName)
            {
                remote.AppliedName = name;
                string text = "<noparse>" + name.Replace("</noparse>", "") + "</noparse>";
                remote.NameHeadset.text = text;
                if (remote.NameSpectator != null) remote.NameSpectator.text = text;
            }

            bool show = Config.ShowPlayerNames && name.Length > 0 && remote.Head != null && curHas[PartHead];
            Vector3 position = show ? remote.Head.position + Vector3.up * NameHeight : Vector3.zero;

            ShowName(remote.NameHeadset, show, splitNames ? HeadsetNameLayer : SharedNameLayer, position, headsetEye);
            ShowName(remote.NameSpectator, show && splitNames && spectatorEye != null, SpectatorNameLayer, position, spectatorEye);
        }

        private static void ShowName(TextMeshPro label, bool show, int layer, Vector3 position, Transform eye)
        {
            if (label == null) return;
            GameObject go = label.gameObject;
            if (go.activeSelf != show) go.SetActive(show);
            if (!show) return;

            if (go.layer != layer) SetLayerRecursive(go, layer); // TMP sub-meshes are children

            Transform t = label.transform;
            t.position = position;
            if (eye == null) return;
            // TextMeshPro reads from its -Z side, so +Z points away from the viewer; upright, no roll.
            Vector3 away = position - eye.position;
            if (away.sqrMagnitude > 0.0001f) t.rotation = Quaternion.LookRotation(away, Vector3.up);
        }

        /// <summary>A hidden label under the avatar root, with its own material (kept in owned).</summary>
        private static TextMeshPro CreateNameLabel(GameObject root, string name, List<Material> owned)
        {
            GameObject go = new GameObject(name);
            go.transform.SetParent(root.transform, false);
            try
            {
                TextMeshPro text = go.AddComponent<TextMeshPro>();
                text.fontSize = NameFontSize;
                text.alignment = TextAlignmentOptions.Bottom;
                text.enableWordWrapping = false;
                text.overflowMode = TextOverflowModes.Overflow;
                text.richText = true;
                text.color = Color.white;
                ApplyNameFont(text);
                // fontMaterial makes this label's own material instance, so the outline never
                // touches the game's shared font material.
                Material mat = text.fontMaterial;
                if (mat != null) owned.Add(mat);
                text.outlineWidth = NameOutlineWidth;
                text.outlineColor = Color.black;
                text.rectTransform.pivot = new Vector2(0.5f, 0f); // grows upward from the label point
                text.rectTransform.sizeDelta = new Vector2(3f, 0.5f);
                go.SetActive(false); // shown once there is a head and a name
                return text;
            }
            catch (Exception ex)
            {
                Log($"name label {name} failed: {ex.Message}");
                UnityEngine.Object.Destroy(go);
                return null;
            }
        }

        /// <summary>The default TextMeshPro material is dim in game; copies a native label's (as LobbyPanel).</summary>
        private static void ApplyNameFont(TextMeshPro target)
        {
            try
            {
                GameObject source = GameObject.Find(NameFontSourcePath);
                TextMeshPro template = source != null ? source.GetComponentInChildren<TextMeshPro>(true) : null;
                if (template == null || template.font == null) return; // keep the default
                target.font = template.font;
                target.fontSharedMaterial = template.fontSharedMaterial;
            }
            catch (Exception ex)
            {
                Log($"couldn't copy the game font for name labels: {ex.Message}");
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

            // Trigger pull (v2.40): lerped when both sides have one, otherwise the nearer side's
            // pull, or its trigger bool as 0/1 when it has none (a v2.39 sender).
            for (int h = 0; h < 2; h++)
            {
                float pa = a.HasPull[h] ? a.Pull[h] : (a.Trigger(h) ? 1f : 0f);
                float pb = b.HasPull[h] ? b.Pull[h] : (b.Trigger(h) ? 1f : 0f);
                curPull[h] = a.HasPull[h] && b.HasPull[h] ? Mathf.Lerp(pa, pb, frac) : (frac < 0.5f ? pa : pb);

                // v2.42 (19.15.4): lerped; with one side only, that side; with neither, the rest
                // values (HandAnim [0, 0, 0], no fingers).
                LerpUnitValues(a.HasHandAnim[h], a.HandAnim[h], b.HasHandAnim[h], b.HandAnim[h], frac, curHandAnim[h]);
                curHasFingers[h] = LerpUnitValues(a.HasFingers[h], a.Fingers[h], b.HasFingers[h], b.Fingers[h], frac, curFingers[h]);
            }
        }

        /// <summary>Fills dest from two samples' values (see Interpolate). Returns whether either side had any.</summary>
        private static bool LerpUnitValues(bool hasA, float[] a, bool hasB, float[] b, float frac, float[] dest)
        {
            for (int i = 0; i < dest.Length; i++)
            {
                if (hasA && hasB) dest[i] = Mathf.Lerp(a[i], b[i], frac);
                else if (hasA) dest[i] = a[i];
                else if (hasB) dest[i] = b[i];
                else dest[i] = 0f;
            }
            return hasA || hasB;
        }

        // ── Shots from others (v2.40, 19.13.4) ──

        /// <summary>
        /// Reads every sample render time has passed since the last call, in order, and fires the
        /// shots they show. The very first read (or the first after the player was hidden) only
        /// takes the newest passed sample as the baseline, so a player appearing never fires shots
        /// from before they were drawn.
        /// </summary>
        private static void ReadShotsUpTo(Remote remote, long renderTime, bool fire)
        {
            List<Sample> samples = remote.Samples;

            if (remote.ShotsReadUpTo == 0)
            {
                Sample newestPassed = null;
                for (int i = 0; i < samples.Count && samples[i].T <= renderTime; i++) newestPassed = samples[i];
                if (newestPassed == null) return; // nothing passed yet
                for (int h = 0; h < 2; h++) ReadShotSample(remote, newestPassed, h, false, true);
                remote.ShotsReadUpTo = newestPassed.T;
                return;
            }

            for (int i = 0; i < samples.Count; i++)
            {
                Sample sample = samples[i];
                if (sample.T > renderTime) break;
                if (sample.T <= remote.ShotsReadUpTo) continue;
                for (int h = 0; h < 2; h++) ReadShotSample(remote, sample, h, fire, false);
                remote.ShotsReadUpTo = sample.T;
            }
        }

        /// <summary>One hand of one sample: the count when the sender has one, else the trigger edge.</summary>
        private static void ReadShotSample(Remote remote, Sample sample, int hand, bool fire, bool baselineOnly)
        {
            ShotState state = remote.Shot[hand];

            if (sample.HasShots[hand])
            {
                state.HasLastTrigger = false; // switching back to the bool later starts it fresh
                long count = sample.Shots[hand];

                if (!state.HasBaseline || baselineOnly)
                {
                    state.Baseline = count;
                    state.HasBaseline = true;
                    return;
                }

                if (count < state.Baseline)
                {
                    // They reconnected and count from 0 again.
                    Log($"{remote.UserId} shot count {HandName(hand)} went {state.Baseline} -> {count} (reconnected?), new baseline");
                    state.Baseline = count;
                    return;
                }

                long rise = count - state.Baseline;
                state.Baseline = count;
                if (rise > 0) FireShots(remote, hand, (int)Math.Min(rise, MaxShotsPerJump), rise, fire);
                return;
            }

            // No count: one shot per false -> true trigger change.
            state.HasBaseline = false;
            bool trigger = sample.Trigger(hand);
            if (!baselineOnly && state.HasLastTrigger && !state.LastTrigger && trigger) FireShots(remote, hand, 1, 1, fire);
            state.LastTrigger = trigger;
            state.HasLastTrigger = true;
        }

        /// <summary>
        /// count shots on one hand (rise = what the count went up by, before the cap): logged, and
        /// shown on the avatar's gun the way the game shows its own (PlayShot).
        /// </summary>
        private static void FireShots(Remote remote, int hand, int count, long rise, bool fire)
        {
            if (!fire || count <= 0) return;
            ShotState state = remote.Shot[hand];
            state.Fired += count;

            string capped = rise > count ? $" (count rose by {rise}, capped at {MaxShotsPerJump})" : "";
            string source = remote.Samples.Count > 0 && remote.Samples[remote.Samples.Count - 1].HasShots[hand] ? "count" : "trigger";
            Log($"{remote.UserId} shot {HandName(hand)}{(count > 1 ? " x" + count : "")}{capped} [{source}], {state.Fired} so far");

            try
            {
                PlayShot(remote, hand, count);
            }
            catch (Exception ex)
            {
                MelonLogger.Log($"[MpPresence] shot effect for {remote.UserId} {HandName(hand)} failed: {ex}");
            }
        }

        // ── Shot effects (the game's success beam, muzzle flash and beam dust) ──

        /// <summary>
        /// What Gun.Fire shows for a shot, from the remote gun's firepoint (Ghidra, 2026-10-29; the
        /// same steps checked in game from the UnityExplorer console the same day):
        ///  - beam: the next of BeamPoolSize success beams, at the firepoint, forward along it, roll
        ///    = the gun's world z angle (Quaternion.Euler(local x, local y, roll)), prefab scale and
        ///    speed, then GunBeam.Fire (it animates and switches itself off)
        ///  - muzzle flash: MuzzleFlash.Play(position, forward, roll)
        ///  - beam dust: Gun.DoBeamDust's steps on UGPUManager's shared dust system for that hand
        /// Only while the avatar and that gun are shown. The beam is in that player's gun colour; the
        /// muzzle flash and dust take the game's per-hand colour slot, so they show this player's
        /// own colours for that hand. One muzzle flash and one dust burst however many shots came at once.
        /// </summary>
        private static void PlayShot(Remote remote, int hand, int count)
        {
            if (remote.Root == null || !remote.Root.activeSelf) return;
            Transform gunPart = hand == 0 ? remote.LGun : remote.RGun;
            Transform firepoint = hand == 0 ? remote.LFire : remote.RFire;
            if (gunPart == null || !gunPart.gameObject.activeSelf) return;
            if (firepoint == null) firepoint = gunPart;

            Gun local = hand == 0 ? localGunL : localGunR;
            if (local == null) return;

            ShotFx fx = EnsureShotFx(remote, hand, local);
            if (fx == null) return;
            PaintBeamTexture(fx, RemoteGunColor(remote, hand));

            Vector3 position = firepoint.position;
            Vector3 forward = firepoint.forward;
            float roll = gunPart.eulerAngles.z;

            int beams = Math.Min(count, BeamPoolSize);
            for (int i = 0; i < beams; i++)
            {
                fx.Cycle = (fx.Cycle + 1) % fx.Beams.Length; // BeamPool.GetBeam
                FireBeam(fx.Beams[fx.Cycle], fx.Prefab, position, forward, roll);
            }

            if (IsAlive(fx.Muzzle)) fx.Muzzle.Play(position, forward, roll);
            PlayBeamDust(local, hand, position, forward);
            remote.SlideStart[hand] = Time.time; // the gun slide's fire movement (UpdateSlide)
        }

        /// <summary>Gun.Fire's placement of a pooled beam, then GunBeam.Fire.</summary>
        private static void FireBeam(GunBeam beam, GunBeam prefab, Vector3 position, Vector3 forward, float roll)
        {
            if (!IsAlive(beam)) return;
            Transform t = beam.transform;
            t.position = position;
            t.forward = forward;
            Vector3 local = t.localEulerAngles;
            t.localRotation = Quaternion.Euler(local.x, local.y, roll);
            t.localScale = prefab.transform.localScale;
            beam.speed = prefab.speed;
            beam.Fire();
        }

        /// <summary>
        /// Gun.DoBeamDust's steps at another position: the shared dust system for that hand moved
        /// to the firepoint and pointed along the shot, the beam emitter shape, then a burst emitted
        /// at once. SetBeamEmitterShape is called on the local gun of the same hand, which picks
        /// the same system.
        /// </summary>
        private static void PlayBeamDust(Gun local, int hand, Vector3 position, Vector3 forward)
        {
            UGPUManager gpu = UGPUManager.I;
            if (gpu == null) return;
            GPUParticleSystem dust = hand == 0 ? gpu.beamDustLeft : gpu.beamDustRight;
            if (dust == null) return;

            Transform t = dust.transform;
            t.position = position;
            t.forward = forward;
            local.SetBeamEmitterShape((GPUParticleSystem.EmitterShape)BeamDustEmitterShape);
            if (!dust.gameObject.activeInHierarchy) return;
            dust.burstNum += BeamDustBurst;
            dust.Update();
        }

        /// <summary>
        /// This remote hand's effects, built on first use (or again if something destroyed them, e.g.
        /// a scene change took the muzzle flash's parent). Null while the local gun isn't set up yet.
        /// </summary>
        private static ShotFx EnsureShotFx(Remote remote, int hand, Gun local)
        {
            ShotFx fx = remote.Fx[hand];
            if (fx != null && ShotFxAlive(fx)) return fx;
            if (fx != null)
            {
                Log($"{remote.UserId} {HandName(hand)}: shot effects were destroyed, rebuilding");
                DestroyShotFx(fx);
                remote.Fx[hand] = null;
            }

            KataConfig config = KataConfig.I;
            GunBeam prefab = local.successBeamPrefab;
            if (config == null || prefab == null) return null;

            fx = new ShotFx { Prefab = prefab, Beams = new GunBeam[BeamPoolSize] };
            try
            {
                // The texture first, so the beams' materials can use it from the start.
                if (!PaintBeamTexture(fx, RemoteGunColor(remote, hand)))
                {
                    Log($"{remote.UserId} {HandName(hand)}: no beam texture yet, beams keep the prefab look");
                }

                // Gun.SetUp: Instantiate under KataConfig, Init, hidden until fired.
                for (int i = 0; i < BeamPoolSize; i++)
                {
                    GunBeam beam = UnityEngine.Object.Instantiate<GunBeam>(prefab, config.transform);
                    beam.name = $"MpBeam {HandName(hand)} {remote.UserId}";
                    beam.Init();
                    UseBeamMaterials(fx, beam);
                    beam.gameObject.SetActive(false);
                    fx.Beams[i] = beam;
                }

                // Gun.CreateMuzzleFlash: under instantiateParent, linked to the local gun of the
                // same hand for its colour slot.
                if (local.muzzleFlashPrefab != null)
                {
                    Transform parent = config.instantiateParent;
                    MuzzleFlash muzzle = parent != null
                        ? UnityEngine.Object.Instantiate<MuzzleFlash>(local.muzzleFlashPrefab, parent)
                        : UnityEngine.Object.Instantiate<MuzzleFlash>(local.muzzleFlashPrefab);
                    muzzle.name = $"MpMuzzleFlash {HandName(hand)} {remote.UserId}";
                    muzzle.mGun = local;
                    muzzle.UpdateColor();
                    fx.Muzzle = muzzle;
                }

                remote.Fx[hand] = fx;
                Log($"{remote.UserId} {HandName(hand)}: shot effects built ({BeamPoolSize} beams, muzzle flash={fx.Muzzle != null}, texture={fx.Texture != null})");
                return fx;
            }
            catch (Exception ex)
            {
                MelonLogger.Log($"[MpPresence] building shot effects for {remote.UserId} {HandName(hand)} failed: {ex}");
                DestroyShotFx(fx);
                return null;
            }
        }

        /// <summary>
        /// TargetColorSetter.GetBeamMaterial with this player's texture: each beam quad gets a copy
        /// of its own material (one copy per source material, shared by the pool) with mainTexture
        /// set. The noise line keeps its shared white material, as on the game's own beams.
        /// </summary>
        private static void UseBeamMaterials(ShotFx fx, GunBeam beam)
        {
            if (fx.Texture == null) return;
            var meshes = beam.meshes;
            if (meshes == null) return;
            for (int i = 0; i < meshes.Length; i++)
            {
                MeshRenderer quad = meshes[i];
                if (quad == null) continue;
                Material source = quad.sharedMaterial;
                if (source == null) continue;
                if (!fx.Materials.TryGetValue(source.Pointer, out Material copy) || copy == null)
                {
                    copy = new Material(source) { mainTexture = fx.Texture };
                    fx.Materials[source.Pointer] = copy;
                }
                quad.sharedMaterial = copy;
            }
        }

        /// <summary>
        /// Paints fx.Texture in color, the way TargetColorSetter makes the player's own beam textures
        /// (checked in game 2026-10-29): CreateTexturesBeam needs an existing texture (it doesn't
        /// make one), so the first time the native one is copied, then CreateTexturesBeam and
        /// ShiftTextureColors (from beamTextureRed) repaint it. Only repainted when the colour changes.
        /// </summary>
        private static bool PaintBeamTexture(ShotFx fx, Color color)
        {
            string key = $"{color.r:F3},{color.g:F3},{color.b:F3}";
            if (fx.Texture != null && key == fx.TextureColorKey) return true;

            TargetColorSetter setter = TargetColorSetter.I;
            if (setter == null) return false;

            if (fx.Texture == null)
            {
                RenderTexture source = setter.mBeamTextureRight != null ? setter.mBeamTextureRight : setter.mBeamTextureLeft;
                if (source == null) return false;
                fx.Texture = UnityEngine.Object.Instantiate<RenderTexture>(source);
                fx.Texture.name = "MpBeamTexture";
            }

            RenderTexture texture = fx.Texture;
            setter.CreateTexturesBeam(color, ref texture);
            if (texture != null && texture.Pointer != fx.Texture.Pointer)
            {
                // The game handed back a different texture: use that one everywhere.
                UnityEngine.Object.Destroy(fx.Texture);
                fx.Texture = texture;
                foreach (Material material in fx.Materials.Values)
                {
                    if (material != null) material.mainTexture = texture;
                }
            }
            if (setter.beamTextureRed != null) setter.ShiftTextureColors(color, setter.beamTextureRed, fx.Texture);

            fx.TextureColorKey = key;
            return true;
        }

        /// <summary>That player's gun colour for one hand (0 = left), with the same defaults as their gun models.</summary>
        private static Color RemoteGunColor(Remote remote, int hand)
        {
            MpPlayerState player = MpSession.Lobby?.FindPlayer(remote.UserId);
            MpGunColors colors = player != null ? player.gunColors : null;
            if (hand == 0) return ToColor(colors != null ? colors.left : null, DefaultLeftColor);
            return ToColor(colors != null ? colors.right : null, DefaultRightColor);
        }

        private static bool ShotFxAlive(ShotFx fx)
        {
            if (fx.Beams == null) return false;
            for (int i = 0; i < fx.Beams.Length; i++)
            {
                if (!IsAlive(fx.Beams[i])) return false;
            }
            return fx.Muzzle == null || IsAlive(fx.Muzzle);
        }

        private static void DestroyShotFx(ShotFx fx)
        {
            if (fx == null) return;
            try
            {
                if (fx.Beams != null)
                {
                    foreach (GunBeam beam in fx.Beams)
                    {
                        if (IsAlive(beam)) UnityEngine.Object.Destroy(beam.gameObject);
                    }
                }
                if (IsAlive(fx.Muzzle)) UnityEngine.Object.Destroy(fx.Muzzle.gameObject);
                foreach (Material material in fx.Materials.Values)
                {
                    if (IsAlive(material)) UnityEngine.Object.Destroy(material);
                }
                if (IsAlive(fx.Texture)) UnityEngine.Object.Destroy(fx.Texture);
            }
            catch (Exception ex)
            {
                Log($"destroying shot effects failed: {ex.Message}");
            }
            fx.Materials.Clear();
            fx.Beams = null;
            fx.Muzzle = null;
            fx.Texture = null;
        }

        private static string HandName(int hand)
        {
            return hand == 0 ? "L" : "R";
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
                remote.LFire = remote.LGun.Find(FirepointName);
                remote.RFire = remote.RGun.Find(FirepointName);
                remote.Hands[0] = MakeHandRig(remote.LHand);
                remote.Hands[1] = MakeHandRig(remote.RHand);
                for (int h = 0; h < 2; h++)
                {
                    Transform slide = FindChildNamed(h == 0 ? remote.LGun : remote.RGun, SlideName);
                    remote.Slide[h] = slide;
                    remote.SlideStart[h] = -1f;
                    if (slide == null) continue;
                    remote.SlideRestPos[h] = slide.localPosition;
                    remote.SlideRestRot[h] = slide.localRotation;
                }
                remote.Disc = CopyDisc(root);
                remote.NameHeadset = CreateNameLabel(root, "NameHeadset", remote.OwnedMaterials);
                remote.NameSpectator = CreateNameLabel(root, "NameSpectator", remote.OwnedMaterials);
                remote.AppliedName = null;

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
            for (int hand = 0; hand < 2; hand++)
            {
                DestroyShotFx(remote.Fx[hand]);
                remote.Fx[hand] = null;
            }
            remote.LFire = remote.RFire = null;
            for (int hand = 0; hand < 2; hand++)
            {
                remote.Hands[hand] = null;
                remote.Slide[hand] = null;
                remote.SlideStart[hand] = -1f;
            }

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
            remote.NameHeadset = remote.NameSpectator = null;
            remote.AppliedName = null;
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
                remote.ResetShots();
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

            // Hands: normalHand under HandLeft/HandRight.
            // v2.42: the hands keep their Animator, driven per remote player (UpdateHand), and each
            // side's open and fist finger poses are sampled from it once.
            tplHandL = MakeTemplate("TplHandL", localKataL.transform, handL.transform, true);
            tplHandR = MakeTemplate("TplHandR", localKataR.transform, handR.transform, true);
            fingerPoses[0] = SampleFingerPoses(tplHandL, "left hand");
            fingerPoses[1] = SampleFingerPoses(tplHandR, "right hand");

            // Guns: the default model, anchored on the same transform the pose is sent from
            // (tricks/mesh), so its offset doesn't include a trick that happens to be playing now.
            // Keeps its 0.021 scale; template-owned material copies so nothing points at the local
            // gun's instances.
            // (FindGunModel finds nothing mid-throw, so a build then fails above and is retried.)
            tplGunL = MakeTemplate("TplGunL", GunPoseTransform(localGunL, ref localGunPoseL), gunModelL);
            tplGunR = MakeTemplate("TplGunR", GunPoseTransform(localGunR, ref localGunPoseR), gunModelR);
            OwnMaterials(tplGunL);
            OwnMaterials(tplGunR);

            // Where shots leave from: the local gun's firepoint (GunX/tricks/firepoint), placed
            // relative to the same anchor as the model.
            AddFirepointMarker(tplGunL, localGunL, localGunPoseL);
            AddFirepointMarker(tplGunR, localGunR, localGunPoseR);

            // The slide's movement in the game's fire animation, replayed on remote shots.
            SampleGunSlide(localGunR);

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
        /// keepAnimator keeps the source's Animator (the hands, v2.42).
        /// </summary>
        private static GameObject MakeTemplate(string name, Transform anchor, Transform source, bool keepAnimator = false)
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

            Strip(copy, keepAnimator);
            return root;
        }

        /// <summary>
        /// One hand side's finger poses (v2.42), from the template's own Animator: a temporary active
        /// copy, both layers off and Pinch 0, evaluated at Flex 0 (open) and Flex 1 (fist), recording
        /// every finger bone's local rotation. Bones are matched to fingers by name
        /// (hands:b_r_thumb1..3, index1..3, middle1..3, ring1..3, pinky0..3; *_ignore skipped).
        /// Null, so finger curls fall back to HandAnim, when anything is missing or the two poses
        /// barely differ.
        /// </summary>
        private static FingerPoses SampleFingerPoses(GameObject template, string label)
        {
            if (template == null || !ReadAnimNames()) return null;
            GameObject probe = null;
            try
            {
                probe = UnityEngine.Object.Instantiate<GameObject>(template); // at the scene root, so active
                probe.name = "MpHandProbe";
                Animator animator = probe.GetComponentInChildren<Animator>(true);
                SkinnedMeshRenderer skin = probe.GetComponentInChildren<SkinnedMeshRenderer>(true);
                if (animator == null || skin == null)
                {
                    Log($"{label}: no Animator or skinned mesh to sample finger poses from");
                    return null;
                }
                // Same frame as the copy is made, never drawn: make sure the Animator is bound to its
                // controller and evaluates even though no renderer of it is visible.
                AnimatorCullingMode culling = animator.cullingMode;
                animator.enabled = true;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.applyRootMotion = false;
                animator.Rebind();
                Log($"{label}: probe animator controller={(animator.runtimeAnimatorController != null ? animator.runtimeAnimatorController.name : "none")} culling={(int)culling} layers={animator.layerCount} initialized={animator.isInitialized}");

                Transform[] bones = SkinBones(skin);
                FingerPoses poses = new FingerPoses
                {
                    Finger = new int[bones.Length],
                    Open = new Quaternion[bones.Length],
                    Fist = new Quaternion[bones.Length]
                };
                for (int i = 0; i < bones.Length; i++)
                {
                    poses.Finger[i] = FingerOf(bones[i]);
                    if (poses.Finger[i] >= 0) poses.FingerBones++;
                }

                // After Rebind, which puts parameters and weights back to their defaults.
                int pointLayer = animator.GetLayerIndex(animPointLayer);
                int thumbLayer = animator.GetLayerIndex(animThumbLayer);
                if (pointLayer >= 0) animator.SetLayerWeight(pointLayer, 0f);
                if (thumbLayer >= 0) animator.SetLayerWeight(thumbLayer, 0f);

                // For the log only: how far Pinch moves the fingers, in case Flex isn't what closes them.
                Quaternion[] pinched = new Quaternion[bones.Length];
                RecordBonePose(animator, bones, 0f, 1f, pinched);

                RecordBonePose(animator, bones, 0f, 0f, poses.Open);
                RecordBonePose(animator, bones, 1f, 0f, poses.Fist);

                float pinchAngle = 0f;
                for (int i = 0; i < bones.Length; i++)
                {
                    if (poses.Finger[i] < 0) continue;
                    poses.MaxAngle = Mathf.Max(poses.MaxAngle, Quaternion.Angle(poses.Open[i], poses.Fist[i]));
                    pinchAngle = Mathf.Max(pinchAngle, Quaternion.Angle(poses.Open[i], pinched[i]));
                }
                Log($"{label}: finger poses sampled ({poses.FingerBones} finger bones, up to {poses.MaxAngle:F0} deg between open and fist, {pinchAngle:F0} deg for pinch)");
                if (poses.FingerBones == 0 || poses.MaxAngle < FingerPoseMinAngle)
                {
                    Log($"{label}: open and fist poses too similar, finger tracking falls back to HandAnim");
                    return null;
                }
                return poses;
            }
            catch (Exception ex)
            {
                MelonLogger.Log($"[MpPresence] sampling {label} finger poses failed: {ex}");
                return null;
            }
            finally
            {
                if (probe != null) UnityEngine.Object.DestroyImmediate(probe);
            }
        }

        /// <summary>
        /// Sets Flex and Pinch and steps the Animator SampleSteps x SampleStepSeconds (so any
        /// transition or damping on the way gets there), then records the bones' local rotations.
        /// </summary>
        private static void RecordBonePose(Animator animator, Transform[] bones, float flex, float pinch, Quaternion[] dest)
        {
            for (int step = 0; step < SampleSteps; step++)
            {
                animator.SetFloat(animFlex, flex);
                animator.SetFloat(animPinch, pinch);
                animator.Update(SampleStepSeconds);
            }
            for (int i = 0; i < bones.Length; i++) dest[i] = bones[i] != null ? bones[i].localRotation : Quaternion.identity;
        }

        /// <summary>0 thumb, 1 index, 2 middle, 3 ring, 4 pinky, or -1 (the hand, grip, *_ignore and collider bones).</summary>
        private static int FingerOf(Transform bone)
        {
            if (bone == null) return -1;
            string name = bone.name.ToLowerInvariant();
            if (name.Contains("ignore") || name.Contains("coll")) return -1;
            if (name.Contains("thumb")) return 0;
            if (name.Contains("index")) return 1;
            if (name.Contains("middle")) return 2;
            if (name.Contains("ring")) return 3;
            if (name.Contains("pinky")) return 4;
            return -1;
        }

        private static Transform[] SkinBones(SkinnedMeshRenderer skin)
        {
            var bones = skin.bones;
            Transform[] result = new Transform[bones != null ? bones.Length : 0];
            for (int i = 0; i < result.Length; i++) result[i] = bones[i];
            return result;
        }

        /// <summary>
        /// Records the slide's movement in the game's gun_fire clip (0.62 s), at SlideSamplesPerSecond,
        /// by sampling the clip on the local gun itself. Every transform under the gun is put back
        /// afterwards (and the gun's Animator rewrites them this frame anyway), so nothing shows.
        /// Without the clip or the slide, remote slides just stay at rest.
        /// </summary>
        private static void SampleGunSlide(Gun gun)
        {
            slidePos = null;
            slideRot = null;
            slideLength = 0f;
            if (gun == null) return;

            Transform[] all = null;
            Vector3[] savedPos = null;
            Quaternion[] savedRot = null;
            try
            {
                Animator animator = gun.GetComponent<Animator>();
                if (animator == null) animator = gun.gameObject.GetComponentInChildren<Animator>(true);
                RuntimeAnimatorController controller = animator != null ? animator.runtimeAnimatorController : null;
                Transform model = FindGunModel(gun.transform);
                Transform slide = model != null ? FindChildNamed(model, SlideName) : null;
                if (controller == null || slide == null)
                {
                    Log($"gun slide: {(controller == null ? "no gun Animator controller" : "no " + SlideName)}, remote slides stay at rest");
                    return;
                }

                AnimationClip clip = null;
                var clips = controller.animationClips;
                for (int i = 0; clips != null && i < clips.Length; i++)
                {
                    if (clips[i] != null && clips[i].name == GunFireClipName) clip = clips[i];
                }
                if (clip == null || clip.length <= 0f)
                {
                    Log($"gun slide: no {GunFireClipName} clip, remote slides stay at rest");
                    return;
                }

                GameObject animated = animator.gameObject; // the clip's paths are relative to the Animator
                var transforms = animated.GetComponentsInChildren<Transform>(true);
                all = new Transform[transforms.Length];
                savedPos = new Vector3[all.Length];
                savedRot = new Quaternion[all.Length];
                for (int i = 0; i < all.Length; i++)
                {
                    all[i] = transforms[i];
                    savedPos[i] = all[i].localPosition;
                    savedRot[i] = all[i].localRotation;
                }

                int count = Mathf.Max(2, Mathf.CeilToInt(clip.length * SlideSamplesPerSecond) + 1);
                Vector3[] positions = new Vector3[count];
                Quaternion[] rotations = new Quaternion[count];
                for (int i = 0; i < count; i++)
                {
                    clip.SampleAnimation(animated, clip.length * i / (count - 1));
                    positions[i] = slide.localPosition;
                    rotations[i] = slide.localRotation;
                }

                float travel = 0f;
                for (int i = 0; i < count; i++) travel = Mathf.Max(travel, (positions[i] - positions[0]).magnitude);
                slidePos = positions;
                slideRot = rotations;
                slideLength = clip.length;
                Log($"gun slide: {count} samples over {clip.length:F3} s, travel {travel:F3} (local units)");
            }
            catch (Exception ex)
            {
                MelonLogger.Log($"[MpPresence] sampling the gun slide failed: {ex}");
                slidePos = null;
                slideRot = null;
            }
            finally
            {
                if (all != null)
                {
                    for (int i = 0; i < all.Length; i++)
                    {
                        if (all[i] == null) continue;
                        all[i].localPosition = savedPos[i];
                        all[i].localRotation = savedRot[i];
                    }
                }
            }
        }

        /// <summary>
        /// An empty FirepointName child of a gun template, placed relative to anchor (tricks/mesh)
        /// exactly as the local gun's firepoint is (measured 2026-10-29: local (0, -0.13, 0.74),
        /// no rotation, 1.55 cm from the mesh origin). Without a firepoint it sits on the root.
        /// </summary>
        private static void AddFirepointMarker(GameObject template, Gun gun, Transform anchor)
        {
            if (template == null) return;
            GameObject marker = new GameObject(FirepointName);
            marker.transform.SetParent(template.transform, false);

            Transform firepoint = gun != null ? gun.firepoint : null;
            if (firepoint == null || anchor == null)
            {
                Log($"{template.name}: no firepoint, shots leave from the gun's origin");
                return;
            }
            marker.transform.localPosition = anchor.InverseTransformPoint(firepoint.position);
            marker.transform.localRotation = Quaternion.Inverse(anchor.rotation) * firepoint.rotation;
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

        /// <summary>The first transform under root (root included) with that name.</summary>
        private static Transform FindChildNamed(Transform root, string name)
        {
            if (root == null) return null;
            var all = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < all.Length; i++)
            {
                if (all[i] != null && all[i].name == name) return all[i];
            }
            return null;
        }

        /// <summary>
        /// A remote hand copy's rig: its Animator (root motion off, since the hand's pose is set by
        /// MpPresence) and its skinned bones, in the same order as the sampled FingerPoses.
        /// </summary>
        private static HandRig MakeHandRig(Transform part)
        {
            HandRig rig = new HandRig();
            if (part == null) return rig;
            rig.Animator = part.gameObject.GetComponentInChildren<Animator>(true);
            if (rig.Animator != null)
            {
                rig.Animator.applyRootMotion = false;
                rig.Animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            }
            SkinnedMeshRenderer skin = part.gameObject.GetComponentInChildren<SkinnedMeshRenderer>(true);
            rig.Bones = skin != null ? SkinBones(skin) : null;
            return rig;
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
        private static void Strip(GameObject root, bool keepAnimator = false)
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
                    if (c == null) continue;
                    string type = TypeName(c);
                    if (KeepComponentTypes.Contains(type) || (keepAnimator && type == "Animator")) continue;
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
            sb.AppendLine($"[MpPresence] names: show={Config.ShowPlayerNames} spectatorCam={(spectatorCam != null ? "found" : "none")} split={splitNames}");
            sb.AppendLine($"[MpPresence] colliders: gunL={gunCollidersL} gunR={gunCollidersR} head={headColliders} body={bodyColliders}");
            sb.AppendLine($"[MpPresence] sending: {MpSession.PoseRateHz} Hz ({(MpSession.PoseRateEnabled ? "server" : "default")}), my shots L={localShots[0]} R={localShots[1]}, pull L={PullJson(localGunL)} R={PullJson(localGunR)}");
            bool knuckles = UsingKnuckles();
            sb.AppendLine($"[MpPresence] hands: knuckles={knuckles} handAnim L={JsonDump(HandAnimJson(localKataL, 0, knuckles))} R={JsonDump(HandAnimJson(localKataR, 1, knuckles))} fingers L={JsonDump(FingersJson(localKataL, knuckles))} R={JsonDump(FingersJson(localKataR, knuckles))}");
            sb.AppendLine($"[MpPresence] finger poses: L={FingerPoseDump(fingerPoses[0])} R={FingerPoseDump(fingerPoses[1])}, slide: {(slidePos != null ? slidePos.Length + " samples over " + slideLength.ToString("F3") + " s" : "none")}");
            long renderTime = MpSession.ServerNowMs - RenderDelayMs;
            foreach (Remote remote in remotes.Values)
            {
                MpPlayerState player = MpSession.Lobby?.FindPlayer(remote.UserId);
                long newest = remote.Samples.Count > 0 ? remote.Samples[remote.Samples.Count - 1].T : 0;
                sb.AppendLine($"[MpPresence]   {remote.UserId} \"{player?.nickname}\" seat={player?.seat} present={remote.Present} samples={remote.Samples.Count} " +
                              $"newest={(newest > 0 ? (newest - renderTime) + " ms ahead of render" : "-")} " +
                              $"shots L={ShotDump(remote, 0)} R={ShotDump(remote, 1)} fx L={FxDump(remote.Fx[0])} R={FxDump(remote.Fx[1])} pull L={remote.Pull[0]:F2} R={remote.Pull[1]:F2} hand L={HandDump(remote, 0)} R={HandDump(remote, 1)} slide L={remote.Slide[0] != null} R={remote.Slide[1] != null} built={remote.Root != null} visible={remote.Root != null && remote.Root.activeSelf} colors={player?.gunColors != null} name={(remote.NameHeadset != null && remote.NameHeadset.gameObject.activeSelf ? "headset@" + remote.NameHeadset.gameObject.layer : "off")}/{(remote.NameSpectator != null && remote.NameSpectator.gameObject.activeSelf ? "spectator" : "off")}");
            }
            foreach (string line in sb.ToString().TrimEnd().Split('\n')) MelonLogger.Log(line.TrimEnd('\r'));
        }

        private static string JsonDump(JToken token)
        {
            return token == null || token.Type == JTokenType.Null ? "null" : token.ToString(Newtonsoft.Json.Formatting.None);
        }

        private static string FingerPoseDump(FingerPoses poses)
        {
            return poses == null ? "none" : $"{poses.FingerBones} bones/{poses.MaxAngle:F0}deg";
        }

        /// <summary>For the debug dump: how a remote hand is driven ("anim", "fingers" or "-") and whether it has an Animator.</summary>
        private static string HandDump(Remote remote, int h)
        {
            HandRig rig = remote.Hands[h];
            if (rig == null) return "none";
            return $"{rig.Mode}(animator={rig.Animator != null} bones={(rig.Bones != null ? rig.Bones.Length : 0)})";
        }

        /// <summary>For the debug dump: whether a hand's shot effects exist and are intact.</summary>
        private static string FxDump(ShotFx fx)
        {
            if (fx == null) return "none";
            return (ShotFxAlive(fx) ? "ok" : "broken") + $"(muzzle={fx.Muzzle != null} tex={fx.Texture != null} color={fx.TextureColorKey ?? "-"})";
        }

        /// <summary>For the debug dump: shots fired, and the baseline (or "trigger" for the bool fallback).</summary>
        private static string ShotDump(Remote remote, int hand)
        {
            ShotState state = remote.Shot[hand];
            string tracking = state.HasBaseline ? "at " + state.Baseline : (state.HasLastTrigger ? "trigger" : "none");
            return $"{state.Fired} ({tracking})";
        }

        private static void Log(string message)
        {
            DebugLog.Log("[MpPresence] " + message);
        }

        /// <summary>
        /// Counts this player's shots (v2.40): Gun.UpdateTrigger returns true on the frame a press
        /// registers, which is when the gun fires (menus included). PreciseTiming has its own
        /// postfix on the same method; the two are independent, so this works with Precise Shot
        /// Timing off. Not Gun.Fire: patching that breaks shots that hit no target (PreciseTiming.cs).
        /// </summary>
        [HarmonyPatch(typeof(Gun), "UpdateTrigger")]
        private static class Gun_UpdateTrigger_CountShots
        {
            private static void Postfix(Gun __instance, bool __result)
            {
                if (!__result) return;
                try
                {
                    CountLocalShot(__instance);
                }
                catch (Exception ex)
                {
                    Log($"shot count failed: {ex.Message}");
                }
            }
        }
    }
}