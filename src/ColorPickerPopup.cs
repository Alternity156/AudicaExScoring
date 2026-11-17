using System;
using System.Collections;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;
using UnhollowerBaseLib;      // Il2CppStructArray, TryCast
using UnhollowerRuntimeLib;   // Il2CppType (ML 0.5.7)

namespace ExScoringMod
{
    /// <summary>
    /// Free-standing colour picker built from the game's own ShootableColorSelector prefab
    /// (OptionsMenu.colorSelectorPrefab), used the same way SearchKeyboard uses the keyboard.
    ///
    /// The native picker is a single hue strip (HSVToRGB(pos, 1, 1)), so it can only produce fully
    /// saturated, full-brightness colours. This popup instantiates the prefab several times, strips the
    /// ShootableColorSelector component off each copy (before its Start() runs, so it never hooks the
    /// button or touches TargetColorSetter) and drives the copies' GunButtons with our own callbacks.
    ///
    /// Layout:
    ///
    ///              [ Title, e.g. "Left Hand Color" ]
    ///   [════════════════ hue strip ════════════════]
    ///    ┌──────────┐      Before    After
    ///    │  shade   │     [██████][██████]    display only
    ///    │  square  │     [Cancel][Apply ]
    ///    │          │     [   Default    ]    hidden when no default is given
    ///    └──────────┘
    ///    Shoot the strip to pick a color, the square to pick its shade
    ///
    ///   - Hue strip    : native look (Kata/ColorStrip shader). Shoot to set the hue.
    ///   - Shade square : same prefab scaled square, Quad re-textured with a generated Texture2D.
    ///                    X = saturation, Y = brightness. Shoot to set both.
    ///   - Before/After : display-only swatches (their GunButton is disabled). After shows the picked
    ///                    colour, or the aimed-at one while aiming at the strip / square.
    ///   - Markers      : solid marker = current pick, grey ghost marker = where the aim is.
    ///   - Buttons      : Cancel (revert + close), Apply (confirm + close), Default (sets the slot's
    ///                    default colour as the current pick; Apply still needed). They brighten on aim.
    ///
    /// Placement reuses the search keyboard's position/tilt settings.
    /// </summary>
    public static class ColorPickerPopup
    {
        // ── Defaults (Audica's native gun colours) ─────────────────────────────────────────────
        public static readonly Color DefaultLeftHandColor = new Color(0f, 0.665f, 1f, 1f);
        public static readonly Color DefaultRightHandColor = new Color(1f, 0.467f, 0f, 1f);
        public static readonly Color DefaultEitherHandColor = Color.white;

        // ── Prefab geometry (from the UnityExplorer dump of ColorSelector) ─────────────────────
        // Quad child: localPosition (-0.04, -0.88, -0.01), localScale (17.27, 3.33, 1).
        private static readonly Vector3 PrefabQuadOffset = new Vector3(-0.04f, -0.88f, 0f);
        private static readonly Vector2 PrefabQuadSize = new Vector2(17.27f, 3.33f);

        // ── Layout, in popup-local prefab units (before the popup's overall scale) ─────────────
        private static readonly Vector3 TitleCenter = new Vector3(0f, 7.6f, 0f);

        private static readonly Vector3 HueStripCenter = new Vector3(0f, 4.5f, 0f);
        private static readonly Vector2 HueStripSize = new Vector2(17.27f, 3.33f);

        // Spans x -8.5..-0.5, y -6.75..1.25.
        private static readonly Vector3 SVSquareCenter = new Vector3(-4.5f, -2.75f, 0f);
        private static readonly Vector2 SVSquareSize = new Vector2(8f, 8f);

        // Right column spans x 0.75..8.45: two half-width columns centred at 2.65 and 6.55.
        private const float LeftColX = 2.65f;
        private const float RightColX = 6.55f;
        private const float ColumnCenterX = 4.6f;

        private static readonly Vector3 BeforeLabelCenter = new Vector3(LeftColX, 0.75f, 0f);
        private static readonly Vector3 AfterLabelCenter = new Vector3(RightColX, 0.75f, 0f);
        private static readonly Vector3 BeforeSwatchCenter = new Vector3(LeftColX, -1.0f, 0f);
        private static readonly Vector3 AfterSwatchCenter = new Vector3(RightColX, -1.0f, 0f);
        private static readonly Vector2 SwatchSize = new Vector2(3.8f, 2.4f); // the pair touches in the middle

