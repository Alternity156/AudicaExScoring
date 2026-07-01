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
    }
}