using MelonLoader;
using TMPro;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// Owns the HowToPlay/ReleaseNotes swap under the Main page's ShellPanel_Left.
    /// Disables HowToPlay, enables ReleaseNotes, hides the ReleaseNotes logo, and repositions the
    /// text now that the logo is gone. The text's content is set by ReleaseNotesOnEnablePatch in
    /// Hooks.cs (patches ReleaseNotes.OnEnable directly), not here - that's the only way to catch
    /// it reliably on initial game boot as well as on later menu visits.
    /// </summary>
    public static class ReleaseNotesPanel
    {
        private const string HowToPlayName = "HowToPlay";
        private const string HowToPlayTitleName = "HowToPlay_Title";
        private const string ReleaseNotesName = "ReleaseNotes";
        private const string ContentPath = "ReleaseNotes/Scroll View/Viewport/Content";

        private static readonly Vector3 TextLocalPosition = new Vector3(0f, -10f, 0.003f);

        /// <summary>Called every time the Main page is entered. Idempotent: re-applying is harmless.</summary>
        public static void Setup()
        {
            GameObject left = GameObject.Find("menu/ShellPage_Main/page/ShellPanel_Left");
            if (left == null)
            {
                MelonLogger.Log("ReleaseNotesPanel: ShellPanel_Left not found");
                return;
            }

            Transform leftT = left.transform;

            Transform howToPlay = leftT.Find(HowToPlayName);
            if (howToPlay != null)
                howToPlay.gameObject.SetActive(false);
            else
                MelonLogger.Log("ReleaseNotesPanel: HowToPlay not found");

            Transform howToPlayTitle = leftT.Find(HowToPlayTitleName);
            if (howToPlayTitle != null)
            {
                TextMeshPro titleText = howToPlayTitle.GetComponent<TextMeshPro>();
                if (titleText != null)
                {
                    Localizer titleLocalizer = howToPlayTitle.GetComponent<Localizer>();
                    if (titleLocalizer != null)
                        GameObject.Destroy(titleLocalizer);

                    titleText.text = "Audica EX";
                }
                else
                {
                    MelonLogger.Log("ReleaseNotesPanel: HowToPlay_Title has no TextMeshPro");
                }
            }
            else
            {
                MelonLogger.Log("ReleaseNotesPanel: HowToPlay_Title not found");
            }

            Transform releaseNotes = leftT.Find(ReleaseNotesName);
            if (releaseNotes == null)
            {
                MelonLogger.Log("ReleaseNotesPanel: ReleaseNotes not found");
                return;
            }

            Transform content = leftT.Find(ContentPath);
            if (content == null)
            {
                MelonLogger.Log("ReleaseNotesPanel: Content not found");
                releaseNotes.gameObject.SetActive(true);
                return;
            }

            Transform logo = content.Find("logo_tm");
            if (logo != null)
                logo.gameObject.SetActive(false);
            else
                MelonLogger.Log("ReleaseNotesPanel: logo_tm not found");

            // Text content itself is handled by ReleaseNotesOnEnablePatch in Hooks.cs, which
            // skips the original OnEnable (no fetch, no "loading..." placeholder) and sets our
            // custom string directly - reliable regardless of when OnEnable first fires.
            Transform text = content.Find("text");
            if (text != null)
                text.localPosition = TextLocalPosition;
            else
                MelonLogger.Log("ReleaseNotesPanel: text not found");

            releaseNotes.gameObject.SetActive(true);
        }
    }
}