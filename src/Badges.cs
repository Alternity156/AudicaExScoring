using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MelonLoader;
using Newtonsoft.Json;
using TMPro;
using UnhollowerRuntimeLib;
using UnityEngine;
using UnityEngine.Networking;

namespace ExScoringMod
{
    /// <summary>
    /// Player badges (ApiContract.md v2.63): an icon in front of a player's name on EX leaderboards,
    /// like the official game's developer icon.
    ///
    /// Native builds a row's name as "badge sprite tag + LaurelWrap(name)" (LeaderboardRow.SetData,
    /// Ghidra), and this does the same:
    /// - Native icon (badge.nativeSprite set): &lt;sprite="icons" name="hmx"&gt;, straight from the
    ///   game's own "icons" TMP sprite asset.
    /// - Custom icon (a PNG uploaded on the website): the PNG is downloaded once, then drawn through a
    ///   clone of "icons" whose sheet is a blank texture with the PNG in hmx's slot. The clone is
    ///   registered with TMP as "exb_&lt;badgeId&gt;", so &lt;sprite="exb_&lt;id&gt;" name="hmx"&gt; draws the
    ///   custom icon with exactly the native icons' size and baseline. Tested in game 2026-11-23
    ///   (TestCustomBadgeSprite.cs).
    ///
    /// Also the Badge row in Options > Leaderboard (Auto / None / each owned badge).
    /// </summary>
    public partial class ExScoring : MelonMod
    {
        // ── Native icons ──────────────────────────────────────────────────────────────────────

        private const string NativeIconsAssetName = "icons";

        // Sprites in "icons" the server may name as a badge (exported 2026-11-23). Not the laurels
        // (FC wreath), and not "early_access", which has no entry in the sprite table. A name this
        // version doesn't know falls back to downloading iconUrl, so the server can add more later.
        private static readonly HashSet<string> NativeBadgeSprites = new HashSet<string>
        {
            "hmx", "mapper_official", "mapper_unofficial", "icons_0", "icons_1"
        };

        // ── Custom icons ──────────────────────────────────────────────────────────────────────

        // Where a custom icon is drawn on its clone's sheet: hmx's rect on the 1024x256 "icons" sheet
        // (Unity coordinates, bottom-left origin). The tag then asks for name="hmx", so TMP uses
        // hmx's metrics for it.
        private const string BadgeSlotSprite = "hmx";
        private const int BadgeSlotX = 519;
        private const int BadgeSlotY = 0;
        private const int BadgeSlotSize = 128;

        private const string CustomBadgeAssetPrefix = "exb_";

        // A failed icon download is tried again after this long, on the next leaderboard that shows it.
        private const float BadgeIconRetrySeconds = 60f;

        // Badge ids are slugs (ApiContract v2.63). Anything else is never put into a sprite tag.
        private static readonly Regex BadgeIdRegex = new Regex("^[a-z0-9_]{1,32}$");

        private class CustomBadgeAsset
        {
            public TMP_SpriteAsset asset;
            public Texture2D sheet;
            public Material material;
            /// <summary>The iconUrl whose image is currently on the sheet.</summary>
            public string iconUrl;
        }

        // By badge id. Kept for the whole session: a new upload rebuilds the sheet in place, so the
        // registration with TMP never has to change.
        private static readonly Dictionary<string, CustomBadgeAsset> customBadgeAssets = new Dictionary<string, CustomBadgeAsset>();

        // Icon downloads in flight, by iconUrl.
        private static readonly HashSet<string> badgeIconDownloads = new HashSet<string>();

        // When a download failed, by iconUrl (Time.realtimeSinceStartup).
        private static readonly Dictionary<string, float> badgeIconFailedAt = new Dictionary<string, float>();

        // The newest iconUrl seen for each badge id. A download that finishes for an older URL
        // (the icon was changed again meanwhile) is dropped.
        private static readonly Dictionary<string, string> latestBadgeIconUrl = new Dictionary<string, string>();

