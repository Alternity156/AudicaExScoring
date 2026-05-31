using UnityEngine;
using MelonLoader;
using UnhollowerRuntimeLib;   // Il2CppType (ML 0.5.7)
using UnhollowerBaseLib;      // TryCast

namespace ExScoringMod
{
    public static class SearchKeyboard
    {
        private static GameObject keyboardCopy = null;

        private static KeyboardEntry FindSourceKeyboard()
        {
            KeyboardEntry fallback = null;

            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<KeyboardEntry>()))
            {
                var kb = o.TryCast<KeyboardEntry>();
                if (kb == null || kb.transform.parent == null) continue;
                if (kb.transform.parent.name != "Settings") continue;

                // Prefer the original settings keyboard (root "menu") — same source we tested,
                // so we inherit its lower, in-front-of-player position rather than the (Clone)'s.
                if (kb.transform.root.name == "menu")
                    return kb;

                fallback = kb; // remember the (Clone) variant in case the original is gone
            }

            if (fallback != null) return fallback;

            var menu = SongSearchScreen.primaryMenu;
            if (menu != null && menu.keyboard != null) return menu.keyboard;

            return null;
        }

        private static void EnsureCopy()
        {
            if (keyboardCopy != null) return; // Unity-null check: recreates after scene change

            KeyboardEntry src = FindSourceKeyboard();
            if (src == null)
            {
                MelonLogger.Log("[SearchKeyboard] No source keyboard found");
                return;
            }

            // Capture the original keyboard's world placement (valid even while inactive)
            Vector3 wpos = src.transform.position;
            Quaternion wrot = src.transform.rotation;
            Vector3 wscale = src.transform.lossyScale;

            keyboardCopy = GameObject.Instantiate(src.gameObject);
            keyboardCopy.name = "ExScoring_SearchKeyboard";
            keyboardCopy.transform.SetParent(null, true);   // scene root: localScale == world scale
            keyboardCopy.transform.position = wpos;
            keyboardCopy.transform.rotation = wrot;
            keyboardCopy.transform.localScale = wscale;
            keyboardCopy.SetActive(false);
        }

        public static void Show()
        {
            EnsureCopy();
            if (keyboardCopy == null) return;

            keyboardCopy.SetActive(true);
            var ke = keyboardCopy.GetComponent<KeyboardEntry>();
            if (ke != null) ke.Show();

            SongSearch.query = SongSearch.query ?? "";
            ExScoring.shouldShowKeyboard = true;
            SongSearch.searchInProgress = true;
            SongSearch.UpdateLiveText();
        }

        public static void Hide()
        {
            ExScoring.shouldShowKeyboard = false;
            SongSearch.searchInProgress = false;
            if (keyboardCopy != null)
                keyboardCopy.SetActive(false);
        }
    }
}