        private static readonly Vector3 CancelButtonCenter = new Vector3(LeftColX, -3.55f, 0f);
        private static readonly Vector3 ApplyButtonCenter = new Vector3(RightColX, -3.55f, 0f);
        private static readonly Vector2 HalfButtonSize = new Vector2(3.7f, 1.7f);

        private static readonly Vector3 DefaultButtonCenter = new Vector3(ColumnCenterX, -5.85f, 0f);
        private static readonly Vector2 WideButtonSize = new Vector2(7.6f, 1.4f);

        private static readonly Vector3 HintCenter = new Vector3(0f, -8.0f, 0f);

        // Labels are cloned from OptionsMenu.buttonPrefab's "label" TextMeshPro (native font/style).
        // Uniform localScale of the labels (popup-local), tuned in UnityExplorer.
        private const float TitleScale = 1.5f;
        private const float SwatchLabelScale = 1.1f;
        private const float ButtonLabelScale = 1.0f;
        private const float HintScale = 0.85f;
        private const float LabelZ = -0.15f;

        private const float MarkerZ = -0.15f;          // in front of the quads (they face -Z)
        private const float GhostMarkerZ = -0.1f;      // just behind the real marker
        private const int SVTextureSize = 64;
        private const float HoverHoldSeconds = 0.1f;   // hover feedback lingers this long after aim leaves

        // Display-only dimming of the two swatches to tame the game's bloom. The colour returned /
        // saved is never dimmed — only what the swatch quads show.
        private const float SwatchDisplayFactor = 0.9f;

        // Button colours (same family as the song list's Create / Delete rows).
        private static readonly Color CancelColor = new Color(0.30f, 0.10f, 0.10f);
        private static readonly Color CancelHoverColor = new Color(0.55f, 0.18f, 0.18f);
        private static readonly Color ApplyColor = new Color(0.10f, 0.28f, 0.14f);
        private static readonly Color ApplyHoverColor = new Color(0.18f, 0.50f, 0.25f);
        private static readonly Color DefaultColor = new Color(0.20f, 0.20f, 0.24f);
        private static readonly Color DefaultHoverColor = new Color(0.36f, 0.36f, 0.42f);

        private static readonly Color GhostOuterColor = new Color(0.6f, 0.6f, 0.6f);
        private static readonly Color GhostInnerColor = new Color(0.15f, 0.15f, 0.15f);

        private const string DefaultTitle = "Pick a Color";
        private const string HintText = "Shoot the strip to pick a color, the square to pick its shade";

        // Uniform scale of the popup root (world scale, since it sits at the scene root).
        private const float PopupScale = 0.1f;

        // ── State ──────────────────────────────────────────────────────────────────────────────
        private class Piece
        {
            public GameObject go;
            public Transform quad;
            public MeshRenderer quadRenderer;
            public GunButton button;      // null for display-only pieces
            public Material material;     // buttons and swatches: their own Unlit/Color material
            public Color baseColor;       // buttons: idle colour
            public Color hoverColor;      // buttons: aimed-at colour
            public GameObject label;      // buttons: their text (a root child, so it's toggled with the piece)
            public float lastHoverTime = -999f;
        }

        private static GameObject root;
        private static Piece hueStrip;
        private static Piece svSquare;
        private static Piece beforeSwatch;
        private static Piece afterSwatch;
        private static Piece cancelButton;
        private static Piece applyButton;
        private static Piece defaultButton;
        private static Transform hueMarker;
        private static Transform svMarker;
        private static Transform hueGhost;
        private static Transform svGhost;
        private static TMPro.TextMeshPro titleText;

        private static Texture2D svTexture;
        private static Il2CppStructArray<Color> svPixels;
        private static Material svMaterial;

        // Keep the managed delegates alive for as long as the Il2Cpp side references them.
        private static readonly List<Delegate> keepAlive = new List<Delegate>();

        private static float hue, sat, val;
        private static float lastBuiltHue = -1f;
        private static Color originalColor;
        private static Color? defaultColor;

