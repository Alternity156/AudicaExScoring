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
    /// button or touches TargetColorSetter) and drives the copies' GunButtons with our own callbacks:
    ///
    ///   - Hue strip      : native look (Kata/ColorStrip shader). Shoot to set the hue.
    ///   - S/V square     : same prefab scaled square, Quad re-textured with a generated Texture2D.
    ///                      X = saturation, Y = brightness. Shoot to set both.
    ///   - Original swatch: shows the starting colour. Shoot to CANCEL (revert + close).
    ///   - New swatch     : shows the picked colour (or the hovered one, live). Shoot to CONFIRM.
    ///   - Markers        : small quads showing the current hue / S-V point.
    ///
    /// Placement reuses the search keyboard's position/tilt settings.
    /// </summary>
    public static class ColorPickerPopup
    {
        // ── Prefab geometry (from the UnityExplorer dump of ColorSelector) ─────────────────────
        // Quad child: localPosition (-0.04, -0.88, -0.01), localScale (17.27, 3.33, 1).
        private static readonly Vector3 PrefabQuadOffset = new Vector3(-0.04f, -0.88f, 0f);
        private static readonly Vector2 PrefabQuadSize = new Vector2(17.27f, 3.33f);

        // ── Layout, in popup-local prefab units (before the popup's overall scale) ─────────────
        private static readonly Vector3 HueStripCenter = new Vector3(0f, 4.5f, 0f);
        private static readonly Vector2 HueStripSize = new Vector2(17.27f, 3.33f);

        private static readonly Vector3 SVSquareCenter = new Vector3(-4.5f, -2.75f, 0f);
        private static readonly Vector2 SVSquareSize = new Vector2(8f, 8f);

        // Right column: "Old" label, Old swatch, "New" label, New swatch.
        private static readonly Vector3 OldLabelCenter = new Vector3(5f, 1f, 0f);
        private static readonly Vector3 OriginalSwatchCenter = new Vector3(5f, -0.75f, 0f);
        private static readonly Vector3 NewLabelCenter = new Vector3(5f, -2.6f, 0f);
        private static readonly Vector3 NewSwatchCenter = new Vector3(5f, -4.25f, 0f);
        private static readonly Vector2 SwatchSize = new Vector2(5f, 2.5f);

        // Labels are cloned from OptionsMenu.buttonPrefab's "label" TextMeshPro (native font/style).
        // Uniform localScale of the labels (popup-local), tuned in UnityExplorer.
        private const float LabelScale = 1.25f;
        private const float LabelZ = -0.15f;

        private const float MarkerZ = -0.15f;          // in front of the quads (they face -Z)
        private const int SVTextureSize = 64;
        private const float HoverHoldSeconds = 0.1f;   // hover preview lingers this long after aim leaves

        // Display-only dimming of the two swatches to tame the game's bloom. The colour returned /
        // saved is never dimmed — only what the swatch quads show.
        private const float SwatchDisplayFactor = 0.9f;

        // Uniform scale of the popup root (world scale, since it sits at the scene root).
        private const float PopupScale = 0.1f;

        // ── State ──────────────────────────────────────────────────────────────────────────────
        private class Piece
        {
            public GameObject go;
            public Transform quad;
            public MeshRenderer quadRenderer;
            public GunButton button;
        }

        private static GameObject root;
        private static Piece hueStrip;
        private static Piece svSquare;
        private static Piece originalSwatch;
        private static Piece newSwatch;
        private static Transform hueMarker;
        private static Transform svMarker;

        private static Texture2D svTexture;
        private static Il2CppStructArray<Color> svPixels;
        private static Material svMaterial;
        private static Material originalMaterial;
        private static Material newMaterial;

        // Keep the managed delegates alive for as long as the Il2Cpp side references them.
        private static readonly List<Delegate> keepAlive = new List<Delegate>();

        private static float hue, sat, val;
        private static float lastBuiltHue = -1f;
        private static Color originalColor;
        private static Color hoverColor;
        private static float lastHoverTime = -999f;

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
        /// Generic picker. onChanged fires on every committed pick (hue strip or S/V square shot),
        /// onDone when the New swatch is shot, onCancel when the Original swatch is shot (or the
        /// popup is closed from outside via CancelIfOpen).
        /// </summary>
        public static void Show(Color initial, Action<Color> onChanged, Action<Color> onDone, Action onCancel)
        {
            if (IsOpen) CancelIfOpen(); // close any previous session cleanly first

            EnsureBuilt();
            if (root == null) return;

            originalColor = initial;
            Color.RGBToHSV(initial, out hue, out sat, out val);
            onChangedCb = onChanged;
            onDoneCb = onDone;
            onCancelCb = onCancel;
            lastHoverTime = -999f;

            ApplyTransform();
            root.SetActive(true);
            SetButtonsInteractable();

            if (originalMaterial != null) originalMaterial.color = SwatchDisplay(originalColor);
            RefreshVisuals(true);

            tickRoutine = MelonCoroutines.Start(Tick());
            DebugLog.Log("[ColorPicker] shown");
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

            Color origLeft = KataConfig.I.leftHandColor;
            Color origRight = KataConfig.I.rightHandColor;
            Color initial = hand == Target.TargetHandType.Left ? origLeft : origRight;

            Show(initial,
                c =>
                {
                    Color l = hand == Target.TargetHandType.Left ? c : KataConfig.I.leftHandColor;
                    Color r = hand == Target.TargetHandType.Right ? c : KataConfig.I.rightHandColor;
                    SetHandColors(l, r, true);
                    onChanged?.Invoke(c);
                },
                c =>
                {
                    Color l = hand == Target.TargetHandType.Left ? c : KataConfig.I.leftHandColor;
                    Color r = hand == Target.TargetHandType.Right ? c : KataConfig.I.rightHandColor;
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
                });
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
                });
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

            root = new GameObject("ExScoring_ColorPicker");
            root.SetActive(false); // children's Awake/Start wait until Show — lets us strip components first
            root.transform.SetParent(null, false);

            root.transform.localScale = new Vector3(PopupScale, PopupScale, PopupScale);
            DebugLog.Log("[ColorPicker] building, scale " + PopupScale.ToString("F3"));

            // Hue strip — keeps the native Kata/ColorStrip material.
            hueStrip = CreatePiece(prefab, "HueStrip", HueStripCenter, HueStripSize, OnHueHit, OnHueHover);

            // S/V square — re-textured Quad.
            svSquare = CreatePiece(prefab, "SVSquare", SVSquareCenter, SVSquareSize, OnSVHit, OnSVHover);
            svTexture = new Texture2D(SVTextureSize, SVTextureSize, TextureFormat.RGBA32, false);
            svTexture.wrapMode = TextureWrapMode.Clamp;
            svTexture.filterMode = FilterMode.Bilinear;
            svPixels = new Il2CppStructArray<Color>(SVTextureSize * SVTextureSize);
            svMaterial = new Material(unlitTexture);
            svMaterial.mainTexture = svTexture;
            if (svSquare != null) svSquare.quadRenderer.material = svMaterial;

            // Swatches — plain colour.
            originalSwatch = CreatePiece(prefab, "OriginalSwatch", OriginalSwatchCenter, SwatchSize, OnOriginalHit, null);
            originalMaterial = new Material(unlitColor);
            if (originalSwatch != null) originalSwatch.quadRenderer.material = originalMaterial;

            newSwatch = CreatePiece(prefab, "NewSwatch", NewSwatchCenter, SwatchSize, OnNewHit, null);
            newMaterial = new Material(unlitColor);
            if (newSwatch != null) newSwatch.quadRenderer.material = newMaterial;

            // "Old" / "New" labels above the swatches.
            Transform labelSource = menu.buttonPrefab != null ? menu.buttonPrefab.transform.Find("label") : null;
            if (labelSource != null)
            {
                CreateLabel(labelSource, "OldLabel", "Old", OldLabelCenter);
                CreateLabel(labelSource, "NewLabel", "New", NewLabelCenter);
            }
            else
            {
                MelonLogger.Log("[ColorPicker] buttonPrefab label not found; swatch labels skipped");
            }

            // Markers — reuse the prefab's own Quad mesh (no collider, so they never block shots).
            Transform quadSource = hueStrip != null ? hueStrip.quad : null;
            if (quadSource != null)
            {
                hueMarker = CreateMarker(quadSource, unlitColor, "HueMarker", new Vector2(0.35f, 3.9f), new Vector2(0.15f, 3.5f));
                svMarker = CreateMarker(quadSource, unlitColor, "SVMarker", new Vector2(0.7f, 0.7f), new Vector2(0.4f, 0.4f));
            }

            lastBuiltHue = -1f;
        }

        private static Piece CreatePiece(GameObject prefab, string name, Vector3 center, Vector2 size,
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
            if (buttonT != null) piece.button = buttonT.GetComponent<GunButton>();

            if (piece.quad == null || piece.quadRenderer == null || piece.button == null)
            {
                MelonLogger.Log($"[ColorPicker] {name}: prefab layout unexpected (quad={piece.quad != null}, button={piece.button != null})");
                return null;
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

        private static Transform CreateMarker(Transform quadSource, Shader shader, string name, Vector2 outerSize, Vector2 innerSize)
        {
            var marker = new GameObject(name).transform;
            marker.SetParent(root.transform, false);

            var outer = GameObject.Instantiate(quadSource.gameObject, marker, false);
            outer.name = "Outer";
            outer.transform.localPosition = Vector3.zero;
            outer.transform.localRotation = Quaternion.identity;
            outer.transform.localScale = new Vector3(outerSize.x, outerSize.y, 1f);
            var outerMat = new Material(shader); outerMat.color = Color.white;
            outer.GetComponent<MeshRenderer>().material = outerMat;

            var inner = GameObject.Instantiate(quadSource.gameObject, marker, false);
            inner.name = "Inner";
            inner.transform.localPosition = new Vector3(0f, 0f, -0.02f);
            inner.transform.localRotation = Quaternion.identity;
            inner.transform.localScale = new Vector3(innerSize.x, innerSize.y, 1f);
            var innerMat = new Material(shader); innerMat.color = Color.black;
            inner.GetComponent<MeshRenderer>().material = innerMat;

            return marker;
        }

        private static void CreateLabel(Transform source, string name, string text, Vector3 center)
        {
            GameObject go = GameObject.Instantiate(source.gameObject, root.transform, false);
            go.name = name;

            // In case a Localizer rides along, remove it so it can't overwrite our text.
            var loc = go.GetComponent<Localizer>();
            if (loc != null) GameObject.DestroyImmediate(loc);

            go.transform.localPosition = new Vector3(center.x, center.y, LabelZ);
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = new Vector3(LabelScale, LabelScale, LabelScale);

            var tmp = go.GetComponent<TMPro.TextMeshPro>();
            if (tmp == null) return;
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
        }

        private static void SetButtonsInteractable()
        {
            foreach (var p in new[] { hueStrip, svSquare, originalSwatch, newSwatch })
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
            SetHover(Color.HSVToRGB(hue, uv.x, uv.y));
        }

        private static void OnOriginalHit(Vector3 pos, Gun gun)
        {
            if (!IsOpen) return;
            DebugLog.Log("[ColorPicker] cancel");
            CancelIfOpen();
        }

        private static void OnNewHit(Vector3 pos, Gun gun)
        {
            if (!IsOpen) return;
            Color c = CurrentColor;
            var cb = onDoneCb;
            DebugLog.Log("[ColorPicker] confirm " + FormatColor(c));
            Close();
            cb?.Invoke(c);
        }

        private static void SetHover(Color c)
        {
            hoverColor = c;
            lastHoverTime = Time.time;
        }

        private static void Commit()
        {
            lastHoverTime = -999f; // show the committed colour immediately
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

            if (newMaterial != null) newMaterial.color = SwatchDisplay(CurrentColor);

            if (hueMarker != null && hueStrip != null)
                PlaceMarker(hueMarker, hueStrip, new Vector2(hue, 0.5f));
            if (svMarker != null && svSquare != null)
                PlaceMarker(svMarker, svSquare, new Vector2(sat, val));
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

        private static void PlaceMarker(Transform marker, Piece piece, Vector2 uv)
        {
            Vector3 world = piece.quad.TransformPoint(new Vector3(uv.x - 0.5f, uv.y - 0.5f, 0f));
            marker.position = world;
            Vector3 lp = marker.localPosition;
            marker.localPosition = new Vector3(lp.x, lp.y, MarkerZ);
            marker.localRotation = Quaternion.identity;
        }

        private static IEnumerator Tick()
        {
            while (IsOpen)
            {
                if (newMaterial != null)
                {
                    bool hovering = Time.time - lastHoverTime <= HoverHoldSeconds;
                    newMaterial.color = SwatchDisplay(hovering ? hoverColor : CurrentColor);
                }
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
            if (root != null) root.SetActive(false);
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