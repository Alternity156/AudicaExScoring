using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using MelonLoader;

namespace ExScoringMod
{
    /// <summary>
    /// Lightweight, reflection-based bridge to Silzoid's optional "SongRequest" mod
    /// (namespace AudicaModding). The mod is never referenced at compile time, so all
    /// access goes through cached reflection and degrades to harmless no-ops when it is
    /// absent.
    ///
    /// Note: SongRequest's SongRequests / Request / MissingRequest are plain managed .NET
    /// types in the mod assembly, so standard System.Reflection applies even though the
    /// game itself is Il2Cpp. We resolve types by scanning loaded assemblies (the same
    /// approach PlaylistEndlessManager uses for AuthorableModifiers); by the time any
    /// OnApplicationStart runs, MelonLoader has loaded every mod assembly, so load order
    /// does not matter.
    /// </summary>
    internal static class SongRequestIntegration
    {
        /// <summary>
        /// A flattened, read-only copy of one request so the rest of ExScoring never
        /// touches SongRequest's own types. DownloadURL is null for already-available
        /// (downloaded) requests and populated for missing ones.
        /// </summary>
        public struct RequestInfo
        {
            public string SongID;
            public string Title;
            public string Artist;
            public string Mapper;
            public string DownloadURL;
        }

        private static bool initialized;
        private static bool present;

        private static Type songRequestsType;
        private static MethodInfo getRequestsMethod;      // internal static List<Request> GetRequests()
        private static MethodInfo getMissingSongsMethod;  // internal static List<MissingRequest> GetMissingSongs()
        private static MethodInfo removeRequestMethod;    // internal static void RemoveRequest(string)
        private static FieldInfo requestsEnabledField;   // internal static bool requestsEnabled

        private static Type requestUIType;
        private static MethodInfo enableQueueMethod;      // public static void EnableQueue(bool)

        private static MethodInfo emitMessageMethod;      // private static void EmitMessage(string,object,string,string,string)
        private static MethodInfo addRequestMethod;       // internal static bool AddRequest(SongList.SongData,string,DateTime)
        private static FieldInfo downloadNameCacheField; // private static List<string> downloadNameCache

        // AudicaModding.Config.LetModsIgnoreQueueStatus — property or plain field depending on build.
        private static PropertyInfo letModsIgnoreProp;
        private static FieldInfo letModsIgnoreField;

        // Field accessors for the request element types (public instance fields).
        private static FieldInfo fSongID, fTitle, fArtist, fMapper, fDownloadURL;

        /// <summary>True if a usable SongRequest mod is loaded.</summary>
        public static bool IsPresent
        {
            get
            {
                EnsureInitialized();
                return present;
            }
        }

