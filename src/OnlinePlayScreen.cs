using System;
using System.Collections;
using MelonLoader;
using UnityEngine;
using UnityEngine.Events;

namespace ExScoringMod
{
    /// <summary>
    /// The Online Play menu, opened from the Main page's Online Play button (OnlinePlayButton).
    /// Same approach as the Mod Settings mod: a copy of the whole native Settings page
    /// (ShellPage_Settings, with its own glass, title and Back button at the bottom) is shown in
    /// place of the Main page. The Main page is switched off entirely while it's open, so none of
    /// its buttons (including its own Back to the title screen) are there. The page's Back button
    /// is rewired to close it and bring the Main page back. Content is OnlinePlayMenu.Build.
    ///
    /// MenuState stays MainPage the whole time, so the game never takes the page over. Leaving the
    /// Main page for any reason (e.g. a match launching) hides it. The copy is a scene object
    /// like the menu, so it's rebuilt whenever the menu scene is.
    /// </summary>
    internal static class OnlinePlayScreen
    {
        private const string SettingsPagePath = "menu/ShellPage_Settings";
        private const string OptionsPath = "page/ShellPanel_Center/Settings/Options";
        private const string BackButtonPath = "page/backParent/back/Button";
        private const string Title = "Online Play";

        private static GameObject page;
        private static ShellPage shellPage;
        private static OptionsMenu menu;

        public static bool IsOpen { get; private set; }

        /// <summary>The screen's ShellPage (for buttons outside it that act as part of the current page).</summary>
        public static ShellPage Page => shellPage;

        /// <summary>True for this screen's OptionsMenu (the ShowPage patch keeps native pages off it).</summary>
        public static bool IsScreenMenu(OptionsMenu candidate)
        {
            return menu != null && candidate != null && candidate.Pointer == menu.Pointer;
        }

        public static void Open()
        {
            if (ExScoring.menuState != MenuState.State.MainPage || MenuState.I == null)
            {
                DebugLog.Log($"[OnlinePlayScreen] not opening: menuState={ExScoring.menuState}");
                return;
            }
            if (!EnsureCreated()) return;

            MenuState.I.mainPage.SetPageActive(false, false);
            shellPage.SetPageActive(true, true);
            IsOpen = true;

            // Let the page come up before drawing on it (same wait as Mod Settings).
            MelonCoroutines.Start(DrawAfter(0.1f));
            DebugLog.Log("[OnlinePlayScreen] opened");
        }

        /// <summary>The page's Back button: back to the Main page.</summary>
        public static void Close()
        {
            if (!IsOpen) return;
            Hide();
            if (MenuState.I != null && ExScoring.menuState == MenuState.State.MainPage)
            {
                MenuState.I.mainPage.SetPageActive(true, false);
            }
            DebugLog.Log("[OnlinePlayScreen] closed");
        }

        /// <summary>From SetStatePatch: leaving the Main page hides the screen (the new page takes over).</summary>
        public static void OnMenuStateChanged(MenuState.State to)
        {
            if (IsOpen && to != MenuState.State.MainPage) Hide();
        }

        /// <summary>
        /// Redraws next frame if open. Next frame, because a redraw wipes every row, including a
        /// button whose hit event may still be running.
        /// </summary>
        public static void RedrawIfShowing()
        {
            if (!IsOpen) return;
            MelonCoroutines.Start(DrawAfter(0f));
        }

        private static void Hide()
        {
            IsOpen = false;
            OnlinePlayMenu.CancelCodeEntry();
            if (shellPage != null) shellPage.SetPageActive(false, false);
        }

        private static IEnumerator DrawAfter(float seconds)
        {
            if (seconds > 0f) yield return new WaitForSeconds(seconds);
            else yield return null;

            if (IsOpen && menu != null) OptionsMenuClone.DrawOn(menu, shellPage, Title, OnlinePlayMenu.Build);
        }

        private static bool EnsureCreated()
        {
            if (page != null && shellPage != null && menu != null) return true; // Unity-null: rebuilt with the menu scene

            GameObject source = GameObject.Find(SettingsPagePath);
            if (source == null)
            {
                DebugLog.Log("[OnlinePlayScreen] " + SettingsPagePath + " not found");
                return false;
            }

            if (page != null) GameObject.Destroy(page);
            page = GameObject.Instantiate(source);
            page.name = "ExScoring_OnlinePlayPage";
            page.hideFlags |= HideFlags.DontUnloadUnusedAsset;

            shellPage = page.GetComponent<ShellPage>();
            Transform options = page.transform.Find(OptionsPath);
            menu = options != null ? options.GetComponent<OptionsMenu>() : null;
            Transform back = page.transform.Find(BackButtonPath);
            GunButton backButton = back != null ? back.GetComponent<GunButton>() : null;

            if (shellPage == null || menu == null || backButton == null)
            {
                DebugLog.Log($"[OnlinePlayScreen] copy incomplete (shellPage={shellPage != null}, options={menu != null}, back={backButton != null})");
                GameObject.Destroy(page);
                page = null;
                shellPage = null;
                menu = null;
                return false;
            }

            backButton.onHitEvent = new UnityEvent();
            backButton.onHitEvent.AddListener(new Action(Close));

            DebugLog.Log("[OnlinePlayScreen] created");
            return true;
        }
    }
}