        private static TMP_SpriteAsset nativeIconsAsset;

        // Unknown native names already logged, so each is logged once.
        private static readonly HashSet<string> loggedUnknownNativeSprites = new HashSet<string>();

        // ── Rows waiting for an icon ───────────────────────────────────────────────────────────

        private class BadgeRowState
        {
            public TextMeshPro text;
            public string nameText;
            public BadgeInfo badge;
        }

        // Every row currently showing a badge, by row GameObject instance id (same slot key as the
        // rest of the leaderboard code). Used to redraw rows when a custom icon finishes downloading.
        private static readonly Dictionary<int, BadgeRowState> badgeRows = new Dictionary<int, BadgeRowState>();

        /// <summary>
        /// Writes a leaderboard row's name: badge tag (if any) + nameText (the already laurel-wrapped
        /// nickname). A custom icon that isn't downloaded yet is started now and the name is shown
        /// without it; the row is redrawn when it arrives, if it still shows the same player.
        /// </summary>
        internal static void ApplyBadgedUsername(LeaderboardRow row, int slot, BadgeInfo badge, string nameText)
        {
            if (row == null || row.username == null) return;

            row.username.text = BadgePrefix(badge) + nameText;

            if (badge != null)
                badgeRows[slot] = new BadgeRowState { text = row.username, nameText = nameText, badge = badge };
            else
                badgeRows.Remove(slot);
        }

        /// <summary>Called when a row is blanked or handed back to native.</summary>
        internal static void ForgetBadgeRow(int slot)
        {
            badgeRows.Remove(slot);
        }

        /// <summary>
        /// The sprite tag to put in front of a name for this badge, or "" for none (no badge, an
        /// unusable one, or a custom icon that isn't ready yet).
        /// </summary>
        private static string BadgePrefix(BadgeInfo badge)
        {
            if (badge == null || string.IsNullOrEmpty(badge.id)) return "";

            if (!string.IsNullOrEmpty(badge.nativeSprite))
            {
                if (NativeBadgeSprites.Contains(badge.nativeSprite))
                    return $"<sprite=\"{NativeIconsAssetName}\" name=\"{badge.nativeSprite}\">";

                if (loggedUnknownNativeSprites.Add(badge.nativeSprite))
                    DebugLog.Log($"[Badges] Unknown native sprite '{badge.nativeSprite}' (badge '{badge.id}'), using its iconUrl instead.");
            }

            return CustomBadgePrefix(badge);
        }

        private static string CustomBadgePrefix(BadgeInfo badge)
        {
            if (!BadgeIdRegex.IsMatch(badge.id))
            {
                DebugLog.Log($"[Badges] Badge id '{badge.id}' isn't a valid slug, not drawn.");
                return "";
            }

            if (string.IsNullOrEmpty(badge.iconUrl)) return "";

            latestBadgeIconUrl[badge.id] = badge.iconUrl;

            customBadgeAssets.TryGetValue(badge.id, out CustomBadgeAsset entry);
            if (entry == null || entry.iconUrl != badge.iconUrl)
                StartBadgeIconDownload(badge.id, badge.iconUrl);

            // A badge whose icon was just changed keeps showing the old icon until the new one is in.
            if (entry == null) return "";
            return $"<sprite=\"{CustomBadgeAssetPrefix}{badge.id}\" name=\"{BadgeSlotSprite}\">";
        }

        private static void StartBadgeIconDownload(string badgeId, string iconUrl)
        {
            if (badgeIconDownloads.Contains(iconUrl)) return;

            if (badgeIconFailedAt.TryGetValue(iconUrl, out float failedAt)
                && Time.realtimeSinceStartup - failedAt < BadgeIconRetrySeconds)
                return;

            string url = ResolveBadgeIconUrl(iconUrl);
            if (url == null)
            {
                DebugLog.Log($"[Badges] Badge '{badgeId}': iconUrl '{iconUrl}' isn't a site path or an https:// URL, not downloaded.");
                badgeIconFailedAt[iconUrl] = Time.realtimeSinceStartup;
                return;
            }

            badgeIconDownloads.Add(iconUrl);
            MelonCoroutines.Start(DownloadBadgeIconCoroutine(badgeId, iconUrl, url));
        }