        // Live preview of the aimed-at colour (After swatch) and the ghost markers.
        private static Color hoverColor;
        private static float lastHoverTime = -999f;
        private static Vector2 hueHoverUV, svHoverUV;
        private static float hueHoverTime = -999f, svHoverTime = -999f;

        private static Action<Color> onChangedCb;
        private static Action<Color> onDoneCb;
        private static Action onCancelCb;
        private static object tickRoutine;

        public static bool IsOpen => root != null && root.activeSelf;

        public static Color CurrentColor => Color.HSVToRGB(hue, sat, val);

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  Public API
        // ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Generic picker. onChanged fires on every committed pick (hue strip, shade square or
        /// Default), onDone when Apply is shot, onCancel when Cancel is shot (or the popup is closed
        /// from outside via CancelIfOpen). title is shown above the picker; defaultColor, when
        /// given, enables the Default button.
        /// </summary>
        public static void Show(Color initial, Action<Color> onChanged, Action<Color> onDone, Action onCancel,
            string title = null, Color? defaultColor = null)
        {
            if (IsOpen) CancelIfOpen(); // close any previous session cleanly first

            EnsureBuilt();
            if (root == null) return;

            originalColor = initial;
            ColorPickerPopup.defaultColor = defaultColor;
            Color.RGBToHSV(initial, out hue, out sat, out val);
            onChangedCb = onChanged;
            onDoneCb = onDone;
            onCancelCb = onCancel;
            ResetHover();

            if (titleText != null) titleText.text = string.IsNullOrEmpty(title) ? DefaultTitle : title;
            SetPieceActive(defaultButton, defaultColor.HasValue);

            ApplyTransform();
            root.SetActive(true);
            SetButtonsInteractable();

            if (beforeSwatch != null) beforeSwatch.material.color = SwatchDisplay(originalColor);
            foreach (var b in new[] { cancelButton, applyButton, defaultButton })
                if (b != null) b.material.color = b.baseColor;
            RefreshVisuals(true);

            tickRoutine = MelonCoroutines.Start(Tick());
            DebugLog.Log("[ColorPicker] shown: " + (title ?? DefaultTitle));
        }

        /// <summary>
        /// Hand-colour mode. Picks are previewed live in-game with TargetColorSetter.SetColors(l, r, true)
        /// (writes KataConfig + prefs in memory, no disk save), confirmed with SetColors(l, r, false)
        /// (full apply + SaveIO.SavePrefsDelayed), and reverted on cancel.
        /// onChanged / onClosed are optional UI hooks (e.g. to re-tint an options row).
        /// </summary>
        public static void ShowHand(Target.TargetHandType hand, Action<Color> onChanged = null, Action onClosed = null)
        {
            if (KataConfig.I == null) { DebugLog.Log("[ColorPicker] KataConfig.I is null"); return; }
            if (hand != Target.TargetHandType.Left && hand != Target.TargetHandType.Right)
            {
                DebugLog.Log($"[ColorPicker] ShowHand: unsupported hand {hand}, use ShowEitherHand");
                return;
            }

            bool left = hand == Target.TargetHandType.Left;
            Color origLeft = KataConfig.I.leftHandColor;
            Color origRight = KataConfig.I.rightHandColor;
            Color initial = left ? origLeft : origRight;

            Show(initial,
                c =>
                {
                    Color l = left ? c : KataConfig.I.leftHandColor;
                    Color r = !left ? c : KataConfig.I.rightHandColor;
                    SetHandColors(l, r, true);
                    onChanged?.Invoke(c);
                },
                c =>
                {
                    Color l = left ? c : KataConfig.I.leftHandColor;
                    Color r = !left ? c : KataConfig.I.rightHandColor;
                    SetHandColors(l, r, false);
                    onClosed?.Invoke();
                },
                () =>
                {
                    // simpleChange=true always applies (no early-out), restoring memory/prefs; the
                    // follow-up false call then does the full visual refresh if anything differs.
                    SetHandColors(origLeft, origRight, true);
                    SetHandColors(origLeft, origRight, false);
                    onClosed?.Invoke();
                },
                left ? "Left Hand Color" : "Right Hand Color",
                left ? DefaultLeftHandColor : DefaultRightHandColor);
        }

