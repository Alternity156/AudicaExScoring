using MelonLoader;

namespace ExScoringMod
{
    public partial class ExScoring : MelonMod
    {
        public static void AudicaScoringTest()
        {
            for (int i = 0; i < 100; i++)
            {
                MelonLogger.Log("Offset: " + i.ToString() + " Score: " + ScoreKeeper.GetTimingSuccessAmount(i));
                MelonLogger.Log(GetAudicaTimingScore(i));
            }
        }
    }
}
