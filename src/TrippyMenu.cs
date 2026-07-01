using UnityEngine;

namespace ExScoringMod
{
    internal static class TrippyMenu
    {
        private static float timer = 0f;
        private const float DefaultPhaseSeconds = 14.28f;

        /// <summary>Call once per frame. Only runs outside of actual gameplay (menus only).</summary>
        public static void Tick()
        {
            if (!Config.TrippyMenuEnabled) return;
            if (ExScoring.menuState == MenuState.State.Launched) return;

            float phaseTime = DefaultPhaseSeconds / Config.TrippyMenuSpeed;

            if (timer <= phaseTime)
            {
                timer += Time.deltaTime;
                GameplayModifiers.I.mPsychedeliaPhase = timer / phaseTime;
            }
            else
            {
                timer = 0f;
            }
        }

        /// <summary>Call when a song is actually launched, to stop/clear the effect for gameplay.</summary>
        public static void ResetOnSongStart()
        {
            timer = 0f;
            GameplayModifiers.I.mPsychedeliaPhase = 0f;
        }
    }
}