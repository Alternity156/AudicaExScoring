using System.Collections.Generic;
using UnityEngine;

namespace ExScoringMod
{
    internal static class ChainArrow
    {
        private const string ArrowObjectName = "ExChainArrow";

        private static readonly Dictionary<int, Color> DefaultChainColors = new Dictionary<int, Color>();

        public static LineRenderer GetOrCreate(Target target)
        {
            Transform existing = target.transform.Find(ArrowObjectName);
            if (existing != null)
                return existing.GetComponent<LineRenderer>();

            LineRenderer src = target.chainLine;
            if (src == null)
                return null;

            Material srcMat = src.material; // may be null if chainLine has no material assigned
            if (srcMat == null)
            {
                MelonLoader.MelonLogger.Log("[ChainArrow] chainLine.material is null, skipping arrow creation");
                return null;
            }

            GameObject go = new GameObject(ArrowObjectName);
            go.transform.SetParent(target.transform, false);

            LineRenderer lr = go.AddComponent<LineRenderer>();
            lr.material = new Material(srcMat);
            lr.widthMultiplier = src.widthMultiplier;
            lr.useWorldSpace = src.useWorldSpace;
            lr.textureMode = src.textureMode;
            lr.numCapVertices = src.numCapVertices;
            lr.numCornerVertices = src.numCornerVertices;
            lr.sortingLayerID = src.sortingLayerID;
            lr.sortingOrder = src.sortingOrder;
            lr.positionCount = 3;
            lr.enabled = false;

            return lr;
        }

        public static void Hide(Target target)
        {
            Transform existing = target.transform.Find(ArrowObjectName);
            if (existing != null)
                existing.GetComponent<LineRenderer>().enabled = false;
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
            HandTypeCache.Remove(target.GetInstanceID());
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

            bool shouldLog = Time.frameCount % 30 == 0;

            // UpdateValue appears to write through a MaterialPropertyBlock rather
            // than the Material asset directly (reading chain.material.GetFloat
            // always returned a frozen default, never the live animated value).
            // Reusing two static blocks avoids allocating every call.
            var chainBlock = sChainBlock ?? (sChainBlock = new MaterialPropertyBlock());
            var arrowBlock = sArrowBlock ?? (sArrowBlock = new MaterialPropertyBlock());

            chain.GetPropertyBlock(chainBlock);
            arrow.GetPropertyBlock(arrowBlock);

            if (travelProp != null)
            {
                float travelValue = chainBlock.GetFloat(travelProp.mID);
                arrowBlock.SetFloat(travelProp.mID, travelValue);
                if (shouldLog)
                    MelonLoader.MelonLogger.Log($"[ChainArrow] travelValue(block)={travelValue}");
            }

            if (glowProp != null)
            {
                float glowValue = chainBlock.GetFloat(glowProp.mID);
                arrowBlock.SetFloat(glowProp.mID, glowValue);
                if (shouldLog)
                    MelonLoader.MelonLogger.Log($"[ChainArrow] glowValue(block)={glowValue}");
            }

            arrow.SetPropertyBlock(arrowBlock);
        }

        private static MaterialPropertyBlock sChainBlock;
        private static MaterialPropertyBlock sArrowBlock;
    }
}