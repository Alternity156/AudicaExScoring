using System;
using UnityEngine;
using Harmony;
using MelonLoader;

namespace ExScoringMod
{
    internal static class ChainArrow
    {
        private const string ArrowObjectName = "ExChainArrow";

        public static LineRenderer GetOrCreate(Target target)
        {
            Transform existing = target.transform.Find(ArrowObjectName);
            if (existing != null)
                return existing.GetComponent<LineRenderer>();

            LineRenderer src = target.chainLine;
            if (src == null)
                return null;

            GameObject go = new GameObject(ArrowObjectName);
            go.transform.SetParent(target.transform, false);

            LineRenderer lr = go.AddComponent<LineRenderer>();
            lr.material = src.material;
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
    }
}