        private static void EnsureInitialized()
        {
            if (initialized) return;
            initialized = true;

            songRequestsType = FindType("AudicaModding.SongRequests");
            requestUIType = FindType("AudicaModding.RequestUI");

            if (songRequestsType == null)
            {
                MelonLogger.Log("[SongRequestIntegration] SongRequest mod not detected — integration disabled.");
                present = false;
                return;
            }

            present = true;
            MelonLogger.Log("[SongRequestIntegration] SongRequest mod detected — enabling integration.");

            getRequestsMethod = ResolveMethod(songRequestsType, "GetRequests", Type.EmptyTypes);
            getMissingSongsMethod = ResolveMethod(songRequestsType, "GetMissingSongs", Type.EmptyTypes);
            removeRequestMethod = ResolveMethod(songRequestsType, "RemoveRequest", new[] { typeof(string) });
            requestsEnabledField = ResolveField(songRequestsType, "requestsEnabled");

            if (requestUIType != null)
                enableQueueMethod = ResolveMethod(requestUIType, "EnableQueue", new[] { typeof(bool) });
            else
                MelonLogger.Log("[SongRequestIntegration] AudicaModding.RequestUI not found — queue toggle will be inert.");

            // Members used to drive SongRequest's own messaging / available-queue for parity.
            emitMessageMethod = ResolveMethod(songRequestsType, "EmitMessage",
                new[] { typeof(string), typeof(object), typeof(string), typeof(string), typeof(string) });
            addRequestMethod = ResolveMethod(songRequestsType, "AddRequest",
                new[] { typeof(SongList.SongData), typeof(string), typeof(DateTime) });
            downloadNameCacheField = ResolveField(songRequestsType, "downloadNameCache");

            // Config.LetModsIgnoreQueueStatus governs whether mods may request while the queue is off.
            // It can be a property or a plain static field depending on the build, so try both.
            Type configType = songRequestsType.Assembly.GetType("AudicaModding.Config");
            if (configType != null)
            {
                const BindingFlags STATIC_ANY = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                letModsIgnoreProp = configType.GetProperty("LetModsIgnoreQueueStatus", STATIC_ANY);
                if (letModsIgnoreProp == null)
                    letModsIgnoreField = configType.GetField("LetModsIgnoreQueueStatus", STATIC_ANY);
            }
            else
            {
                MelonLogger.Log("[SongRequestIntegration] AudicaModding.Config not found — mods won't bypass a disabled queue.");
            }

            // Field accessors live on the request element types, not on SongRequests.
            Type requestType = songRequestsType.Assembly.GetType("AudicaModding.Request");
            Type missingType = songRequestsType.Assembly.GetType("AudicaModding.MissingRequest");
            const BindingFlags PUB_INST = BindingFlags.Public | BindingFlags.Instance;

            if (requestType != null)
            {
                fSongID = requestType.GetField("SongID", PUB_INST);
                fTitle = requestType.GetField("Title", PUB_INST);
                fArtist = requestType.GetField("Artist", PUB_INST);
                fMapper = requestType.GetField("Mapper", PUB_INST);
            }
            else
            {
                MelonLogger.Log("[SongRequestIntegration] AudicaModding.Request not found — request reads will be empty.");
            }

            if (missingType != null)
                fDownloadURL = missingType.GetField("DownloadURL", PUB_INST);
            else
                MelonLogger.Log("[SongRequestIntegration] AudicaModding.MissingRequest not found — download URLs unavailable.");
        }

        // ── Reads ──────────────────────────────────────────────────────────────

        /// <summary>Downloaded requests, ready to play. DownloadURL is null on these.</summary>
        public static List<RequestInfo> GetAvailableRequests()
        {
            EnsureInitialized();
            return ReadList(getRequestsMethod, includeDownloadUrl: false);
        }

        /// <summary>Requested songs that are not yet downloaded. DownloadURL is populated.</summary>
        public static List<RequestInfo> GetMissingRequests()
        {
            EnsureInitialized();
            return ReadList(getMissingSongsMethod, includeDownloadUrl: true);
        }

        private static List<RequestInfo> ReadList(MethodInfo method, bool includeDownloadUrl)
        {
            var list = new List<RequestInfo>();
            if (!present || method == null) return list;

            try
            {
                if (method.Invoke(null, null) is IEnumerable raw)
                {
                    foreach (object item in raw)
                        list.Add(ToInfo(item, includeDownloadUrl));
                }
            }
            catch (Exception e)
            {
                MelonLogger.Log($"[SongRequestIntegration] {method.Name} read failed: {e.Message}");
            }
            return list;
        }

        private static RequestInfo ToInfo(object req, bool includeDownloadUrl)
        {
            var info = new RequestInfo();
            if (req == null) return info;
            try
            {
                if (fSongID != null) info.SongID = fSongID.GetValue(req) as string;
                if (fTitle != null) info.Title = fTitle.GetValue(req) as string;
                if (fArtist != null) info.Artist = fArtist.GetValue(req) as string;
                if (fMapper != null) info.Mapper = fMapper.GetValue(req) as string;
                if (includeDownloadUrl && fDownloadURL != null)
                    info.DownloadURL = fDownloadURL.GetValue(req) as string;
            }
            catch (Exception e)
            {
                MelonLogger.Log($"[SongRequestIntegration] request field read failed: {e.Message}");
            }
            return info;
        }

        // ── Writes / state ───────────────────────────────────────────────────────

        /// <summary>Removes a song from the request queue by song ID.</summary>
        public static void RemoveRequest(string songID)
        {
            EnsureInitialized();
            if (!present || removeRequestMethod == null || string.IsNullOrEmpty(songID)) return;
            try
            {
                removeRequestMethod.Invoke(null, new object[] { songID });
            }
            catch (Exception e)
            {
                MelonLogger.Log($"[SongRequestIntegration] RemoveRequest failed: {e.Message}");
            }
        }

