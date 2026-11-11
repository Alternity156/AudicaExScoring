using System;
using System.Collections.Generic;
using MelonLoader;
using UnhollowerBaseLib;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// Loads another player's custom head, body or gun (ApiContract.md v2.44, 19.15.12) the way the
    /// game loads the player's own (CustomModelLoader.LoadMesh, Ghidra 2026-11-02), but into a frame
    /// given by MpPresence instead of the local rig. Main thread only.
    ///
    ///  - Reader: the game's own CustomModelLoader.objReader, so the result matches what the owner
    ///    sees (in-game settings 2026-11-03: combineMultipleGroups off, so one object per OBJ group,
    ///    which the colour prefixes rely on; useSubmeshesWhenCombining, use32BitIndices and
    ///    useMTLFallback on; autoCenterOnOrigin and computeTangents off). The config goes on as
    ///    LoadMesh puts it (scaleFactor, objRotation = orientationOffset, objPosition =
    ///    positionOffset, useSuppliedNormals = !recalculateNormals), and every field it touches is
    ///    put back afterwards, so the player's own models are unaffected.
    ///  - Base material: a copy of standardMaterialHead / standardMaterialBody, or for guns
    ///    KataConfig.I.guns[0].customModel.metalMat, with shading and reflection set on it. LoadMesh
    ///    sets two floats there (string literals 8477 / 8478); the Shiny shader's only adjustable
    ///    floats are _Metallic and _Smoothness (in-game probe 2026-11-03), so shading -> _Metallic
    ///    and reflection -> _Smoothness.
    ///  - Parts: ObjReader.ConvertFile(path, useMtl: true, base, transparentMaterial), each object
    ///    then moved under the frame keeping its own local pose. LoadMesh's objects end up at world
    ///    scale whatever the parent (it moves CustomModelLoader to the parent and reparents keeping
    ///    the world pose), so the frame MpPresence passes has world scale 1 (mirrored for the left
    ///    gun, as UpdateCustomGun's (-1, 1, 1)).
    ///  - Colours, by object name, as AvatarSelector.SetCustomMaterials (heads, bodies) and
    ///    Gun.UpdateGunModel (guns) do:
    ///      heads/bodies (in-game dumps 2026-11-03): blades -> bladesMat; glowleft/glowright -> gunGlowMat;
    ///            colorleft/colorright -> gunMetalMat
    ///      guns (v2.50, Ghidra and the metadata strings 2026-11-10, ApplyGunMaterials): blades, color,
    ///            glow, othercolor, otherglow, the fallback rule, muzzle, form, multiplier and trigger;
    ///            the colour lists, muzzle, form and trigger go back in a CustomGunParts for MpPresence
    ///    Head and body tints get this template's own copies, named TintMetalName / TintGlowName, so
    ///    MpPresence can give every avatar its own copies and colour them with that player's colours.
    ///    Guns are coloured by MpPresence through property blocks on the CustomGunParts lists, as the
    ///    game does. Nothing shared is ever modified.
    ///  - Mod-only limits: the finished model must fit MaxAvatarSize / MaxGunSize metres and sit within
    ///    the same distance of its anchor; a config offset beyond MaxOffset is clamped.
    /// </summary>
    internal static class MpCustomModels
    {
        public const string ShadingProperty = "_Metallic";      // LoadMesh: string literal 8477 <- config.shading
        public const string ReflectionProperty = "_Smoothness"; // LoadMesh: string literal 8478 <- config.reflection
        public const string TintMetalName = "MpTintMetal";
        public const string TintGlowName = "MpTintGlow";
        /// <summary>v2.50: the gun's base copy. Not an MpTint name: the game leaves base parts untinted unless a colour list holds them.</summary>
        public const string GunBaseName = "MpGunBase";

        // Gun.UpdateGunModel's object-name prefixes for a custom gun (Ghidra 2026-11-10; the values
        // read from global-metadata.dat, literals 3895-3903 there, in this order).
        private const string PrefixMuzzle = "muzzle", MuzzleParentName = "muzzleparent", PrefixForm = "form", PrefixMultiplier = "multiplier";
        private const string PrefixBlades = "blades", PrefixColor = "color", PrefixGlow = "glow", PrefixOtherColor = "othercolor", PrefixOtherGlow = "otherglow";
        private const string PrefixTrigger = "trigger";

        /// <summary>
        /// v2.50: what Gun.UpdateGunModel records on a custom gun model, from this template's parts:
        /// the renderers coloured with this gun's colour and with the other hand's (GunModel's
        /// gunMetal/GlowMeshes and otherGunMetal/GlowMeshes), the muzzle, the form indicator and the
        /// trigger. MpPresence turns it into its GunParts.
        /// </summary>
        public class CustomGunParts
        {
            public readonly List<Renderer> Own = new List<Renderer>();
            public readonly List<Renderer> Other = new List<Renderer>();
            public Transform Muzzle, Form, Trigger;
            /// <summary>For the log: how the parts were sorted.</summary>
            public string Summary = "";
        }

        public const float MaxAvatarSize = 3f;   // metres, largest side of a head or body
        public const float MaxGunSize = 1.5f;    // metres, largest side of a gun
        public const float MaxOffset = 3f;       // metres, per positionOffset component
        public const float MaxScale = 10f;       // per scaleFactor component

        private const float DefaultShading = 0.5f;
        private const float DefaultReflection = 1f;

        /// <summary>
        /// Loads objPath into frame (which must sit under an inactive holder). owned receives every
        /// material, mesh and texture made for it, to destroy with the template. False with a reason
        /// when it can't be shown; whatever was made is then already destroyed.
        /// </summary>
        public static bool Load(int slot, string objPath, MpModelConfig config, Transform frame, Gun localGun, List<UnityEngine.Object> owned, out CustomGunParts gunParts, out string error)
        {
            error = null;
            gunParts = null;
            bool gun = slot == MpCosmetics.SlotLGun || slot == MpCosmetics.SlotRGun;

            CustomModelLoader loader = CustomModelLoader.I;
            ObjReader reader = loader != null ? loader.objReader : null;
            if (reader == null)
            {
                error = "the game's model loader isn't ready";
                return false;
            }

            Material source = gun ? GunBaseMaterial(localGun) : (slot == MpCosmetics.SlotHead ? loader.standardMaterialHead : loader.standardMaterialBody);
            if (source == null)
            {
                error = "no base material";
                return false;
            }

            Material baseMat = new Material(source) { name = gun ? GunBaseName : source.name + " (Mp)" };
            owned.Add(baseMat);
            float shading = Finite(config != null ? config.shading : null, DefaultShading);
            float reflection = Finite(config != null ? config.reflection : null, DefaultReflection);
            if (baseMat.HasProperty(ShadingProperty)) baseMat.SetFloat(ShadingProperty, shading);
            if (baseMat.HasProperty(ReflectionProperty)) baseMat.SetFloat(ReflectionProperty, reflection);

            Vector3 scale = Vector(config != null ? config.scaleFactor : null, Vector3.one, MaxScale);
            Vector3 position = Vector(config != null ? config.positionOffset : null, Vector3.zero, MaxOffset);
            Vector3 rotation = Vector(config != null ? config.orientationOffset : null, Vector3.zero, 360f);
            bool recalculate = config == null || !config.recalculateNormals.HasValue || config.recalculateNormals.Value;

            Il2CppReferenceArray<GameObject> objects = null;
            Vector3 savedScale = reader.scaleFactor;
            Vector3 savedRotation = reader.objRotation;
            Vector3 savedPosition = reader.objPosition;
            bool savedNormals = reader.useSuppliedNormals;
            try
            {
                reader.scaleFactor = scale;
                reader.objRotation = rotation;
                reader.objPosition = position;
                reader.useSuppliedNormals = !recalculate;
                objects = reader.ConvertFile(objPath, true, baseMat, loader.transparentMaterial);
            }
            catch (Exception ex)
            {
                error = "the model couldn't be read (" + ex.Message + ")";
            }
            finally
            {
                reader.scaleFactor = savedScale;
                reader.objRotation = savedRotation;
                reader.objPosition = savedPosition;
                reader.useSuppliedNormals = savedNormals;
            }

            int parts = 0;
            if (objects != null)
            {
                for (int i = 0; i < objects.Length; i++)
                {
                    GameObject go = objects[i];
                    if (go == null) continue;
                    go.transform.SetParent(frame, false); // created at the scene root: same local pose under the frame
                    parts++;
                }
            }

            CollectOwned(frame, source, loader, owned);

            if (error == null && parts == 0) error = "the model has no parts";
            if (error == null)
            {
                try
                {
                    if (gun) gunParts = ApplyGunMaterials(frame, localGun, owned);
                    else ApplyAvatarMaterials(frame, owned);
                    error = CheckSize(frame, gun ? MaxGunSize : MaxAvatarSize);
                }
                catch (Exception ex)
                {
                    error = "setting up the model failed (" + ex.Message + ")";
                }
            }

            if (error != null)
            {
                for (int i = frame.childCount - 1; i >= 0; i--) UnityEngine.Object.Destroy(frame.GetChild(i).gameObject);
                DestroyOwned(owned);
                gunParts = null;
                return false;
            }
            return true;
        }

        /// <summary>Destroys everything Load made (meshes, materials, textures).</summary>
        public static void DestroyOwned(List<UnityEngine.Object> owned)
        {
            if (owned == null) return;
            foreach (UnityEngine.Object o in owned)
            {
                try
                {
                    if (o != null) UnityEngine.Object.Destroy(o);
                }
                catch
                {
                    // Already gone.
                }
            }
            owned.Clear();
        }

        /// <summary>LoadMesh's gun base: KataConfig.I.guns[0].customModel.metalMat, else this hand's own.</summary>
        private static Material GunBaseMaterial(Gun localGun)
        {
            try
            {
                KataConfig config = KataConfig.I;
                if (config != null && config.guns != null && config.guns.Length > 0 && config.guns[0] != null && config.guns[0].customModel != null && config.guns[0].customModel.metalMat != null)
                    return config.guns[0].customModel.metalMat;
            }
            catch
            {
                // Fall back below.
            }
            return localGun != null && localGun.customModel != null ? localGun.customModel.metalMat : null;
        }

        /// <summary>
        /// Everything ConvertFile made: every mesh, every material that isn't one of the game's
        /// shared ones, and those materials' textures that aren't the base's.
        /// </summary>
        private static void CollectOwned(Transform frame, Material source, CustomModelLoader loader, List<UnityEngine.Object> owned)
        {
            HashSet<IntPtr> known = new HashSet<IntPtr>();
            foreach (UnityEngine.Object o in owned) if (o != null) known.Add(o.Pointer);
            HashSet<IntPtr> shared = new HashSet<IntPtr>();
            AddPointer(shared, source);
            AddPointer(shared, loader.transparentMaterial);
            AddPointer(shared, loader.standardMaterialHead);
            AddPointer(shared, loader.standardMaterialBody);
            HashSet<IntPtr> sharedTextures = new HashSet<IntPtr>();
            if (source != null) AddPointer(sharedTextures, source.mainTexture);
            if (loader.transparentMaterial != null) AddPointer(sharedTextures, loader.transparentMaterial.mainTexture);

            var filters = frame.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < filters.Length; i++)
            {
                Mesh mesh = filters[i] != null ? filters[i].sharedMesh : null;
                if (mesh != null && known.Add(mesh.Pointer)) owned.Add(mesh);
            }

            var renderers = frame.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] == null) continue;
                var mats = renderers[i].sharedMaterials;
                for (int j = 0; j < mats.Length; j++)
                {
                    Material mat = mats[j];
                    if (mat == null || shared.Contains(mat.Pointer)) continue;
                    if (known.Add(mat.Pointer)) owned.Add(mat);
                    Texture tex = mat.mainTexture;
                    if (tex != null && !sharedTextures.Contains(tex.Pointer) && known.Add(tex.Pointer)) owned.Add(tex);
                }
            }
        }

        /// <summary>AvatarSelector.SetCustomMaterials on this template, with copies for the coloured parts.</summary>
        private static void ApplyAvatarMaterials(Transform frame, List<UnityEngine.Object> owned)
        {
            AvatarSelector selector = AvatarSelector.I;
            if (selector == null) return;
            Material glow = null, metal = null;

            var renderers = frame.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null) continue;
                string name = r.gameObject.name ?? "";
                if (name.StartsWith("blades", StringComparison.Ordinal) && selector.bladesMat != null) r.sharedMaterial = selector.bladesMat;
                if (name.StartsWith("glowleft", StringComparison.Ordinal) || name.StartsWith("glowright", StringComparison.Ordinal))
                {
                    if (glow == null) glow = Copy(selector.gunGlowMat, TintGlowName, owned);
                    if (glow != null) r.sharedMaterial = glow;
                }
                if (name.StartsWith("colorleft", StringComparison.Ordinal) || name.StartsWith("colorright", StringComparison.Ordinal))
                {
                    if (metal == null) metal = Copy(selector.gunMetalMat, TintMetalName, owned);
                    if (metal != null) r.sharedMaterial = metal;
                }
            }
        }

        /// <summary>
        /// Gun.UpdateGunModel's custom-model branch (Ghidra 2026-11-10) on this template, every
        /// transform under the frame in hierarchy order, each name checked by StartsWith (ordinal):
        ///  - muzzle: wrapped in a new "muzzleparent" that takes its scale (the muzzle's own goes to
        ///    one), given an Animator with gunModels[0].muzzle.animator's controller and
        ///    gunModels[0].muzzle's material: the game's own muzzle setup. MpPresence leaves it at rest
        ///    (its Animator's default state, colour x 0.5)
        ///  - form: gunModels[0].formIndicator's material, shown static (its script only reacts to aim)
        ///  - multiplier: its renderer off (the game puts a MultiplierDisplay there instead)
        ///  - then one of: blades -> customGunBladesMat; color -> metalMat (own colour); glow -> glowMat
        ///    (own); othercolor -> metalMat (other hand's colour); otherglow -> glowMat (other); anything
        ///    else with a renderer -> the fallback list, keeping its own material
        ///  - trigger: the fingerTrigger
        /// When nothing matched the four colour prefixes, the fallback list becomes the own-colour list
        /// (so a model without colour names is tinted all over, its textures kept). The form indicator
        /// is left out of it here, since its script would recolour it every frame natively. metalMat
        /// and glowMat are template copies (TintMetalName / TintGlowName); the colours themselves are
        /// MpPresence's property blocks.
        /// </summary>
        private static CustomGunParts ApplyGunMaterials(Transform frame, Gun localGun, List<UnityEngine.Object> owned)
        {
            CustomGunParts parts = new CustomGunParts();
            GunModel model = localGun != null ? localGun.customModel : null;
            GunModel first = localGun != null && localGun.gunModels != null && localGun.gunModels.Length > 0 ? localGun.gunModels[0] : null;
            Material blades = localGun != null ? localGun.customGunBladesMat : null;
            if (blades == null && AvatarSelector.I != null) blades = AvatarSelector.I.bladesMat;
            Material metal = null, glow = null;
            List<Renderer> fallback = new List<Renderer>();
            int colored = 0, muzzles = 0, forms = 0, multipliers = 0, bladeParts = 0;

            var transforms = frame.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                Transform t = transforms[i];
                if (t == null || t.Pointer == frame.Pointer) continue;
                string name = t.gameObject.name ?? "";
                Renderer r = t.GetComponent<Renderer>();

                if (name.StartsWith(PrefixMuzzle, StringComparison.Ordinal) && name != MuzzleParentName)
                {
                    SetUpMuzzle(t, r, first);
                    parts.Muzzle = t;
                    muzzles++;
                }
                if (name.StartsWith(PrefixForm, StringComparison.Ordinal))
                {
                    Renderer source = first != null && first.formIndicator != null ? first.formIndicator.GetComponent<Renderer>() : null;
                    if (r != null && source != null && source.sharedMaterial != null) r.sharedMaterial = source.sharedMaterial;
                    parts.Form = t;
                    forms++;
                }
                if (name.StartsWith(PrefixMultiplier, StringComparison.Ordinal))
                {
                    if (r != null) r.enabled = false;
                    multipliers++;
                }

                if (r != null)
                {
                    if (name.StartsWith(PrefixBlades, StringComparison.Ordinal))
                    {
                        if (blades != null) r.sharedMaterial = blades;
                        bladeParts++;
                    }
                    else if (name.StartsWith(PrefixColor, StringComparison.Ordinal))
                    {
                        if (metal == null) metal = Copy(model != null ? model.metalMat : null, TintMetalName, owned);
                        if (metal != null) r.sharedMaterial = metal;
                        parts.Own.Add(r);
                        colored++;
                    }
                    else if (name.StartsWith(PrefixGlow, StringComparison.Ordinal))
                    {
                        if (glow == null) glow = Copy(model != null ? model.glowMat : null, TintGlowName, owned);
                        if (glow != null) r.sharedMaterial = glow;
                        parts.Own.Add(r);
                        colored++;
                    }
                    else if (name.StartsWith(PrefixOtherColor, StringComparison.Ordinal))
                    {
                        if (metal == null) metal = Copy(model != null ? model.metalMat : null, TintMetalName, owned);
                        if (metal != null) r.sharedMaterial = metal;
                        parts.Other.Add(r);
                        colored++;
                    }
                    else if (name.StartsWith(PrefixOtherGlow, StringComparison.Ordinal))
                    {
                        if (glow == null) glow = Copy(model != null ? model.glowMat : null, TintGlowName, owned);
                        if (glow != null) r.sharedMaterial = glow;
                        parts.Other.Add(r);
                        colored++;
                    }
                    else if (!name.StartsWith(PrefixForm, StringComparison.Ordinal))
                    {
                        fallback.Add(r);
                    }
                }

                if (name.StartsWith(PrefixTrigger, StringComparison.Ordinal)) parts.Trigger = t;
            }

            bool usedFallback = colored == 0;
            if (usedFallback) parts.Own.AddRange(fallback);
            parts.Summary = $"own {parts.Own.Count}{(usedFallback ? " (fallback: no color/glow names)" : "")} other {parts.Other.Count} blades {bladeParts} " +
                            $"muzzle {muzzles} form {forms} multiplier {multipliers} trigger={(parts.Trigger != null ? parts.Trigger.name : "-")}";
            return parts;
        }

        /// <summary>
        /// The game's custom muzzle (Gun.UpdateGunModel): a new "muzzleparent" under the muzzle's
        /// parent takes the muzzle's scale and the muzzle goes under it at scale one, so the muzzle
        /// Animator's curves work around it as on the default model. The wrapper also takes the
        /// muzzle's local position and rotation (the muzzle's own become identity), keeping it where
        /// it was. Then the Animator with the default muzzle's controller, and that muzzle's material.
        /// </summary>
        private static void SetUpMuzzle(Transform muzzle, Renderer r, GunModel first)
        {
            Transform parent = muzzle.parent;
            GameObject wrapper = new GameObject(MuzzleParentName);
            wrapper.layer = muzzle.gameObject.layer;
            Transform w = wrapper.transform;
            w.SetParent(parent, false);
            w.localPosition = muzzle.localPosition;
            w.localRotation = muzzle.localRotation;
            w.localScale = muzzle.localScale;
            w.SetSiblingIndex(muzzle.GetSiblingIndex());
            muzzle.SetParent(w, false);
            muzzle.localPosition = Vector3.zero;
            muzzle.localRotation = Quaternion.identity;
            muzzle.localScale = Vector3.one;

            Muzzle source = first != null ? first.muzzle : null;
            RuntimeAnimatorController controller = source != null && source.animator != null ? source.animator.runtimeAnimatorController : null;
            if (controller != null)
            {
                Animator animator = muzzle.gameObject.GetComponent<Animator>();
                if (animator == null) animator = muzzle.gameObject.AddComponent<Animator>();
                animator.runtimeAnimatorController = controller;
            }
            Renderer sourceRenderer = source != null ? source.GetComponent<Renderer>() : null;
            if (r != null && sourceRenderer != null && sourceRenderer.sharedMaterial != null) r.sharedMaterial = sourceRenderer.sharedMaterial;
        }

        /// <summary>Null when the model fits within limit metres (largest side, and its centre from the anchor).</summary>
        private static string CheckSize(Transform frame, float limit)
        {
            bool any = false;
            Bounds bounds = new Bounds();
            var filters = frame.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < filters.Length; i++)
            {
                MeshFilter filter = filters[i];
                Mesh mesh = filter != null ? filter.sharedMesh : null;
                if (mesh == null) continue;
                Bounds b = mesh.bounds;
                Vector3 min = b.min, max = b.max;
                for (int c = 0; c < 8; c++)
                {
                    Vector3 corner = new Vector3((c & 1) == 0 ? min.x : max.x, (c & 2) == 0 ? min.y : max.y, (c & 4) == 0 ? min.z : max.z);
                    Vector3 p = frame.InverseTransformPoint(filter.transform.TransformPoint(corner));
                    if (!any)
                    {
                        bounds = new Bounds(p, Vector3.zero);
                        any = true;
                    }
                    else bounds.Encapsulate(p);
                }
            }
            if (!any) return "the model has no meshes";

            Vector3 size = bounds.size;
            float largest = Mathf.Max(size.x, Mathf.Max(size.y, size.z));
            if (largest > limit) return $"the model is {largest:F1} m across, the limit is {limit:F1} m";
            if (bounds.center.magnitude > limit) return $"the model sits {bounds.center.magnitude:F1} m from where it's worn, the limit is {limit:F1} m";
            return null;
        }

        private static Material Copy(Material source, string name, List<UnityEngine.Object> owned)
        {
            if (source == null) return null;
            Material copy = new Material(source) { name = name };
            owned.Add(copy);
            return copy;
        }

        private static void AddPointer(HashSet<IntPtr> set, UnityEngine.Object o)
        {
            if (o != null) set.Add(o.Pointer);
        }

        private static float Finite(float? value, float fallback)
        {
            if (!value.HasValue || float.IsNaN(value.Value) || float.IsInfinity(value.Value)) return fallback;
            return value.Value;
        }

        /// <summary>A config vector: exactly three finite numbers, each clamped to +-limit; the fallback otherwise.</summary>
        private static Vector3 Vector(float[] v, Vector3 fallback, float limit)
        {
            if (v == null || v.Length != 3) return fallback;
            for (int i = 0; i < 3; i++) if (float.IsNaN(v[i]) || float.IsInfinity(v[i])) return fallback;
            return new Vector3(Mathf.Clamp(v[0], -limit, limit), Mathf.Clamp(v[1], -limit, limit), Mathf.Clamp(v[2], -limit, limit));
        }
    }
}