        /// <summary>A site-relative path goes on ApiBaseUrl; an absolute URL must be https.</summary>
        private static string ResolveBadgeIconUrl(string iconUrl)
        {
            if (iconUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return iconUrl;
            if (iconUrl.StartsWith("/") && !iconUrl.StartsWith("//")) return ApiBaseUrl + iconUrl;
            return null;
        }

        private static IEnumerator DownloadBadgeIconCoroutine(string badgeId, string iconUrl, string url)
        {
            DebugLog.Log($"[Badges] Badge '{badgeId}': downloading icon {url}");

            byte[] png = null;
            UnityWebRequest request = UnityWebRequest.Get(url);
            try
            {
                yield return request.SendWebRequest();

                if (request.isNetworkError || request.isHttpError)
                    DebugLog.Log($"[Badges] Badge '{badgeId}': icon download failed ({request.responseCode}): {request.error}");
                else
                    png = CopyBadgeBytes(request.downloadHandler.data);
            }
            finally
            {
                request.Dispose();
                badgeIconDownloads.Remove(iconUrl);
            }

            if (png == null || png.Length == 0)
            {
                badgeIconFailedAt[iconUrl] = Time.realtimeSinceStartup;
                yield break;
            }

            if (latestBadgeIconUrl.TryGetValue(badgeId, out string latest) && latest != iconUrl)
            {
                DebugLog.Log($"[Badges] Badge '{badgeId}': icon {iconUrl} arrived after a newer one was seen, dropped.");
                yield break;
            }

            bool applied;
            try
            {
                applied = ApplyCustomBadgeIcon(badgeId, iconUrl, png);
            }
            catch (Exception ex)
            {
                DebugLog.Log($"[Badges] Badge '{badgeId}': building the icon failed: {ex}");
                applied = false;
            }

            if (!applied)
            {
                badgeIconFailedAt[iconUrl] = Time.realtimeSinceStartup;
                yield break;
            }

            badgeIconFailedAt.Remove(iconUrl);
            RedrawBadgeRows(badgeId);
        }

        private static byte[] CopyBadgeBytes(UnhollowerBaseLib.Il2CppStructArray<byte> data)
        {
            if (data == null) return null;
            byte[] bytes = new byte[data.Length];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = data[i];
            return bytes;
        }

        /// <summary>
        /// Puts a downloaded PNG on the badge's sprite sheet, creating and registering the clone of
        /// "icons" the first time. Returns false if it couldn't (bad image, "icons" not loaded).
        /// </summary>
        private static bool ApplyCustomBadgeIcon(string badgeId, string iconUrl, byte[] png)
        {
            TMP_SpriteAsset icons = FindNativeIconsAsset();
            if (icons == null || icons.spriteSheet == null || icons.material == null)
            {
                DebugLog.Log($"[Badges] Badge '{badgeId}': the '{NativeIconsAssetName}' sprite asset isn't loaded, will try again later.");
                return false;
            }

            int sheetW = icons.spriteSheet.width, sheetH = icons.spriteSheet.height;
            if (BadgeSlotX + BadgeSlotSize > sheetW || BadgeSlotY + BadgeSlotSize > sheetH)
            {
                DebugLog.Log($"[Badges] '{NativeIconsAssetName}' sheet is {sheetW}x{sheetH}, too small for the badge slot. Custom badges disabled.");
                return false;
            }

            var src = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                if (!ImageConversion.LoadImage(src, png))
                {
                    DebugLog.Log($"[Badges] Badge '{badgeId}': the icon isn't a readable image.");
                    return false;
                }

                customBadgeAssets.TryGetValue(badgeId, out CustomBadgeAsset entry);
                if (entry == null)
                {
                    entry = CreateCustomBadgeAsset(badgeId, icons, sheetW, sheetH);
                    customBadgeAssets[badgeId] = entry;
                }

                // Resample whatever size was uploaded into the 128x128 slot.
                var slot = new Color[BadgeSlotSize * BadgeSlotSize];
                for (int y = 0; y < BadgeSlotSize; y++)
                    for (int x = 0; x < BadgeSlotSize; x++)
                        slot[y * BadgeSlotSize + x] = src.GetPixelBilinear((x + 0.5f) / BadgeSlotSize, (y + 0.5f) / BadgeSlotSize);

                entry.sheet.SetPixels(BadgeSlotX, BadgeSlotY, BadgeSlotSize, BadgeSlotSize, slot);
                entry.sheet.Apply(false, false);
                entry.iconUrl = iconUrl;

                DebugLog.Log($"[Badges] Badge '{badgeId}': custom icon ready ({src.width}x{src.height} → {BadgeSlotSize}x{BadgeSlotSize}).");
                return true;
            }
            finally
            {
                UnityEngine.Object.Destroy(src);
            }
        }

