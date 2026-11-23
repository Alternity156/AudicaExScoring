using MelonLoader;

namespace ExScoringMod
{
    /// <summary>
    /// The old spectator debug tools (Record Spectator Poses, Replay Fake Player) are replaced by
    /// Replays (Options > Replays): recording any finished song and watching it in the spectator
    /// window, online or offline. Only the console dump is kept here, for old notes that call it.
    /// The last debug recording (UserData/ExScoring_SpectatorRecording.jsonl) isn't read any more.
    ///
    /// From the UnityExplorer console: ExScoringMod.SpectatorDebug.DebugDump();
    /// </summary>
    internal static class SpectatorDebug
    {
        public static void DebugDump()
        {
            MelonLogger.Log("[SpectatorDebug] replaced by Replays:");
            Replays.DebugDump();
        }
    }
}