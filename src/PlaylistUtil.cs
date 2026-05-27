using MelonLoader;
using UnityEngine;

namespace ExScoringMod
{
    internal static class PlaylistUtil
    {
        public static void Popup(string text)
        {
            MelonLogger.Log(text);
            KataConfig.I.CreateDebugText(text, new Vector3(0f, -1f, 5f), 5f, null, false, 0.2f);
        }
    }
}