        private static CustomBadgeAsset CreateCustomBadgeAsset(string badgeId, TMP_SpriteAsset icons, int sheetW, int sheetH)
        {
            string assetName = CustomBadgeAssetPrefix + badgeId;

            var sheet = new Texture2D(sheetW, sheetH, TextureFormat.RGBA32, false);
            sheet.SetPixels(new Color[sheetW * sheetH]); // fully transparent
            sheet.Apply(false, false);
            sheet.name = assetName + "_sheet";
            sheet.wrapMode = TextureWrapMode.Clamp;
            sheet.hideFlags = HideFlags.DontUnloadUnusedAsset;

            // The clone keeps every sprite entry and rect of "icons"; only its texture differs.
            TMP_SpriteAsset clone = UnityEngine.Object.Instantiate(icons);
            clone.name = assetName;
            clone.hideFlags = HideFlags.DontUnloadUnusedAsset;
            clone.spriteSheet = sheet;

            var mat = new Material(icons.material);
            mat.name = assetName + "_mat";
            mat.SetTexture("_MainTex", sheet);
            mat.hideFlags = HideFlags.DontUnloadUnusedAsset;
            clone.material = mat;

            // Some TMP versions key materials by a hash of the material's name, and assets by a hash
            // of their own. Refreshed through reflection, only where the property exists.
            int assetHash = TMP_TextUtilities.GetSimpleHashCode(assetName);
            var matHashProp = clone.GetType().GetProperty("materialHashCode");
            if (matHashProp != null && matHashProp.CanWrite) matHashProp.SetValue(clone, TMP_TextUtilities.GetSimpleHashCode(mat.name));
            var hashProp = clone.GetType().GetProperty("hashCode");
            if (hashProp != null && hashProp.CanWrite) hashProp.SetValue(clone, assetHash);

            // Where <sprite="name"> looks assets up before trying Resources.
            MaterialReferenceManager.AddSpriteAsset(assetHash, clone);

            DebugLog.Log($"[Badges] Registered sprite asset '{assetName}' ({sheetW}x{sheetH}).");
            return new CustomBadgeAsset { asset = clone, sheet = sheet, material = mat };
        }

        private static TMP_SpriteAsset FindNativeIconsAsset()
        {
            if (nativeIconsAsset != null) return nativeIconsAsset;

            // Non-generic overload + TryCast, as elsewhere in the mod (the generic one isn't
            // available under unhollower).
            foreach (var o in Resources.FindObjectsOfTypeAll(Il2CppType.Of<TMP_SpriteAsset>()))
            {
                TMP_SpriteAsset sa = o.TryCast<TMP_SpriteAsset>();
                if (sa != null && sa.name == NativeIconsAssetName)
                {
                    nativeIconsAsset = sa;
                    break;
                }
            }
            return nativeIconsAsset;
        }

