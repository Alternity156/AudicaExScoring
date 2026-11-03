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
    ///    Gun.UpdateGunModel (guns) do, in-game dumps 2026-11-03:
    ///      heads/bodies: blades -> bladesMat; glowleft/glowright -> gunGlowMat; colorleft/colorright -> gunMetalMat
    ///      guns: blades -> Gun.customGunBladesMat; gunmesh -> customModel.metalMat; glow -> customModel.glowMat;
    ///            muzzle, form, multiplier: markers the game hides or replaces, hidden here
    ///    The coloured ones get this template's own copies, named TintMetalName / TintGlowName (and
    ///    the gun's base copy TintBaseName), so MpPresence can give every avatar its own copies and
    ///    colour them with that player's colours. Nothing shared is ever modified.
    ///  - Mod-only limits: the finished model must fit MaxAvatarSize / MaxGunSize metres and sit within
    ///    the same distance of its anchor; a config offset beyond MaxOffset is clamped.
    /// </summary>
    internal static class MpCustomModels
    {
        public const string ShadingProperty = "_Metallic";      // LoadMesh: string literal 8477 <- config.shading
        public const string ReflectionProperty = "_Smoothness"; // LoadMesh: string literal 8478 <- config.reflection
        public const string TintMetalName = "MpTintMetal";
        public const string TintGlowName = "MpTintGlow";
        public const string TintBaseName = "MpTintBase";

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
        public static bool Load(int slot, string objPath, MpModelConfig config, Transform frame, Gun localGun, List<UnityEngine.Object> owned, out string error)
        {
            error = null;
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

            Material baseMat = new Material(source) { name = gun ? TintBaseName : source.name + " (Mp)" };
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
                    if (gun) ApplyGunMaterials(frame, localGun, owned);
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

        /// <summary>Gun.UpdateGunModel's custom-model parts on this template, with copies for the coloured parts.</summary>
        private static void ApplyGunMaterials(Transform frame, Gun localGun, List<UnityEngine.Object> owned)
        {
            GunModel model = localGun != null ? localGun.customModel : null;
            Material blades = localGun != null ? localGun.customGunBladesMat : null;
            if (blades == null && AvatarSelector.I != null) blades = AvatarSelector.I.bladesMat;
            Material metal = null, glow = null;

            var renderers = frame.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer r = renderers[i];
                if (r == null) continue;
                string name = r.gameObject.name ?? "";
                if (name.StartsWith("muzzle", StringComparison.Ordinal) || name.StartsWith("form", StringComparison.Ordinal) || name.StartsWith("multiplier", StringComparison.Ordinal))
                {
                    r.enabled = false;
                    continue;
                }
                if (name.StartsWith("blades", StringComparison.Ordinal))
                {
                    if (blades != null) r.sharedMaterial = blades;
                }
                else if (name.StartsWith("gunmesh", StringComparison.Ordinal))
                {
                    if (metal == null) metal = Copy(model != null ? model.metalMat : null, TintMetalName, owned);
                    if (metal != null) r.sharedMaterial = metal;
                }
                else if (name.StartsWith("glow", StringComparison.Ordinal))
                {
                    if (glow == null) glow = Copy(model != null ? model.glowMat : null, TintGlowName, owned);
                    if (glow != null) r.sharedMaterial = glow;
                }
            }
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