        /// <summary>
        /// Either-hand colour mode. Picks are previewed live through
        /// ExScoring.EitherHandColorOverride + RefreshEitherHandColors (no save), confirmed into
        /// Config.EitherHandColor (saved), and the preview is cleared on cancel. The colour is only
        /// visible in-game while Config.OverrideMeleeColor is on (the picker never turns it on).
        /// </summary>
        public static void ShowEitherHand(Action<Color> onChanged = null, Action onClosed = null)
        {
            Color initial = Config.EitherHandColor;

            Show(initial,
                c =>
                {
                    ExScoring.EitherHandColorOverride = c;
                    ExScoring.RefreshEitherHandColors();
                    onChanged?.Invoke(c);
                },
                c =>
                {
                    Config.UpdateEitherHandColor(c);
                    ExScoring.EitherHandColorOverride = null;
                    ExScoring.RefreshEitherHandColors();
                    onClosed?.Invoke();
                },
                () =>
                {
                    ExScoring.EitherHandColorOverride = null;
                    ExScoring.RefreshEitherHandColors();
                    onClosed?.Invoke();
                },
                "Either Hand Color",
                DefaultEitherHandColor);
        }

        /// <summary>Closes the popup as a cancel, if it's open. Safe to call any time.</summary>
        public static void CancelIfOpen()
        {
            if (!IsOpen) return;
            var cb = onCancelCb;
            Close();
            cb?.Invoke();
        }

        /// <summary>Re-applies the configured position/tilt (shared with the search keyboard).</summary>
        public static void ApplyTransform()
        {
            if (root == null) return;
            root.transform.position = new Vector3(Config.SearchKeyboardPosX, Config.SearchKeyboardPosY, Config.SearchKeyboardPosZ);
            root.transform.rotation = Quaternion.Euler(Config.SearchKeyboardTilt, 0f, 0f);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  Building
        // ══════════════════════════════════════════════════════════════════════════════════════

        private static OptionsMenu FindMenuWithPrefab()
        {
            if (OptionsMenuClone.Menu != null && OptionsMenuClone.Menu.colorSelectorPrefab != null) return OptionsMenuClone.Menu;
            if (SongSearchScreen.primaryMenu != null && SongSearchScreen.primaryMenu.colorSelectorPrefab != null) return SongSearchScreen.primaryMenu;
            if (OptionsMenu.I != null && OptionsMenu.I.colorSelectorPrefab != null) return OptionsMenu.I;

            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<OptionsMenu>()))
            {
                var m = o.TryCast<OptionsMenu>();
                if (m != null && m.colorSelectorPrefab != null) return m;
            }
            return null;
        }

