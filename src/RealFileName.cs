using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Harmony;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ExScoringMod
{
    /// <summary>
    /// Two different Maudica maps can have file names that are the same, or differ only in case
    /// (NewJeans "ATTENTION-Osiris.audica" vs Joji "Attention-Osiris.audica"). Windows can't keep
    /// both in one folder, and a custom song's ID is its file name + "_" + the cue checksum, so the
    /// second one can't simply be renamed either: its ID would no longer match Maudica, the
    /// leaderboards or other players.
    ///
    /// The second file is saved under a free name on disk, the real name plus "~N"
    /// ("Attention-Osiris~2.audica", no spaces), with its real name stored inside the .audica as
    /// an extra, uncompressed zip entry (exscoring.json: {"fileName", "md5"}; md5 is the file as
    /// Maudica serves it, for "same file?" checks, since adding the entry changes the bytes). The
    /// cue checksum doesn't read the zip, so it is unchanged.
    ///
    /// When the game loads a file whose name has the "~" marker and that tag
    /// (ProcessSingleSongRealNamePatch), originalSongID becomes the real name and the ID is rebuilt
    /// with it, so the song has its normal ID everywhere. The song cache harvests the result, so
    /// cache hits get the real name too. Without the mod, the file just loads as "Name~2_hash".
    ///
    /// Disk names stay unique across Downloads, the songs folder and its subfolders, so folder
    /// management keeps working on file names. Whether two files are the same map (an update) or
    /// two different songs is decided by the real name (exact case) plus title and artist.
    /// </summary>
    internal static class RealFileName
    {
        private const string LogPrefix = "[RealFileName]";
        internal const string TagEntryName = "exscoring.json";
        internal const char Marker = '~';
        private const int MaxSuffix = 999;
        private const int MaxSmallEntryBytes = 4 * 1024 * 1024;

        internal sealed class Tag
        {
            public string fileName;
            public string md5;
        }

        /// <summary>Title and artist of a map, from Maudica, a loaded song or a song.desc.</summary>
        internal sealed class MapMeta
        {
            public string Title;
            public string Artist;
        }

        // ── Names ─────────────────────────────────────────────────────────────

        /// <summary>True when the file name (no extension) contains the "~" marker: it may carry a tag.</summary>
        internal static bool HasMarker(string pathOrName)
        {
            if (string.IsNullOrEmpty(pathOrName)) return false;
            string stem;
            try { stem = Path.GetFileNameWithoutExtension(pathOrName); }
            catch { return false; }
            return stem != null && stem.IndexOf(Marker) >= 0;
        }

        /// <summary>The real name stored in a tagged file, or null.</summary>
        internal static string ReadTagName(string path)
        {
            Tag tag = ReadTag(path);
            return tag != null && !string.IsNullOrEmpty(tag.fileName) ? tag.fileName : null;
        }

        /// <summary>The name a file's song ID is built from: its tag's name, else its file name (no extension).</summary>
        internal static string RealNameOf(string path)
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            if (!HasMarker(path)) return stem;
            return ReadTagName(path) ?? stem;
        }

        /// <summary>
        /// A free disk name (with ".audica") for a map whose real name is realName: the real name
        /// itself when no file of that name (any case) is in Downloads, the songs folder or its
        /// subfolders, else realName~2, ~3... Null when none is free.
        /// </summary>
        internal static string ChooseDiskName(string realName)
        {
            List<string> dirs = SongDirectories();
            string plain = realName + ".audica";
            if (!IsTaken(dirs, plain)) return plain;
            for (int n = 2; n <= MaxSuffix; n++)
            {
                string name = realName + Marker + n + ".audica";
                if (!IsTaken(dirs, name)) return name;
            }
            return null;
        }

        private static bool IsTaken(List<string> dirs, string fileName)
        {
            foreach (string dir in dirs)
            {
                try
                {
                    // File.Exists ignores case on Windows, which is what matters here.
                    if (File.Exists(Path.Combine(dir, fileName))) return true;
                }
                catch { }
            }
            return false;
        }

        /// <summary>Downloads, the songs folder and its subfolders (the ones that exist), in that order.</summary>
        internal static List<string> SongDirectories()
        {
            var dirs = new List<string>();
            try { if (Directory.Exists(ExScoring.downloadsDirectory)) dirs.Add(ExScoring.downloadsDirectory); } catch { }
            try
            {
                if (Directory.Exists(ExScoring.mainSongDirectory))
                {
                    dirs.Add(ExScoring.mainSongDirectory);
                    dirs.AddRange(Directory.GetDirectories(ExScoring.mainSongDirectory, "*", SearchOption.TopDirectoryOnly));
                }
            }
            catch (Exception ex)
            {
                DebugLog.Log($"{LogPrefix} Couldn't list the song folders: {ex.Message}");
            }
            return dirs;
        }

        /// <summary>
        /// Every .audica in Downloads, the songs folder and its subfolders whose real name is
        /// exactly realName (case included): "realName.audica", and tagged "realName~N.audica".
        /// Downloads first, then the songs folder, then the subfolders.
        /// </summary>
        internal static List<string> FindFilesWithRealName(string realName)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(realName)) return result;
            string markedPrefix = realName + Marker;

            foreach (string dir in SongDirectories())
            {
                string[] files;
                try { files = Directory.GetFiles(dir, "*.audica", SearchOption.TopDirectoryOnly); }
                catch { continue; }

                foreach (string f in files)
                {
                    string stem = Path.GetFileNameWithoutExtension(f);
                    if (string.Equals(stem, realName, StringComparison.Ordinal))
                    {
                        result.Add(f);
                    }
                    else if (stem.StartsWith(markedPrefix, StringComparison.OrdinalIgnoreCase)
                             && string.Equals(ReadTagName(f), realName, StringComparison.Ordinal))
                    {
                        result.Add(f);
                    }
                }
            }
            return result;
        }

        /// <summary>The source dir ProcessSingleSong needs for a file in Downloads, the songs folder or a subfolder.</summary>
        internal static SongList.SongSourceDir SourceDirFor(string file)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(file));
            if (SamePath(dir, ExScoring.downloadsDirectory))
                return new SongList.SongSourceDir(Application.dataPath, ExScoring.downloadsDirectory);
            if (SamePath(dir, ExScoring.mainSongDirectory))
                return new SongList.SongSourceDir(Application.streamingAssetsPath, ExScoring.mainSongDirectory);
            return new SongList.SongSourceDir(Application.streamingAssetsPath, dir);
        }

        private static bool SamePath(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('\\', '/'), Path.GetFullPath(b).TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        // ── Song IDs ──────────────────────────────────────────────────────────

        /// <summary>
        /// Gives a loaded song its real name: originalSongID = realName, songID = realName + "_" +
        /// the same checksum. Only for a custom (dynamic) ID built from its disk name. True when
        /// something changed.
        /// </summary>
        internal static bool ApplyRealName(SongList.SongData sd, string realName)
        {
            if (sd == null || string.IsNullOrEmpty(realName)) return false;
            string orig = sd.originalSongID;
            string id = sd.songID;
            if (string.IsNullOrEmpty(orig) || string.IsNullOrEmpty(id)) return false;
            if (orig == realName) return false;
            if (id.Length <= orig.Length + 1 || !id.StartsWith(orig + "_", StringComparison.Ordinal)) return false;

            sd.originalSongID = realName;
            sd.songID = realName + id.Substring(orig.Length);
            try { sd.communityMapsContest = SongList.IsCommunityMapsContestSong(sd.songID); }
            catch { }
            return true;
        }

        // ── Same map or another song ─────────────────────────────────────────

        internal static MapMeta MetaOf(CatalogMap map)
        {
            return map == null ? null : new MapMeta { Title = map.title, Artist = map.artist };
        }

        /// <summary>Title and artist from the file's song.desc, or null when it can't be read.</summary>
        internal static MapMeta MetaOfFile(string path)
        {
            try
            {
                byte[] desc = ReadEntry(path, "song.desc", true);
                if (desc == null) return null;
                JObject obj = JObject.Parse(Encoding.UTF8.GetString(desc).TrimStart('﻿'));
                return new MapMeta { Title = (string)obj["title"], Artist = (string)obj["artist"] };
            }
            catch (Exception ex)
            {
                DebugLog.Log($"{LogPrefix} Couldn't read song.desc of {path}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// False only when both sides are known and the title or the artist differ (rich text
        /// tags, case, spaces and punctuation ignored). Unknown counts as the same map, like before.
        /// </summary>
        internal static bool SameMap(MapMeta a, MapMeta b)
        {
            if (a == null || b == null) return true;
            return SameField(a.Title, b.Title) && SameField(a.Artist, b.Artist);
        }

        private static readonly Regex richText = new Regex("<[^>]*>");

        private static bool SameField(string a, string b)
        {
            string na = NormalizeField(a), nb = NormalizeField(b);
            if (na.Length == 0 || nb.Length == 0) return true;
            return na == nb;
        }

        private static string NormalizeField(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return MaudicaCatalog.Normalize(richText.Replace(s, ""));
        }

        // ── Contents ──────────────────────────────────────────────────────────

        internal static string Md5Hex(byte[] bytes)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(bytes);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>
        /// True when the file is the file Maudica served (bytes, bytesMd5): a tagged file by the
        /// MD5 in its tag, any other file byte for byte.
        /// </summary>
        internal static bool SameContent(byte[] bytes, string bytesMd5, string filePath)
        {
            try
            {
                if (bytes == null || !File.Exists(filePath)) return false;

                if (HasMarker(filePath))
                {
                    Tag tag = ReadTag(filePath);
                    if (tag != null && !string.IsNullOrEmpty(tag.md5))
                        return string.Equals(tag.md5, bytesMd5 ?? Md5Hex(bytes), StringComparison.OrdinalIgnoreCase);
                }

                if (new FileInfo(filePath).Length != bytes.Length) return false;
                byte[] onDisk = File.ReadAllBytes(filePath);
                for (int i = 0; i < bytes.Length; i++)
                    if (bytes[i] != onDisk[i]) return false;
                return true;
            }
            catch (Exception ex)
            {
                DebugLog.Log($"{LogPrefix} Couldn't compare with {filePath}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Writes src to dst as the map named realName: a file that already has a tag is copied
        /// as is, any other file gets the tag. Doesn't touch src.
        /// </summary>
        internal static void WriteTaggedCopy(string src, string dst, string realName)
        {
            if (HasMarker(src) && ReadTag(src) != null)
            {
                File.Copy(src, dst, false);
                return;
            }
            byte[] bytes = File.ReadAllBytes(src);
            File.WriteAllBytes(dst, AddTag(bytes, realName, Md5Hex(bytes)));
        }

        // ── Tag (an extra zip entry) ─────────────────────────────────────────

        /// <summary>The file's tag, or null when it has none or can't be read.</summary>
        internal static Tag ReadTag(string path)
        {
            try
            {
                byte[] data = ReadEntry(path, TagEntryName, false);
                if (data == null) return null;
                return JsonConvert.DeserializeObject<Tag>(Encoding.UTF8.GetString(data));
            }
            catch (Exception ex)
            {
                DebugLog.Log($"{LogPrefix} Couldn't read the tag of {path}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// The zip with one more entry, TagEntryName (stored, not compressed), holding realName and
        /// the MD5 of the file as Maudica serves it. Everything else is kept byte for byte; the
        /// entry goes right before the central directory. Throws on zip64 or an odd layout.
        /// </summary>
        internal static byte[] AddTag(byte[] zip, string realName, string md5)
        {
            if (!TryReadEocd(zip, out int eocd, out int count, out long cdSize, out long cdOffset))
                throw new InvalidDataException("not a zip (no end of central directory)");
            if (count == 0xFFFF || cdOffset == 0xFFFFFFFF || cdSize == 0xFFFFFFFF)
                throw new InvalidDataException("zip64 isn't supported");
            if (cdOffset + cdSize != eocd)
                throw new InvalidDataException("unexpected data before the end of central directory");

            byte[] data = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new Tag { fileName = realName, md5 = md5 }));
            byte[] name = Encoding.UTF8.GetBytes(TagEntryName);
            uint crc = Crc32(data);
            DosDateTime(DateTime.Now, out ushort time, out ushort date);
            int commentLength = BitConverter.ToUInt16(zip, eocd + 20);

            using (var ms = new MemoryStream(zip.Length + 256))
            using (var w = new BinaryWriter(ms))
            {
                // Everything up to the central directory, unchanged.
                w.Write(zip, 0, (int)cdOffset);

                // Local header + data of the new entry.
                long localOffset = ms.Position;
                w.Write(0x04034b50u);
                w.Write((ushort)20);       // version needed
                w.Write((ushort)0x0800);   // flags: UTF-8 name
                w.Write((ushort)0);        // method: stored
                w.Write(time);
                w.Write(date);
                w.Write(crc);
                w.Write((uint)data.Length);
                w.Write((uint)data.Length);
                w.Write((ushort)name.Length);
                w.Write((ushort)0);        // extra length
                w.Write(name);
                w.Write(data);

                // The old central directory, then the new entry's record.
                long newCdOffset = ms.Position;
                w.Write(zip, (int)cdOffset, (int)cdSize);
                w.Write(0x02014b50u);
                w.Write((ushort)20);       // version made by
                w.Write((ushort)20);       // version needed
                w.Write((ushort)0x0800);
                w.Write((ushort)0);
                w.Write(time);
                w.Write(date);
                w.Write(crc);
                w.Write((uint)data.Length);
                w.Write((uint)data.Length);
                w.Write((ushort)name.Length);
                w.Write((ushort)0);        // extra length
                w.Write((ushort)0);        // comment length
                w.Write((ushort)0);        // disk number start
                w.Write((ushort)0);        // internal attributes
                w.Write(0u);               // external attributes
                w.Write((uint)localOffset);
                w.Write(name);
                long newCdSize = ms.Position - newCdOffset;

                // End of central directory, with the original comment.
                w.Write(0x06054b50u);
                w.Write((ushort)0);
                w.Write((ushort)0);
                w.Write((ushort)(count + 1));
                w.Write((ushort)(count + 1));
                w.Write((uint)newCdSize);
                w.Write((uint)newCdOffset);
                w.Write((ushort)commentLength);
                w.Write(zip, eocd + 22, commentLength);

                w.Flush();
                return ms.ToArray();
            }
        }

        // ── Minimal zip reading (central directory, stored / deflate, no zip64) ──

        private sealed class CdEntry
        {
            public string Name;
            public int Method;
            public long CompressedSize;
            public long UncompressedSize;
            public long LocalOffset;
        }

        /// <summary>
        /// The uncompressed bytes of an entry, or null when the zip has none. byFileName: also
        /// matches the name alone in any folder, ignoring case (song.desc). Several entries with
        /// the name: the last one (an added tag goes last).
        /// </summary>
        private static byte[] ReadEntry(string path, string entryName, bool byFileName)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                List<CdEntry> entries = ReadCentralDirectory(fs);
                CdEntry found = null;
                foreach (CdEntry e in entries)
                {
                    bool match = byFileName
                        ? string.Equals(Path.GetFileName(e.Name.Replace('\\', '/')), entryName, StringComparison.OrdinalIgnoreCase)
                        : string.Equals(e.Name, entryName, StringComparison.Ordinal);
                    if (match) found = e;
                }
                if (found == null) return null;
                if (found.UncompressedSize > MaxSmallEntryBytes || found.CompressedSize > MaxSmallEntryBytes)
                    throw new InvalidDataException($"'{found.Name}' is too large");

                byte[] local = new byte[30];
                fs.Seek(found.LocalOffset, SeekOrigin.Begin);
                ReadFully(fs, local, 30);
                if (BitConverter.ToUInt32(local, 0) != 0x04034b50) throw new InvalidDataException($"bad local header for '{found.Name}'");
                int nameLen = BitConverter.ToUInt16(local, 26);
                int extraLen = BitConverter.ToUInt16(local, 28);
                fs.Seek(found.LocalOffset + 30 + nameLen + extraLen, SeekOrigin.Begin);

                byte[] raw = new byte[found.CompressedSize];
                ReadFully(fs, raw, raw.Length);
                if (found.Method == 0) return raw;
                if (found.Method != 8) throw new InvalidDataException($"zip method {found.Method} isn't supported");

                using (var input = new MemoryStream(raw))
                using (var inflate = new DeflateStream(input, CompressionMode.Decompress))
                using (var output = new MemoryStream((int)Math.Max(0, found.UncompressedSize)))
                {
                    byte[] buf = new byte[16384];
                    int n;
                    while ((n = inflate.Read(buf, 0, buf.Length)) > 0)
                    {
                        output.Write(buf, 0, n);
                        if (output.Length > MaxSmallEntryBytes) throw new InvalidDataException($"'{found.Name}' is too large");
                    }
                    return output.ToArray();
                }
            }
        }

        private static List<CdEntry> ReadCentralDirectory(Stream s)
        {
            long length = s.Length;
            int tail = (int)Math.Min(length, 65535 + 22);
            byte[] buf = new byte[tail];
            s.Seek(length - tail, SeekOrigin.Begin);
            ReadFully(s, buf, tail);

            if (!TryReadEocd(buf, out int eocd, out int count, out long cdSize, out long cdOffset))
                throw new InvalidDataException("not a zip (no end of central directory)");
            if (cdOffset == 0xFFFFFFFF || cdSize == 0xFFFFFFFF) throw new InvalidDataException("zip64 isn't supported");
            if (cdSize > int.MaxValue || cdOffset + cdSize > length) throw new InvalidDataException("bad central directory");

            byte[] cd = new byte[cdSize];
            s.Seek(cdOffset, SeekOrigin.Begin);
            ReadFully(s, cd, cd.Length);

            var entries = new List<CdEntry>();
            int p = 0;
            for (int i = 0; i < count && p + 46 <= cd.Length; i++)
            {
                if (BitConverter.ToUInt32(cd, p) != 0x02014b50) throw new InvalidDataException("bad central directory");
                int nameLen = BitConverter.ToUInt16(cd, p + 28);
                int extraLen = BitConverter.ToUInt16(cd, p + 30);
                int commentLen = BitConverter.ToUInt16(cd, p + 32);
                if (p + 46 + nameLen > cd.Length) throw new InvalidDataException("bad central directory");
                entries.Add(new CdEntry
                {
                    Method = BitConverter.ToUInt16(cd, p + 10),
                    CompressedSize = BitConverter.ToUInt32(cd, p + 20),
                    UncompressedSize = BitConverter.ToUInt32(cd, p + 24),
                    LocalOffset = BitConverter.ToUInt32(cd, p + 42),
                    Name = Encoding.UTF8.GetString(cd, p + 46, nameLen),
                });
                p += 46 + nameLen + extraLen + commentLen;
            }
            return entries;
        }

        /// <summary>
        /// Finds the end of central directory record in buf (a whole zip, or its tail). Offsets
        /// read from it are file offsets; eocd is the record's position in buf.
        /// </summary>
        private static bool TryReadEocd(byte[] buf, out int eocd, out int count, out long cdSize, out long cdOffset)
        {
            eocd = -1;
            count = 0;
            cdSize = 0;
            cdOffset = 0;
            int min = Math.Max(0, buf.Length - 22 - 65535);
            for (int i = buf.Length - 22; i >= min; i--)
            {
                if (buf[i] != 0x50 || buf[i + 1] != 0x4b || buf[i + 2] != 0x05 || buf[i + 3] != 0x06) continue;
                int commentLen = BitConverter.ToUInt16(buf, i + 20);
                if (i + 22 + commentLen != buf.Length) continue; // a stray signature, not the record
                eocd = i;
                count = BitConverter.ToUInt16(buf, i + 10);
                cdSize = BitConverter.ToUInt32(buf, i + 12);
                cdOffset = BitConverter.ToUInt32(buf, i + 16);
                return true;
            }
            return false;
        }

        private static void ReadFully(Stream s, byte[] buf, int count)
        {
            int read = 0;
            while (read < count)
            {
                int n = s.Read(buf, read, count - read);
                if (n <= 0) throw new EndOfStreamException();
                read += n;
            }
        }

        private static uint[] crcTable;

        private static uint Crc32(byte[] data)
        {
            if (crcTable == null)
            {
                var table = new uint[256];
                for (uint i = 0; i < 256; i++)
                {
                    uint c = i;
                    for (int k = 0; k < 8; k++)
                        c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                    table[i] = c;
                }
                crcTable = table;
            }
            uint crc = 0xFFFFFFFFu;
            foreach (byte b in data)
                crc = crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }

        private static void DosDateTime(DateTime t, out ushort time, out ushort date)
        {
            if (t.Year < 1980) t = new DateTime(1980, 1, 1);
            time = (ushort)((t.Hour << 11) | (t.Minute << 5) | (t.Second / 2));
            date = (ushort)(((t.Year - 1980) << 9) | (t.Month << 5) | t.Day);
        }
    }

    /// <summary>
    /// A tagged file (RealFileName) loaded by SongList.ProcessSingleSong gets its real name:
    /// originalSongID and songID are rebuilt from the name in its tag instead of its disk name.
    /// Covers every caller (the song list assembly, SongDownloader, the partial reload). Runs
    /// before SongListAssembler harvests the song, so the song cache stores the real name. A song
    /// that then has the same ID as one already in the list (the same map twice) is taken out
    /// again, like the game's own duplicate check.
    /// </summary>
    [HarmonyPatch(typeof(SongList), "ProcessSingleSong")]
    internal static class ProcessSingleSongRealNamePatch
    {
        private static void Prefix(SongList __instance, string found, out int __state)
        {
            __state = -1;
            try
            {
                if (RealFileName.HasMarker(found) && __instance.songs != null)
                    __state = __instance.songs.Count;
            }
            catch { }
        }

        private static void Postfix(SongList __instance, string found, bool __result, int __state)
        {
            if (__state < 0 || !__result) return;
            try
            {
                Il2CppSystem.Collections.Generic.List<SongList.SongData> songs = __instance.songs;
                if (songs == null || songs.Count <= __state) return;

                string realName = RealFileName.ReadTagName(found);
                if (realName == null) return;

                SongList.SongData newest = songs[songs.Count - 1];
                string diskId = newest.songID;
                if (!RealFileName.ApplyRealName(newest, realName)) return;

                for (int i = 0; i < songs.Count - 1; i++)
                {
                    SongList.SongData sd = songs[i];
                    if (sd != null && sd.songID == newest.songID)
                    {
                        songs.RemoveAt(songs.Count - 1);
                        DebugLog.Log($"[RealFileName] {Path.GetFileName(found)} is {newest.songID}, which is already loaded: skipped as a duplicate");
                        return;
                    }
                }

                DebugLog.Log($"[RealFileName] {Path.GetFileName(found)} loaded as {newest.songID} (real file name {realName}.audica, was {diskId})");
            }
            catch (Exception ex)
            {
                DebugLog.Log($"[RealFileName] Couldn't apply the real name of {found}: {ex.Message}");
            }
        }
    }
}