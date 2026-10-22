using System;
using System.IO;
using System.IO.Compression;
using Freehand.Tools;
using UnityEngine;

namespace Freehand.MR
{
    /// <summary>What a saved wall file says about itself, before its paint is read.</summary>
    public struct MRWallHeader
    {
        public string wallId;
        public Vector2 sizeMeters;
        public int textureSize;
        public int paintedTexels;
        public long savedUtcTicks;
    }

    /// <summary>
    /// The artwork on real walls, kept between sessions:
    /// Application.persistentDataPath/mr/walls/&lt;wall id&gt;.fhwall, one file per wall.
    ///
    /// A wall's identity is the id the Quest gives it in its Space Setup (the
    /// AR plane's trackableId, which Meta documents as stable across sessions
    /// in the same space setup). The file stores only what was painted - the
    /// colour and opacity of every texel that has paint, deflate-compressed -
    /// next to the wall's size, so the artwork is put back only on a wall that
    /// is still the same size. It is separate from the can skins
    /// (persistentDataPath/cans/) and from anything virtual: nothing here can
    /// overwrite or be mistaken for them.
    ///
    /// Writes go to a temporary file first and replace the old one only once
    /// complete. A wall that has been cleaned has no file.
    /// </summary>
    public static class MRWallStore
    {
        public const int FormatVersion = 1;
        const uint Magic = 0x4C574D46; // "FMWL"

        public static string Folder => Path.Combine(Application.persistentDataPath, "mr", "walls");

        static string PathFor(string wallId) => Path.Combine(Folder, Safe(wallId) + ".fhwall");

        static string Safe(string id)
        {
            var chars = id.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (!char.IsLetterOrDigit(chars[i]) && chars[i] != '-' && chars[i] != '_')
                    chars[i] = '_';
            }

            return new string(chars);
        }

        public static bool Exists(string wallId) => File.Exists(PathFor(wallId));

        /// <summary>Reads only the header. False if there is no file, or it is not a readable wall file of a known version.</summary>
        public static bool TryReadHeader(string wallId, out MRWallHeader header)
        {
            header = default;
            try
            {
                var path = PathFor(wallId);
                if (!SafeFile.Recover(path))
                    return false;

                using (var stream = File.OpenRead(path))
                using (var reader = new BinaryReader(stream))
                    return ReadHeader(reader, out header);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MRWallStore] header of '{wallId}' unreadable: {e.Message}");
                return false;
            }
        }

        static bool ReadHeader(BinaryReader reader, out MRWallHeader header)
        {
            header = default;
            if (reader.ReadUInt32() != Magic)
                return false;
            var version = reader.ReadInt32();
            if (version > FormatVersion || version < 1)
            {
                Debug.LogWarning($"[MRWallStore] wall file v{version}: this build reads up to v{FormatVersion}.");
                return false;
            }

            header.wallId = reader.ReadString();
            header.sizeMeters = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            header.textureSize = reader.ReadInt32();
            header.paintedTexels = reader.ReadInt32();
            header.savedUtcTicks = reader.ReadInt64();
            return header.textureSize > 0 && header.textureSize <= 4096;
        }

        /// <summary>Reads a wall's paint into `output` (size x size), resampling if it was saved at another resolution. False if it cannot be read.</summary>
        public static bool TryRead(string wallId, int size, Color32[] output, out MRWallHeader header)
        {
            header = default;
            try
            {
                var path = PathFor(wallId);
                if (!SafeFile.Recover(path))
                    return false;

                using (var stream = File.OpenRead(path))
                using (var reader = new BinaryReader(stream))
                {
                    if (!ReadHeader(reader, out header) || header.wallId != wallId)
                        return false;

                    var stored = header.textureSize;
                    var raw = new byte[stored * stored * 4];
                    using (var deflate = new DeflateStream(stream, CompressionMode.Decompress))
                    {
                        var read = 0;
                        while (read < raw.Length)
                        {
                            var n = deflate.Read(raw, read, raw.Length - read);
                            if (n <= 0)
                                break;
                            read += n;
                        }

                        if (read < raw.Length)
                            return false;
                    }

                    for (var y = 0; y < size; y++)
                    {
                        var sy = y * stored / size;
                        for (var x = 0; x < size; x++)
                        {
                            var j = (sy * stored + x * stored / size) * 4;
                            output[y * size + x] = new Color32(raw[j], raw[j + 1], raw[j + 2], raw[j + 3]);
                        }
                    }
                }

                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MRWallStore] '{wallId}' unreadable: {e.Message}");
                return false;
            }
        }

        /// <summary>
        /// Saves a wall's paint. A wall with no paint has no file (an old one is removed). Returns the
        /// number of painted texels, or -1 if it could not be written.
        /// </summary>
        public static int Write(string wallId, Vector2 sizeMeters, int size, Color32[] pixels)
        {
            try
            {
                var painted = 0;
                var raw = new byte[size * size * 4];
                for (var i = 0; i < pixels.Length; i++)
                {
                    var p = pixels[i];
                    if (p.a == 0)
                        continue;
                    raw[i * 4] = p.r;
                    raw[i * 4 + 1] = p.g;
                    raw[i * 4 + 2] = p.b;
                    raw[i * 4 + 3] = p.a;
                    painted++;
                }

                var path = PathFor(wallId);
                if (painted == 0)
                {
                    // V0.15: a wall cleaned (Clear All, the cleaner) has no artwork - but the last artwork is kept
                    // aside rather than deleted, in case the clean was not what the player meant to keep.
                    if (File.Exists(path))
                        SafeFile.SetAside(path, "cleared");
                    return 0;
                }

                Directory.CreateDirectory(Folder);
                using (var memory = new MemoryStream())
                {
                    using (var writer = new BinaryWriter(memory, System.Text.Encoding.UTF8, true))
                    {
                        writer.Write(Magic);
                        writer.Write(FormatVersion);
                        writer.Write(wallId);
                        writer.Write(sizeMeters.x);
                        writer.Write(sizeMeters.y);
                        writer.Write(size);
                        writer.Write(painted);
                        writer.Write(DateTime.UtcNow.Ticks);
                    }

                    using (var deflate = new DeflateStream(memory, System.IO.Compression.CompressionLevel.Fastest, true))
                        deflate.Write(raw, 0, raw.Length);

                    WriteAtomic(path, memory.ToArray());
                }

                return painted;
            }
            catch (Exception e)
            {
                Debug.LogError($"[MRWallStore] could not save '{wallId}': {e.Message}");
                return -1;
            }
        }

        /// <summary>Moves a wall's file out of the way - paint saved for a wall that no longer matches is not thrown away. Every set-aside version is kept (V0.15: until then only the last one).</summary>
        public static void SetAside(string wallId)
        {
            try
            {
                SafeFile.SetAside(PathFor(wallId), "old");
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[MRWallStore] could not set '{wallId}' aside: {e.Message}");
            }
        }

        public static int CountSaved()
        {
            try
            {
                return Directory.Exists(Folder) ? Directory.GetFiles(Folder, "*.fhwall").Length : 0;
            }
            catch
            {
                return 0;
            }
        }

        static void WriteAtomic(string path, byte[] bytes) => SafeFile.Write(path, bytes);
    }
}
