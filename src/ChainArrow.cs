using System.Collections.Generic;
using UnityEngine;

namespace ExScoringMod
{
    internal static class ChainArrow
    {
        private const string ArrowObjectName = "ExChainArrow";

        private static readonly Dictionary<int, Color> DefaultChainColors = new Dictionary<int, Color>();

        // One shared material for every arrow, ever. Created once from whatever
        // chainLine sharedMaterial we first see, then reused via sharedMaterial so
        // arrows batch together instead of each holding a private clone. Color
        // comes from LineRenderer.startColor/endColor (not the material), and
        // travel/glow come from a per-renderer MaterialPropertyBlock, so a shared
        // material asset is all any arrow ever actually needs.
        private static Material sSharedArrowMaterial;

        // Caches the arrow LineRenderer per target so GetOrCreate/Hide don't need
        // to walk the transform hierarchy with Find() every frame.
        private static readonly Dictionary<int, LineRenderer> ArrowCache = new Dictionary<int, LineRenderer>();

        public static LineRenderer GetOrCreate(Target target)
        {
            int id = target.GetInstanceID();

            if (ArrowCache.TryGetValue(id, out LineRenderer cached) && cached != null)
                return cached;

            // Fallback for the rare case the cache missed (e.g. first call after
            // a pooled target got reused) but the arrow object still exists.
            Transform existing = target.transform.Find(ArrowObjectName);
            if (existing != null)
            {
                LineRenderer existingLr = existing.GetComponent<LineRenderer>();
                ArrowCache[id] = existingLr;
                return existingLr;
            }

            LineRenderer src = target.chainLine;
            if (src == null)
                return null;

            if (sSharedArrowMaterial == null)
            {
                // sharedMaterial never triggers Unity's auto-instancing, unlike .material.
                // We only ever need to read this once, to grab a shader/template for the
                // one arrow material every arrow will reuse from then on.
                Material template = src.sharedMaterial;
                if (template == null)
                {
                    MelonLoader.MelonLogger.Log("[ChainArrow] chainLine.sharedMaterial is null, skipping arrow creation");
                    return null;
                }
                sSharedArrowMaterial = new Material(template);
            }

            GameObject go = new GameObject(ArrowObjectName);
            go.transform.SetParent(target.transform, false);

            LineRenderer lr = go.AddComponent<LineRenderer>();
            lr.sharedMaterial = sSharedArrowMaterial;
            lr.widthMultiplier = src.widthMultiplier;
            lr.useWorldSpace = src.useWorldSpace;
            lr.textureMode = src.textureMode;
            lr.numCapVertices = src.numCapVertices;
            lr.numCornerVertices = src.numCornerVertices;
            lr.sortingLayerID = src.sortingLayerID;
            lr.sortingOrder = src.sortingOrder;
            lr.positionCount = 3;
            lr.enabled = false;

            ArrowCache[id] = lr;
            return lr;
        }

        public static void Hide(Target target)
        {
            int id = target.GetInstanceID();

            if (ArrowCache.TryGetValue(id, out LineRenderer cached) && cached != null)
            {
                cached.enabled = false;
                return;
            }

            Transform existing = target.transform.Find(ArrowObjectName);
            if (existing != null)
            {
                LineRenderer lr = existing.GetComponent<LineRenderer>();
                ArrowCache[id] = lr;
                lr.enabled = false;
            }
        }

        public static Color GetHandColor(Target.TargetHandType hand)
        {
            if (KataConfig.I == null)
                return Color.white;

            switch (hand)
            {
                case Target.TargetHandType.Left: return KataConfig.I.leftHandColor;
                case Target.TargetHandType.Right: return KataConfig.I.rightHandColor;
                case Target.TargetHandType.Either: return KataConfig.I.eitherHandColor;
                default: return Color.white;
            }
        }

        public static Color GetChainHandColor(Target.TargetHandType hand)
        {
            Color c = GetHandColor(hand);
            return hand == Target.TargetHandType.Either ? c : c / 2;
        }

        public static void ApplyColor(LineRenderer lr, Color color)
        {
            lr.startColor = color;
            lr.endColor = color;
        }

        public static Color GetDefaultChainColor(LineRenderer chain)
        {
            int id = chain.GetInstanceID();
            if (!DefaultChainColors.TryGetValue(id, out Color c))
            {
                c = chain.startColor;
                DefaultChainColors[id] = c;
            }
            return c;
        }

        private static readonly Dictionary<int, Target.TargetHandType> HandTypeCache = new Dictionary<int, Target.TargetHandType>();

        public static void CacheHandType(Target target, Target.TargetHandType handType)
        {
            HandTypeCache[target.GetInstanceID()] = handType;
        }

