using System;
using TMPro;
using UnityEngine;
using UnityEngine.Events;

namespace ExScoringMod
{
    /// <summary>
    /// Turns the Main page's native Tutorial button into "Online Play", the same way the Mod
    /// Settings mod turns the Party button into "Mod Settings": relabel it (removing its Localizer,
    /// which would put the text back) and replace its hit event. Shooting it opens the Online Play
    /// screen (OnlinePlayScreen), connecting first if Online Play is enabled. Set up from
    /// ReleaseNotesPanel.Setup() on every Main page entry (the menu scene, and so the button, is
    /// rebuilt after every song).
    /// </summary>
    internal static class OnlinePlayButton
    {
        private const string CenterPanelPath = "menu/ShellPage_Main/page/ShellPanel_Center";
        private const string TutorialName = "Tutorial";
        private const string Label = "Online Play";

        public static void Setup()
        {
            GameObject center = GameObject.Find(CenterPanelPath);
            if (center == null)
            {
                DebugLog.Log("[OnlinePlayButton] ShellPanel_Center not found");
                return;
            }

            Transform tutorial = FindTutorial(center.transform);
            if (tutorial == null)
            {
                DebugLog.Log("[OnlinePlayButton] Tutorial button not found, not adding Online Play");
                return;
            }

            Transform labelT = tutorial.Find("Label");
            TextMeshPro label = labelT != null ? labelT.GetComponent<TextMeshPro>() : tutorial.gameObject.GetComponentInChildren<TextMeshPro>(true);
            if (label != null)
            {
                Localizer localizer = label.gameObject.GetComponent<Localizer>();
                if (localizer != null) GameObject.Destroy(localizer);
                label.text = Label;
            }

            Transform buttonT = tutorial.Find("Button");
            GunButton button = buttonT != null ? buttonT.GetComponent<GunButton>() : tutorial.gameObject.GetComponentInChildren<GunButton>(true);
            if (button == null)
            {
                DebugLog.Log("[OnlinePlayButton] no GunButton on the Tutorial button");
                return;
            }

            button.onHitEvent = new UnityEvent();
            button.onHitEvent.AddListener(new Action(OnShot));
        }

        private static Transform FindTutorial(Transform center)
        {
            Transform byName = center.Find(TutorialName);
            if (byName != null) return byName;

            // Fallback: the child whose label reads "Tutorial" (or already reads "Online Play").
            for (int i = 0; i < center.childCount; i++)
            {
                Transform child = center.GetChild(i);
                TextMeshPro text = child.gameObject.GetComponentInChildren<TextMeshPro>(true);
                if (text == null || text.text == null) continue;
                if (text.text.IndexOf("tutorial", StringComparison.OrdinalIgnoreCase) >= 0 || text.text == Label)
                {
                    DebugLog.Log($"[OnlinePlayButton] Tutorial button found by label: {child.name}");
                    return child;
                }
            }
            return null;
        }

        private static void OnShot()
        {
            DebugLog.Log("[OnlinePlayButton] shot");
            if (Config.EnableOnlinePlay) MpSession.Connect();
            OnlinePlayScreen.Open();
        }
    }
}