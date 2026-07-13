using MelonLoader;
using UnityEngine;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        // ITG "stock" grade thresholds (minimum Judgement % required for each grade), plus an
        // added F for failed runs. Tunable like the other weight/window fields in Judgement.cs.
        public static float gradeQuadThreshold = 100f;
        public static float gradeTristarThreshold = 99f;
        public static float gradeDoubleStarThreshold = 98f;
        public static float gradeStarThreshold = 96f;
        public static float gradeSPlusThreshold = 94f;
        public static float gradeSThreshold = 92f;
        public static float gradeSMinusThreshold = 89f;
        public static float gradeAPlusThreshold = 86f;
        public static float gradeAThreshold = 83f;
        public static float gradeAMinusThreshold = 80f;
        public static float gradeBPlusThreshold = 76f;
        public static float gradeBThreshold = 72f;
        public static float gradeBMinusThreshold = 68f;
        public static float gradeCPlusThreshold = 64f;
        public static float gradeCThreshold = 60f;
        public static float gradeCMinusThreshold = 55f;
        // Below gradeCMinusThreshold falls through to D.

        public enum Grade
        {
            Quad,
            Tristar,
            DoubleStar,
            Star,
            SPlus,
            S,
            SMinus,
            APlus,
            A,
            AMinus,
            BPlus,
            B,
            BMinus,
            CPlus,
            C,
            CMinus,
            D,
            F
        }

        /// <summary>
        /// Failed runs always grade F regardless of the percentage reached at the time of failure,
        /// matching stock ITG behavior. Otherwise walks the thresholds from best to worst.
        /// </summary>
        public static Grade GetGrade(float judgementPercent, bool failed)
        {
            if (failed) return Grade.F;

            if (judgementPercent >= gradeQuadThreshold) return Grade.Quad;
            if (judgementPercent >= gradeTristarThreshold) return Grade.Tristar;
            if (judgementPercent >= gradeDoubleStarThreshold) return Grade.DoubleStar;
            if (judgementPercent >= gradeStarThreshold) return Grade.Star;
            if (judgementPercent >= gradeSPlusThreshold) return Grade.SPlus;
            if (judgementPercent >= gradeSThreshold) return Grade.S;
            if (judgementPercent >= gradeSMinusThreshold) return Grade.SMinus;
            if (judgementPercent >= gradeAPlusThreshold) return Grade.APlus;
            if (judgementPercent >= gradeAThreshold) return Grade.A;
            if (judgementPercent >= gradeAMinusThreshold) return Grade.AMinus;
            if (judgementPercent >= gradeBPlusThreshold) return Grade.BPlus;
            if (judgementPercent >= gradeBThreshold) return Grade.B;
            if (judgementPercent >= gradeBMinusThreshold) return Grade.BMinus;
            if (judgementPercent >= gradeCPlusThreshold) return Grade.CPlus;
            if (judgementPercent >= gradeCThreshold) return Grade.C;
            if (judgementPercent >= gradeCMinusThreshold) return Grade.CMinus;
            return Grade.D;
        }

        /// <summary>True for the top 4 grades, which render as stars rather than text.</summary>
        public static bool IsStarGrade(Grade grade)
        {
            return grade == Grade.Quad || grade == Grade.Tristar || grade == Grade.DoubleStar || grade == Grade.Star;
        }

        /// <summary>Star count for the 4 star-based grades (4/3/2/1), 0 for text-based grades.</summary>
        public static int GetStarCount(Grade grade)
        {
            switch (grade)
            {
                case Grade.Quad: return 4;
                case Grade.Tristar: return 3;
                case Grade.DoubleStar: return 2;
                case Grade.Star: return 1;
                default: return 0;
            }
        }

        /// <summary>
        /// Text form of the grade. Used for text-based grades (S+ down to F) directly on the main
        /// panel, and for ALL grades (including a compact star-count label) in tight spots like
        /// Play History rows where a full mesh star visual doesn't fit.
        /// </summary>
        public static string GetGradeText(Grade grade)
        {
            switch (grade)
            {
                case Grade.Quad: return "4-Star";
                case Grade.Tristar: return "3-Star";
                case Grade.DoubleStar: return "2-Star";
                case Grade.Star: return "1-Star";
                case Grade.SPlus: return "S+";
                case Grade.S: return "S";
                case Grade.SMinus: return "S-";
                case Grade.APlus: return "A+";
                case Grade.A: return "A";
                case Grade.AMinus: return "A-";
                case Grade.BPlus: return "B+";
                case Grade.B: return "B";
                case Grade.BMinus: return "B-";
                case Grade.CPlus: return "C+";
                case Grade.C: return "C";
                case Grade.CMinus: return "C-";
                case Grade.D: return "D";
                case Grade.F: return "F";
                default: return "";
            }
        }

        public static Color GetGradeColor(Grade grade)
        {
            switch (grade)
            {
                case Grade.Quad:
                case Grade.Tristar:
                case Grade.DoubleStar:
                case Grade.Star:
                    return new Color(1.0f, 0.84f, 0f); // Gold
                case Grade.SPlus:
                    return new Color(0.4f, 1f, 1f); // Bright cyan
                case Grade.S:
                    return Color.cyan;
                case Grade.SMinus:
                    return new Color(0f, 0.75f, 0.75f); // Dimmer cyan
                case Grade.APlus:
                    return new Color(0.3f, 1f, 0.3f); // Bright green
                case Grade.A:
                    return Color.green;
                case Grade.AMinus:
                    return new Color(0f, 0.7f, 0f); // Dimmer green
                case Grade.BPlus:
                    return new Color(0.2f, 0.65f, 0.2f); // Forest green (bright)
                case Grade.B:
                    return new Color(0.133f, 0.545f, 0.133f); // Forest green
                case Grade.BMinus:
                    return new Color(0.09f, 0.4f, 0.09f); // Forest green (dim)
                case Grade.CPlus:
                    return new Color(1f, 0.84f, 0.3f); // Light gold/yellow
                case Grade.C:
                    return Color.yellow;
                case Grade.CMinus:
                    return new Color(1f, 0.6f, 0f); // Orange
                case Grade.D:
                    return Color.gray;
                case Grade.F:
                    return Color.red;
                default:
                    return Color.white;
            }
        }
    }
}