        /// <summary>
        /// Rewrites the name of every row showing this badge, once its custom icon is ready. Rows
        /// that were cleared, reused for another player or handed back to native are skipped.
        /// </summary>
        private static void RedrawBadgeRows(string badgeId)
        {
            if (!Config.ExType) return;

            int redrawn = 0;
            foreach (var kvp in badgeRows.ToList())
            {
                BadgeRowState state = kvp.Value;
                if (state.badge == null || state.badge.id != badgeId) continue;

                try
                {
                    if (state.text == null || !state.text.gameObject.activeInHierarchy)
                    {
                        badgeRows.Remove(kvp.Key);
                        continue;
                    }

                    state.text.text = BadgePrefix(state.badge) + state.nameText;
                    redrawn++;
                }
                catch (Exception ex)
                {
                    DebugLog.Log($"[Badges] Redrawing a row for '{badgeId}' failed, dropping it: {ex.Message}");
                    badgeRows.Remove(kvp.Key);
                }
            }

            if (redrawn > 0) DebugLog.Log($"[Badges] Badge '{badgeId}': redrew {redrawn} row(s).");
        }

        // ── Options > Leaderboard: Badge row ──────────────────────────────────────────────────

        private const string LeaderboardOptionsCategoryId = "opt_leaderboard";

        // Fetch again when the page is opened this long after the last fetch, so badges granted
        // while the game is running show up.
        private const float MyBadgesRefreshSeconds = 60f;

        // How long the Badge row waits after the last shot before sending the choice, so cycling
        // through several badges sends one request.
        private const float BadgeChoiceSendDelay = 0.75f;

        private static MyBadgesApiResponse myBadges;
        private static string myBadgesApiKey;
        private static float myBadgesFetchedAt = -1000f;
        private static bool myBadgesLoading;
        private static bool myBadgesNotSupported; // 404: server older than v2.63
        private static int myBadgesRequestVersion;
        private static int badgeChoiceSendVersion;

        private const string BadgeOptionHover =
            "Which badge shows next to your name on AudicaEX leaderboards. Auto shows your highest-priority badge, None hides it. Badges are given out on AudicaEX.org";

        /// <summary>
        /// Adds the Badge row to Options > Leaderboard (called from GlobalOptions while it builds the
        /// page). Shown only with an API key and at least one owned badge. The first time (and after a
        /// minute) it fetches the player's badges and redraws the page when they arrive.
        /// </summary>
        internal static void AddBadgeOptionRow()
        {
            if (string.IsNullOrEmpty(Config.ApiKey)) return;

            if (myBadgesApiKey != Config.ApiKey)
            {
                // Different key (pasted, linked or deleted since): start over.
                myBadgesApiKey = Config.ApiKey;
                myBadges = null;
                myBadgesNotSupported = false;
                myBadgesFetchedAt = -1000f;
                ++myBadgesRequestVersion;
                myBadgesLoading = false;
            }

            if (!myBadgesNotSupported && !myBadgesLoading
                && Time.realtimeSinceStartup - myBadgesFetchedAt > MyBadgesRefreshSeconds)
                LoadMyBadges();

            BadgeInfo[] owned = myBadges?.owned;
            if (owned == null || owned.Length == 0) return;

            var options = new string[owned.Length + 2];
            options[0] = "Auto";
            options[1] = "None";
            for (int i = 0; i < owned.Length; i++)
                options[i + 2] = string.IsNullOrEmpty(owned[i]?.name) ? owned[i]?.id ?? "?" : owned[i].name;

            // owned is captured here: a refetch that changes it redraws the page (and this row).
            var badgeCycle = OptionsMenuClone.CreateCycle(0, "Badge", options,
                () => GetBadgeChoiceIndex(owned),
                idx => OnBadgeChoiceCycled(owned, idx),
                0,
                BadgeOptionHover);
            OptionsMenuClone.AddRow(badgeCycle);
        }

