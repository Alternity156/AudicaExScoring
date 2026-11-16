using System;
using System.Collections.Generic;
using TMPro;
using UnhollowerRuntimeLib;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// The watched player's text popups in the spectator window: the score popup (the game's
    /// points and multiplier, or this mod's EX judgement text with the player's own size, opacity
    /// and abbreviation settings) and the early / late popups, exactly as they saw them.
    ///
    /// The watched player's side (SpectatorEvents, a postfix on TextPopupPool.CreatePopup) sends
    /// each popup as a "popup" event: the finished text of both lines, whether it is rich text,
    /// which pool it came from, and where the game put it (position, rotation, scale in their
    /// play space). With popups turned off in their game nothing is created and nothing is sent.
    ///
    /// Here: SpectatorTargets hands over each popup event (Add) and calls Update every frame with
    /// the window's song position; a popup is shown when that position reaches the event's tick.
    ///
    /// The game's popups can't be copied: outside a song there is no popup, pool or prefab loaded
    /// at all (UnityExplorer dump 2026-11-15: they live under ScoreKeeper). So a popup is built
    /// here like the game's (same dump, in a song):
    ///   ScorePopup: root (TextPopup, Animation) > "text" (TextMeshPro, LiberationSans SDF, size 3,
    ///     white, centred, rect 1 x 1) and "multiplier" (the same, grey 0.47).
    ///   EarlyPopup / LatePopup: root > "text" (Teko-Regular / Teko-Medium SDF, size 5, rich text,
    ///     yellow (1, 0.89, 0) / blue (0, 0.66, 1)).
    /// and animated from the clips sampled at 60 fps in that dump (0.6 s, legacy, no events):
    ///   score_popup_animation (score without a multiplier, early, late): the text rises from
    ///     y 0.231 to 1.031, swells to 1.2 by 0.083 s and shrinks to 0.7, alpha 0.8 until 0.18 s
    ///     then down to 0 by 0.57 s; the multiplier text is off. At 0.6 s the text is switched off.
    ///   score_popup_mult_animation: the same scale and alpha on both texts, the text rising from
    ///     y 0.081 to 1.881 and the multiplier from 0.381 to 2.181.
    /// The root itself isn't animated: it stays where CreatePopup put it.
    ///
    /// From the UnityExplorer console: ExScoringMod.SpectatorPopups.DebugDump();
    /// </summary>
    internal static class SpectatorPopups
    {
        public const string KindScore = "score";
        public const string KindEarly = "early";
        public const string KindLate = "late";

        private const float ClipLength = 0.6f;
        private const float SampleRate = 60f;
        private const int MaxShown = 40;            // the game's pools hold 30 + 10 + 10
        private const double LateMs = 600;          // an event this far behind the song position isn't shown any more
        private const int MaxPending = 256;

        private const float ScoreFontSize = 3f, TimingFontSize = 5f;
        private static readonly Color ScoreColor = Color.white;
        private static readonly Color ExtraColor = new Color(0.47f, 0.47f, 0.47f, 1f);
        private static readonly Color EarlyColor = new Color(1f, 0.89f, 0f, 1f);
        private static readonly Color LateColor = new Color(0f, 0.66f, 1f, 1f);
        private const string EarlyFontName = "Teko-Regular SDF", LateFontName = "Teko-Medium SDF";

        // The clips, sampled every 1/60 s (37 samples, 0 .. 0.6 s).
        private static readonly float[] NormalY = { 0.231f, 0.231f, 0.233f, 0.238f, 0.248f, 0.26f, 0.275f, 0.293f, 0.314f, 0.337f, 0.362f, 0.39f, 0.418f, 0.449f, 0.48f, 0.513f, 0.546f, 0.58f, 0.614f, 0.648f, 0.682f, 0.716f, 0.749f, 0.782f, 0.813f, 0.844f, 0.872f, 0.899f, 0.925f, 0.948f, 0.968f, 0.987f, 1.002f, 1.014f, 1.023f, 1.029f, 1.031f };
        private static readonly float[] TextScale = { 1f, 1.021f, 1.07f, 1.13f, 1.179f, 1.2f, 1.198f, 1.194f, 1.186f, 1.176f, 1.163f, 1.148f, 1.131f, 1.112f, 1.092f, 1.07f, 1.048f, 1.024f, 1f, 0.975f, 0.95f, 0.925f, 0.9f, 0.876f, 0.852f, 0.83f, 0.808f, 0.788f, 0.769f, 0.752f, 0.737f, 0.724f, 0.714f, 0.706f, 0.702f, 0.7f, 0.7f };
        private static readonly float[] Alpha = { 0.8f, 0.8f, 0.8f, 0.8f, 0.8f, 0.8f, 0.8f, 0.8f, 0.8f, 0.8f, 0.8f, 0.8f, 0.79f, 0.77f, 0.75f, 0.72f, 0.68f, 0.65f, 0.61f, 0.56f, 0.52f, 0.47f, 0.42f, 0.38f, 0.33f, 0.28f, 0.24f, 0.19f, 0.15f, 0.12f, 0.08f, 0.05f, 0.03f, 0.01f, 0f, 0f, 0f };
        private static readonly float[] MultTextY = { 0.081f, 0.081f, 0.085f, 0.098f, 0.118f, 0.146f, 0.181f, 0.221f, 0.268f, 0.32f, 0.377f, 0.438f, 0.503f, 0.571f, 0.641f, 0.714f, 0.789f, 0.865f, 0.942f, 1.019f, 1.096f, 1.172f, 1.247f, 1.32f, 1.391f, 1.459f, 1.524f, 1.585f, 1.642f, 1.694f, 1.74f, 1.781f, 1.816f, 1.843f, 1.864f, 1.877f, 1.881f };
        private static readonly float[] MultExtraY = { 0.381f, 0.381f, 0.385f, 0.398f, 0.418f, 0.446f, 0.481f, 0.521f, 0.568f, 0.62f, 0.677f, 0.738f, 0.803f, 0.871f, 0.941f, 1.015f, 1.089f, 1.165f, 1.242f, 1.319f, 1.396f, 1.472f, 1.547f, 1.62f, 1.691f, 1.759f, 1.824f, 1.885f, 1.942f, 1.994f, 2.04f, 2.081f, 2.116f, 2.143f, 2.164f, 2.177f, 2.181f };
        private static readonly float[] MultExtraScale = { 1f, 1.021f, 1.07f, 1.13f, 1.179f, 1.2f, 1.198f, 1.194f, 1.187f, 1.177f, 1.165f, 1.151f, 1.135f, 1.117f, 1.098f, 1.077f, 1.056f, 1.033f, 1.01f, 0.986f, 0.962f, 0.938f, 0.914f, 0.89f, 0.867f, 0.844f, 0.823f, 0.802f, 0.783f, 0.765f, 0.749f, 0.735f, 0.723f, 0.713f, 0.706f, 0.702f, 0.7f };

        private class Pending
        {
            public MpSpectateEvent E;
            public double Ms;
        }

        private class Shown
        {
            public GameObject Root;
            public TextMeshPro Text, Extra;
            public Color TextColor;
            public bool WithExtra;
            public float StartedAt = -1f;  // unscaled time; < 0 = free
        }

        private static readonly List<Pending> pending = new List<Pending>();
        private static readonly List<Shown> pool = new List<Shown>();
        private static TMP_FontAsset earlyFont, lateFont;
        private static bool fontsLooked;
        private static int shownTotal, droppedLate, failed;
        private static string lastError;

        private static void Log(string message) => DebugLog.Log("[SpectatorPopups] " + message);

        /// <summary>A popup event of the watched player, with the song time (ms) it happened at.</summary>
        internal static void Add(MpSpectateEvent e, double ms)
        {
            if (e == null || e.p == null || e.p.Length != 3) return;
            if (pending.Count >= MaxPending) pending.RemoveAt(0);
            pending.Add(new Pending { E = e, Ms = ms });
        }

        /// <summary>Every frame from SpectatorTargets: shows the popups whose time has come and moves the ones on screen.</summary>
        internal static void Update(double nowMs, Transform space)
        {
            try
            {
                for (int i = pending.Count - 1; i >= 0; i--)
                {
                    Pending item = pending[i];
                    if (item.Ms > nowMs) continue;
                    pending.RemoveAt(i);
                    if (nowMs - item.Ms > LateMs) droppedLate++;
                    else if (space != null) Show(item.E, space);
                }

                float now = Time.unscaledTime;
                for (int i = 0; i < pool.Count; i++)
                {
                    Shown popup = pool[i];
                    if (popup.StartedAt < 0f) continue;
                    if (popup.Root == null)
                    {
                        popup.StartedAt = -1f;
                        continue;
                    }
                    float time = now - popup.StartedAt;
                    if (time >= ClipLength)
                    {
                        popup.Root.SetActive(false);
                        popup.StartedAt = -1f;
                        continue;
                    }
                    Animate(popup, time);
                }
            }
            catch (Exception ex)
            {
                failed++;
                if (ex.Message != lastError)
                {
                    lastError = ex.Message;
                    Log("popups failed: " + ex.Message);
                }
            }
        }

        /// <summary>Hides every popup and forgets the waiting ones (the song position jumped, or the chart is gone).</summary>
        internal static void Clear()
        {
            pending.Clear();
            for (int i = 0; i < pool.Count; i++)
            {
                if (pool[i].Root != null && pool[i].Root.activeSelf) pool[i].Root.SetActive(false);
                pool[i].StartedAt = -1f;
            }
        }

        private static float Sample(float[] table, float time)
        {
            float at = Mathf.Clamp(time * SampleRate, 0f, table.Length - 1);
            int index = Mathf.Min((int)at, table.Length - 2);
            return Mathf.Lerp(table[index], table[index + 1], at - index);
        }

        private static void Animate(Shown popup, float time)
        {
            float alpha = Sample(Alpha, time);
            float scale = Sample(TextScale, time);

            Transform text = popup.Text.transform;
            text.localPosition = new Vector3(0f, Sample(popup.WithExtra ? MultTextY : NormalY, time), 0f);
            text.localScale = new Vector3(scale, scale, scale);
            Color color = popup.TextColor;
            color.a = alpha;
            popup.Text.color = color;

            if (popup.WithExtra && popup.Extra != null)
            {
                float extraScale = Sample(MultExtraScale, time);
                Transform extra = popup.Extra.transform;
                extra.localPosition = new Vector3(0f, Sample(MultExtraY, time), 0f);
                extra.localScale = new Vector3(extraScale, extraScale, extraScale);
                Color extraColor = ExtraColor;
                extraColor.a = alpha;
                popup.Extra.color = extraColor;
            }
        }

        private static void Show(MpSpectateEvent e, Transform space)
        {
            Shown popup = Take(space);
            if (popup == null) return;

            bool early = e.pk == KindEarly, late = e.pk == KindLate;
            bool withExtra = !early && !late && !string.IsNullOrEmpty(e.extra);

            Transform root = popup.Root.transform;
            if (root.parent != space) root.SetParent(space, false);
            // Where the game put it, in the watched player's play space.
            root.localPosition = new Vector3(e.p[0], e.p[1], e.p[2]);
            root.localRotation = e.q != null && e.q.Length == 4 ? new Quaternion(e.q[0], e.q[1], e.q[2], e.q[3]) : Quaternion.identity;
            root.localScale = e.s != null && e.s.Length == 3 ? new Vector3(e.s[0], e.s[1], e.s[2]) : Vector3.one;

            LookForFonts();
            TMP_FontAsset font = early ? earlyFont : late ? lateFont : null;
            if (font != null && popup.Text.font != font) popup.Text.font = font;
            else if (font == null && defaultFont != null && popup.Text.font != defaultFont) popup.Text.font = defaultFont;

            popup.Text.fontSize = early || late ? TimingFontSize : ScoreFontSize;
            popup.Text.richText = early || late || e.rich;
            popup.Text.text = e.text ?? "";
            popup.TextColor = early ? EarlyColor : late ? LateColor : ScoreColor;
            popup.WithExtra = withExtra;

            if (popup.Extra != null)
            {
                if (popup.Extra.gameObject.activeSelf != withExtra) popup.Extra.gameObject.SetActive(withExtra);
                if (withExtra) popup.Extra.text = e.extra;
            }

            popup.StartedAt = Time.unscaledTime;
            Animate(popup, 0f);
            if (!popup.Root.activeSelf) popup.Root.SetActive(true);
            shownTotal++;
        }

        private static TMP_FontAsset defaultFont;

        /// <summary>A free popup, a new one, or (all in use) the oldest.</summary>
        private static Shown Take(Transform space)
        {
            Shown oldest = null;
            for (int i = 0; i < pool.Count; i++)
            {
                Shown popup = pool[i];
                if (popup.Root == null)
                {
                    pool.RemoveAt(i--); // destroyed with what it hung under
                    continue;
                }
                if (popup.StartedAt < 0f) return popup;
                if (oldest == null || popup.StartedAt < oldest.StartedAt) oldest = popup;
            }
            if (pool.Count >= MaxShown) return oldest;

            try
            {
                Shown made = new Shown();
                made.Root = new GameObject("ExScoring_SpectatorPopup");
                made.Root.layer = MpPresence.SongOnlyLayer;
                made.Root.transform.SetParent(space, false);
                made.Root.SetActive(false);
                made.Text = MakeText(made.Root.transform, "text");
                made.Extra = MakeText(made.Root.transform, "multiplier");
                if (made.Text == null)
                {
                    GameObject.Destroy(made.Root);
                    return null;
                }
                if (defaultFont == null) defaultFont = made.Text.font;
                pool.Add(made);
                return made;
            }
            catch (Exception ex)
            {
                failed++;
                if (ex.Message != lastError)
                {
                    lastError = ex.Message;
                    Log("a popup can't be made: " + ex.Message);
                }
                return null;
            }
        }

        private static TextMeshPro MakeText(Transform parent, string name)
        {
            GameObject go = new GameObject(name);
            go.layer = MpPresence.SongOnlyLayer;
            go.transform.SetParent(parent, false);
            TextMeshPro text = go.AddComponent<TextMeshPro>();
            if (text == null) return null;
            // As the game's popup texts: the default font and material, centred, in a 1 x 1 rect.
            text.fontSize = ScoreFontSize;
            text.alignment = TextAlignmentOptions.Center;
            text.enableWordWrapping = false;
            text.overflowMode = TextOverflowModes.Overflow;
            text.richText = false;
            text.color = Color.white;
            text.rectTransform.sizeDelta = new Vector2(1f, 1f);
            go.layer = MpPresence.SongOnlyLayer;
            return text;
        }

        /// <summary>The early / late popups' fonts, by name, when they are loaded here; else the default font stays.</summary>
        private static void LookForFonts()
        {
            if (fontsLooked) return;
            fontsLooked = true;
            try
            {
                string names = "";
                foreach (UnityEngine.Object o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<TMP_FontAsset>()))
                {
                    if (o == null) continue;
                    names += o.name + ", ";
                    if (o.name == EarlyFontName) earlyFont = o.TryCast<TMP_FontAsset>();
                    else if (o.name == LateFontName) lateFont = o.TryCast<TMP_FontAsset>();
                }
                Log($"fonts loaded here: {names}| early popup font: {(earlyFont != null ? earlyFont.name : "not found, the default is used")}, late popup font: {(lateFont != null ? lateFont.name : "not found, the default is used")}");
            }
            catch (Exception ex)
            {
                Log("looking for the popup fonts failed (" + ex.Message + "): the default font is used");
            }
        }

        public static void DebugDump()
        {
            MelonLoader.MelonLogger.Log("[SpectatorPopups] -- dump --");
            int onScreen = 0;
            for (int i = 0; i < pool.Count; i++) if (pool[i].StartedAt >= 0f) onScreen++;
            MelonLoader.MelonLogger.Log($"  shown={shownTotal} droppedLate={droppedLate} failed={failed} waiting={pending.Count} made={pool.Count} onScreen={onScreen} lastError={lastError ?? "none"}");
            MelonLoader.MelonLogger.Log($"  fonts: looked={fontsLooked} default={(defaultFont != null ? defaultFont.name : "none")} early={(earlyFont != null ? earlyFont.name : "none")} late={(lateFont != null ? lateFont.name : "none")}");
            for (int i = 0; i < pool.Count && i < 6; i++)
            {
                Shown popup = pool[i];
                if (popup.Root == null || popup.StartedAt < 0f) continue;
                MelonLoader.MelonLogger.Log($"  [{i}] \"{popup.Text.text}\" pos={popup.Root.transform.position} scale={popup.Root.transform.lossyScale} layer={popup.Text.gameObject.layer} age={(Time.unscaledTime - popup.StartedAt):0.00}");
            }
        }
    }
}