        public static Target.TargetHandType GetCachedHandType(Target target)
        {
            return HandTypeCache.TryGetValue(target.GetInstanceID(), out var hand)
                ? hand
                : Target.TargetHandType.None;
        }

        public static void ClearCache(Target target)
        {
            int id = target.GetInstanceID();
            HandTypeCache.Remove(id);
            ArrowCache.Remove(id);
            DefaultChainColors.Remove(target.chainLine != null ? target.chainLine.GetInstanceID() : -1);
        }

        /// <summary>
        /// Mirrors the chain's own current reveal ("travel") and glow shader
        /// values onto the arrow's material, by reading them straight off the
        /// chain's material and copying them via the shared property IDs
        /// (Target.mChainTravelProperty.mID / mChainGlowProperty.mID). We do
        /// NOT call MaterialFloatPropertyUpdater.UpdateValue here -- it appears
        /// to be a stateful/damped updater (it has an internal mValue field),
        /// and the game's own UpdateChainLineAnim already calls it once per
        /// frame for the chain. Calling it a second time for the arrow would
        /// double-step that internal damping, which is what caused the arrow
        /// to visibly race ahead of the chain's real reveal on long lead-in
        /// windows (isolated chains / the first link in a sequence). Reading
        /// the already-updated value straight from the chain's material and
        /// copying it avoids touching that state a second time.
        /// </summary>
        public static void SyncRevealAnimation(Target target, LineRenderer arrow, LineRenderer chain)
        {
            if (arrow == null || chain == null)
                return;

            var travelProp = target.mChainTravelProperty;
            var glowProp = target.mChainGlowProperty;

            var chainBlock = sChainBlock ?? (sChainBlock = new MaterialPropertyBlock());
            var arrowBlock = sArrowBlock ?? (sArrowBlock = new MaterialPropertyBlock());

            chain.GetPropertyBlock(chainBlock);
            arrow.GetPropertyBlock(arrowBlock);

            if (travelProp != null)
            {
                float travelValue = chainBlock.GetFloat(travelProp.mID);
                arrowBlock.SetFloat(travelProp.mID, travelValue);
            }

            if (glowProp != null)
            {
                float glowValue = chainBlock.GetFloat(glowProp.mID);
                arrowBlock.SetFloat(glowProp.mID, glowValue);
            }

            arrow.SetPropertyBlock(arrowBlock);
        }

        private static MaterialPropertyBlock sChainBlock;
        private static MaterialPropertyBlock sArrowBlock;

        // ── Grid-based "too short to bother" filter ──
        // Uses chart data (pitch + gridOffset) rather than live world position, so
        // it's unaffected by which way the player's looking (VR head orientation)
        // and by whatever distance a mapper chose to place a given note at.
        // Mirrors DifficultyCalculator's GetTrueCoordinates.
        private static Vector2 GetGridCoordinates(SongCues.Cue cue)
        {
            float x = cue.pitch % 12;
            float y = (int)(cue.pitch / 12);
            x += cue.gridOffset.x;
            y += cue.gridOffset.y;
            return new Vector2(x, y);
        }

        public static bool IsChainSegmentTooShort(Target target)
        {
            var cue = target.mCue;
            if (cue == null || cue.chainPrevious == null)
                return false; // nothing to compare against; don't skip

            float dist = Vector2.Distance(GetGridCoordinates(cue), GetGridCoordinates(cue.chainPrevious));
            return dist < Config.ChainArrowMinPitchDistance;
        }

        // ── Concurrent-density thinning ──
        // Once more than Config.ChainArrowMaxSimultaneous arrow-worthy chains are
        // active in the same frame, skip every Nth one so total arrow draw calls
        // stay bounded during extreme bursts. Skip level is based on last frame's
        // count (this frame's total isn't known until it's over), and only counts
        // chains that already passed the pitch-distance filter, so degenerate/
        // stacked chains don't eat into the budget.
        private static int sDensityFrame = -1;
        private static int sDensityIndexThisFrame = 0;
        private static int sDensityCountThisFrame = 0;
        private static int sDensityCountLastFrame = 0;
        private static int sDensitySkipLevel = 0;

        public static bool ShouldShowArrowForDensity()
        {
            int frame = Time.frameCount;
            if (frame != sDensityFrame)
            {
                sDensityCountLastFrame = sDensityCountThisFrame;
                sDensityCountThisFrame = 0;
                sDensityIndexThisFrame = 0;
                sDensityFrame = frame;

                int threshold = Mathf.Max(1, Config.ChainArrowMaxSimultaneous);
                sDensitySkipLevel = sDensityCountLastFrame <= threshold
                    ? 0
                    : (sDensityCountLastFrame - 1) / threshold;
            }

            sDensityCountThisFrame++;
            int index = sDensityIndexThisFrame++;

            return (index % (sDensitySkipLevel + 1)) == 0;
        }
    }
}