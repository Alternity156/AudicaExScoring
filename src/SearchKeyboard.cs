using UnityEngine;
using MelonLoader;
using UnhollowerRuntimeLib;   // Il2CppType (ML 0.5.7)
using UnhollowerBaseLib;      // TryCast

namespace ExScoringMod
{
    public static class SearchKeyboard
    {
        private static GameObject keyboardCopy = null;

        private static Vector3 KeyboardPosition =>
            new Vector3(Config.SearchKeyboardPosX, Config.SearchKeyboardPosY, Config.SearchKeyboardPosZ);

        private static Quaternion KeyboardRotation =>
            Quaternion.Euler(Config.SearchKeyboardTilt, 0f, 0f);

        /// <summary>
        /// Re-applies the configured position/rotation to the live keyboard copy, if it exists.
        /// Called when the user adjusts the position/tilt sliders in the options menu, so changes
        /// are visible immediately without needing to reopen the keyboard.
        /// </summary>
        public static void ApplyTransform()
        {
            if (keyboardCopy == null) return;
            keyboardCopy.transform.position = KeyboardPosition;
            keyboardCopy.transform.rotation = KeyboardRotation;
        }

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
                DebugLog.Log("[SearchKeyboard] No source keyboard found");
                return;
            }

            keyboardCopy = GameObject.Instantiate(src.gameObject);
            keyboardCopy.name = "ExScoring_SearchKeyboard";
            keyboardCopy.transform.SetParent(null, true);   // scene root: localScale == world scale
            keyboardCopy.transform.position = KeyboardPosition;
            keyboardCopy.transform.rotation = KeyboardRotation;
            keyboardCopy.transform.localScale = src.transform.lossyScale;
            keyboardCopy.SetActive(false);
        }

        public static void Show()
        {
            EnsureCopy();
            if (keyboardCopy == null) return;

            ApplyTransform();
            keyboardCopy.SetActive(true);
            var ke = keyboardCopy.GetComponent<KeyboardEntry>();
            if (ke != null) ke.Show();

            SongSearch.query = SongSearch.query ?? "";
            ExScoring.shouldShowKeyboard = true;
            SongSearch.searchInProgress = true;
            SongSearch.UpdateLiveText();
        }

        /// <summary>
        /// Show the keyboard WITHOUT entering song-search mode. Input routing then falls to whatever
        /// other context is active (e.g. playlist creation), gated by shouldShowKeyboard.
        /// </summary>
        public static void ShowGeneric()
        {
            EnsureCopy();
            if (keyboardCopy == null) return;

            ApplyTransform();
            keyboardCopy.SetActive(true);
            var ke = keyboardCopy.GetComponent<KeyboardEntry>();
            if (ke != null) ke.Show();

            ExScoring.shouldShowKeyboard = true;
        }

        public static void Hide()
        {
            ExScoring.shouldShowKeyboard = false;
            SongSearch.searchInProgress = false;
            if (keyboardCopy != null)
                keyboardCopy.SetActive(false);
        }

        /// <summary>True while the keyboard copy exists and is shown.</summary>
        public static bool IsVisible => keyboardCopy != null && keyboardCopy.activeSelf;

        /// <summary>
        /// Closes the keyboard no matter which context opened it (song search, playlist creation,
        /// lobby code entry) and clears every input-routing flag, so nothing is left stuck open.
        /// Called when a song launches. The typed search query is kept for the next search.
        /// </summary>
        public static void ForceClose()
        {
            bool wasOpen = IsVisible || ExScoring.shouldShowKeyboard || SongSearch.searchInProgress
                           || PlaylistNav.Creating || OnlinePlayMenu.EnteringCode;
            if (!wasOpen) return;

            try { if (PlaylistNav.Creating) PlaylistNav.CancelCreate(); }
            catch (System.Exception e) { DebugLog.Log("[SearchKeyboard] CancelCreate failed: " + e); }

            try { if (OnlinePlayMenu.EnteringCode) OnlinePlayMenu.CancelCodeEntry(); }
            catch (System.Exception e) { DebugLog.Log("[SearchKeyboard] CancelCodeEntry failed: " + e); }

            Hide();
            DebugLog.Log("[SearchKeyboard] Force-closed");
        }
    }
}