using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MelonLoader;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// Custom model packages for online play (ApiContract.md v2.44, 19.15.5-19.15.8): the package
    /// format and its id, building this player's own packages from the game's custom-model files,
    /// uploading them, and downloading other players' into a local cache.
    ///
    /// Everything that touches the network, the disk or a hash runs on worker threads; results come
    /// back through a queue drained by Tick on the main thread, so callbacks and state changes only
    /// ever happen there. Nothing here touches Il2Cpp objects off the main thread.
    ///
    /// Cache: one folder per package under ExScoringModelCache (next to Audica_Data), named by its
    /// packageId, holding the package's files under their own names (so the OBJ's mtllib and the
    /// MTL's texture names resolve as in the game) plus manifest.json, written last: a folder without
    /// it is incomplete. Folders not used for the longest go first once the cache passes
    /// CacheMaxBytes. Downloads are checked against the manifest's sizes and SHA-256s, and the
    /// manifest against 19.15.5 (and its id recomputed) before anything is written.
    ///
    /// From the UnityExplorer console: ExScoringMod.MpAssets.DebugDump();
    /// </summary>
    public static class MpAssets
    {
        private const string ApiBaseUrl = "https://audicaexscoringweb-production.up.railway.app";
        private const string CacheFolderName = "ExScoringModelCache";
        private const string ManifestFileName = "manifest.json";
        private const string PartialSuffix = ".part";
        private const string IdHeader = "audicaex-model-package-v1";

        // 19.15.5 limits.
        public const long MaxObjBytes = 16L * 1024 * 1024;
        public const long MaxMtlBytes = 128L * 1024;
        public const long MaxTextureBytes = 8L * 1024 * 1024;
        public const long MaxPackageBytes = 32L * 1024 * 1024;
        public const int MaxTextures = 16;
        public const int MaxFiles = 18;
        public const int MaxImageSide = 4096;
        private const int MaxNameLength = 64;
        private const int MaxMissingQuery = 32;

        // Mod-only limits on what gets loaded (19.15.12): counted from the OBJ text before loading.
        public const int MaxVertices = 300000;
        public const int MaxFaces = 300000;
        public const int MaxObjects = 512;

        private const long CacheMaxBytes = 512L * 1024 * 1024;
        private const int MaxConcurrentDownloads = 2;
        private const int MaxRateLimitRetries = 3;
        private const int MaxRetryAfterSeconds = 30;
        private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(120);

        private static readonly Regex NamePattern = new Regex(@"^[A-Za-z0-9][A-Za-z0-9_.\-]{0,63}$", RegexOptions.CultureInvariant);
        private static readonly Regex HashPattern = new Regex(@"^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
        private static readonly HashSet<string> ReservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        /// <summary>MTL statements that name a texture by their last token (19.15.5).</summary>
        private static readonly HashSet<string> TextureKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "map_Ka", "map_Kd", "map_Ks", "map_Ke", "map_Ns", "map_d", "map_bump", "bump", "norm", "disp", "decal", "refl"
        };

        public const string RoleObj = "obj", RoleMtl = "mtl", RoleTexture = "texture";

        /// <summary>One manifest entry (19.15.5), field names as in the contract's JSON.</summary>
        public class ManifestFile
        {
            public string role;
            public string name;
            public string sha256;
            public long size;
        }

        private class ManifestBody
        {
            public string packageId;
            public long totalSize;
            public ManifestFile[] files;
        }

        public enum PackageStatus { Downloading, Ready, Failed }

        /// <summary>Another player's package, as far as this client got with it.</summary>
        public class PackageInfo
        {
            public string PackageId;
            public PackageStatus Status;
            /// <summary>Full path of the cached OBJ, once Ready.</summary>
            public string ObjPath;
            public string Error;
            public long TotalSize;
            public float LastUsed;
        }

        /// <summary>This player's own package, built from the game's custom-model files and normalised (19.15.12).</summary>
        public class LocalPackage
        {
            public string PackageId;
            public List<ManifestFile> Files;
            /// <summary>The normalised bytes per file name, kept for the upload.</summary>
            public Dictionary<string, byte[]> Bytes;
            public long TotalSize;
            /// <summary>What normalising changed, for the log ("renamed X", "dropped texture Y").</summary>
            public List<string> Notes;
        }

        /// <summary>How an upload ended.</summary>
        public enum UploadOutcome { Registered, Transient, Permanent }

        private static readonly ConcurrentQueue<Action> mainThread = new ConcurrentQueue<Action>();
        private static readonly Dictionary<string, PackageInfo> packages = new Dictionary<string, PackageInfo>();
        private static readonly Queue<string> downloadQueue = new Queue<string>();
        private static int activeDownloads;
        private static int downloadsDone, downloadsFailed, uploadsDone;
        private static string cacheDir;
        private static bool initialized;
        private static HttpClient http;

        // ── Main thread ──

        /// <summary>Once per frame (from MpPresence.Tick): runs finished work's callbacks and starts queued downloads.</summary>
        public static void Tick()
        {
            EnsureInit();

            int budget = 64;
            while (budget-- > 0 && mainThread.TryDequeue(out Action action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    MelonLogger.Log($"[MpAssets] callback threw: {ex}");
                }
            }

            while (activeDownloads < MaxConcurrentDownloads && downloadQueue.Count > 0)
            {
                string id = downloadQueue.Dequeue();
                if (!packages.TryGetValue(id, out PackageInfo info) || info.Status != PackageStatus.Downloading) continue;
                activeDownloads++;
                Task.Run(() => DownloadJob(id));
            }
        }

        /// <summary>The folder packages are cached in. Null until the first Tick.</summary>
        public static string CacheDirectory => cacheDir;

        /// <summary>
        /// Another player's package (main thread): its state, starting the download (or the check of
        /// an earlier cached copy) on first use. Null for something that isn't a package id.
        /// </summary>
        public static PackageInfo Request(string packageId)
        {
            EnsureInit();
            if (packageId == null || !HashPattern.IsMatch(packageId)) return null;

            if (!packages.TryGetValue(packageId, out PackageInfo info))
            {
                info = new PackageInfo { PackageId = packageId, Status = PackageStatus.Downloading };
                packages[packageId] = info;
                downloadQueue.Enqueue(packageId);
                Log($"package {Short(packageId)}: requested");
            }
            info.LastUsed = Time.unscaledTime;
            return info;
        }

        /// <summary>A package that can't be shown (it failed to load): no further attempts this session.</summary>
        public static void MarkFailed(string packageId, string reason)
        {
            if (packageId == null || !packages.TryGetValue(packageId, out PackageInfo info)) return;
            info.Status = PackageStatus.Failed;
            info.Error = reason;
            Log($"package {Short(packageId)}: failed ({reason})");
        }

        /// <summary>Marks a cached package as used now, so the cache keeps it longest.</summary>
        public static void Touch(string packageId)
        {
            if (cacheDir == null || packageId == null || !HashPattern.IsMatch(packageId)) return;
            string dir = Path.Combine(cacheDir, packageId);
            Task.Run(() =>
            {
                try
                {
                    if (Directory.Exists(dir)) Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow);
                }
                catch
                {
                    // Only the eviction order depends on it.
                }
            });
        }

        /// <summary>
        /// Builds this player's own package from a custom OBJ (main thread call, the work is on a
        /// worker): reads the OBJ, its MTL and the MTL's textures, normalises names and references,
        /// hashes everything and computes the packageId, and writes the normalised copy to the cache.
        /// onDone (main thread) gets the package, or null and the reason.
        /// </summary>
        public static void BuildLocalPackage(string objPath, Action<LocalPackage, string> onDone)
        {
            EnsureInit();
            string cache = cacheDir;
            Task.Run(() =>
            {
                LocalPackage package = null;
                string error = null;
                try
                {
                    package = BuildPackage(objPath, out error);
                    if (package != null) WriteToCache(cache, package);
                }
                catch (Exception ex)
                {
                    package = null;
                    error = ex.Message;
                }
                LocalPackage result = package;
                string reason = error;
                mainThread.Enqueue(() =>
                {
                    if (result != null) RegisterLocalCopy(result);
                    onDone?.Invoke(result, reason);
                });
            });
        }

        /// <summary>
        /// Uploads a package (19.15.7): missing → PUT each missing blob → register. Main thread call;
        /// onDone (main thread) gets the outcome and a reason. Transient: try again later (network,
        /// a stale token, rate limits). Permanent: the server refused this package.
        /// </summary>
        public static void Upload(LocalPackage package, string assetToken, Action<UploadOutcome, string> onDone)
        {
            EnsureInit();
            Task.Run(async () =>
            {
                UploadOutcome outcome;
                string reason;
                try
                {
                    var result = await UploadAsync(package, assetToken).ConfigureAwait(false);
                    outcome = result.Key;
                    reason = result.Value;
                }
                catch (Exception ex)
                {
                    outcome = UploadOutcome.Transient;
                    reason = ex.GetBaseException().Message;
                }
                UploadOutcome o = outcome;
                string r = reason;
                mainThread.Enqueue(() =>
                {
                    if (o == UploadOutcome.Registered) uploadsDone++;
                    onDone?.Invoke(o, r);
                });
            });
        }

        public static void DebugDump()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"[MpAssets] cache={cacheDir ?? "none"} downloads: active={activeDownloads} queued={downloadQueue.Count} done={downloadsDone} failed={downloadsFailed} uploads={uploadsDone}");
            foreach (PackageInfo info in packages.Values)
            {
                sb.AppendLine($"[MpAssets]   {Short(info.PackageId)} {info.Status} size={info.TotalSize} obj={(info.ObjPath != null ? Path.GetFileName(info.ObjPath) : "-")}{(info.Error != null ? " error=" + info.Error : "")}");
            }
            foreach (string line in sb.ToString().TrimEnd().Split('\n')) MelonLogger.Log(line.TrimEnd('\r'));
        }

        private static void EnsureInit()
        {
            if (initialized) return;
            initialized = true;
            try
            {
                cacheDir = Application.dataPath.Replace("Audica_Data", CacheFolderName);
                Directory.CreateDirectory(cacheDir);
            }
            catch (Exception ex)
            {
                MelonLogger.Log($"[MpAssets] cache folder unavailable: {ex.Message}");
                cacheDir = null;
            }

            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                http = new HttpClient { Timeout = HttpTimeout };
            }
            catch (Exception ex)
            {
                MelonLogger.Log($"[MpAssets] HTTP client unavailable: {ex.Message}");
                http = null;
            }
            try
            {
                if (http != null) http.DefaultRequestHeaders.UserAgent.ParseAdd("AudicaExScoring/" + ExScoring.BuildInfo.Version);
            }
            catch
            {
                // Only cosmetic.
            }

            string dir = cacheDir;
            if (dir != null) Task.Run(() => PruneCache(dir, new HashSet<string>()));
        }

        /// <summary>A package this player built is also in the cache: anyone wearing the same one shows at once.</summary>
        private static void RegisterLocalCopy(LocalPackage package)
        {
            if (cacheDir == null) return;
            if (packages.TryGetValue(package.PackageId, out PackageInfo existing) && existing.Status == PackageStatus.Ready) return;

            ManifestFile obj = package.Files.Find(f => f.role == RoleObj);
            packages[package.PackageId] = new PackageInfo
            {
                PackageId = package.PackageId,
                Status = PackageStatus.Ready,
                ObjPath = Path.Combine(Path.Combine(cacheDir, package.PackageId), obj.name),
                TotalSize = package.TotalSize,
                LastUsed = Time.unscaledTime
            };
        }

        // ── Package format (any thread) ──

        /// <summary>19.15.5: SHA-256 of the canonical manifest text, lowercase hex.</summary>
        public static string PackageIdOf(IEnumerable<ManifestFile> files)
        {
            List<ManifestFile> sorted = new List<ManifestFile>(files);
            sorted.Sort(CompareCanonical);

            StringBuilder sb = new StringBuilder();
            sb.Append(IdHeader).Append('\n');
            foreach (ManifestFile f in sorted)
            {
                sb.Append(f.role).Append('\t').Append(f.name).Append('\t').Append(f.sha256).Append('\t')
                  .Append(f.size.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
            return Sha256Hex(new UTF8Encoding(false).GetBytes(sb.ToString()));
        }

        private static int RoleOrder(string role)
        {
            return role == RoleObj ? 0 : role == RoleMtl ? 1 : 2;
        }

        private static int CompareCanonical(ManifestFile a, ManifestFile b)
        {
            int byRole = RoleOrder(a.role).CompareTo(RoleOrder(b.role));
            return byRole != 0 ? byRole : string.CompareOrdinal(a.name, b.name);
        }

        public static string Sha256Hex(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                StringBuilder sb = new StringBuilder(64);
                foreach (byte b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        /// <summary>
        /// Checks a manifest against 19.15.5 (roles, counts, names, sizes, hashes). Null when it's
        /// fine, otherwise the reason. The server has checked the content already; this keeps a bad
        /// or hostile server from writing anything odd to this player's disk.
        /// </summary>
        private static string ValidateManifest(ManifestFile[] files)
        {
            if (files == null || files.Length == 0) return "no files";
            if (files.Length > MaxFiles) return "too many files";
            int objs = 0, mtls = 0, textures = 0;
            long total = 0;
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ManifestFile f in files)
            {
                if (f == null || f.name == null || f.sha256 == null) return "malformed entry";
                if (!HashPattern.IsMatch(f.sha256)) return "bad hash for " + f.name;
                if (!IsValidName(f.name)) return "bad name " + f.name;
                if (!names.Add(f.name)) return "duplicate name " + f.name;
                string ext = Path.GetExtension(f.name).ToLowerInvariant();
                long limit;
                switch (f.role)
                {
                    case RoleObj:
                        objs++;
                        limit = MaxObjBytes;
                        if (ext != ".obj") return "bad extension " + f.name;
                        break;
                    case RoleMtl:
                        mtls++;
                        limit = MaxMtlBytes;
                        if (ext != ".mtl") return "bad extension " + f.name;
                        break;
                    case RoleTexture:
                        textures++;
                        limit = MaxTextureBytes;
                        if (ext != ".png" && ext != ".jpg" && ext != ".jpeg") return "bad extension " + f.name;
                        break;
                    default:
                        return "bad role " + f.role;
                }
                if (f.size <= 0 || f.size > limit) return "bad size for " + f.name;
                total += f.size;
            }
            if (objs != 1) return "needs exactly one OBJ";
            if (mtls > 1 || textures > MaxTextures) return "too many files";
            if (total > MaxPackageBytes) return "package too large";
            return null;
        }

        private static bool IsValidName(string name)
        {
            if (name == null || !NamePattern.IsMatch(name) || name.EndsWith(".", StringComparison.Ordinal)) return false;
            int dot = name.IndexOf('.');
            string stem = dot >= 0 ? name.Substring(0, dot) : name;
            return !ReservedNames.Contains(stem);
        }

        /// <summary>A name that fits 19.15.5, from an original file name, unique (ignoring case) among used.</summary>
        private static string MakeName(string original, string extension, HashSet<string> used)
        {
            string stem = Path.GetFileNameWithoutExtension(original ?? "") ?? "";
            StringBuilder clean = new StringBuilder();
            foreach (char c in stem)
            {
                bool ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-' || c == '.';
                clean.Append(ok ? c : '_');
            }
            stem = clean.ToString().TrimEnd('.');
            if (stem.Length == 0) stem = "model";
            if (!char.IsLetterOrDigit(stem[0]) || stem[0] > 127) stem = "m" + stem;
            int dot = stem.IndexOf('.');
            if (ReservedNames.Contains(dot >= 0 ? stem.Substring(0, dot) : stem)) stem = "m_" + stem;

            int maxStem = MaxNameLength - extension.Length;
            if (stem.Length > maxStem) stem = stem.Substring(0, maxStem).TrimEnd('.');

            string name = stem + extension;
            for (int n = 2; used.Contains(name); n++)
            {
                string suffix = "_" + n.ToString(CultureInfo.InvariantCulture);
                string s = stem.Length + suffix.Length > maxStem ? stem.Substring(0, maxStem - suffix.Length) : stem;
                name = s + suffix + extension;
            }
            used.Add(name);
            return name;
        }

        // ── Building a local package (worker thread) ──

        private static LocalPackage BuildPackage(string objPath, out string error)
        {
            error = null;
            FileInfo objInfo = new FileInfo(objPath);
            if (!objInfo.Exists)
            {
                error = "model file not found";
                return null;
            }
            if (objInfo.Length > MaxObjBytes)
            {
                error = $"model file is {objInfo.Length / (1024 * 1024)} MB, the limit is {MaxObjBytes / (1024 * 1024)} MB";
                return null;
            }

            string folder = objInfo.DirectoryName;
            List<string> notes = new List<string>();
            HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, byte[]> bytes = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            List<ManifestFile> files = new List<ManifestFile>();

            string objName = MakeName(objInfo.Name, ".obj", used);
            if (objName != objInfo.Name) notes.Add($"renamed {objInfo.Name} -> {objName}");

            string objText = ReadText(objPath);
            ObjStats stats = CountObj(objText);
            string statsError = stats.Check();
            if (statsError != null)
            {
                error = statsError;
                return null;
            }

            // The first mtllib that names an existing file in the folder becomes the package's MTL;
            // every mtllib line is replaced by one pointing at it (or dropped when there is none).
            string mtlSource = null;
            List<string> lines = SplitLines(objText);
            int firstMtllib = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                if (!StatementIs(lines[i], "mtllib", out string rest)) continue;
                if (firstMtllib < 0) firstMtllib = i;
                if (mtlSource == null) mtlSource = ResolveReference(folder, rest, ".mtl", false);
                lines[i] = null;
            }

            string mtlName = null;
            string mtlText = null;
            if (mtlSource != null)
            {
                FileInfo mtlInfo = new FileInfo(mtlSource);
                if (mtlInfo.Length > MaxMtlBytes)
                {
                    notes.Add($"dropped {mtlInfo.Name} (over {MaxMtlBytes / 1024} KB)");
                    mtlSource = null;
                }
                else
                {
                    mtlName = MakeName(mtlInfo.Name, ".mtl", used);
                    if (mtlName != mtlInfo.Name) notes.Add($"renamed {mtlInfo.Name} -> {mtlName}");
                    mtlText = RewriteMtl(ReadText(mtlSource), folder, used, bytes, files, notes);
                }
            }
            else if (firstMtllib >= 0)
            {
                notes.Add("dropped the mtllib line (its file isn't next to the model)");
            }

            if (mtlName != null) lines[firstMtllib] = "mtllib " + mtlName;
            byte[] objBytes = new UTF8Encoding(false).GetBytes(JoinLines(lines));
            if (objBytes.LongLength > MaxObjBytes)
            {
                error = "model file is over the size limit";
                return null;
            }
            AddFile(files, bytes, RoleObj, objName, objBytes);

            if (mtlName != null)
            {
                byte[] mtlBytes = new UTF8Encoding(false).GetBytes(mtlText);
                if (mtlBytes.LongLength > MaxMtlBytes)
                {
                    error = "material file is over the size limit";
                    return null;
                }
                AddFile(files, bytes, RoleMtl, mtlName, mtlBytes);
            }

            long total = 0;
            foreach (ManifestFile f in files) total += f.size;
            if (total > MaxPackageBytes)
            {
                error = $"package is {total / (1024 * 1024)} MB, the limit is {MaxPackageBytes / (1024 * 1024)} MB";
                return null;
            }

            files.Sort(CompareCanonical);
            return new LocalPackage
            {
                PackageId = PackageIdOf(files),
                Files = files,
                Bytes = bytes,
                TotalSize = total,
                Notes = notes
            };
        }

        /// <summary>
        /// The MTL with every texture statement pointing at a package file: each referenced PNG/JPEG
        /// next to the model (within the limits) is added under a valid name; statements it can't
        /// satisfy are dropped.
        /// </summary>
        private static string RewriteMtl(string text, string folder, HashSet<string> used, Dictionary<string, byte[]> bytes, List<ManifestFile> files, List<string> notes)
        {
            Dictionary<string, string> renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int textures = 0;
            List<string> lines = SplitLines(text);
            for (int i = 0; i < lines.Count; i++)
            {
                string line = lines[i];
                string trimmed = line.TrimStart();
                if (trimmed.Length == 0 || trimmed[0] == '#') continue;
                int space = IndexOfWhitespace(trimmed);
                string keyword = space < 0 ? trimmed : trimmed.Substring(0, space);
                if (!TextureKeywords.Contains(keyword)) continue;

                string rest = space < 0 ? "" : trimmed.Substring(space).Trim();
                string[] tokens = rest.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                string options = tokens.Length > 1 ? string.Join(" ", tokens, 0, tokens.Length - 1) : "";

                // The last token is the file (19.15.5); a name with spaces only works without options.
                string source = tokens.Length > 0 ? ResolveReference(folder, tokens[tokens.Length - 1], null, true) : null;
                if (source == null && tokens.Length > 1 && !rest.StartsWith("-", StringComparison.Ordinal))
                {
                    source = ResolveReference(folder, rest, null, true);
                    if (source != null) options = "";
                }

                string name = null;
                if (source != null && !renamed.TryGetValue(source, out name))
                {
                    name = AddTexture(source, used, bytes, files, notes, textures);
                    if (name != null)
                    {
                        renamed[source] = name;
                        textures++;
                    }
                }

                if (name == null)
                {
                    notes.Add($"dropped \"{keyword} {rest}\"");
                    lines[i] = null;
                    continue;
                }
                lines[i] = keyword + " " + (options.Length > 0 ? options + " " : "") + name;
            }
            return JoinLines(lines);
        }

        /// <summary>Adds one texture file if it is a PNG/JPEG within the limits. Its new name, or null.</summary>
        private static string AddTexture(string source, HashSet<string> used, Dictionary<string, byte[]> bytes, List<ManifestFile> files, List<string> notes, int count)
        {
            string fileName = Path.GetFileName(source);
            if (count >= MaxTextures)
            {
                notes.Add($"dropped {fileName} (more than {MaxTextures} textures)");
                return null;
            }

            FileInfo info = new FileInfo(source);
            if (info.Length <= 0 || info.Length > MaxTextureBytes)
            {
                notes.Add($"dropped {fileName} (empty or over {MaxTextureBytes / (1024 * 1024)} MB)");
                return null;
            }

            byte[] data = File.ReadAllBytes(source);
            string ext = Path.GetExtension(source).ToLowerInvariant();
            if (!ReadImageSize(data, ext, out int width, out int height))
            {
                notes.Add($"dropped {fileName} (not a {ext.TrimStart('.').ToUpperInvariant()} image)");
                return null;
            }
            if (width < 1 || height < 1 || width > MaxImageSide || height > MaxImageSide)
            {
                notes.Add($"dropped {fileName} ({width}x{height}, the limit is {MaxImageSide})");
                return null;
            }

            string name = MakeName(fileName, ext == ".jpeg" ? ".jpeg" : ext, used);
            if (name != fileName) notes.Add($"renamed {fileName} -> {name}");
            AddFile(files, bytes, RoleTexture, name, data);
            return name;
        }

        private static void AddFile(List<ManifestFile> files, Dictionary<string, byte[]> bytes, string role, string name, byte[] data)
        {
            files.Add(new ManifestFile { role = role, name = name, sha256 = Sha256Hex(data), size = data.LongLength });
            bytes[name] = data;
        }

        /// <summary>
        /// A referenced file next to the model: the reference flattened to its file name (forward or
        /// back slashes), never absolute or with "..". Null when there's no such file, or it has the
        /// wrong extension (requiredExtension for the MTL, PNG/JPEG for textures).
        /// </summary>
        private static string ResolveReference(string folder, string reference, string requiredExtension, bool image)
        {
            if (string.IsNullOrWhiteSpace(reference)) return null;
            string flat = reference.Trim().Trim('"').Replace('\\', '/');
            if (flat.Length == 0 || flat.Contains("..") || flat.StartsWith("/", StringComparison.Ordinal) || flat.Contains(":")) return null;
            int slash = flat.LastIndexOf('/');
            string name = slash >= 0 ? flat.Substring(slash + 1) : flat;
            if (name.Length == 0) return null;

            string ext = Path.GetExtension(name).ToLowerInvariant();
            if (requiredExtension != null && ext != requiredExtension) return null;
            if (image && ext != ".png" && ext != ".jpg" && ext != ".jpeg") return null;

            string path = Path.Combine(folder, name);
            return File.Exists(path) ? path : null;
        }

        /// <summary>Width and height from a PNG or JPEG header, when the magic bytes match the extension.</summary>
        private static bool ReadImageSize(byte[] d, string ext, out int width, out int height)
        {
            width = height = 0;
            if (ext == ".png")
            {
                byte[] sig = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
                if (d.Length < 24) return false;
                for (int i = 0; i < 8; i++) if (d[i] != sig[i]) return false;
                width = (d[16] << 24) | (d[17] << 16) | (d[18] << 8) | d[19];
                height = (d[20] << 24) | (d[21] << 16) | (d[22] << 8) | d[23];
                return true;
            }
            if (ext == ".jpg" || ext == ".jpeg")
            {
                if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) return false;
                int p = 2;
                while (p + 9 < d.Length)
                {
                    if (d[p] != 0xFF) { p++; continue; }
                    byte marker = d[p + 1];
                    if (marker == 0xFF) { p++; continue; }
                    if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { p += 2; continue; }
                    int length = (d[p + 2] << 8) | d[p + 3];
                    bool sof = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                    if (sof)
                    {
                        height = (d[p + 5] << 8) | d[p + 6];
                        width = (d[p + 7] << 8) | d[p + 8];
                        return true;
                    }
                    if (length < 2) return false;
                    p += 2 + length;
                }
                return false;
            }
            return false;
        }

        private struct ObjStats
        {
            public int Vertices, Faces, Objects;

            public string Check()
            {
                if (Vertices > MaxVertices) return $"{Vertices} vertices, the limit is {MaxVertices}";
                if (Faces > MaxFaces) return $"{Faces} faces, the limit is {MaxFaces}";
                if (Objects > MaxObjects) return $"{Objects} objects, the limit is {MaxObjects}";
                if (Vertices == 0 || Faces == 0) return "no geometry";
                return null;
            }
        }

        private static ObjStats CountObj(string text)
        {
            ObjStats stats = new ObjStats();
            int i = 0, n = text.Length;
            while (i < n)
            {
                while (i < n && (text[i] == ' ' || text[i] == '\t')) i++;
                if (i + 1 < n && (text[i + 1] == ' ' || text[i + 1] == '\t'))
                {
                    char c = text[i];
                    if (c == 'v') stats.Vertices++;
                    else if (c == 'f') stats.Faces++;
                    else if (c == 'o' || c == 'g') stats.Objects++;
                }
                int nl = text.IndexOf('\n', i);
                if (nl < 0) break;
                i = nl + 1;
            }
            return stats;
        }

        /// <summary>The file as UTF-8 text (invalid bytes replaced, NULs removed, BOM dropped), which the server requires.</summary>
        private static string ReadText(string path)
        {
            string text = new UTF8Encoding(false, false).GetString(File.ReadAllBytes(path));
            if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);
            return text.IndexOf('\0') >= 0 ? text.Replace("\0", "") : text;
        }

        /// <summary>Lines with their own line ending kept, so rewriting one leaves the rest byte-for-byte.</summary>
        private static List<string> SplitLines(string text)
        {
            List<string> lines = new List<string>();
            int start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] != '\n') continue;
                lines.Add(text.Substring(start, i + 1 - start));
                start = i + 1;
            }
            if (start < text.Length) lines.Add(text.Substring(start));
            return lines;
        }

        /// <summary>Joins SplitLines' output back; null entries are dropped and replaced lines get a newline.</summary>
        private static string JoinLines(List<string> lines)
        {
            StringBuilder sb = new StringBuilder();
            foreach (string line in lines)
            {
                if (line == null) continue;
                sb.Append(line);
                if (!line.EndsWith("\n", StringComparison.Ordinal)) sb.Append('\n');
            }
            return sb.ToString();
        }

        private static bool StatementIs(string line, string keyword, out string rest)
        {
            rest = null;
            if (line == null) return false;
            string trimmed = line.TrimStart();
            if (!trimmed.StartsWith(keyword, StringComparison.OrdinalIgnoreCase)) return false;
            if (trimmed.Length > keyword.Length && !char.IsWhiteSpace(trimmed[keyword.Length])) return false;
            rest = trimmed.Substring(keyword.Length).Trim();
            return true;
        }

        private static int IndexOfWhitespace(string s)
        {
            for (int i = 0; i < s.Length; i++) if (s[i] == ' ' || s[i] == '\t') return i;
            return -1;
        }

        /// <summary>Writes a built package to the cache (if it isn't there already), manifest last.</summary>
        private static void WriteToCache(string cache, LocalPackage package)
        {
            if (cache == null) return;
            string dir = Path.Combine(cache, package.PackageId);
            if (File.Exists(Path.Combine(dir, ManifestFileName))) return;

            string partial = dir + PartialSuffix;
            if (Directory.Exists(partial)) Directory.Delete(partial, true);
            Directory.CreateDirectory(partial);
            foreach (ManifestFile f in package.Files) File.WriteAllBytes(Path.Combine(partial, f.name), package.Bytes[f.name]);
            WriteManifest(partial, package.PackageId, package.TotalSize, package.Files.ToArray());
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.Move(partial, dir);
        }

        private static void WriteManifest(string dir, string packageId, long totalSize, ManifestFile[] files)
        {
            string json = JsonConvert.SerializeObject(new ManifestBody { packageId = packageId, totalSize = totalSize, files = files });
            File.WriteAllText(Path.Combine(dir, ManifestFileName), json, new UTF8Encoding(false));
        }

        // ── Downloading (worker thread) ──

        private static async Task DownloadJob(string packageId)
        {
            string objPath = null;
            string error = null;
            long totalSize = 0;
            try
            {
                var result = await DownloadAsync(packageId).ConfigureAwait(false);
                objPath = result.ObjPath;
                totalSize = result.TotalSize;
                error = result.Error;
            }
            catch (Exception ex)
            {
                error = ex.GetBaseException().Message;
            }

            string obj = objPath;
            string reason = error;
            long size = totalSize;
            mainThread.Enqueue(() =>
            {
                activeDownloads--;
                if (!packages.TryGetValue(packageId, out PackageInfo info)) return;
                if (obj != null)
                {
                    info.Status = PackageStatus.Ready;
                    info.ObjPath = obj;
                    info.TotalSize = size;
                    downloadsDone++;
                    Log($"package {Short(packageId)}: ready ({size / 1024} KB)");
                    PruneLater(packageId);
                }
                else
                {
                    info.Status = PackageStatus.Failed;
                    info.Error = reason;
                    downloadsFailed++;
                    Log($"package {Short(packageId)}: download failed ({reason})");
                }
            });
        }

        private struct DownloadResult
        {
            public string ObjPath;
            public long TotalSize;
            public string Error;
        }

        private static async Task<DownloadResult> DownloadAsync(string packageId)
        {
            if (cacheDir == null) return new DownloadResult { Error = "no cache folder" };
            if (http == null) return new DownloadResult { Error = "no HTTP client" };

            string dir = Path.Combine(cacheDir, packageId);
            DownloadResult cached = CheckCached(dir, packageId);
            if (cached.ObjPath != null) return cached;

            // The manifest: checked against 19.15.5, and its id recomputed.
            ManifestBody manifest = null;
            for (int attempt = 0; ; attempt++)
            {
                using (HttpResponseMessage response = await http.GetAsync($"{ApiBaseUrl}/api/mp/assets/packages/{packageId}").ConfigureAwait(false))
                {
                    if ((int)response.StatusCode == 429 && attempt < MaxRateLimitRetries)
                    {
                        await Task.Delay(RetryAfterMs(response)).ConfigureAwait(false);
                        continue;
                    }
                    string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode) return new DownloadResult { Error = $"manifest {(int)response.StatusCode} {ErrorCode(body)}" };
                    manifest = JsonConvert.DeserializeObject<ManifestBody>(body, MpJson.Settings);
                    break;
                }
            }

            string invalid = manifest == null ? "no manifest" : ValidateManifest(manifest.files);
            if (invalid != null) return new DownloadResult { Error = "manifest: " + invalid };
            if (PackageIdOf(manifest.files) != packageId) return new DownloadResult { Error = "manifest doesn't match the package id" };

            string partial = dir + PartialSuffix;
            if (!Directory.Exists(partial)) Directory.CreateDirectory(partial);

            long total = 0;
            string objName = null;
            foreach (ManifestFile f in manifest.files)
            {
                total += f.size;
                if (f.role == RoleObj) objName = f.name;
                string target = Path.Combine(partial, f.name);
                if (File.Exists(target) && new FileInfo(target).Length == f.size && Sha256Hex(File.ReadAllBytes(target)) == f.sha256) continue;

                byte[] data = null;
                for (int attempt = 0; ; attempt++)
                {
                    using (HttpResponseMessage response = await http.GetAsync($"{ApiBaseUrl}/api/mp/assets/blobs/{f.sha256}", HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                    {
                        if ((int)response.StatusCode == 429 && attempt < MaxRateLimitRetries)
                        {
                            await Task.Delay(RetryAfterMs(response)).ConfigureAwait(false);
                            continue;
                        }
                        if (!response.IsSuccessStatusCode)
                        {
                            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                            return new DownloadResult { Error = $"{f.name}: {(int)response.StatusCode} {ErrorCode(body)}" };
                        }
                        long? length = response.Content.Headers.ContentLength;
                        if (length.HasValue && length.Value != f.size) return new DownloadResult { Error = $"{f.name}: wrong size" };
                        data = await ReadLimited(response, f.size).ConfigureAwait(false);
                        break;
                    }
                }
                if (data == null || data.LongLength != f.size) return new DownloadResult { Error = $"{f.name}: wrong size" };
                if (Sha256Hex(data) != f.sha256) return new DownloadResult { Error = $"{f.name}: hash mismatch" };
                File.WriteAllBytes(target, data);
            }

            ObjStats stats = CountObj(ReadText(Path.Combine(partial, objName)));
            string statsError = stats.Check();
            if (statsError != null) return new DownloadResult { Error = statsError };

            WriteManifest(partial, packageId, total, manifest.files);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.Move(partial, dir);
            return new DownloadResult { ObjPath = Path.Combine(dir, objName), TotalSize = total };
        }

        /// <summary>An earlier complete copy in the cache, checked against its manifest (sizes and hashes).</summary>
        private static DownloadResult CheckCached(string dir, string packageId)
        {
            try
            {
                string manifestPath = Path.Combine(dir, ManifestFileName);
                if (!File.Exists(manifestPath)) return new DownloadResult();
                ManifestBody manifest = JsonConvert.DeserializeObject<ManifestBody>(File.ReadAllText(manifestPath), MpJson.Settings);
                if (manifest == null || ValidateManifest(manifest.files) != null || PackageIdOf(manifest.files) != packageId) return new DownloadResult();

                long total = 0;
                string objName = null;
                foreach (ManifestFile f in manifest.files)
                {
                    string path = Path.Combine(dir, f.name);
                    if (!File.Exists(path) || new FileInfo(path).Length != f.size) return new DownloadResult();
                    if (Sha256Hex(File.ReadAllBytes(path)) != f.sha256) return new DownloadResult();
                    total += f.size;
                    if (f.role == RoleObj) objName = f.name;
                }
                if (CountObj(ReadText(Path.Combine(dir, objName))).Check() != null) return new DownloadResult();
                Directory.SetLastWriteTimeUtc(dir, DateTime.UtcNow);
                return new DownloadResult { ObjPath = Path.Combine(dir, objName), TotalSize = total };
            }
            catch
            {
                return new DownloadResult();
            }
        }

        /// <summary>The body, refusing to read past the expected size (a server sending more than it announced).</summary>
        private static async Task<byte[]> ReadLimited(HttpResponseMessage response, long expected)
        {
            using (Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            using (MemoryStream buffer = new MemoryStream((int)Math.Min(expected, int.MaxValue)))
            {
                byte[] chunk = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(chunk, 0, chunk.Length).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + read > expected) return null;
                    buffer.Write(chunk, 0, read);
                }
                return buffer.ToArray();
            }
        }

        // ── Uploading (worker thread) ──

        private static async Task<KeyValuePair<UploadOutcome, string>> UploadAsync(LocalPackage package, string token)
        {
            if (http == null) return Result(UploadOutcome.Transient, "no HTTP client");
            if (string.IsNullOrEmpty(token)) return Result(UploadOutcome.Transient, "no asset token");

            List<string> hashes = new List<string>();
            foreach (ManifestFile f in package.Files)
            {
                if (!hashes.Contains(f.sha256)) hashes.Add(f.sha256);
            }

            // 1. Which blobs the server lacks.
            List<string> missing = new List<string>();
            for (int start = 0; start < hashes.Count; start += MaxMissingQuery)
            {
                List<string> batch = hashes.GetRange(start, Math.Min(MaxMissingQuery, hashes.Count - start));
                var reply = await SendJson(HttpMethod.Post, "/api/mp/assets/missing", new JObject { ["hashes"] = new JArray(batch) }, null).ConfigureAwait(false);
                if (reply.Status != 200) return Result(Classify(reply.Status), $"missing: {reply.Status} {reply.Code}");
                JArray list = reply.Body?["missing"] as JArray;
                if (list != null) foreach (JToken t in list) missing.Add((string)t);
            }

            // 2. Each missing blob.
            foreach (string hash in missing)
            {
                ManifestFile file = package.Files.Find(f => f.sha256 == hash);
                if (file == null) continue;
                var put = await PutBlob(hash, package.Bytes[file.name], token).ConfigureAwait(false);
                if (put.Status != 200 && put.Status != 201) return Result(Classify(put.Status), $"{file.name}: {put.Status} {put.Code}");
            }

            // 3. Register (once more after uploading anything the server says is still missing).
            JArray files = new JArray();
            foreach (ManifestFile f in package.Files)
            {
                files.Add(new JObject { ["role"] = f.role, ["name"] = f.name, ["sha256"] = f.sha256, ["size"] = f.size });
            }
            JObject body = new JObject { ["manifest"] = new JObject { ["files"] = files } };

            for (int attempt = 0; attempt < 2; attempt++)
            {
                var reply = await SendJson(HttpMethod.Post, "/api/mp/assets/packages", body, token).ConfigureAwait(false);
                if (reply.Status == 200 || reply.Status == 201)
                {
                    string id = (string)reply.Body?["packageId"];
                    if (id != package.PackageId) return Result(UploadOutcome.Permanent, $"the server computed a different package id ({Short(id)})");
                    return Result(UploadOutcome.Registered, reply.Status == 201 ? "registered" : "already registered");
                }
                if (reply.Status == 409 && attempt == 0 && reply.Body?["missing"] is JArray stillMissing)
                {
                    foreach (JToken t in stillMissing)
                    {
                        ManifestFile file = package.Files.Find(f => f.sha256 == (string)t);
                        if (file == null) continue;
                        var put = await PutBlob(file.sha256, package.Bytes[file.name], token).ConfigureAwait(false);
                        if (put.Status != 200 && put.Status != 201) return Result(Classify(put.Status), $"{file.name}: {put.Status} {put.Code}");
                    }
                    continue;
                }
                string detail = reply.Code;
                if (reply.Code == "BAD_PACKAGE") detail += $" {(string)reply.Body?["reason"]} {(string)reply.Body?["file"]}".TrimEnd();
                return Result(Classify(reply.Status), $"register: {reply.Status} {detail}");
            }
            return Result(UploadOutcome.Transient, "register: blobs kept going missing");
        }

        private static KeyValuePair<UploadOutcome, string> Result(UploadOutcome outcome, string reason)
        {
            return new KeyValuePair<UploadOutcome, string>(outcome, reason);
        }

        /// <summary>401 (stale token), 429 and network trouble are worth retrying; the rest isn't for this package.</summary>
        private static UploadOutcome Classify(int status)
        {
            if (status == 0 || status == 401 || status == 429 || status == 408 || status >= 500 && status != 503 && status != 507) return UploadOutcome.Transient;
            return UploadOutcome.Permanent;
        }

        private struct Reply
        {
            public int Status;
            public string Code;
            public JObject Body;
        }

        private static async Task<Reply> SendJson(HttpMethod method, string path, JObject body, string token)
        {
            for (int attempt = 0; ; attempt++)
            {
                using (HttpRequestMessage request = new HttpRequestMessage(method, ApiBaseUrl + path))
                {
                    request.Content = new StringContent(body.ToString(Formatting.None), new UTF8Encoding(false), "application/json");
                    if (token != null) request.Headers.TryAddWithoutValidation("Authorization", "MpAsset " + token);
                    SentReply sent = await Send(request).ConfigureAwait(false);
                    if (sent.Status == 429 && attempt < MaxRateLimitRetries && sent.RetryAfterMs > 0)
                    {
                        await Task.Delay(sent.RetryAfterMs).ConfigureAwait(false);
                        continue;
                    }
                    return sent.Reply;
                }
            }
        }

        private static async Task<Reply> PutBlob(string hash, byte[] data, string token)
        {
            for (int attempt = 0; ; attempt++)
            {
                using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Put, $"{ApiBaseUrl}/api/mp/assets/blobs/{hash}"))
                {
                    ByteArrayContent content = new ByteArrayContent(data);
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                    request.Content = content;
                    request.Headers.TryAddWithoutValidation("Authorization", "MpAsset " + token);
                    SentReply sent = await Send(request).ConfigureAwait(false);
                    if (sent.Status == 429 && attempt < MaxRateLimitRetries && sent.RetryAfterMs > 0)
                    {
                        await Task.Delay(sent.RetryAfterMs).ConfigureAwait(false);
                        continue;
                    }
                    return sent.Reply;
                }
            }
        }

        private struct SentReply
        {
            public Reply Reply;
            public int Status => Reply.Status;
            public int RetryAfterMs;
        }

        private static async Task<SentReply> Send(HttpRequestMessage request)
        {
            try
            {
                using (HttpResponseMessage response = await http.SendAsync(request).ConfigureAwait(false))
                {
                    string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    JObject json = null;
                    try
                    {
                        if (!string.IsNullOrEmpty(text) && text.TrimStart().StartsWith("{", StringComparison.Ordinal)) json = JObject.Parse(text);
                    }
                    catch
                    {
                        json = null;
                    }
                    return new SentReply
                    {
                        Reply = new Reply { Status = (int)response.StatusCode, Code = (string)json?["error"] ?? "", Body = json },
                        RetryAfterMs = (int)response.StatusCode == 429 ? RetryAfterMs(response) : 0
                    };
                }
            }
            catch (Exception ex)
            {
                return new SentReply { Reply = new Reply { Status = 0, Code = ex.GetBaseException().Message } };
            }
        }

        private static int RetryAfterMs(HttpResponseMessage response)
        {
            RetryConditionHeaderValue retry = response.Headers.RetryAfter;
            double seconds = 5;
            if (retry != null && retry.Delta.HasValue) seconds = retry.Delta.Value.TotalSeconds;
            else if (retry != null && retry.Date.HasValue) seconds = (retry.Date.Value - DateTimeOffset.UtcNow).TotalSeconds;
            seconds = Math.Max(1, Math.Min(MaxRetryAfterSeconds, seconds));
            return (int)(seconds * 1000);
        }

        private static string ErrorCode(string body)
        {
            try
            {
                return (string)JObject.Parse(body)["error"] ?? "";
            }
            catch
            {
                return "";
            }
        }

        // ── Cache size ──

        /// <summary>After a download: prune the cache, keeping every package this session knows as Ready.</summary>
        private static void PruneLater(string justAdded)
        {
            if (cacheDir == null) return;
            HashSet<string> keep = new HashSet<string>(StringComparer.Ordinal) { justAdded };
            foreach (PackageInfo info in packages.Values)
            {
                if (info.Status == PackageStatus.Ready) keep.Add(info.PackageId);
            }
            string dir = cacheDir;
            Task.Run(() => PruneCache(dir, keep));
        }

        /// <summary>Removes stale partial downloads, then the least recently used packages past CacheMaxBytes.</summary>
        private static void PruneCache(string root, HashSet<string> keep)
        {
            try
            {
                List<KeyValuePair<DirectoryInfo, long>> entries = new List<KeyValuePair<DirectoryInfo, long>>();
                long total = 0;
                foreach (DirectoryInfo dir in new DirectoryInfo(root).GetDirectories())
                {
                    if (dir.Name.EndsWith(PartialSuffix, StringComparison.Ordinal))
                    {
                        if (DateTime.UtcNow - dir.LastWriteTimeUtc > TimeSpan.FromHours(1)) TryDelete(dir);
                        continue;
                    }
                    if (!HashPattern.IsMatch(dir.Name)) continue;
                    long size = 0;
                    foreach (FileInfo file in dir.GetFiles()) size += file.Length;
                    total += size;
                    entries.Add(new KeyValuePair<DirectoryInfo, long>(dir, size));
                }
                if (total <= CacheMaxBytes) return;

                entries.Sort((a, b) => a.Key.LastWriteTimeUtc.CompareTo(b.Key.LastWriteTimeUtc));
                foreach (var entry in entries)
                {
                    if (total <= CacheMaxBytes) break;
                    if (keep.Contains(entry.Key.Name)) continue;
                    if (TryDelete(entry.Key)) total -= entry.Value;
                }
            }
            catch (Exception ex)
            {
                string message = ex.Message;
                mainThread.Enqueue(() => Log($"cache prune failed: {message}"));
            }
        }

        private static bool TryDelete(DirectoryInfo dir)
        {
            try
            {
                dir.Delete(true);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ── Helpers ──

        internal static string Short(string id)
        {
            return string.IsNullOrEmpty(id) ? "-" : (id.Length > 12 ? id.Substring(0, 12) : id);
        }

        private static void Log(string message)
        {
            DebugLog.Log("[MpAssets] " + message);
        }
    }
}