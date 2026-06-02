using System;
using System.Reflection;
using System.Text.RegularExpressions;
using Harmony;
using MelonLoader;

namespace ExScoringMod
{
    /// <summary>
    /// Completes the requests SongRequest can't resolve without SongBrowser, with full chat parity.
    ///
    /// Two seams, neither of which patches SongRequest's fragile internal methods:
    ///   - Title misses: a prefix on TwitchConnectorMod.SendMessage (a trivial, cleanly-patchable method)
    ///     watches for SongRequest's "'<query>' not found." reply. When it sees one it SUPPRESSES it and
    ///     runs our own web search for that query. SongRequest emitting "not found" is itself the proof
    ///     that the song isn't local, so we need no local-match guess. This seam is order-independent.
    ///   - "-id" requests: caught via the TwitchConnectorMod chat event (SongRequest stays silent on -id
    ///     without SongBrowser, so there's nothing to suppress). DEFERRED until we have a maudica by-id
    ///     lookup from the SongBrowser source.
    ///
    /// Parity messages are produced by reflection-invoking SongRequest's own EmitMessage (see
    /// SongRequestIntegration), so wording, transport (TwitchConnectorMod / websocket) and timing match
    /// what real SongBrowser would have produced.
    /// </summary>
    internal static class SongRequestInterceptor
    {
        private static bool applied;

        private static FieldInfo parsedMessageField;            // ParsedTwitchMessage.Message
        private static FieldInfo parsedUserIdField;             // ParsedTwitchMessage.UserId
        private static FieldInfo parsedModField;                // ParsedTwitchMessage.Mod ("1" if mod)
        private static FieldInfo parsedBroadcasterField;        // ParsedTwitchMessage.Broadcaster ("1" if broadcaster)
        private static readonly Regex notFoundPattern = new Regex(@"^'(.+?)' not found\.$");

        // Set while WE are deliberately emitting through SendMessage, so our SendMessage prefix lets our
        // own re-emitted "not found" through instead of suppressing it.
        private static bool selfEmitting;

        public static void Apply()
        {
            if (applied) return;
            applied = true;

            if (!SongRequestIntegration.IsPresent)
                return;

            Type tcmType = FindType("TwitchConnectorMod.TwitchConnectorMod");
            Type ircType = FindType("TwitchConnectorMod.TwitchIRC");
            Type msgType = FindType("TwitchConnectorMod.ParsedTwitchMessage");
            if (tcmType == null || ircType == null || msgType == null)
            {
                MelonLogger.Log("[SongRequestInterceptor] TwitchConnectorMod not found; interception disabled.");
                return;
            }

            parsedMessageField = msgType.GetField("Message", BindingFlags.Public | BindingFlags.Instance);
            parsedUserIdField = msgType.GetField("UserId", BindingFlags.Public | BindingFlags.Instance);
            parsedModField = msgType.GetField("Mod", BindingFlags.Public | BindingFlags.Instance);
            parsedBroadcasterField = msgType.GetField("Broadcaster", BindingFlags.Public | BindingFlags.Instance);

            SubscribeToChat(tcmType, ircType);
            PatchSendMessage(tcmType);

            MelonLogger.Log("[SongRequestInterceptor] Phase 2 active (suppress + search + store + parity messaging).");
        }

        // ── -id seam (chat event) ─────────────────────────────────────────────────
        private static void SubscribeToChat(Type tcmType, Type ircType)
        {
            try
            {
                Type handlerDelegateType = ircType.GetNestedType("MessageReceivedEventHandler",
                    BindingFlags.Public | BindingFlags.NonPublic);
                MethodInfo addHandler = tcmType.GetMethod("AddChatMsgReceivedEventHandler",
                    BindingFlags.Public | BindingFlags.Static);
                if (parsedMessageField == null || handlerDelegateType == null || addHandler == null)
                {
                    MelonLogger.Log("[SongRequestInterceptor] Could not resolve TwitchConnectorMod chat API; -id seam off.");
                    return;
                }

                MethodInfo handler = typeof(SongRequestInterceptor)
                    .GetMethod(nameof(OnChatMessage), BindingFlags.Static | BindingFlags.NonPublic);
                Delegate d = Delegate.CreateDelegate(handlerDelegateType, handler);
                addHandler.Invoke(null, new object[] { d });
            }
            catch (Exception e)
            {
                MelonLogger.Log($"[SongRequestInterceptor] Chat subscribe failed: {e.Message}");
            }
        }