        /// <summary>Whether SongRequest is currently accepting new !asr requests.</summary>
        public static bool RequestsEnabled
        {
            get
            {
                EnsureInitialized();
                if (!present || requestsEnabledField == null) return false;
                try { return (bool)requestsEnabledField.GetValue(null); }
                catch (Exception e)
                {
                    MelonLogger.Log($"[SongRequestIntegration] read requestsEnabled failed: {e.Message}");
                    return false;
                }
            }
        }

        /// <summary>SongRequest's "let mods ignore queue status" config — whether mods may request while the
        /// queue is off. Defaults to false if the config can't be read.</summary>
        public static bool LetModsIgnoreQueueStatus
        {
            get
            {
                EnsureInitialized();
                try
                {
                    if (letModsIgnoreProp != null) return (bool)letModsIgnoreProp.GetValue(null);
                    if (letModsIgnoreField != null) return (bool)letModsIgnoreField.GetValue(null);
                }
                catch (Exception e) { MelonLogger.Log($"[SongRequestIntegration] read LetModsIgnoreQueueStatus failed: {e.Message}"); }
                return false;
            }
        }

        /// <summary>Enable/disable the request queue.</summary>
        public static void SetRequestsEnabled(bool enabled)
        {
            EnsureInitialized();
            if (!present) return;

            // Prefer RequestUI.EnableQueue so any internal side effects run. It null-guards the
            // (now-suppressed) button text internally, so it stays safe with our UI blocked.
            if (enableQueueMethod != null)
            {
                try { enableQueueMethod.Invoke(null, new object[] { enabled }); return; }
                catch (Exception e) { MelonLogger.Log($"[SongRequestIntegration] EnableQueue failed: {e.Message}"); }
            }

            if (requestsEnabledField != null)
            {
                try { requestsEnabledField.SetValue(null, enabled); }
                catch (Exception e) { MelonLogger.Log($"[SongRequestIntegration] set requestsEnabled failed: {e.Message}"); }
            }
        }

        // ── Parity messaging / available-queue (drive SongRequest's own methods) ───

        /// <summary>Emit SongRequest's native "added to the queue" message/event for a found song.</summary>
        public static void EmitNewSongQueueItem(string title, string artist, string mapper)
        {
            EnsureInitialized();
            if (!present || emitMessageMethod == null) return;
            try { emitMessageMethod.Invoke(null, new object[] { "NewSongQueueItem", null, title ?? "", artist ?? "", mapper ?? "" }); }
            catch (Exception e) { MelonLogger.Log($"[SongRequestIntegration] EmitNewSongQueueItem failed: {e.Message}"); }
        }

        /// <summary>Emit SongRequest's native "already in the queue" message/event.</summary>
        public static void EmitSongAlreadyInQueue(string title)
        {
            EnsureInitialized();
            if (!present || emitMessageMethod == null) return;
            try { emitMessageMethod.Invoke(null, new object[] { "SongAlreadyInQueue", null, title ?? "", "", "" }); }
            catch (Exception e) { MelonLogger.Log($"[SongRequestIntegration] EmitSongAlreadyInQueue failed: {e.Message}"); }
        }

        /// <summary>Emit SongRequest's native "not found" message/event for a query.</summary>
        public static void EmitSongNotFound(string query)
        {
            EnsureInitialized();
            if (!present || emitMessageMethod == null) return;
            try { emitMessageMethod.Invoke(null, new object[] { "SongNotFound", null, query ?? "", "", "" }); }
            catch (Exception e) { MelonLogger.Log($"[SongRequestIntegration] EmitSongNotFound failed: {e.Message}"); }
        }

        /// <summary>
        /// Send a "request list cleared" notice to chat (and the websocket, if present). SongRequest has no
        /// native "cleared" event, so this rides EmitMessage's default case (which sends the eventType string
        /// verbatim), keeping the same delivery path as every other request message.
        /// </summary>
        public static void EmitQueueCleared()
        {
            EnsureInitialized();
            if (!present || emitMessageMethod == null) return;
            try { emitMessageMethod.Invoke(null, new object[] { "Request list cleared.", null, "", "", "" }); }
            catch (Exception e) { MelonLogger.Log($"[SongRequestIntegration] EmitQueueCleared failed: {e.Message}"); }
        }