        private static int GetBadgeChoiceIndex(BadgeInfo[] owned)
        {
            string choice = myBadges?.choice;
            if (string.IsNullOrEmpty(choice) || choice == "auto") return 0;
            if (choice == "none") return 1;
            for (int i = 0; i < owned.Length; i++)
                if (owned[i] != null && owned[i].id == choice) return i + 2;
            return 0; // an id the player no longer owns resolves as Auto (server rule)
        }

        private static void OnBadgeChoiceCycled(BadgeInfo[] owned, int idx)
        {
            if (myBadges == null) return;

            string choice;
            if (idx <= 0) choice = "auto";
            else if (idx == 1) choice = "none";
            else if (idx - 2 < owned.Length && owned[idx - 2] != null) choice = owned[idx - 2].id;
            else return;

            if (choice == myBadges.choice) return;

            // Shown at once; sent once the player stops cycling.
            myBadges.choice = choice;
            int thisSend = ++badgeChoiceSendVersion;
            MelonCoroutines.Start(SendBadgeChoiceAfterDelay(thisSend, choice));
        }

        private static IEnumerator SendBadgeChoiceAfterDelay(int thisSend, string choice)
        {
            float until = Time.realtimeSinceStartup + BadgeChoiceSendDelay;
            while (Time.realtimeSinceStartup < until) yield return null;
            if (thisSend != badgeChoiceSendVersion) yield break; // cycled again since

            SetBadgeChoice(choice, response =>
            {
                if (thisSend != badgeChoiceSendVersion) return;

                if (response != null)
                {
                    myBadges = response;
                    myBadgesFetchedAt = Time.realtimeSinceStartup;
                    DebugLog.Log($"[Badges] Badge choice saved: {response.choice} (showing '{response.displayed?.id ?? "none"}').");
                }
                else
                {
                    // Put the row back to what the server really has.
                    DebugLog.Log("[Badges] Saving the badge choice failed, reloading the player's badges.");
                    myBadgesFetchedAt = -1000f;
                    LoadMyBadges();
                }
            });
        }

        private static void LoadMyBadges()
        {
            int thisRequest = ++myBadgesRequestVersion;
            myBadgesLoading = true;

            FetchMyBadges((response, statusCode) =>
            {
                if (thisRequest != myBadgesRequestVersion) return;
                myBadgesLoading = false;
                myBadgesFetchedAt = Time.realtimeSinceStartup;

                if (statusCode == 404)
                {
                    DebugLog.Log("[Badges] Server has no badges endpoint (older than v2.63), Badge row hidden this session.");
                    myBadgesNotSupported = true;
                    return;
                }

                if (response == null) return; // keep what we had

                bool changed = !SameBadgeState(myBadges, response);
                myBadges = response;
                if (changed) GlobalOptions.RedrawIfShowing(LeaderboardOptionsCategoryId);
            });
        }

        /// <summary>Same choice and same owned ids/names, i.e. the Badge row would look the same.</summary>
        private static bool SameBadgeState(MyBadgesApiResponse a, MyBadgesApiResponse b)
        {
            if (a == null || b == null) return a == b;
            if (a.choice != b.choice) return false;

            BadgeInfo[] ao = a.owned ?? new BadgeInfo[0], bo = b.owned ?? new BadgeInfo[0];
            if (ao.Length != bo.Length) return false;
            for (int i = 0; i < ao.Length; i++)
                if (ao[i]?.id != bo[i]?.id || ao[i]?.name != bo[i]?.name) return false;
            return true;
        }

        // ── API calls ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// GET /api/users/me/badges (ApiContract.md v2.63). Always calls onComplete exactly once: with
        /// the response and 200, or null and the status code (0 for a network error or bad body).
        /// </summary>
        private static void FetchMyBadges(Action<MyBadgesApiResponse, long> onComplete)
        {
            if (string.IsNullOrEmpty(Config.ApiKey))
            {
                onComplete?.Invoke(null, 0);
                return;
            }
            MelonCoroutines.Start(FetchMyBadgesCoroutine(onComplete));
        }

