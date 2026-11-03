using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using MelonLoader;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// This player's head, body and guns for online play (ApiContract.md v2.44, 19.15.2-19.15.3):
    /// read from the game's own choices, custom models packaged and uploaded (MpAssets), and sent
    /// with MpSession.SetCosmetics. What others wear is drawn by MpPresence.
    ///
    /// The game keeps each choice in one string pref (CustomModelLoader.GetPlayerPref, in-game dumps
    /// 2026-11-03):
    ///   ""          the default (mask_01, torso_01, Gun1)              -> { kind: default }
    ///   "__N"       avatars[N-1] for heads and bodies, gunModels[N-1]  -> { kind: builtin, index: N-1 }
    ///   "__extraN"  gunModelsExtras[N-1] (guns only)                    -> { kind: builtin, index: 32 + N-1 }
    ///   anything else: a custom model's file name without extension    -> { kind: custom, ... }
    /// Guns number the two lists as one: 0-31 gunModels, 32-63 gunModelsExtras (the contract allows
    /// 0-63). A custom model is customization/{gun|head|body}/NAME.obj with NAME.txt as its config
    /// (CustomModelLoader.ObjConfig as JSON; missing fields take the game's defaults).
    ///
    /// A custom slot is announced only once its package is registered on the server; until then (or
    /// when that fails, or the server has no asset storage) it goes out as default. The package is
    /// rebuilt when the model file or a same-named .mtl changes; the config is sent inline, so
    /// editing it needs no upload. Both guns usually share one file, and so one package.
    ///
    /// Polled twice a second outside songs, from MpPresence.Tick.
    /// From the UnityExplorer console: ExScoringMod.MpCosmetics.DebugDump();
    /// </summary>
    public static class MpCosmetics
    {
        public const int SlotHead = 0, SlotBody = 1, SlotLGun = 2, SlotRGun = 3, SlotCount = 4;
        /// <summary>Gun built-in indexes from here on are gunModelsExtras (index - GunExtrasBase).</summary>
        public const int GunExtrasBase = 32;
        public const int MaxBuiltinIndex = 63;

        private const string BuiltinPrefix = "__";
        private const string ExtraPrefix = "__extra";
        private const string ModelExtension = ".obj";
        private const string ConfigExtension = ".txt";
        private const string MtlExtension = ".mtl";
        private const int MaxDisplayName = 64;
        private const float PollSeconds = 0.5f;
        private const float UploadRetrySeconds = 15f;

        private static readonly string[] SlotNames = { "head", "body", "lGun", "rGun" };
        private static readonly CustomModelLoader.CustomModelType[] SlotTypes =
        {
            CustomModelLoader.CustomModelType.AvatarHead,
            CustomModelLoader.CustomModelType.AvatarBody,
            CustomModelLoader.CustomModelType.GunLeft,
            CustomModelLoader.CustomModelType.GunRight
        };

        private enum BuildStatus { Building, Built, Uploading, Ready, Failed }

        /// <summary>One custom model file's package, shared by every slot wearing it.</summary>
        private class Build
        {
            public string Fingerprint;
            public string ObjPath;
            public BuildStatus Status;
            public MpAssets.LocalPackage Package;
            public string Error;
            public float RetryAt;
        }

        private class Slot
        {
            public string PrefValue;
            public string Dir;          // full path of the type's folder
            public string ObjPath;
            public string ConfigPath;
            public string ConfigStamp;
            public JObject Config;
            /// <summary>What this slot announces right now (for the debug dump).</summary>
            public string Announced = "default";
        }

        private static readonly Slot[] slots = { new Slot(), new Slot(), new Slot(), new Slot() };
        private static readonly Dictionary<string, Build> builds = new Dictionary<string, Build>(StringComparer.Ordinal);
        /// <summary>Packages registered on the server this session (setCosmetics may name them).</summary>
        private static readonly HashSet<string> registered = new HashSet<string>(StringComparer.Ordinal);
        private static float nextPoll;
        private static bool subscribed;
        private static string lastSentKey;

        /// <summary>From MpPresence.Tick, every frame.</summary>
        public static void Tick(bool inSongScene)
        {
            if (!subscribed)
            {
                subscribed = true;
                MpSession.OnCosmeticsError += HandleCosmeticsError;
            }

            if (!MpSession.CosmeticsEnabled || !MpSession.InLobby || inSongScene) return;
            if (Time.unscaledTime < nextPoll) return;
            nextPoll = Time.unscaledTime + PollSeconds;

            CustomModelLoader loader = CustomModelLoader.I;
            if (loader == null) return;

            JObject message = new JObject();
            for (int s = 0; s < SlotCount; s++)
            {
                JObject json;
                try
                {
                    json = ReadSlot(loader, s);
                }
                catch (Exception ex)
                {
                    Log($"{SlotNames[s]}: reading the choice failed ({ex.Message}), sending default");
                    json = DefaultSlot();
                }
                message[SlotNames[s]] = json;
            }

            string key = message.ToString(Newtonsoft.Json.Formatting.None);
            if (key != lastSentKey)
            {
                lastSentKey = key;
                Log($"cosmetics: {Describe()}");
            }
            MpSession.SetCosmetics(message);
        }

        /// <summary>One slot as the contract's JSON, starting any packaging or upload it needs.</summary>
        private static JObject ReadSlot(CustomModelLoader loader, int s)
        {
            Slot slot = slots[s];
            CustomModelLoader.CustomModelType type = SlotTypes[s];
            PlayerPreferences.StringPref pref = loader.GetPlayerPref(type);
            string value = pref != null ? pref.mVal ?? "" : "";
            slot.PrefValue = value;

            if (value.Length == 0) return Announce(slot, DefaultSlot(), "default");

            bool gun = s == SlotLGun || s == SlotRGun;
            if (gun && value.StartsWith(ExtraPrefix, StringComparison.Ordinal))
            {
                if (int.TryParse(value.Substring(ExtraPrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 1)
                    return Builtin(slot, GunExtrasBase + n - 1);
                return Announce(slot, DefaultSlot(), "default (unreadable " + value + ")");
            }
            if (value.StartsWith(BuiltinPrefix, StringComparison.Ordinal))
            {
                if (int.TryParse(value.Substring(BuiltinPrefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n >= 1)
                    return Builtin(slot, n - 1);
                return Announce(slot, DefaultSlot(), "default (unreadable " + value + ")");
            }

            return Custom(loader, slot, s, type, value);
        }

        private static JObject Builtin(Slot slot, int index)
        {
            if (index <= 0) return Announce(slot, DefaultSlot(), "default");
            if (index > MaxBuiltinIndex) return Announce(slot, DefaultSlot(), "default (index " + index + " out of range)");
            return Announce(slot, new JObject { ["kind"] = "builtin", ["index"] = index }, "builtin " + index);
        }

        private static JObject Custom(CustomModelLoader loader, Slot slot, int s, CustomModelLoader.CustomModelType type, string name)
        {
            if (slot.Dir == null)
            {
                string dir = loader.GetCustomModelDir(type);
                slot.Dir = string.IsNullOrEmpty(dir) ? null : Path.GetFullPath(dir);
            }
            if (slot.Dir == null) return Announce(slot, DefaultSlot(), "default (no folder)");

            slot.ObjPath = Path.Combine(slot.Dir, name + ModelExtension);
            slot.ConfigPath = Path.Combine(slot.Dir, name + ConfigExtension);

            FileInfo obj = new FileInfo(slot.ObjPath);
            if (!obj.Exists) return Announce(slot, DefaultSlot(), "default (" + name + ModelExtension + " missing)");

            // The package: rebuilt when the model or a same-named .mtl changes.
            FileInfo mtl = new FileInfo(Path.Combine(slot.Dir, name + MtlExtension));
            string fingerprint = $"{slot.ObjPath}|{obj.Length}|{obj.LastWriteTimeUtc.Ticks}|{(mtl.Exists ? mtl.Length + "|" + mtl.LastWriteTimeUtc.Ticks : "-")}";
            if (!builds.TryGetValue(fingerprint, out Build build))
            {
                build = new Build { Fingerprint = fingerprint, ObjPath = slot.ObjPath, Status = BuildStatus.Building };
                builds[fingerprint] = build;
                StartBuild(build, name);
            }

            ReadConfig(slot);

            if (!MpSession.AssetsEnabled) return Announce(slot, DefaultSlot(), "default (no asset storage on this server)");

            switch (build.Status)
            {
                case BuildStatus.Building:
                    return Announce(slot, DefaultSlot(), "default (packaging " + name + ")");
                case BuildStatus.Failed:
                    return Announce(slot, DefaultSlot(), "default (" + name + ": " + build.Error + ")");
                case BuildStatus.Built:
                    if (registered.Contains(build.Package.PackageId))
                    {
                        build.Status = BuildStatus.Ready;
                        break;
                    }
                    if (Time.unscaledTime >= build.RetryAt && MpSession.IsConnected) StartUpload(build, name);
                    return Announce(slot, DefaultSlot(), "default (uploading " + name + ")");
                case BuildStatus.Uploading:
                    return Announce(slot, DefaultSlot(), "default (uploading " + name + ")");
            }

            JObject json = new JObject
            {
                ["kind"] = "custom",
                ["package"] = build.Package.PackageId,
                ["name"] = DisplayName(name),
                ["config"] = slot.Config ?? DefaultConfig()
            };
            return Announce(slot, json, "custom " + name + " (" + MpAssets.Short(build.Package.PackageId) + ")");
        }

        private static JObject Announce(Slot slot, JObject json, string description)
        {
            slot.Announced = description;
            return json;
        }

        private static void StartBuild(Build build, string name)
        {
            Log($"packaging {name}");
            MpAssets.BuildLocalPackage(build.ObjPath, (package, error) =>
            {
                if (package == null)
                {
                    build.Status = BuildStatus.Failed;
                    build.Error = error ?? "packaging failed";
                    MelonLogger.Log($"[MpCosmetics] {name} can't be shared: {build.Error}");
                    return;
                }
                build.Package = package;
                build.Status = BuildStatus.Built;
                Log($"{name} packaged: {MpAssets.Short(package.PackageId)}, {package.Files.Count} file(s), {package.TotalSize / 1024} KB");
                foreach (string note in package.Notes) Log($"  {name}: {note}");
            });
        }

        private static void StartUpload(Build build, string name)
        {
            string token = MpSession.AssetToken;
            if (string.IsNullOrEmpty(token)) return;
            build.Status = BuildStatus.Uploading;
            Log($"uploading {name} ({MpAssets.Short(build.Package.PackageId)})");
            MpAssets.Upload(build.Package, token, (outcome, reason) =>
            {
                switch (outcome)
                {
                    case MpAssets.UploadOutcome.Registered:
                        registered.Add(build.Package.PackageId);
                        build.Status = BuildStatus.Ready;
                        Log($"{name} uploaded ({reason})");
                        nextPoll = 0f; // announce it now
                        break;
                    case MpAssets.UploadOutcome.Transient:
                        build.Status = BuildStatus.Built;
                        build.RetryAt = Time.unscaledTime + UploadRetrySeconds;
                        Log($"{name} upload failed ({reason}), retrying in {UploadRetrySeconds:F0} s");
                        break;
                    default:
                        build.Status = BuildStatus.Failed;
                        build.Error = reason;
                        MelonLogger.Log($"[MpCosmetics] {name} was refused by the server: {reason}");
                        break;
                }
            });
        }

        /// <summary>
        /// setCosmetics was refused (v2.44): a package the server doesn't know is uploaded again once;
        /// a blocked one stays default for the session.
        /// </summary>
        private static void HandleCosmeticsError(MpError error)
        {
            if (error == null) return;
            Log($"setCosmetics refused: {error}{(error.package != null ? " package=" + MpAssets.Short(error.package) : "")}");
            if (error.package == null) return;

            foreach (Build build in builds.Values)
            {
                if (build.Package == null || build.Package.PackageId != error.package) continue;
                if (error.code == "UNKNOWN_PACKAGE" && registered.Remove(error.package))
                {
                    build.Status = BuildStatus.Built;
                    build.RetryAt = 0f;
                }
                else
                {
                    build.Status = BuildStatus.Failed;
                    build.Error = error.code == "PACKAGE_BLOCKED" ? "blocked by an admin" : error.code;
                }
            }
            nextPoll = 0f;
        }

        // ── Config ──

        /// <summary>The model's config file, re-read only when it changes. Missing or unreadable: the game's defaults.</summary>
        private static void ReadConfig(Slot slot)
        {
            FileInfo info = new FileInfo(slot.ConfigPath);
            string stamp = info.Exists ? $"{slot.ConfigPath}|{info.Length}|{info.LastWriteTimeUtc.Ticks}" : slot.ConfigPath + "|none";
            if (stamp == slot.ConfigStamp && slot.Config != null) return;
            slot.ConfigStamp = stamp;

            JObject config = DefaultConfig();
            if (info.Exists)
            {
                try
                {
                    JObject source = JObject.Parse(File.ReadAllText(slot.ConfigPath));
                    ReadVector(source, "scaleFactor", config);
                    ReadVector(source, "positionOffset", config);
                    ReadVector(source, "orientationOffset", config);
                    ReadFloat(source, "shading", config);
                    ReadFloat(source, "reflection", config);
                    if (source["recalculateNormals"] != null && source["recalculateNormals"].Type == JTokenType.Boolean)
                        config["recalculateNormals"] = (bool)source["recalculateNormals"];
                }
                catch (Exception ex)
                {
                    Log($"{Path.GetFileName(slot.ConfigPath)} unreadable ({ex.Message}), using the defaults");
                }
            }
            slot.Config = config;
        }

        private static void ReadVector(JObject source, string field, JObject dest)
        {
            JObject v = source[field] as JObject;
            if (v == null) return;
            float[] values = { Float(v["x"], 0f), Float(v["y"], 0f), Float(v["z"], 0f) };
            for (int i = 0; i < 3; i++) if (float.IsNaN(values[i]) || float.IsInfinity(values[i])) return;
            dest[field] = new JArray(Round4(values[0]), Round4(values[1]), Round4(values[2]));
        }

        private static void ReadFloat(JObject source, string field, JObject dest)
        {
            JToken t = source[field];
            if (t == null || (t.Type != JTokenType.Float && t.Type != JTokenType.Integer)) return;
            float v = (float)t;
            if (float.IsNaN(v) || float.IsInfinity(v)) return;
            dest[field] = Round4(v);
        }

        private static float Float(JToken t, float fallback)
        {
            if (t == null || (t.Type != JTokenType.Float && t.Type != JTokenType.Integer)) return fallback;
            return (float)t;
        }

        private static double Round4(float v)
        {
            return Math.Round(v, 4);
        }

        /// <summary>CustomModelLoader.LoadMesh's defaults when a model has no config file.</summary>
        private static JObject DefaultConfig()
        {
            return new JObject
            {
                ["scaleFactor"] = new JArray(1, 1, 1),
                ["positionOffset"] = new JArray(0, 0, 0),
                ["orientationOffset"] = new JArray(0, 0, 0),
                ["shading"] = 0.5,
                ["reflection"] = 1.0,
                ["recalculateNormals"] = true
            };
        }

        private static JObject DefaultSlot()
        {
            return new JObject { ["kind"] = "default" };
        }

        private static string DisplayName(string name)
        {
            StringBuilder clean = new StringBuilder();
            foreach (char c in name) if (!char.IsControl(c)) clean.Append(c);
            string result = clean.ToString().Trim();
            if (result.Length > MaxDisplayName) result = result.Substring(0, MaxDisplayName).Trim();
            return result.Length > 0 ? result : "custom";
        }

        // ── Debug ──

        private static string Describe()
        {
            StringBuilder sb = new StringBuilder();
            for (int s = 0; s < SlotCount; s++)
            {
                if (s > 0) sb.Append(", ");
                sb.Append(SlotNames[s]).Append('=').Append(slots[s].Announced);
            }
            return sb.ToString();
        }

        public static void DebugDump()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"[MpCosmetics] cosmetics={MpSession.CosmeticsEnabled} assets={MpSession.AssetsEnabled} registered={registered.Count}");
            for (int s = 0; s < SlotCount; s++)
            {
                Slot slot = slots[s];
                sb.AppendLine($"[MpCosmetics]   {SlotNames[s]}: pref='{slot.PrefValue}' -> {slot.Announced}");
            }
            foreach (Build build in builds.Values)
            {
                sb.AppendLine($"[MpCosmetics]   build {Path.GetFileName(build.ObjPath)}: {build.Status} package={(build.Package != null ? MpAssets.Short(build.Package.PackageId) : "-")}{(build.Error != null ? " error=" + build.Error : "")}");
            }
            foreach (string line in sb.ToString().TrimEnd().Split('\n')) MelonLogger.Log(line.TrimEnd('\r'));
        }

        private static void Log(string message)
        {
            DebugLog.Log("[MpCosmetics] " + message);
        }
    }
}