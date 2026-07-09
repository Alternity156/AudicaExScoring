using UnityEngine;
using MelonLoader;

namespace ExScoringMod
{
    /// <summary>
    /// Reveals the game's real Modifiers page (menu/ShellPage_Modifiers/page) as an Options
    /// category, instead of drawing custom rows on the cloned OptionsMenu (like OptionsMenuClone
    /// does for Colors/Customize/etc). It stays under its original parent (ShellPage_Modifiers) —
    /// reparenting it elsewhere caused it to snap to the wrong spot — and we apply a known-good
    /// local position/rotation/scale (found via UnityExplorer) so it lines up with where the other
    /// Options categories appear. We also disable its own Back button since our category list's
    /// Back handles navigation, and flip the ShellPage's active/interactable flags (mirroring
    /// ExScoring.ShowLaunchPanel/HideLaunchPanel) so its GunButtons actually respond to shots.
    /// </summary>
    internal static class ModifiersPage
    {
        private const string RootPath = "menu/ShellPage_Modifiers";
        private const string PagePath = "menu/ShellPage_Modifiers/page";

        private static GameObject pageObj;
        private static ShellPage shellPage;
        private static GameObject backParent;

        private static bool cached = false;
        private static Transform originalParent;
        private static Vector3 originalLocalPosition;
        private static Quaternion originalLocalRotation;
        private static Vector3 originalLocalScale;

        private static bool EnsureRefs()
        {
            if (pageObj != null && shellPage != null) return true;

            GameObject root = GameObject.Find(RootPath);
            if (root == null) { MelonLogger.Log("[ModifiersPage] root not found: " + RootPath); return false; }

            shellPage = root.GetComponent<ShellPage>();
            if (shellPage == null) { MelonLogger.Log("[ModifiersPage] ShellPage component not found on " + RootPath); return false; }

            pageObj = GameObject.Find(PagePath);
            if (pageObj == null) { MelonLogger.Log("[ModifiersPage] page not found: " + PagePath); return false; }

            Transform bp = pageObj.transform.Find("backParent");
            backParent = bp != null ? bp.gameObject : null;

            if (!cached)
            {
                originalParent = pageObj.transform.parent;
                originalLocalPosition = pageObj.transform.localPosition;
                originalLocalRotation = pageObj.transform.localRotation;
                originalLocalScale = pageObj.transform.localScale;
                cached = true;
                MelonLogger.Log("[ModifiersPage] cached original transform");
            }

            return true;
        }

        // Good spot, found via UnityExplorer — this is WORLD position (UnityExplorer's "Position"
        // field), not local. Rotation/scale were left at identity/1,1,1.
        private static readonly Vector3 ShownWorldPosition = new Vector3(10.75f, 0.5f, 0f);
        private static readonly Quaternion ShownLocalRotation = Quaternion.identity;
        private static readonly Vector3 ShownLocalScale = new Vector3(1f, 1f, 1f);

        public static void Show()
        {
            if (!EnsureRefs()) return;

            if (backParent != null) backParent.SetActive(false);

            // Stays under its original parent (ShellPage_Modifiers) — reparenting elsewhere caused
            // it to snap to the wrong spot. Apply the known-good world position directly.
            pageObj.transform.position = ShownWorldPosition;
            pageObj.transform.localRotation = ShownLocalRotation;
            pageObj.transform.localScale = ShownLocalScale;

            shellPage.mActive = true;
            shellPage.mInteractable = true;
            shellPage.mTransitioning = false;

            pageObj.SetActive(true);
            MelonLogger.Log("[ModifiersPage] shown");
        }

        public static void Hide()
        {
            if (pageObj == null) return;

            shellPage.mActive = false;
            shellPage.mInteractable = false;

            pageObj.SetActive(false);

            // Restore original local transform + Back button so the page is left exactly as
            // Audica authored it, in case anything else ever expects to find it untouched.
            pageObj.transform.localPosition = originalLocalPosition;
            pageObj.transform.localRotation = originalLocalRotation;
            pageObj.transform.localScale = originalLocalScale;
            if (backParent != null) backParent.SetActive(true);

            MelonLogger.Log("[ModifiersPage] hidden");
        }
    }
}