        private static void EnsureBuilt()
        {
            if (root != null) return; // Unity-null check: rebuilds after a scene change

            OptionsMenu menu = FindMenuWithPrefab();
            if (menu == null)
            {
                MelonLogger.Log("[ColorPicker] No OptionsMenu with colorSelectorPrefab found");
                return;
            }
            GameObject prefab = menu.colorSelectorPrefab;

            Shader unlitTexture = Shader.Find("Unlit/Texture");
            Shader unlitColor = Shader.Find("Unlit/Color");
            if (unlitTexture == null || unlitColor == null)
            {
                MelonLogger.Log("[ColorPicker] Unlit/Texture or Unlit/Color shader missing");
                return;
            }

            keepAlive.Clear();
            titleText = null;

            root = new GameObject("ExScoring_ColorPicker");
            root.SetActive(false); // children's Awake/Start wait until Show — lets us strip components first
            root.transform.SetParent(null, false);

            root.transform.localScale = new Vector3(PopupScale, PopupScale, PopupScale);
            DebugLog.Log("[ColorPicker] building, scale " + PopupScale.ToString("F3"));

            Transform labelSource = menu.buttonPrefab != null ? menu.buttonPrefab.transform.Find("label") : null;
            if (labelSource == null)
                MelonLogger.Log("[ColorPicker] buttonPrefab label not found; text labels skipped");

            // Hue strip — keeps the native Kata/ColorStrip material.
            hueStrip = CreatePiece(prefab, "HueStrip", HueStripCenter, HueStripSize, true, OnHueHit, OnHueHover);

            // Shade square — re-textured Quad.
            svSquare = CreatePiece(prefab, "SVSquare", SVSquareCenter, SVSquareSize, true, OnSVHit, OnSVHover);
            svTexture = new Texture2D(SVTextureSize, SVTextureSize, TextureFormat.RGBA32, false);
            svTexture.wrapMode = TextureWrapMode.Clamp;
            svTexture.filterMode = FilterMode.Bilinear;
            svPixels = new Il2CppStructArray<Color>(SVTextureSize * SVTextureSize);
            svMaterial = new Material(unlitTexture);
            svMaterial.mainTexture = svTexture;
            if (svSquare != null) svSquare.quadRenderer.material = svMaterial;

            // Before / After swatches — display only.
            beforeSwatch = CreatePiece(prefab, "BeforeSwatch", BeforeSwatchCenter, SwatchSize, false, null, null);
            afterSwatch = CreatePiece(prefab, "AfterSwatch", AfterSwatchCenter, SwatchSize, false, null, null);
            foreach (var s in new[] { beforeSwatch, afterSwatch })
            {
                if (s == null) continue;
                s.material = new Material(unlitColor);
                s.quadRenderer.material = s.material;
            }

            // Buttons.
            cancelButton = CreateButton(prefab, labelSource, unlitColor, "CancelButton", "Cancel",
                CancelButtonCenter, HalfButtonSize, CancelColor, CancelHoverColor, OnCancelHit);
            applyButton = CreateButton(prefab, labelSource, unlitColor, "ApplyButton", "Apply",
                ApplyButtonCenter, HalfButtonSize, ApplyColor, ApplyHoverColor, OnApplyHit);
            defaultButton = CreateButton(prefab, labelSource, unlitColor, "DefaultButton", "Default",
                DefaultButtonCenter, WideButtonSize, DefaultColor, DefaultHoverColor, OnDefaultHit);

            // Text.
            if (labelSource != null)
            {
                var title = CreateLabel(labelSource, "Title", DefaultTitle, TitleCenter, TitleScale);
                if (title != null) titleText = title.GetComponent<TMPro.TextMeshPro>();
                CreateLabel(labelSource, "BeforeLabel", "Before", BeforeLabelCenter, SwatchLabelScale);
                CreateLabel(labelSource, "AfterLabel", "After", AfterLabelCenter, SwatchLabelScale);
                CreateLabel(labelSource, "Hint", HintText, HintCenter, HintScale);
            }

            // Markers — reuse the prefab's own Quad mesh (no collider, so they never block shots).
            // Ghosts first so the real markers draw over them when they overlap.
            Transform quadSource = hueStrip != null ? hueStrip.quad : null;
            if (quadSource != null)
            {
                hueGhost = CreateMarker(quadSource, unlitColor, "HueGhost", new Vector2(0.3f, 3.7f), new Vector2(0.12f, 3.4f), GhostOuterColor, GhostInnerColor);
                svGhost = CreateMarker(quadSource, unlitColor, "SVGhost", new Vector2(0.6f, 0.6f), new Vector2(0.34f, 0.34f), GhostOuterColor, GhostInnerColor);
                hueMarker = CreateMarker(quadSource, unlitColor, "HueMarker", new Vector2(0.35f, 3.9f), new Vector2(0.15f, 3.5f), Color.white, Color.black);
                svMarker = CreateMarker(quadSource, unlitColor, "SVMarker", new Vector2(0.7f, 0.7f), new Vector2(0.4f, 0.4f), Color.white, Color.black);
                hueGhost.gameObject.SetActive(false);
                svGhost.gameObject.SetActive(false);
            }

            lastBuiltHue = -1f;
        }

