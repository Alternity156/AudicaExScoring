using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// How an image fills its area on a folder button.
    ///   Crop    — fill the area, keep proportions, cut off what overflows (default).
    ///   Fit     — whole image visible, keep proportions; the folder color shows around it.
    ///   Stretch — fill the area exactly, ignoring proportions.
    /// </summary>
    internal enum FolderImageFit { Crop, Fit, Stretch }

    /// <summary>
    /// Optional per-folder look, read from a "folder.json" inside a pack subfolder of the
    /// songs directory. Every field is optional; a missing/invalid field falls back to the
    /// normal behaviour for that field only.
    /// </summary>
    internal sealed class FolderStyle
    {
        public string displayName;      // null → use the folder's name on disk
        public bool hideName;           // "hideName": true → no title text, e.g. when the image is the name
        public Color? color;            // null → normal "Other Folders" color
        public Color? textColor;        // null → the button's default text color
        public Texture2D image;         // null → no image
        public FolderImageFit imageFit = FolderImageFit.Crop;
        public Vector2 imageSize = Vector2.one;     // fraction of the button (1,1 = whole button)
        public Vector2 imagePosition = Vector2.zero; // image-center offset, fraction of the button (+x right, +y up)
        public float imageDim;          // 0 = untouched, 1 = black

        // Cache bookkeeping
        internal DateTime jsonWriteTime;
        internal string imagePath;
        internal DateTime imageWriteTime;
    }

    /// <summary>
    /// Loads and caches folder.json customizations for the pack subfolders. Keyed by the folder's
    /// name on disk, which stays the folder's identifier everywhere else — only what is drawn on
    /// the button changes.
    ///
    /// folder.json example (all fields optional):
    /// {
    ///   "name": "Neon Pack",           // "" or missing = the folder's name on disk
    ///   "hideName": false,             // true = show no name (the image serves as the name)
    ///   "color": "#FF3080",            // #RRGGBB or #RRGGBBAA, drawn behind the image
    ///   "textColor": "#FFFFFF",
    ///   "image": "banner.png",         // relative to the folder
    ///   "imageFit": "crop",            // crop | fit | stretch
    ///   "imageSize": [0.5, 1],         // or a single number for both
    ///   "imagePosition": [-0.25, 0],
    ///   "imageDim": 0.3                // 0..1
    /// }
    /// </summary>
    internal static class FolderCustomization
    {
        public const string FileName = "folder.json";

        private static readonly Dictionary<string, FolderStyle> styles =
            new Dictionary<string, FolderStyle>(StringComparer.Ordinal);

        /// <summary>
        /// Customization for a folder, or null if it has none. Styles are keyed by the on-disk
        /// name; a folder with a pending rename (Edit Custom Folders) is shown under its new name
        /// before it's renamed on disk, so that name is mapped back first.
        /// </summary>
        public static FolderStyle Get(string folder)
        {
            if (folder == null) return null;
            string onDisk = FolderOpsJournal.DiskNameFor(folder);
            return styles.TryGetValue(onDisk ?? folder, out FolderStyle s) ? s : null;
        }

        /// <summary>Custom name if set, otherwise the folder name. Used for sorting, so a hidden
        /// name still sorts by its on-disk name.</summary>
        public static string DisplayName(string folder)
        {
            FolderStyle s = Get(folder);
            return s != null && !string.IsNullOrEmpty(s.displayName) ? s.displayName : folder;
        }

        /// <summary>
        /// Re-reads folder.json for every pack subfolder of the songs directory. Unchanged files
        /// (same write time) reuse the cached style and texture, so calling this on every
        /// SongFolderManager.Rebuild() is cheap.
        /// </summary>
        public static void Reload(string mainSongDirectory)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);

            string[] subdirs = null;
            try
            {
                if (Directory.Exists(mainSongDirectory))
                    subdirs = Directory.GetDirectories(mainSongDirectory, "*", SearchOption.TopDirectoryOnly);
            }
            catch (Exception e)
            {
                DebugLog.Log($"[FolderCustomization] Couldn't list song folders: {e.Message}");
            }

            if (subdirs != null)
            {
                foreach (string dir in subdirs)
                {
                    string folder = Path.GetFileName(dir);
                    string jsonPath = Path.Combine(dir, FileName);
                    if (!File.Exists(jsonPath)) continue;

                    seen.Add(folder);
                    try
                    {
                        LoadOne(folder, dir, jsonPath);
                    }
                    catch (Exception e)
                    {
                        DebugLog.Log($"[FolderCustomization] '{folder}': failed to load {FileName}: {e.Message}");
                        Remove(folder);
                    }
                }
            }

            // Drop folders whose folder.json (or whole folder) disappeared.
            var gone = new List<string>();
            foreach (string key in styles.Keys)
                if (!seen.Contains(key)) gone.Add(key);
            foreach (string key in gone) Remove(key);

            DebugLog.Log($"[FolderCustomization] {styles.Count} customized folder(s).");
        }

        // ── Loading ──────────────────────────────────────────────────────────

        private static void LoadOne(string folder, string dir, string jsonPath)
        {
            DateTime jsonTime = File.GetLastWriteTimeUtc(jsonPath);
            styles.TryGetValue(folder, out FolderStyle cached);

            // Fast path: json unchanged, and the image (if any) unchanged and still alive.
            if (cached != null && cached.jsonWriteTime == jsonTime && ImageStillValid(cached))
                return;

            JObject jo = JObject.Parse(File.ReadAllText(jsonPath));
            var style = new FolderStyle { jsonWriteTime = jsonTime };

            // Empty / whitespace / missing "name" → the folder's name on disk. Only "hideName": true hides it.
            string name = GetString(jo, "name");
            if (!string.IsNullOrWhiteSpace(name)) style.displayName = name.Trim();

            JToken hide = GetToken(jo, "hideName");
            if (hide != null && hide.Type == JTokenType.Boolean && (bool)hide) style.hideName = true;

            style.color = GetColor(jo, "color", folder);
            style.textColor = GetColor(jo, "textColor", folder);

            string fit = GetString(jo, "imageFit");
            if (!string.IsNullOrEmpty(fit))
            {
                switch (fit.Trim().ToLowerInvariant())
                {
                    case "crop": style.imageFit = FolderImageFit.Crop; break;
                    case "fit": style.imageFit = FolderImageFit.Fit; break;
                    case "stretch": style.imageFit = FolderImageFit.Stretch; break;
                    default: DebugLog.Log($"[FolderCustomization] '{folder}': unknown imageFit '{fit}', using crop."); break;
                }
            }

            Vector2? size = GetVector2(jo, "imageSize", folder);
            if (size.HasValue)
            {
                if (size.Value.x > 0f && size.Value.y > 0f) style.imageSize = size.Value;
                else DebugLog.Log($"[FolderCustomization] '{folder}': imageSize must be greater than 0, ignored.");
            }

            Vector2? pos = GetVector2(jo, "imagePosition", folder);
            if (pos.HasValue) style.imagePosition = pos.Value;

            float? dim = GetFloat(jo, "imageDim");
            if (dim.HasValue) style.imageDim = Mathf.Clamp01(dim.Value);

            // Image: reuse the cached texture when it's the same, unchanged file.
            string imageRel = GetString(jo, "image");
            if (!string.IsNullOrWhiteSpace(imageRel))
            {
                string imagePath = Path.GetFullPath(Path.Combine(dir, imageRel.Trim()));
                if (!File.Exists(imagePath))
                {
                    DebugLog.Log($"[FolderCustomization] '{folder}': image not found: {imageRel}");
                }
                else
                {
                    DateTime imgTime = File.GetLastWriteTimeUtc(imagePath);
                    if (cached != null && cached.imagePath == imagePath && cached.imageWriteTime == imgTime && Alive(cached.image))
                    {
                        style.image = cached.image;
                        cached.image = null; // ownership moves to the new style
                    }
                    else
                    {
                        style.image = LoadTexture(imagePath, folder);
                    }
                    style.imagePath = imagePath;
                    style.imageWriteTime = imgTime;
                }
            }

            if (cached != null) DestroyImage(cached);
            styles[folder] = style;
        }

        private static bool ImageStillValid(FolderStyle s)
        {
            if (s.imagePath == null) return true; // no image configured (or it was missing)
            if (!File.Exists(s.imagePath)) return false;
            if (File.GetLastWriteTimeUtc(s.imagePath) != s.imageWriteTime) return false;
            return Alive(s.image);
        }

        private static Texture2D LoadTexture(string path, string folder)
        {
            byte[] data = File.ReadAllBytes(path);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(tex, data))
            {
                DebugLog.Log($"[FolderCustomization] '{folder}': couldn't decode image {Path.GetFileName(path)} (PNG or JPG only).");
                UnityEngine.Object.Destroy(tex);
                return null;
            }
            tex.wrapMode = TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Bilinear;
            tex.name = "FolderImage_" + folder;
            tex.hideFlags = HideFlags.DontUnloadUnusedAsset; // survive scene changes / UnloadUnusedAssets
            DebugLog.Log($"[FolderCustomization] '{folder}': loaded image {Path.GetFileName(path)} ({tex.width}x{tex.height}).");
            return tex;
        }

        private static void Remove(string folder)
        {
            if (styles.TryGetValue(folder, out FolderStyle s))
            {
                DestroyImage(s);
                styles.Remove(folder);
            }
        }

        private static void DestroyImage(FolderStyle s)
        {
            if (Alive(s.image)) UnityEngine.Object.Destroy(s.image);
            s.image = null;
        }

        private static bool Alive(UnityEngine.Object o) => o != null; // Unity's overloaded == also catches destroyed objects

        // ── JSON helpers (keys are case-insensitive) ─────────────────────────

        private static JToken GetToken(JObject jo, string key)
            => jo.GetValue(key, StringComparison.OrdinalIgnoreCase);

        private static string GetString(JObject jo, string key)
        {
            JToken t = GetToken(jo, key);
            return t != null && t.Type == JTokenType.String ? (string)t : null;
        }

        private static float? GetFloat(JObject jo, string key)
        {
            JToken t = GetToken(jo, key);
            if (t == null) return null;
            if (t.Type == JTokenType.Float || t.Type == JTokenType.Integer) return (float)t;
            return null;
        }

        /// <summary>Accepts [x, y], {"x":..,"y":..}, or a single number (used for both).</summary>
        private static Vector2? GetVector2(JObject jo, string key, string folder)
        {
            JToken t = GetToken(jo, key);
            if (t == null) return null;
            try
            {
                if (t.Type == JTokenType.Float || t.Type == JTokenType.Integer)
                {
                    float v = (float)t;
                    return new Vector2(v, v);
                }
                if (t.Type == JTokenType.Array && ((JArray)t).Count >= 2)
                    return new Vector2((float)t[0], (float)t[1]);
                if (t.Type == JTokenType.Object)
                {
                    var o = (JObject)t;
                    JToken x = o.GetValue("x", StringComparison.OrdinalIgnoreCase);
                    JToken y = o.GetValue("y", StringComparison.OrdinalIgnoreCase);
                    if (x != null && y != null) return new Vector2((float)x, (float)y);
                }
            }
            catch { /* fall through */ }
            DebugLog.Log($"[FolderCustomization] '{folder}': invalid {key}, expected [x, y] or a number.");
            return null;
        }

        private static Color? GetColor(JObject jo, string key, string folder)
        {
            string s = GetString(jo, key);
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (TryParseHex(s.Trim(), out Color c)) return c;
            DebugLog.Log($"[FolderCustomization] '{folder}': invalid {key} '{s}', expected #RRGGBB or #RRGGBBAA.");
            return null;
        }

        private static bool TryParseHex(string s, out Color color)
        {
            color = Color.white;
            if (s.StartsWith("#")) s = s.Substring(1);
            if (s.Length != 6 && s.Length != 8) return false;
            if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return false;

            if (s.Length == 6) v = (v << 8) | 0xFF;
            color = new Color(
                ((v >> 24) & 0xFF) / 255f,
                ((v >> 16) & 0xFF) / 255f,
                ((v >> 8) & 0xFF) / 255f,
                (v & 0xFF) / 255f);
            return true;
        }
    }
}