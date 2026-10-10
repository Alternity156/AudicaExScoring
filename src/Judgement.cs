using MelonLoader;
using System;
using System.Linq;
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

        public static float GetMaxPossibleJudgementScore(string songId)
        {
            float maxJudgementScore = 0;

            SongCues.Cue[] cues = SongCues.GetCues(SongList.I.GetSong(songId), KataConfig.Difficulty.Expert).ToArray();

            foreach (SongCues.Cue cue in cues)
            {
                maxJudgementScore += GetMaxJudgementScoreForCue(cue);
            }

            return maxJudgementScore;
        }

        public static float GetCurrentMaxPossibleJudgementPercentage()
        {
            return (judgementScore / currentMaxPossibleJudgementScore) * 100;
        }
    }
}