        /// <summary>
        /// One copy of the prefab with its visible Quad centred on 'center' and sized 'size'.
        /// shootable=false disables the copy's Button child, so the piece is display only (no
        /// collider to catch shots, no hover haptics).
        /// </summary>
        private static Piece CreatePiece(GameObject prefab, string name, Vector3 center, Vector2 size, bool shootable,
            Action<Vector3, Gun> onHit, Action<Vector3, Gun> onHover)
        {
            GameObject go = GameObject.Instantiate(prefab, root.transform, false);
            go.name = name;

            // Strip the native logic before its Start() runs (root is inactive, so it hasn't).
            var native = go.GetComponent<ShootableColorSelector>();
            if (native != null) GameObject.DestroyImmediate(native);

            var scale = new Vector3(size.x / PrefabQuadSize.x, size.y / PrefabQuadSize.y, 1f);
            go.transform.localScale = scale;
            go.transform.localRotation = Quaternion.identity;
            // Offset so the visible Quad (not the prefab root) lands on 'center'.
            go.transform.localPosition = center - Vector3.Scale(scale, PrefabQuadOffset);

            var piece = new Piece { go = go };
            piece.quad = go.transform.Find("Quad");
            if (piece.quad != null) piece.quadRenderer = piece.quad.GetComponent<MeshRenderer>();

            Transform buttonT = go.transform.Find("Button");
            if (buttonT != null && shootable) piece.button = buttonT.GetComponent<GunButton>();

            if (piece.quad == null || piece.quadRenderer == null || buttonT == null || (shootable && piece.button == null))
            {
                MelonLogger.Log($"[ColorPicker] {name}: prefab layout unexpected (quad={piece.quad != null}, button={buttonT != null})");
                return null;
            }

            if (!shootable)
            {
                buttonT.gameObject.SetActive(false);
                return piece;
            }

            GunButton gb = piece.button;
            gb.destroyOnShot = false;
            gb.disableOnShot = false;
            gb.doMeshExplosion = false;
            gb.doParticles = false;

            // Delegates aren't serialized, so the clone's button starts with no callbacks — these are the only ones.
            if (onHit != null)
            {
                keepAlive.Add(onHit);
                gb.OnHitCallback = onHit;
            }
            if (onHover != null)
            {
                keepAlive.Add(onHover);
                gb.HoveringCallback = onHover;
            }

            return piece;
        }

        /// <summary>A flat coloured button with a centred label that brightens while aimed at.</summary>
        private static Piece CreateButton(GameObject prefab, Transform labelSource, Shader shader, string name, string text,
            Vector3 center, Vector2 size, Color baseColor, Color hoverColor, Action onShot)
        {
            Piece piece = null;
            Action<Vector3, Gun> hit = (pos, gun) => { if (IsOpen) onShot(); };
            Action<Vector3, Gun> hover = (pos, gun) => { if (piece != null) piece.lastHoverTime = Time.time; };

            piece = CreatePiece(prefab, name, center, size, true, hit, hover);
            if (piece == null) return null;

            piece.baseColor = baseColor;
            piece.hoverColor = hoverColor;
            piece.material = new Material(shader);
            piece.material.color = baseColor;
            piece.quadRenderer.material = piece.material;

            if (labelSource != null)
                piece.label = CreateLabel(labelSource, name + "Label", text, center, ButtonLabelScale);

            return piece;
        }

        private static Transform CreateMarker(Transform quadSource, Shader shader, string name, Vector2 outerSize, Vector2 innerSize,
            Color outerColor, Color innerColor)
        {
            var marker = new GameObject(name).transform;
            marker.SetParent(root.transform, false);

            var outer = GameObject.Instantiate(quadSource.gameObject, marker, false);
            outer.name = "Outer";
            outer.transform.localPosition = Vector3.zero;
            outer.transform.localRotation = Quaternion.identity;
            outer.transform.localScale = new Vector3(outerSize.x, outerSize.y, 1f);
            var outerMat = new Material(shader); outerMat.color = outerColor;
            outer.GetComponent<MeshRenderer>().material = outerMat;

            var inner = GameObject.Instantiate(quadSource.gameObject, marker, false);
            inner.name = "Inner";
            inner.transform.localPosition = new Vector3(0f, 0f, -0.02f);
            inner.transform.localRotation = Quaternion.identity;
            inner.transform.localScale = new Vector3(innerSize.x, innerSize.y, 1f);
            var innerMat = new Material(shader); innerMat.color = innerColor;
            inner.GetComponent<MeshRenderer>().material = innerMat;

            return marker;
        }