        private static void OnChatMessage(object sender, object eventArgs)
        {
            try
            {
                if (eventArgs == null) return;
                string msg = parsedMessageField.GetValue(eventArgs) as string;
                if (string.IsNullOrEmpty(msg)) return;

                string trimmed = msg.TrimStart();
                if (!trimmed.ToLowerInvariant().StartsWith("!asr ")) return;

                ParsedQuery q = ParsedQuery.Parse(trimmed.Substring(5).Trim());
                if (q.MaudicaId != null)
                {
                    // Respect the on/off queue state for -id. (The title path is gated by SongRequest itself,
                    // which only emits the "not found" we hook when it actually processed the request.)
                    string mod = parsedModField?.GetValue(eventArgs) as string ?? "";
                    string bc = parsedBroadcasterField?.GetValue(eventArgs) as string ?? "";
                    if (!SongRequestIntegration.RequestsEnabled && bc != "1"
                        && !(mod == "1" && SongRequestIntegration.LetModsIgnoreQueueStatus))
                    {
                        MelonLogger.Log($"[SongRequestInterceptor] -id request ignored (queue disabled): {q.MaudicaId}");
                        return;
                    }

                    string requestedBy = parsedUserIdField != null
                        ? (parsedUserIdField.GetValue(eventArgs) as string ?? "") : "";
                    pendingIdContext[q.MaudicaId] = requestedBy;
                    MelonLogger.Log($"[SongRequestInterceptor] -id request caught: {q.MaudicaId} → looking up.");
                    MelonCoroutines.Start(SongDownloader.DoMaudicaIDSearch(q.MaudicaId, OnIdSearchResult));
                    return;
                }
                // Title misses are handled at the SendMessage seam; local matches are SongRequest's job.
            }
            catch (Exception e)
            {
                MelonLogger.Log($"[SongRequestInterceptor] OnChatMessage failed: {e.Message}");
            }
        }