        /// <summary>Emit SongRequest's native "Request queue enabled/disabled." chat message (and websocket).</summary>
        public static void EmitQueueStateChanged(bool enabled)
        {
            EnsureInitialized();
            if (!present || emitMessageMethod == null) return;
            try { emitMessageMethod.Invoke(null, new object[] { enabled ? "QueueEnabled" : "QueueDisabled", null, "", "", "" }); }
            catch (Exception e) { MelonLogger.Log($"[SongRequestIntegration] EmitQueueStateChanged failed: {e.Message}"); }
        }

        /// <summary>
        /// Add a locally-present song to SongRequest's available queue (its own AddRequest, which
        /// emits the appropriate "added"/"already in queue" message itself).
        /// </summary>
        public static void AddAvailableRequest(SongList.SongData song, string requestedBy, DateTime requestedAt)
        {
            EnsureInitialized();
            if (!present || addRequestMethod == null || song == null) return;
            try { addRequestMethod.Invoke(null, new object[] { song, requestedBy ?? "", requestedAt }); }
            catch (Exception e) { MelonLogger.Log($"[SongRequestIntegration] AddAvailableRequest failed: {e.Message}"); }
        }

        /// <summary>
        /// Mirror AddMissing's behaviour of recording a songID in SongRequest's downloadNameCache so that
        /// when the song is later downloaded and added as available, SongRequest does NOT re-announce it.
        /// </summary>
        public static void PrimeDownloadNameCache(string songID)
        {
            EnsureInitialized();
            if (!present || downloadNameCacheField == null || string.IsNullOrEmpty(songID)) return;
            try
            {
                if (downloadNameCacheField.GetValue(null) is System.Collections.IList list && !list.Contains(songID))
                    list.Add(songID);
            }
            catch (Exception e) { MelonLogger.Log($"[SongRequestIntegration] PrimeDownloadNameCache failed: {e.Message}"); }
        }

        // ── Diagnostics ───────────────────────────────────────────────────────────

        /// <summary>Dumps the current parsed queue to the MelonLoader console (phase-1 check).</summary>
        public static void LogSnapshot()
        {
            EnsureInitialized();
            if (!present)
            {
                MelonLogger.Log("[SongRequestIntegration] not present.");
                return;
            }

            List<RequestInfo> avail = GetAvailableRequests();
            List<RequestInfo> missing = GetMissingRequests();

            MelonLogger.Log($"[SongRequestIntegration] requestsEnabled={RequestsEnabled}; " +
                            $"available={avail.Count}, missing={missing.Count}");
            foreach (RequestInfo r in avail)
                MelonLogger.Log($"  [avail]   {r.SongID} | {r.Title} - {r.Artist} ({r.Mapper})");
            foreach (RequestInfo r in missing)
                MelonLogger.Log($"  [missing] {r.SongID} | {r.Title} - {r.Artist} ({r.Mapper}) url={r.DownloadURL}");
        }

        // ── Reflection helpers ────────────────────────────────────────────────────

        private static MethodInfo ResolveMethod(Type t, string name, Type[] args)
        {
            MethodInfo mi = null;
            try
            {
                mi = t.GetMethod(name,
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null, args, null);
            }
            catch (Exception e)
            {
                MelonLogger.Log($"[SongRequestIntegration] resolving {t.Name}.{name} threw: {e.Message}");
            }
            if (mi == null)
                MelonLogger.Log($"[SongRequestIntegration] method {t.Name}.{name} not found — that feature will be inert.");
            return mi;
        }

        private static FieldInfo ResolveField(Type t, string name)
        {
            FieldInfo fi = t.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (fi == null)
                MelonLogger.Log($"[SongRequestIntegration] field {t.Name}.{name} not found — that feature will be inert.");
            return fi;
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = asm.GetType(fullName); }
                catch { /* dynamic/reflection-only assemblies can throw; ignore */ }
                if (t != null) return t;
            }
            return null;
        }
    }
}