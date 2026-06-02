using System;
using System.Reflection;
using Harmony;
using MelonLoader;

namespace ExScoringMod
{
    /// <summary>
    /// Suppresses SongRequest's own song-list UI — the "Song Requests" filter button and the
    /// queue / skip / download buttons — which ExScoring replaces with its own folder and
    /// relocated controls.
    ///
    /// We can't recompile SongRequest and don't reference it, so RequestUI is resolved by
    /// reflection and patched only when present. All of its song-page buttons are created in
    /// RequestUI.Initialize() (called from a postfix on SongSelect.OnEnable), so skipping that
    /// one method removes them wholesale. DisableFilter() must also be skipped: SongRequest's
    /// FilterAll/FilterMain prefixes call it, and with Initialize skipped its button references
    /// are null, so it would throw.
    ///
    /// SongBrowser is unsupported alongside ExScoring, so the FilterPanel registration path
    /// (RequestUI.Register) never runs and is not handled here. Both patched methods only fire
    /// on song-page entry, which is always after this runs in OnApplicationStart, so mod load
    /// order is irrelevant.
    ///
    /// Uses the legacy Harmony (0Harmony 1.x) API — HarmonyInstance / HarmonyMethod — matching
    /// the rest of ExScoring; HarmonyLib (HarmonyX) is not available in this build.
    /// </summary>
    internal static class SongRequestBlocker
    {
        private static bool applied;

        public static void Apply()
        {
            if (applied) return;
            applied = true;

            if (!SongRequestIntegration.IsPresent)
                return; // mod absent — nothing to suppress

            Type requestUI = FindType("AudicaModding.RequestUI");
            if (requestUI == null)
            {
                MelonLogger.Log("[SongRequestBlocker] RequestUI type not found; UI suppression skipped.");
                return;
            }

            HarmonyInstance harmony = HarmonyInstance.Create("ExScoring.SongRequestBlocker");
            HarmonyMethod skip = new HarmonyMethod(typeof(SongRequestBlocker)
                .GetMethod(nameof(SkipPrefix), BindingFlags.Static | BindingFlags.NonPublic));

            bool any = false;
            any |= Patch(harmony, requestUI, "Initialize", Type.EmptyTypes, skip);
            any |= Patch(harmony, requestUI, "DisableFilter", Type.EmptyTypes, skip);

            if (any)
                MelonLogger.Log("[SongRequestBlocker] SongRequest song-list UI suppressed.");
            else
                MelonLogger.Log("[SongRequestBlocker] No RequestUI methods patched — UI may still appear.");
        }

        // A bool-returning prefix that returns false skips the original (void) method body.
        private static bool SkipPrefix() => false;

        private static bool Patch(HarmonyInstance harmony, Type type, string method, Type[] args, HarmonyMethod prefix)
        {
            try
            {
                MethodInfo mi = type.GetMethod(method,
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null, args, null);

                if (mi == null)
                {
                    MelonLogger.Log($"[SongRequestBlocker] {type.Name}.{method} not found; skipped.");
                    return false;
                }

                harmony.Patch(mi, prefix);
                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Log($"[SongRequestBlocker] Failed to patch {type.Name}.{method}: {e.Message}");
                return false;
            }
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = asm.GetType(fullName); }
                catch { /* ignore assemblies that can't be queried */ }
                if (t != null) return t;
            }
            return null;
        }
    }
}