        private static GameObject CreateLabel(Transform source, string name, string text, Vector3 center, float scale)
        {
            GameObject go = GameObject.Instantiate(source.gameObject, root.transform, false);
            go.name = name;

            // In case a Localizer rides along, remove it so it can't overwrite our text.
            var loc = go.GetComponent<Localizer>();
            if (loc != null) GameObject.DestroyImmediate(loc);

            go.transform.localPosition = new Vector3(center.x, center.y, LabelZ);
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = new Vector3(scale, scale, scale);

            var tmp = go.GetComponent<TMPro.TextMeshPro>();
            if (tmp == null) return go;
            tmp.text = text;
            tmp.alignment = TMPro.TextAlignmentOptions.Center;
            tmp.enableWordWrapping = false;

            // Centre the rect on the position so the alignment is around 'center'.
            var rt = go.GetComponent<RectTransform>();
            if (rt != null)
            {
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.localPosition = new Vector3(center.x, center.y, LabelZ);
            }
            return go;
        }

        private static void SetButtonsInteractable()
        {
            foreach (var p in new[] { hueStrip, svSquare, cancelButton, applyButton, defaultButton })
                if (p != null && p.button != null) p.button.SetInteractable(true);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  Input
        // ══════════════════════════════════════════════════════════════════════════════════════

        /// <summary>Hit point → (u, v) in 0..1 across the visible Quad (UV space).</summary>
        private static Vector2 QuadUV(Piece piece, Vector3 worldPos)
        {
            Vector3 lp = piece.quad.InverseTransformPoint(worldPos);
            return new Vector2(Mathf.Clamp01(lp.x + 0.5f), Mathf.Clamp01(lp.y + 0.5f));
        }

        private static void OnHueHit(Vector3 pos, Gun gun)
        {
            if (!IsOpen || hueStrip == null) return;
            hue = QuadUV(hueStrip, pos).x;
            Commit();
        }

        private static void OnHueHover(Vector3 pos, Gun gun)
        {
            if (!IsOpen || hueStrip == null) return;
            float h = QuadUV(hueStrip, pos).x;
            hueHoverUV = new Vector2(h, 0.5f);
            hueHoverTime = Time.time;
            SetHover(Color.HSVToRGB(h, sat, val));
        }

        private static void OnSVHit(Vector3 pos, Gun gun)
        {
            if (!IsOpen || svSquare == null) return;
            Vector2 uv = QuadUV(svSquare, pos);
            sat = uv.x;
            val = uv.y;
            Commit();
        }

        private static void OnSVHover(Vector3 pos, Gun gun)
        {
            if (!IsOpen || svSquare == null) return;
            Vector2 uv = QuadUV(svSquare, pos);
            svHoverUV = uv;
            svHoverTime = Time.time;
            SetHover(Color.HSVToRGB(hue, uv.x, uv.y));
        }

        private static void OnCancelHit()
        {
            DebugLog.Log("[ColorPicker] cancel");
            CancelIfOpen();
        }

        private static void OnApplyHit()
        {
            Color c = CurrentColor;
            var cb = onDoneCb;
            DebugLog.Log("[ColorPicker] apply " + FormatColor(c));
            Close();
            cb?.Invoke(c);
        }

        private static void OnDefaultHit()
        {
            if (!defaultColor.HasValue) return;
            Color.RGBToHSV(defaultColor.Value, out hue, out sat, out val);
            DebugLog.Log("[ColorPicker] default " + FormatColor(defaultColor.Value));
            Commit();
        }

        private static void SetHover(Color c)
        {
            hoverColor = c;
            lastHoverTime = Time.time;
        }

        private static void ResetHover()
        {
            lastHoverTime = -999f;
            hueHoverTime = -999f;
            svHoverTime = -999f;
            foreach (var b in new[] { cancelButton, applyButton, defaultButton })
                if (b != null) b.lastHoverTime = -999f;
        }

        private static void Commit()
        {
            // Show the committed colour and hide the ghosts immediately.
            lastHoverTime = -999f;
            hueHoverTime = -999f;
            svHoverTime = -999f;
            RefreshVisuals(false);
            onChangedCb?.Invoke(CurrentColor);
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  Visuals
        // ══════════════════════════════════════════════════════════════════════════════════════

        private static void RefreshVisuals(bool force)
        {
            if (force || !Mathf.Approximately(hue, lastBuiltHue))
                RebuildSVTexture();

            if (afterSwatch != null) afterSwatch.material.color = SwatchDisplay(CurrentColor);

            if (hueMarker != null && hueStrip != null)
                PlaceMarker(hueMarker, hueStrip, new Vector2(hue, 0.5f), MarkerZ);
            if (svMarker != null && svSquare != null)
                PlaceMarker(svMarker, svSquare, new Vector2(sat, val), MarkerZ);
        }

        private static void RebuildSVTexture()
        {
            if (svTexture == null || svPixels == null) return;
            int n = SVTextureSize;
            for (int y = 0; y < n; y++)
            {
                float v = y / (float)(n - 1);
                for (int x = 0; x < n; x++)
                {
                    float s = x / (float)(n - 1);
                    svPixels[y * n + x] = Color.HSVToRGB(hue, s, v);
                }
            }
            svTexture.SetPixels(svPixels);
            svTexture.Apply();
            lastBuiltHue = hue;
        }

        private static void PlaceMarker(Transform marker, Piece piece, Vector2 uv, float z)
        {
            Vector3 world = piece.quad.TransformPoint(new Vector3(uv.x - 0.5f, uv.y - 0.5f, 0f));
            marker.position = world;
            Vector3 lp = marker.localPosition;
            marker.localPosition = new Vector3(lp.x, lp.y, z);
            marker.localRotation = Quaternion.identity;
        }

        private static void UpdateGhost(Transform ghost, Piece piece, Vector2 uv, bool visible)
        {
            if (ghost == null || piece == null) return;
            if (ghost.gameObject.activeSelf != visible) ghost.gameObject.SetActive(visible);
            if (visible) PlaceMarker(ghost, piece, uv, GhostMarkerZ);
        }

        private static void UpdateButtonTint(Piece b, float now)
        {
            if (b == null || b.material == null) return;
            bool hovered = now - b.lastHoverTime <= HoverHoldSeconds;
            b.material.color = hovered ? b.hoverColor : b.baseColor;
        }

        private static IEnumerator Tick()
        {
            while (IsOpen)
            {
                float now = Time.time;

                if (afterSwatch != null)
                {
                    bool hovering = now - lastHoverTime <= HoverHoldSeconds;
                    afterSwatch.material.color = SwatchDisplay(hovering ? hoverColor : CurrentColor);
                }

                UpdateGhost(hueGhost, hueStrip, hueHoverUV, now - hueHoverTime <= HoverHoldSeconds);
                UpdateGhost(svGhost, svSquare, svHoverUV, now - svHoverTime <= HoverHoldSeconds);

                UpdateButtonTint(cancelButton, now);
                UpdateButtonTint(applyButton, now);
                UpdateButtonTint(defaultButton, now);

                yield return null;
            }
            tickRoutine = null;
        }

        // ══════════════════════════════════════════════════════════════════════════════════════
        //  Helpers
        // ══════════════════════════════════════════════════════════════════════════════════════

        private static void Close()
        {
            if (tickRoutine != null) { MelonCoroutines.Stop(tickRoutine); tickRoutine = null; }
            onChangedCb = null;
            onDoneCb = null;
            onCancelCb = null;
            if (hueGhost != null) hueGhost.gameObject.SetActive(false);
            if (svGhost != null) svGhost.gameObject.SetActive(false);
            if (root != null) root.SetActive(false);
        }

        private static void SetPieceActive(Piece p, bool active)
        {
            if (p == null) return;
            if (p.go != null && p.go.activeSelf != active) p.go.SetActive(active);
            if (p.label != null && p.label.activeSelf != active) p.label.SetActive(active);
        }

        private static void SetHandColors(Color left, Color right, bool simpleChange)
        {
            var setter = TargetColorSetter.I;
            if (setter == null) { DebugLog.Log("[ColorPicker] TargetColorSetter.I is null"); return; }
            setter.SetColors(left, right, simpleChange);
        }

        /// <summary>Dims RGB by SwatchDisplayFactor (alpha untouched) for swatch display only.</summary>
        private static Color SwatchDisplay(Color c)
        {
            return new Color(c.r * SwatchDisplayFactor, c.g * SwatchDisplayFactor, c.b * SwatchDisplayFactor, c.a);
        }

        // Unhollowed struct ToString(format) is broken — format components manually.
        private static string FormatColor(Color c)
        {
            return "(" + c.r.ToString("F3") + ", " + c.g.ToString("F3") + ", " + c.b.ToString("F3") + ")";
        }
    }
}