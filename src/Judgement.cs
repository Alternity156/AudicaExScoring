using MelonLoader;
using System;
using UnityEngine;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        public static float judgementImpeccableWeight = 1f;
        public static float judgementFantasticWeight = 0.925f;
        public static float judgementExcellentWeight = 0.85f;
        public static float judgementGreatWeight = 0.75f;
        public static float judgementGoodWeight = 0.5f;

        public static float judgementOkWeight = 1f;

        public static float judgementImpeccableTimingWindowMs = 15f;
        public static float judgementFantasticTimingWindowMs = 25f;
        public static float judgementExcellentTimingWindowMs = 45f;
        public static float judgementGreatTimingWindowMs = 70f;
        public static float judgementGoodTimingWindowMs = 100f;

        public static float judgementImpeccableChainAverage = 0.9f;
        public static float judgementFantasticChainAverage = 0.75f;
        public static float judgementExcellentChainAverage = 0.6f;
        public static float judgementGreatChainAverage = 0.4f;
        public static float judgementGoodChainAverage = 0.0f;

        /* Real radius from 0 (pixel perfect) to 5 (entire target)
        */
        public static float judgementImpeccableAimRadius = 0.75f;
        public static float judgementFantasticAimRadius = 1.3875f;
        public static float judgementExcellentAimRadius = 2.2375f;
        public static float judgementGreatAimRadius = 4.15f;
        public static float judgementGoodAimRadius = 5f;

        public enum Judgement
        {
            Impeccable,
            Fantastic,
            Excellent,
            Great,
            Good,
            Miss,
            OK
        }

        public static Color GetJudgementColor(Judgement judgement)
        {
            switch (judgement)
            {
                case Judgement.Impeccable:
                    return Color.cyan;
                case Judgement.Fantastic:
                case Judgement.OK:
                    return Color.white;
                case Judgement.Excellent:
                    return new Color(1.0f, 0.84f, 0f); //Gold
                case Judgement.Great:
                    return Color.green;
                case Judgement.Good:
                    return new Color(0.133f, 0.545f, 0.133f); //Forest Green
                case Judgement.Miss:
                    return Color.red;
            }
            return Color.white;
        }

        public static float GetJudgementScore(Judgement judgement)
        {
            switch (judgement)
            {
                case Judgement.Impeccable:
                    return judgementImpeccableWeight;
                case Judgement.Fantastic:
                    return judgementFantasticWeight;
                case Judgement.Excellent:
                    return judgementExcellentWeight;
                case Judgement.Great:
                    return judgementGreatWeight;
                case Judgement.Good:
                    return judgementGoodWeight;
            }
            return 0f;
        }

        public static string GetMeleeJudgementText()
        {
            return "";
        }

        // Short prefix used by the "Abbreviated Judgements" popup setting. Great is "Gr" so it
        // doesn't collide with Good's "G" (Great Aim = GrA, Good Aim = GA).
        private static string GetJudgementAbbreviation(Judgement judgement)
        {
            switch (judgement)
            {
                case Judgement.Impeccable:
                    return "I";
                case Judgement.Fantastic:
                    return "F";
                case Judgement.Excellent:
                    return "E";
                case Judgement.Great:
                    return "Gr";
                case Judgement.Good:
                    return "G";
            }
            return "";
        }

        // Builds one judgement label, e.g. "Impeccable Timing!!" — or, with the abbreviated popup
        // setting on, "IT" (abbreviatedSuffix is the single letter standing in for fullSuffix).
        private static string GetJudgementLabel(Judgement judgement, string fullSuffix, string abbreviatedSuffix)
        {
            if (Config.ExScorePopupAbbreviated)
            {
                string abbreviation = GetJudgementAbbreviation(judgement);
                return abbreviation == "" ? "" : abbreviation + abbreviatedSuffix;
            }

            switch (judgement)
            {
                case Judgement.Impeccable:
                    return "Impeccable " + fullSuffix + "!!";
                case Judgement.Fantastic:
                    return "Fantastic " + fullSuffix + "!";
                case Judgement.Excellent:
                    return "Excellent " + fullSuffix;
                case Judgement.Great:
                    return "Great " + fullSuffix;
                case Judgement.Good:
                    return "Good " + fullSuffix;
            }
            return "";
        }

        public static string GetChainJudgementText(Judgement judgement)
        {
            string colorHex = "#" + ColorUtility.ToHtmlStringRGB(GetJudgementColor(judgement));

            // Full text says "Tracing", abbreviated uses C for Chain (IC, FC, ...) so it can't be
            // confused with Timing's T.
            return "<color=" + colorHex + ">" + GetJudgementLabel(judgement, "Tracing", "C") + "</color>";
        }

        public static string GetJudgementText(Judgement timingJudgement, Judgement aimJudgement)
        {
            string timingColorHex = "#" + ColorUtility.ToHtmlStringRGB(GetJudgementColor(timingJudgement));
            string aimColorHex = "#" + ColorUtility.ToHtmlStringRGB(GetJudgementColor(aimJudgement));

            return "<color=" + timingColorHex + ">" + GetJudgementLabel(timingJudgement, "Timing", "T") + "</color>\n"
                + "<color=" + aimColorHex + ">" + GetJudgementLabel(aimJudgement, "Aim", "A") + "</color>";
        }

        public static Judgement GetTimingJudgement(float msOffset)
        {
            float x = Math.Abs(msOffset);

            if (x <= judgementImpeccableTimingWindowMs) return Judgement.Impeccable;
            if (x <= judgementFantasticTimingWindowMs) return Judgement.Fantastic;
            if (x <= judgementExcellentTimingWindowMs) return Judgement.Excellent;
            if (x <= judgementGreatTimingWindowMs) return Judgement.Great;
            if (x <= judgementGoodTimingWindowMs) return Judgement.Good;
            return Judgement.Miss;
        }

        public static Judgement GetAimJudgement(Target target, Vector3 intersectionPoint)
        {
            Vector3 targetPos = target.GetContactPosition();
            float distanceFromCenter = (targetPos - intersectionPoint).magnitude;

            if (distanceFromCenter <= judgementImpeccableAimRadius) return Judgement.Impeccable;
            if (distanceFromCenter <= judgementFantasticAimRadius) return Judgement.Fantastic;
            if (distanceFromCenter <= judgementExcellentAimRadius) return Judgement.Excellent;
            if (distanceFromCenter <= judgementGreatAimRadius) return Judgement.Great;
            if (distanceFromCenter <= judgementGoodAimRadius) return Judgement.Good;
            return Judgement.Miss;
        }

        public static Judgement GetChainJudgement(float chainAverage)
        {
            if (chainAverage >= judgementImpeccableChainAverage) return Judgement.Impeccable;
            else if (chainAverage >= judgementFantasticChainAverage) return Judgement.Fantastic;
            else if (chainAverage >= judgementExcellentChainAverage) return Judgement.Excellent;
            else if (chainAverage >= judgementGreatChainAverage) return Judgement.Great;
            else if (chainAverage >= judgementGoodChainAverage) return Judgement.Good;
            else return Judgement.Miss;
        }

        public static float GetMaxJudgementScoreForCue(SongCues.Cue cue)
        {
            float score = 0f;

            if (cue.behavior == Target.TargetBehavior.Vertical ||
                cue.behavior == Target.TargetBehavior.Horizontal ||
                cue.behavior == Target.TargetBehavior.ChainStart ||
                cue.behavior == Target.TargetBehavior.Standard ||
                cue.behavior == Target.TargetBehavior.Hold)
            {
                score += judgementImpeccableWeight * 2;
            }
            if (cue.behavior == Target.TargetBehavior.Chain && cue.chainNext == null)
            {
                score += judgementImpeccableWeight;
            }
            if (cue.behavior == Target.TargetBehavior.Hold ||
                cue.behavior == Target.TargetBehavior.Melee)
            {
                score += 1;
            }

            return score;
        }

        public static float GetJudgementScoreFromJudgement(Judgement judgement)
        {
            if (judgement == Judgement.Impeccable) return judgementImpeccableWeight;
            if (judgement == Judgement.Fantastic) return judgementFantasticWeight;
            if (judgement == Judgement.Excellent) return judgementExcellentWeight;
            if (judgement == Judgement.Great) return judgementGreatWeight;
            if (judgement == Judgement.Good) return judgementGoodWeight;
            if (judgement == Judgement.OK) return judgementOkWeight;
            return 0;
        }

        /// <summary>
        /// Max possible judgement score for the WHOLE chart of songId at the given difficulty — the
        /// equivalent of ITGmania's m_iPossibleDancePoints (computed once from the full chart at
        /// load), as opposed to currentMaxPossibleJudgementScore, which only grows as cues are
        /// judged (ITGmania's m_iCurPossibleDancePoints, what the in-game HUD shows).
        ///
        /// HookUpChains is required: a cold GetCues() call never populates chainNext (see
        /// RunDataRecalculator.BuildChainTailLookup), which would make every Chain cue look like a
        /// tail. Main thread only (Il2Cpp). Returns 0 (never throws) if the song isn't installed,
        /// has no chart at that difficulty, or anything else goes wrong — callers treat 0 as
        /// "unknown" and fall back to whatever max they already have.
        /// </summary>
        public static float GetMaxPossibleJudgementScore(string songId, KataConfig.Difficulty difficulty)
        {
            float maxJudgementScore = 0f;

            try
            {
                if (string.IsNullOrEmpty(songId) || SongList.I == null) return 0f;

                var songData = SongList.I.GetSong(songId);
                if (songData == null) return 0f;

                var cues = SongCues.GetCues(songData, difficulty);
                if (cues == null) return 0f;

                SongCues.HookUpChains(cues);

                foreach (SongCues.Cue cue in cues)
                {
                    maxJudgementScore += GetMaxJudgementScoreForCue(cue);
                }
            }
            catch (Exception ex)
            {
                DebugLog.Log($"[ExScoring] Failed to compute max judgement score for {songId}/{difficulty}: {ex}");
                return 0f;
            }

            return maxJudgementScore;
        }

        /// <summary>
        /// Running percentage against only the cues judged so far (ITGmania's "current possible").
        /// This is what the in-game HUD shows — it must NOT use the whole-chart max, or the live
        /// percentage would start near 0% and climb through the song.
        /// </summary>
        public static float GetCurrentMaxPossibleJudgementPercentage()
        {
            return (judgementScore / currentMaxPossibleJudgementScore) * 100;
        }

        // Whole-chart max for the current run, computed lazily the first time a failed run's final
        // percentage is asked for (see GetFinalJudgementPercentage). -1 = not computed yet for this
        // run. Reset by ResetExScore() at the start of every run.
        private static float totalMaxPossibleJudgementScore = -1f;

        /// <summary>
        /// Final percentage for the results screen. A completed run judged every cue, so the
        /// running max already equals the whole-chart max and the result is unchanged. A failed run
        /// is scored against the whole chart, ITGmania-style: targets never reached add 0 to the
        /// score but still count toward the max (hit 1 of 100 targets -> ~1%, not 100%).
        /// Falls back to the running max if the chart couldn't be read (song gone, etc.), and never
        /// uses a max smaller than what was actually judged.
        /// </summary>
        public static float GetFinalJudgementPercentage()
        {
            float max = currentMaxPossibleJudgementScore;

            if (currentRunFailed)
            {
                if (totalMaxPossibleJudgementScore < 0f)
                {
                    totalMaxPossibleJudgementScore = selectedSongData != null
                        ? GetMaxPossibleJudgementScore(selectedSongData.songID, KataConfig.I.GetDifficulty())
                        : 0f;
                }

                max = Math.Max(max, totalMaxPossibleJudgementScore);
            }

            return max > 0f ? (judgementScore / max) * 100f : 0f;
        }
    }
}