        private static IEnumerator FetchMyBadgesCoroutine(Action<MyBadgesApiResponse, long> onComplete)
        {
            string url = $"{ApiBaseUrl}/api/users/me/badges";
            DebugLog.Log($"[Badges] FetchMyBadges: GET {url}");

            UnityWebRequest request = UnityWebRequest.Get(url);
            try
            {
                request.SetRequestHeader("Authorization", "ApiKey " + Config.ApiKey);

                yield return request.SendWebRequest();

                if (request.isNetworkError)
                {
                    DebugLog.Log($"[Badges] FetchMyBadges failed (network error): {request.error}");
                    onComplete?.Invoke(null, 0);
                    yield break;
                }

                if (request.isHttpError)
                {
                    string errorBody = request.downloadHandler != null ? request.downloadHandler.text : "";
                    DebugLog.Log($"[Badges] FetchMyBadges failed ({request.responseCode}): {request.error} | Body: {errorBody}");
                    onComplete?.Invoke(null, request.responseCode);
                    yield break;
                }

                try
                {
                    MyBadgesApiResponse response = JsonConvert.DeserializeObject<MyBadgesApiResponse>(request.downloadHandler.text);
                    DebugLog.Log($"[Badges] FetchMyBadges succeeded: choice={response?.choice} owned={response?.owned?.Length ?? 0}");
                    onComplete?.Invoke(response, 200);
                }
                catch (Exception ex)
                {
                    DebugLog.Log($"[Badges] FetchMyBadges: failed to parse response ({request.downloadHandler.text}): {ex}");
                    onComplete?.Invoke(null, 0);
                }
            }
            finally
            {
                request.Dispose();
            }
        }

        /// <summary>
        /// PATCH /api/users/me/badge { choice } (ApiContract.md v2.63). Always calls onComplete exactly
        /// once: with the server's updated state, or null on any failure.
        /// </summary>
        private static void SetBadgeChoice(string choice, Action<MyBadgesApiResponse> onComplete)
        {
            if (string.IsNullOrEmpty(Config.ApiKey))
            {
                onComplete?.Invoke(null);
                return;
            }

            byte[] body;
            try
            {
                body = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new Dictionary<string, string> { { "choice", choice } }));
            }
            catch (Exception ex)
            {
                DebugLog.Log($"[Badges] SetBadgeChoice: failed to build request: {ex}");
                onComplete?.Invoke(null);
                return;
            }

            MelonCoroutines.Start(SetBadgeChoiceCoroutine(body, choice, onComplete));
        }

        private static IEnumerator SetBadgeChoiceCoroutine(byte[] body, string choice, Action<MyBadgesApiResponse> onComplete)
        {
            string url = $"{ApiBaseUrl}/api/users/me/badge";
            DebugLog.Log($"[Badges] SetBadgeChoice: PATCH {url} choice={choice}");

            UnityWebRequest request = new UnityWebRequest(url, "PATCH");
            try
            {
                request.uploadHandler = new UploadHandlerRaw(body);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Authorization", "ApiKey " + Config.ApiKey);

                yield return request.SendWebRequest();

                if (request.isNetworkError || request.isHttpError)
                {
                    string errorBody = request.downloadHandler != null ? request.downloadHandler.text : "";
                    DebugLog.Log($"[Badges] SetBadgeChoice failed ({request.responseCode}): {request.error} | Body: {errorBody}");
                    onComplete?.Invoke(null);
                    yield break;
                }

                try
                {
                    onComplete?.Invoke(JsonConvert.DeserializeObject<MyBadgesApiResponse>(request.downloadHandler.text));
                }
                catch (Exception ex)
                {
                    DebugLog.Log($"[Badges] SetBadgeChoice: failed to parse response ({request.downloadHandler.text}): {ex}");
                    onComplete?.Invoke(null);
                }
            }
            finally
            {
                request.Dispose();
            }
        }
    }
}