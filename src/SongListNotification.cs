using TMPro;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// Owns the "new online songs" notification text on the song page. The old filter UI used to
    /// live alongside it under ShellPanel_Left; with filters retired we simply hide every child of
    /// ShellPanel_Left except the notification, leaving it where it is.
    /// </summary>
    public static class SongListNotification
    {
        internal static GameObject notificationPanel;
        private static TextMeshPro notificationText;

        private const string NotificationName = "ShellPanel_SongListNotification";

        /// <summary>Called on song-page enable. Idempotent: re-hiding is harmless.</summary>
        public static void Setup()
        {
            var left = GameObject.Find("menu/ShellPage_Song/page/ShellPanel_Left");
            if (left == null) return;

            Transform t = left.transform;
            for (int i = 0; i < t.childCount; i++)
            {
                Transform child = t.GetChild(i);
                bool keep = child.name == NotificationName;
                child.gameObject.SetActive(keep);

                if (keep)
                {
                    notificationPanel = child.gameObject;
                    if (notificationText == null)
                        notificationText = notificationPanel.GetComponentInChildren<TextMeshPro>();
                }
            }
        }

        public static void SetText(string text)
        {
            if (notificationText != null)
                notificationText.text = text;
        }
    }
}