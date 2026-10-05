using System;
using System.Collections;
using System.Reflection;
using Harmony;
using MelonLoader;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// Single point of contact with Continuum's optional "AuthorableModifiers" mod (AM), and
    /// through it Arena Loader (AM is the only thing that drives Arena Loader during a song).
    ///
    /// Why this exists: AM ties its lifecycle to the vanilla launch page —
    ///   load   = postfix on SongSelectItem.OnSelect (also activates "preload" modifiers such
    ///            as ArenaChange straight away),
    ///   unload = postfix on LaunchPanel.Back.
    /// ExScoring has no launch page: LaunchPanel.Back is never called, and OnSelect fires on
    /// every auto-select. Left alone, AM's modifiers/arena are never cleared when leaving the
    /// song page and are re-applied (with environment switches) while the menu is rebuilding.
    ///
    /// What the bridge does instead — ExScoring owns the lifecycle:
    ///   1. Menu: AM's own load is gated off. Nothing is loaded or switched while browsing.
    ///   2. Play: LaunchPanel.Play is deferred; modifiers are loaded for the song the game is
    ///      actually about to launch, we wait for the arena switch to finish, then Play runs.
    ///   3. Song over (results / failed / stats-on-fail): AM is fully reset once instead of
    ///      being reloaded (AM's reload re-switches the arena and resumes audio under the
    ///      results screen, up to twice with ShowStatsOnFail).
    ///   4. Restart after song over: the bridge reloads the modifiers itself.
    ///   5. Leaving gameplay / the song page: anything AM still holds is cleared.
    ///
    /// AM is never referenced at compile time. Everything goes through cached reflection and
    /// every entry point is a harmless no-op when AM is absent, incompatible, or throws.
    /// Uses the legacy Harmony (0Harmony 1.x) API, same as SongRequestBlocker.
    /// </summary>
    internal static class AuthorableBridge
    {
        private const string Tag = "[AMBridge] ";
        private const float SwitchTimeout = 15f;   // max wait for an environment switch
        private const float LoadTimeout = 15f;     // max wait for AM to report modifiersLoaded
        private const float WarningLinger = 2f;    // keep AM's flashing-lights warning readable
        private const int MaxPrepareAttempts = 3;

        // ── Reflection handles ──
        private static bool initialized;
        private static Type amType;
        private static MethodInfo mLoad;        // LoadModifierCues(bool fromRestart)
        private static MethodInfo mReset;       // Reset(bool fromBack)
        private static MethodInfo mSetEndless;  // SetEndlessActive(bool)
        private static FieldInfo fPath;         // audicaFilePath
        private static FieldInfo fLoaded;       // modifiersLoaded
        private static FieldInfo fEndless;      // endless
        private static FieldInfo fPopups;       // popupTextDictionary
        private static PropertyInfo pFound;     // modifiersFound (auto-property)
        private static FieldInfo fFound;        // ...or a field, if a build declares it that way

        private static bool loadGateActive;
        private static bool restartGateActive;

        // ── State ──
        private static bool bridgeCall;          // true while WE invoke LoadModifierCues
        private static bool bypassPlay;          // true while WE invoke LaunchPanel.Play
        private static bool preparing;           // a deferred Play is in flight
        private static int prepareSeq;
        private static bool busy;                // a mid-session (marathon) load is in flight
        private static string loadedPath;        // .audica the modifiers were last loaded for
        private static bool reloadOnRestart;     // AM was reset at song over; reload on Restart
        private static int bridgeRestartFrame = -1;

        /// <summary>True when AM is installed and exposes everything the bridge needs.</summary>
        public static bool Available =>
            amType != null && mLoad != null && mReset != null && fPath != null;

        /// <summary>True while a marathon next-song load started by BeginLoadForCurrentSong runs.</summary>
        public static bool Busy => busy;

        // ══════════════════════════════════════════════════════════════════
        //  Setup
        // ══════════════════════════════════════════════════════════════════

        public static void Init()
        {
            if (initialized) return;
            initialized = true;

            try
            {
                amType = FindType("AuthorableModifiers.AuthorableModifiersMod");
                if (amType == null)
                {
                    MelonLogger.Log(Tag + "AuthorableModifiers type not found; bridge disabled.");
                    return;
                }

                const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                mLoad = amType.GetMethod("LoadModifierCues", S, null, new[] { typeof(bool) }, null);
                mReset = amType.GetMethod("Reset", S, null, new[] { typeof(bool) }, null);
                mSetEndless = amType.GetMethod("SetEndlessActive", S, null, new[] { typeof(bool) }, null);
                fPath = amType.GetField("audicaFilePath", S);
                fLoaded = amType.GetField("modifiersLoaded", S);
                fEndless = amType.GetField("endless", S);
                fPopups = amType.GetField("popupTextDictionary", S);
                pFound = amType.GetProperty("modifiersFound", S);
                fFound = amType.GetField("modifiersFound", S);

                if (!Available)
                {
                    MelonLogger.Log(Tag + "AuthorableModifiers is missing expected members " +
                                    "(unsupported version?); bridge disabled, AM left untouched.");
                    amType = null;
                    return;
                }

                HarmonyInstance harmony = HarmonyInstance.Create("ExScoring.AuthorableBridge");
                const BindingFlags P = BindingFlags.Static | BindingFlags.NonPublic;

                loadGateActive = TryPatch(harmony, mLoad,
                    typeof(AuthorableBridge).GetMethod(nameof(LoadGatePrefix), P), "LoadModifierCues");

                MethodInfo onRestart = amType.GetMethod("OnRestart", S, null, new[] { typeof(bool) }, null);
                restartGateActive = onRestart != null && TryPatch(harmony, onRestart,
                    typeof(AuthorableBridge).GetMethod(nameof(RestartGatePrefix), P), "OnRestart");

                MelonLogger.Log(Tag + "ready (loadGate=" + loadGateActive +
                                ", restartGate=" + restartGateActive + ").");
            }
            catch (Exception e)
            {
                MelonLogger.Log(Tag + "Init failed; bridge disabled: " + e);
                amType = null;
            }
        }

        private static bool TryPatch(HarmonyInstance harmony, MethodInfo target, MethodInfo prefix, string name)
        {
            try
            {
                if (target == null || prefix == null) return false;
                harmony.Patch(target, new HarmonyMethod(prefix));
                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Log(Tag + "could not patch AuthorableModifiers." + name + ": " + e.Message);
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

        // ══════════════════════════════════════════════════════════════════
        //  Gates on AuthorableModifiers' own methods
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Prefix on AuthorableModifiersMod.LoadModifierCues. Lets our own calls and AM's
        /// in-gameplay calls (pause-menu restart) through; skips AM's menu-time load that its
        /// OnSelect postfix triggers. Any failure lets the original run.
        /// </summary>
        private static bool LoadGatePrefix()
        {
            try
            {
                if (bridgeCall) return true;
                if (MenuState.sState == MenuState.State.Launched) return true;
                return false;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// Prefix on AuthorableModifiersMod.OnRestart(bool ingameRestart).
        /// ingameRestart == false is AM's "song is over" call (GoToResultsPage / GoToFailedPage):
        /// replaced by a single full reset. ingameRestart == true is skipped only when our own
        /// InGameUI.Restart hook already reloaded this frame. Endless mode is left to AM.
        /// </summary>
        private static bool RestartGatePrefix(bool ingameRestart)
        {
            try
            {
                if (GetEndless()) return true;

                if (ingameRestart)
                    return bridgeRestartFrame != Time.frameCount;

                if (GetFound())
                {
                    InvokeReset(false);
                    if (!GetFound()) reloadOnRestart = true;
                }
                return false;
            }
            catch (Exception e)
            {
                MelonLogger.Log(Tag + "RestartGate failed, falling back to AM's own handling: " + e);
                return true;
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  Entry points called from ExScoring's hooks
        // ══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Called from the LaunchPanel.Play prefix. Returns true when Play has been deferred
        /// (the caller must skip the original); the bridge calls Play again itself once the
        /// modifiers for the launching song are loaded and the arena has settled.
        /// </summary>
        public static bool InterceptPlay(LaunchPanel panel)
        {
            if (bypassPlay || !Available) return false;

            try
            {
                if (preparing) return true; // swallow repeated shots while a launch is pending
                if (panel == null) return false;

                preparing = true;
                int seq = ++prepareSeq;
                MelonCoroutines.Start(PrepareAndPlay(panel, seq));
                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Log(Tag + "InterceptPlay failed, launching without bridge: " + e);
                preparing = false;
                return false;
            }
        }

        /// <summary>Called from the InGameUI.Restart prefix.</summary>
        public static void OnInGameRestart()
        {
            if (!Available || !reloadOnRestart) return;
            reloadOnRestart = false;

            try
            {
                if (GetEndless()) return;
                if (KataConfig.I != null && KataConfig.I.practiceMode) return;
                if (string.IsNullOrEmpty(loadedPath)) return;

                // AM's own Restart prefix may run after ours; tell RestartGatePrefix to skip it.
                bridgeRestartFrame = Time.frameCount;
                DoLoad(loadedPath, true);
            }
            catch (Exception e)
            {
                MelonLogger.Log(Tag + "OnInGameRestart failed: " + e);
            }
        }

        /// <summary>Called from the MenuState.SetState postfix, before ExScoring updates menuState.</summary>
        public static void OnMenuStateChanged(MenuState.State prev, MenuState.State next)
        {
            if (!Available) return;

            try
            {
                if (IsGameplayState(next) || next == MenuState.State.LaunchPage) return;

                bool fromGameplay = IsGameplayState(prev);

                // SongPage -> SongPage (auto-select, marathon setup) is not a real exit.
                if (next == MenuState.State.SongPage && !fromGameplay) return;

                reloadOnRestart = false;
                MelonCoroutines.Start(DeferredCleanup(fromGameplay));
            }
            catch (Exception e)
            {
                MelonLogger.Log(Tag + "OnMenuStateChanged failed: " + e);
            }
        }

        /// <summary>Marathon: tell AM it is in endless mode (keeps the arena between songs).</summary>
        public static void SetEndless(bool active)
        {
            if (!Available || mSetEndless == null) return;
            try { mSetEndless.Invoke(null, new object[] { active }); }
            catch (Exception e) { MelonLogger.Log(Tag + "SetEndlessActive failed: " + e.Message); }
        }

        /// <summary>
        /// Marathon next-song: load modifiers for the song now in SongDataHolder. Poll
        /// <see cref="Busy"/> until it clears. No-op (Busy stays false) when AM is absent.
        /// </summary>
        public static void BeginLoadForCurrentSong()
        {
            if (!Available || busy) return;
            busy = true;
            try
            {
                MelonCoroutines.Start(LoadForCurrentSong());
            }
            catch (Exception e)
            {
                busy = false;
                MelonLogger.Log(Tag + "BeginLoadForCurrentSong failed: " + e);
            }
        }

        // ══════════════════════════════════════════════════════════════════
        //  Coroutines
        // ══════════════════════════════════════════════════════════════════

        private static IEnumerator PrepareAndPlay(LaunchPanel panel, int seq)
        {
            MenuState.State startState = SafeMenuState();
            string path = null;

            for (int attempt = 0; attempt < MaxPrepareAttempts; attempt++)
            {
                path = CurrentSongPath();

                // Anything still held from a previous song goes first (same call AM makes on
                // LaunchPanel.Back), then let any environment switch it started finish.
                ResetIfLoaded(true);

                float deadline = Time.unscaledTime + SwitchTimeout;
                while (IsSwitching() && Time.unscaledTime < deadline) yield return null;
                if (PrepareCancelled(panel, seq, startState)) { AbortPrepare(seq); yield break; }

                if (!string.IsNullOrEmpty(path))
                    DoLoad(path, false);

                deadline = Time.unscaledTime + LoadTimeout;
                while ((!GetLoaded() || IsSwitching()) && Time.unscaledTime < deadline) yield return null;
                if (Time.unscaledTime >= deadline)
                    MelonLogger.Log(Tag + "timed out waiting for modifiers/arena; launching anyway.");
                if (PrepareCancelled(panel, seq, startState)) { AbortPrepare(seq); yield break; }

                // AM shows its flashing-lights / rotating-arena warning on load and destroys it
                // in its LaunchPanel.Play postfix. Give the player a moment to read it.
                if (HasPopup())
                {
                    float until = Time.unscaledTime + WarningLinger;
                    while (Time.unscaledTime < until) yield return null;
                    if (PrepareCancelled(panel, seq, startState)) { AbortPrepare(seq); yield break; }
                }

                // The song the game will launch changed while we were waiting.
                if (CurrentSongPath() == path) break;

                if (!InMarathon())
                {
                    // The player picked something else after shooting Play: don't launch.
                    AbortPrepare(seq);
                    yield break;
                }
                // Marathon: go round again so the modifiers match what actually launches.
            }

            if (seq != prepareSeq) yield break;
            preparing = false;

            bypassPlay = true;
            try
            {
                panel.Play();
            }
            catch (Exception e)
            {
                MelonLogger.Log(Tag + "deferred Play failed: " + e);
                ResetIfLoaded(true);
            }
            finally
            {
                bypassPlay = false;
            }
        }

        private static IEnumerator LoadForCurrentSong()
        {
            string path = CurrentSongPath();

            float deadline = Time.unscaledTime + SwitchTimeout;
            while (IsSwitching() && Time.unscaledTime < deadline) yield return null;

            if (!string.IsNullOrEmpty(path))
                DoLoad(path, false);
            else
                ResetIfLoaded(false);

            deadline = Time.unscaledTime + LoadTimeout;
            while ((!GetLoaded() || IsSwitching()) && Time.unscaledTime < deadline) yield return null;

            busy = false;
        }

        /// <summary>
        /// Runs two frames after leaving gameplay / the song page so AM's own postfix
        /// (ReturnToSongList) gets to run first; clears whatever AM still holds.
        /// </summary>
        private static IEnumerator DeferredCleanup(bool fromGameplay)
        {
            yield return null;
            yield return null;

            if (preparing) yield break;
            if (IsGameplayState(SafeMenuState())) yield break;
            if (!GetFound()) yield break;

            MelonLogger.Log(Tag + "clearing modifiers left active outside gameplay.");
            SetEndless(false);
            InvokeReset(!fromGameplay);
        }

        // ══════════════════════════════════════════════════════════════════
        //  Internals
        // ══════════════════════════════════════════════════════════════════

        private static bool PrepareCancelled(LaunchPanel panel, int seq, MenuState.State startState)
        {
            if (seq != prepareSeq) return true;
            if (panel == null) return true;                    // menu was torn down
            if (SafeMenuState() != startState) return true;    // player navigated away
            return false;
        }

        private static void AbortPrepare(int seq)
        {
            if (seq == prepareSeq) preparing = false;
            ResetIfLoaded(true);
        }

        /// <summary>
        /// Points AM at <paramref name="path"/> and runs its loader. Never throws. If AM's
        /// loader throws (malformed modifiers.json, locked file, ...) AM is left half-loaded
        /// with modifiersFound == false, which its own Reset() then refuses to clean — so that
        /// state is repaired here and the song simply plays without modifiers.
        /// </summary>
        private static bool DoLoad(string path, bool fromRestart)
        {
            try
            {
                // Leftovers from an earlier failed load would be appended to this song's lists.
                if (!GetFound()) HardClear();

                fPath.SetValue(null, path);

                bridgeCall = true;
                try { mLoad.Invoke(null, new object[] { fromRestart }); }
                finally { bridgeCall = false; }

                loadedPath = path;
                return true;
            }
            catch (Exception e)
            {
                Exception inner = (e is TargetInvocationException && e.InnerException != null) ? e.InnerException : e;
                MelonLogger.Log(Tag + "AuthorableModifiers failed to load '" + path +
                                "'; playing without modifiers: " + inner.Message);
                Recover(fromRestart);
                return false;
            }
        }

        private static void Recover(bool fromRestart)
        {
            // Undo anything a partially-run load already applied (arena, colours, popups).
            try
            {
                SetFound(true);
                InvokeReset(true);
            }
            catch { /* best effort */ }

            try { SetFound(false); } catch { }
            HardClear();
            try { if (fLoaded != null) fLoaded.SetValue(null, true); } catch { }

            // AM pauses the audio before loading on a restart and resumes when done.
            if (fromRestart)
            {
                try { if (AudioDriver.I != null) AudioDriver.I.Resume(); } catch { }
            }
        }

        private static readonly string[] ListFields =
        {
            "awaitEnableModifiers", "awaitDisableModifiers", "preloadModifiers", "zOffsetList",
            "autoLightings", "modifierQueue", "singleUseModifiers", "oldOffsetDict"
        };

        /// <summary>Empties AM's static collections directly (null-safe, member-safe).</summary>
        private static void HardClear()
        {
            if (amType == null) return;
            const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

            for (int i = 0; i < ListFields.Length; i++)
            {
                try
                {
                    FieldInfo f = amType.GetField(ListFields[i], S);
                    object value = f != null ? f.GetValue(null) : null;
                    if (value is IList list) list.Clear();
                    else if (value is IDictionary dict) dict.Clear();
                }
                catch { /* best effort */ }
            }
        }

        private static void ResetIfLoaded(bool fromBack)
        {
            try
            {
                if (GetFound()) InvokeReset(fromBack);
            }
            catch (Exception e)
            {
                MelonLogger.Log(Tag + "Reset failed: " + e.Message);
            }
        }

        private static void InvokeReset(bool fromBack)
        {
            mReset.Invoke(null, new object[] { fromBack });
        }

        private static bool GetFound()
        {
            try
            {
                if (pFound != null) return (bool)pFound.GetValue(null, null);
                if (fFound != null) return (bool)fFound.GetValue(null);
            }
            catch { }
            return false;
        }

        private static void SetFound(bool value)
        {
            if (pFound != null && pFound.CanWrite) pFound.SetValue(null, value, null);
            else if (fFound != null) fFound.SetValue(null, value);
        }

        /// <summary>AM's modifiersLoaded flag; true when it can't be read so we never wait forever.</summary>
        private static bool GetLoaded()
        {
            try { return fLoaded == null || (bool)fLoaded.GetValue(null); }
            catch { return true; }
        }

        private static bool GetEndless()
        {
            try { return fEndless != null && (bool)fEndless.GetValue(null); }
            catch { return false; }
        }

        private static bool HasPopup()
        {
            try
            {
                return fPopups != null && fPopups.GetValue(null) is IDictionary d && d.Count > 0;
            }
            catch { return false; }
        }

        private static bool IsSwitching()
        {
            try
            {
                EnvironmentLoader loader = EnvironmentLoader.I;
                return loader != null && loader.IsSwitching();
            }
            catch { return false; }
        }

        private static string CurrentSongPath()
        {
            try
            {
                SongDataHolder holder = SongDataHolder.I;
                SongList.SongData data = holder != null ? holder.songData : null;
                if (data == null) data = ExScoring.selectedSongData;
                return data != null ? data.foundPath : null;
            }
            catch { return null; }
        }

        private static bool InMarathon()
        {
            try { return PlaylistManager.state == PlaylistManager.PlaylistState.Endless; }
            catch { return false; }
        }

        private static MenuState.State SafeMenuState()
        {
            try { return MenuState.sState; }
            catch { return ExScoring.menuState; }
        }

        private static bool IsGameplayState(MenuState.State state)
        {
            return state == MenuState.State.Launching || state == MenuState.State.Launched;
        }
    }
}