        // ── Title-miss seam (SendMessage suppression) ──────────────────────────────
        private static void PatchSendMessage(Type tcmType)
        {
            try
            {
                MethodInfo target = tcmType.GetMethod("SendMessage",
                    BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
                if (target == null)
                {
                    MelonLogger.Log("[SongRequestInterceptor] TwitchConnectorMod.SendMessage not found; suppression off.");
                    return;
                }
                MethodInfo prefix = typeof(SongRequestInterceptor)
                    .GetMethod(nameof(SendMessagePrefix), BindingFlags.Static | BindingFlags.NonPublic);
                HarmonyInstance.Create("ExScoring.SongRequestInterceptor").Patch(target, new HarmonyMethod(prefix));
            }
            catch (Exception e)
            {
                MelonLogger.Log($"[SongRequestInterceptor] Failed to patch SendMessage: {e.Message}");
            }
        }

        // Returning false suppresses the outgoing message.
        private static bool SendMessagePrefix(string message)
        {
            if (selfEmitting || string.IsNullOrEmpty(message))
                return true; // let our own messages, and anything non-null-empty we don't care about, through

            Match m = notFoundPattern.Match(message);
            if (!m.Success)
            {
                // Any add/removal changes the request queue — live-update the Song Requests folder if it's
                // open. This is the universal signal: it fires both for SongRequest's own local adds (a song
                // you own, requested by title) and for the adds we emit ourselves.
                if (message.EndsWith("added to the queue.") || message.EndsWith("removed from the queue."))
                    MelonCoroutines.Start(RefreshSoon());
                return true; // not a "not found" — allow (added / removed / queue toggles / etc.)
            }

            string query = m.Groups[1].Value;
            MelonLogger.Log($"[SongRequestInterceptor] Suppressing SongRequest \"not found\" for \"{query}\"; web-searching.");
            MelonCoroutines.Start(HandleTitleMiss(query));
            return false; // suppress SongRequest's premature "not found"
        }

        // SendMessage can fire on a background (chat) thread, so hop onto the coroutine pump before touching
        // the view. RefreshList is a no-op unless the song list is currently shown.
        private static System.Collections.IEnumerator RefreshSoon()
        {
            yield return null;
            FolderRowManager.RefreshList();
        }

        private static System.Collections.IEnumerator HandleTitleMiss(string rawQuery)
        {
            ParsedQuery q = ParsedQuery.Parse(rawQuery);
            string term = q.SearchTerm();
            if (string.IsNullOrEmpty(term))
            {
                EmitNotFound(rawQuery);
                yield break;
            }

            // capture the original query so the result handler can re-emit an accurate "not found"
            pendingQueryByTerm[term] = rawQuery;
            yield return MelonCoroutines.Start(SongDownloader.DoSongWebSearch(term, OnTitleSearchResult, DifficultyFilter.All));
        }

        private static readonly System.Collections.Generic.Dictionary<string, string> pendingQueryByTerm
            = new System.Collections.Generic.Dictionary<string, string>();

        // id -> requestedBy, carried across the async -id lookup
        private static readonly System.Collections.Generic.Dictionary<string, string> pendingIdContext
            = new System.Collections.Generic.Dictionary<string, string>();

        private static void OnTitleSearchResult(string term, APISongList response)
        {
            string rawQuery = pendingQueryByTerm.TryGetValue(term, out string rq) ? rq : term;
            pendingQueryByTerm.Remove(term);

            Song best = PickBestMatch(response, ParsedQuery.Parse(rawQuery));
            QueueResult(best, "", rawQuery);
        }

        private static void OnIdSearchResult(string id, APISongList response)
        {
            string requestedBy = pendingIdContext.TryGetValue(id, out string by) ? by : "";
            pendingIdContext.Remove(id);

            // The ?id= lookup returns the specific map; take the first result if present.
            Song best = (response?.songs != null && response.songs.Length > 0) ? response.songs[0] : null;
            QueueResult(best, requestedBy, "-id " + id);
        }

        /// <summary>
        /// Shared handling for a matched (or unmatched) web result: emit "not found" if none; hand to
        /// SongRequest's available queue if the song is already local; otherwise store it as a missing
        /// request, prime SongRequest's dedupe cache, and emit the native "added" message.
        /// </summary>
        private static void QueueResult(Song best, string requestedBy, string notFoundQuery)
        {
            if (best == null || string.IsNullOrEmpty(best.song_id))
            {
                MelonLogger.Log($"[SongRequestInterceptor] No match for \"{notFoundQuery}\" → not found.");
                EmitNotFound(notFoundQuery);
                return;
            }

            SongList.SongData local = FindLocalSong(best);
            if (local != null)
            {
                MelonLogger.Log($"[SongRequestInterceptor] Request already local: {best.title} - {best.artist} → available queue.");
                SongRequestIntegration.AddAvailableRequest(local, requestedBy, DateTime.Now);
                return;
            }

            if (SongRequestStore.Contains(best.song_id))
            {
                SongRequestIntegration.EmitSongAlreadyInQueue(best.title);
                return;
            }

            var entry = new SongRequestStore.RequestedSong
            {
                SongID = best.song_id,
                Title = best.title,
                Artist = best.artist,
                Mapper = best.author,
                DownloadURL = best.download_url,
                PreviewURL = best.preview_url,
                RequestedBy = requestedBy ?? "",
                RequestedAtUtcTicks = DateTime.UtcNow.Ticks
            };
            SongRequestStore.Add(entry);
            SongRequestIntegration.PrimeDownloadNameCache(best.song_id);
            SongRequestIntegration.EmitNewSongQueueItem(best.title, best.artist, best.author);
            MelonLogger.Log($"[SongRequestInterceptor] Queued missing request: {best.song_id} | {best.title} - {best.artist}");
        }

        private static void EmitNotFound(string query)
        {
            // selfEmitting lets our own "not found" pass the SendMessage prefix (it matches the pattern).
            selfEmitting = true;
            try { SongRequestIntegration.EmitSongNotFound(query); }
            finally { selfEmitting = false; }
        }

        // ── Matching helpers ────────────────────────────────────────────────────────
        // Mirrors SongRequest's LookForMatch selection over the API results.
        private static Song PickBestMatch(APISongList response, ParsedQuery q)
        {
            if (response?.songs == null) return null;

            Song best = null;
            bool any = false, better = false, exact = false;

            foreach (Song s in response.songs)
            {
                if (s == null) continue;
                string title = s.title?.ToLowerInvariant() ?? "";
                string artist = s.artist?.ToLowerInvariant().Replace(" ", "") ?? "";
                string author = s.author?.ToLowerInvariant().Replace(" ", "") ?? "";
                string id = s.song_id?.ToLowerInvariant() ?? "";

                bool hasArtist = q.Artist == null || artist.Contains(q.Artist);
                bool hasMapper = q.Mapper == null || author.Contains(q.Mapper);
                bool hasTitle = title.Contains(q.Title) || id.Contains(q.Title.Replace(" ", ""));

                bool candidate = (hasArtist && hasMapper && hasTitle)
                              || (q.Title == "" && q.Artist != null && hasArtist)
                              || (q.Title == "" && q.Mapper != null && hasMapper);
                if (!candidate) continue;

                bool newBest = false;
                if (!any) { any = true; newBest = true; }
                if (!better && title.StartsWith(q.Title)) { better = true; newBest = true; }
                if (title.Trim() == q.Title) { exact = true; newBest = true; }

                if (newBest) best = s;
                if (exact) break;
            }
            return best;
        }

        private static SongList.SongData FindLocalSong(Song match)
        {
            try
            {
                if (match == null || SongList.I == null || SongList.I.songs == null) return null;

                string id = match.song_id?.ToLowerInvariant() ?? "";
                string bTitle = (match.title ?? "").ToLowerInvariant().Trim();
                string bArtist = (match.artist ?? "").ToLowerInvariant().Replace(" ", "");
                string bMapper = (match.author ?? "").ToLowerInvariant().Replace(" ", "");

                for (int i = 0; i < SongList.I.songs.Count; i++)
                {
                    var s = SongList.I.songs[i];
                    if (s == null) continue;

                    // Fast path: exact songID match.
                    if (id.Length > 0 && (s.songID?.ToLowerInvariant() ?? "") == id)
                        return s;

                    // Metadata path: exact title + artist/mapper contained (mirrors SongRequest's
                    // exact-match rule, since maudica song_id and the local songID often differ).
                    string lTitle = (s.title ?? "").ToLowerInvariant().Trim();
                    string lArtist = (s.artist ?? "").ToLowerInvariant().Replace(" ", "");
                    string lAuthor = (s.author ?? "").ToLowerInvariant().Replace(" ", "");

                    bool titleExact = lTitle.Length > 0 && lTitle == bTitle;
                    bool artistOk = bArtist == "" || lArtist.Contains(bArtist);
                    bool mapperOk = bMapper == "" || lAuthor.Contains(bMapper);

                    if (titleExact && artistOk && mapperOk) return s;
                }
            }
            catch (Exception e) { MelonLogger.Log($"[SongRequestInterceptor] FindLocalSong failed: {e.Message}"); }
            return null;
        }

        // ── Query parsing (mirrors SongRequest's QueryData extraction) ─────────────
        private struct ParsedQuery
        {
            public string MaudicaId;
            public string Title;
            public string Artist;
            public string Mapper;

            public static ParsedQuery Parse(string rawQuery)
            {
                var p = new ParsedQuery();
                string q = (rawQuery ?? "").ToLowerInvariant();
                string modified = q + "-endquery";

                if (q.Contains("-id ") && int.TryParse(q.Replace("-id", "").Trim(), out _))
                {
                    Match m = Regex.Match(modified, "-id.*?(?=-mapper|-artist|-endquery)");
                    p.MaudicaId = m.Value.Replace("-id", "").Trim().Replace(" ", "");
                    return p;
                }
                if (q.Contains("-artist "))
                {
                    Match m = Regex.Match(modified, "-artist.*?(?=-mapper|-endquery)");
                    q = q.Replace(m.Value, "");
                    p.Artist = m.Value.Replace("-artist", "").Trim().Replace(" ", "");
                }
                if (q.Contains("-mapper "))
                {
                    Match m = Regex.Match(modified, "-mapper.*?(?=-artist|-endquery)");
                    q = q.Replace(m.Value, "");
                    p.Mapper = m.Value.Replace("-mapper", "").Trim().Replace(" ", "");
                }
                p.Title = q.Trim();
                return p;
            }

            public string SearchTerm()
            {
                if (!string.IsNullOrEmpty(Title)) return Title;
                if (!string.IsNullOrEmpty(Artist)) return Artist;
                if (!string.IsNullOrEmpty(Mapper)) return Mapper;
                return "";
            }
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = asm.GetType(fullName); }
                catch { }
                if (t != null) return t;
            }
            return null;
        }
    }
}