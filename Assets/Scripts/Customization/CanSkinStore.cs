using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Freehand.Tools;
using UnityEngine;

namespace Freehand.Customization
{
    /// <summary>One saved can: who it is and where its paint lives.</summary>
    [Serializable]
    public class CanSkinEntry
    {
        public string id;
        public string name;

        /// <summary>The unpainted look the paint was laid over (CanSkinStore.BaseMaterialId).</summary>
        public string baseMaterial;
        public string paintFile;
        public int textureSize;
        public int paintedTexels;
        public string created;
        public string modified;
    }

    /// <summary>Every saved can, and which one the player is using.</summary>
    [Serializable]
    public class CanSkinIndex
    {
        public int formatVersion = CanSkinStore.FormatVersion;
        public string activeSkinId = "";
        public List<CanSkinEntry> skins = new List<CanSkinEntry>();

        public CanSkinEntry Find(string id) => string.IsNullOrEmpty(id) ? null : skins.Find(s => s.id == id);
    }

    /// <summary>
    /// Saved cans on the device: Application.persistentDataPath/cans/ (works the
    /// same in the Editor, on Windows and on the Quest). index.json says which
    /// cans exist and which one is in use; each can's paint is its own file.
    ///
    /// A skin is stored as only what the player painted: per texel, the paint
    /// colour where it differs from the unpainted can, nothing elsewhere,
    /// deflate-compressed (a lightly painted 512x512 can is a few KB, a fully
    /// covered one a few hundred). Loading lays it back over the current base.
    /// Both files carry a format version, so a later layout can be migrated.
    /// Writes go to a temporary file first and replace the old one only once
    /// complete - a crash mid-save cannot leave a broken can behind.
    /// </summary>
    public static class CanSkinStore
    {
        public const int FormatVersion = 1;
        public const string BaseMaterialId = "freehand-aluminium-v1";
        const uint PaintMagic = 0x4C504846; // "FHPL"

        public static string Folder => Path.Combine(Application.persistentDataPath, "cans");
        static string IndexPath => Path.Combine(Folder, "index.json");

        public static string NewId() => Guid.NewGuid().ToString("N").Substring(0, 12);

        public static string Now() => DateTime.UtcNow.ToString("o");

        public static CanSkinIndex LoadIndex()
        {
            CanSkinIndex index = null;
            try
            {
                // An index replaced mid-save: its previous copy.
                SafeFile.Recover(IndexPath);
                if (!File.Exists(IndexPath))
                    return Directory.Exists(Folder) && Directory.GetFiles(Folder, "skin_*.fhpaint").Length > 0 ? Rebuild("index missing") : new CanSkinIndex();

                index = JsonUtility.FromJson<CanSkinIndex>(File.ReadAllText(IndexPath));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CanSkinStore] index unreadable: {e.Message}");
                index = null;
            }

            // V0.15: an unreadable index used to mean an empty shelf - and the next save overwrote it, losing every
            // saved can. Now the bad index is kept aside and the shelf is rebuilt from the cans' own files.
            if (index == null)
            {
                try
                {
                    var aside = SafeFile.SetAside(IndexPath, "corrupt");
                    Debug.LogWarning($"[CanSkinStore] unreadable index kept as '{Path.GetFileName(aside)}'; rebuilding the shelf from the saved cans.");
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[CanSkinStore] could not set the unreadable index aside: {e.Message}");
                }

                return Rebuild("index unreadable");
            }

            if (index.skins == null)
                index.skins = new List<CanSkinEntry>();

            Migrate(index);

            // A can whose paint file is gone (and has no previous copy to restore) is not a can - said, not silent.
            index.skins.RemoveAll(s =>
            {
                if (s == null || string.IsNullOrEmpty(s.id))
                    return true;
                if (SafeFile.Recover(Path.Combine(Folder, s.paintFile ?? "")))
                    return false;
                Debug.LogWarning($"[CanSkinStore] saved can '{s.name}' has no paint file any more - left off the shelf.");
                return true;
            });

            if (index.Find(index.activeSkinId) == null)
                index.activeSkinId = "";
            return index;
        }

        // The shelf, from the paint files on disk (their ids are in their names); names are not recoverable.
        static CanSkinIndex Rebuild(string reason)
        {
            var index = new CanSkinIndex();
            try
            {
                if (!Directory.Exists(Folder))
                    return index;

                var number = 1;
                foreach (var file in Directory.GetFiles(Folder, "skin_*.fhpaint"))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    var id = name.Substring("skin_".Length);
                    var size = 0;
                    using (var reader = new BinaryReader(File.OpenRead(file)))
                    {
                        if (reader.ReadUInt32() != PaintMagic)
                            continue;
                        reader.ReadInt32();
                        size = reader.ReadInt32();
                    }

                    var time = File.GetLastWriteTimeUtc(file).ToString("o");
                    index.skins.Add(new CanSkinEntry { id = id, name = "LATA " + number++, baseMaterial = BaseMaterialId, paintFile = Path.GetFileName(file), textureSize = size, created = time, modified = time });
                }

                Debug.LogWarning($"[CanSkinStore] {reason}: {index.skins.Count} saved can(s) recovered from their files.");
                if (index.skins.Count > 0)
                    SaveIndex(index);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CanSkinStore] could not rebuild the shelf: {e.Message}");
            }

            return index;
        }

        // Version 1 is the first format: nothing to migrate yet. Later
        // versions convert older indexes here, before anything reads them.
        static void Migrate(CanSkinIndex index)
        {
            if (index.formatVersion < FormatVersion)
                index.formatVersion = FormatVersion;
        }

        public static bool SaveIndex(CanSkinIndex index)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                index.formatVersion = FormatVersion;
                WriteAtomic(IndexPath, System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(index, true)));
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[CanSkinStore] could not save the index: {e.Message}");
                return false;
            }
        }

        /// <summary>Writes what was painted (pixels that differ from basePixels). Returns the painted texel count, or -1 on failure.</summary>
        public static int WritePaint(CanSkinEntry entry, Color32[] pixels, Color32[] basePixels, int size)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                var raw = new byte[size * size * 4];
                var painted = 0;
                for (var i = 0; i < pixels.Length; i++)
                {
                    var p = pixels[i];
                    var b = basePixels[i];
                    if (p.r == b.r && p.g == b.g && p.b == b.b)
                        continue;

                    raw[i * 4] = p.r;
                    raw[i * 4 + 1] = p.g;
                    raw[i * 4 + 2] = p.b;
                    raw[i * 4 + 3] = 255;
                    painted++;
                }

                using (var memory = new MemoryStream())
                {
                    using (var writer = new BinaryWriter(memory, System.Text.Encoding.UTF8, true))
                    {
                        writer.Write(PaintMagic);
                        writer.Write(FormatVersion);
                        writer.Write(size);
                    }

                    using (var deflate = new DeflateStream(memory, System.IO.Compression.CompressionLevel.Fastest, true))
                        deflate.Write(raw, 0, raw.Length);

                    entry.paintFile = $"skin_{entry.id}.fhpaint";
                    entry.textureSize = size;
                    entry.paintedTexels = painted;
                    WriteAtomic(Path.Combine(Folder, entry.paintFile), memory.ToArray());
                }

                return painted;
            }
            catch (Exception e)
            {
                Debug.LogError($"[CanSkinStore] could not save '{entry?.name}': {e.Message}");
                return -1;
            }
        }

        /// <summary>Lays a saved can's paint over basePixels into output (all the same size). False if it cannot be read.</summary>
        public static bool ReadPaint(CanSkinEntry entry, Color32[] basePixels, Color32[] output, int size)
        {
            try
            {
                var paintPath = Path.Combine(Folder, entry.paintFile);
                SafeFile.Recover(paintPath);
                var bytes = File.ReadAllBytes(paintPath);
                using (var memory = new MemoryStream(bytes))
                using (var reader = new BinaryReader(memory))
                {
                    if (reader.ReadUInt32() != PaintMagic)
                        return false;
                    var version = reader.ReadInt32();
                    var storedSize = reader.ReadInt32();
                    if (version > FormatVersion || storedSize <= 0 || storedSize > 4096)
                    {
                        Debug.LogWarning($"[CanSkinStore] '{entry.name}': paint format v{version} {storedSize}px, this build reads up to v{FormatVersion}.");
                        return false;
                    }

                    var raw = new byte[storedSize * storedSize * 4];
                    using (var deflate = new DeflateStream(memory, CompressionMode.Decompress))
                    {
                        var read = 0;
                        while (read < raw.Length)
                        {
                            var n = deflate.Read(raw, read, raw.Length - read);
                            if (n <= 0)
                                break;
                            read += n;
                        }
                    }

                    // Saved at another resolution (an older or newer build): nearest texel.
                    for (var y = 0; y < size; y++)
                    {
                        var sy = y * storedSize / size;
                        for (var x = 0; x < size; x++)
                        {
                            var j = (sy * storedSize + x * storedSize / size) * 4;
                            var i = y * size + x;
                            output[i] = raw[j + 3] != 0 ? new Color32(raw[j], raw[j + 1], raw[j + 2], 255) : basePixels[i];
                        }
                    }
                }

                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CanSkinStore] could not read '{entry?.name}': {e.Message}");
                return false;
            }
        }

        public static void DeletePaint(CanSkinEntry entry)
        {
            try
            {
                SafeFile.Delete(Path.Combine(Folder, entry.paintFile ?? ""));
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[CanSkinStore] could not delete '{entry?.name}': {e.Message}");
            }
        }

        static void WriteAtomic(string path, byte[] bytes) => SafeFile.Write(path, bytes);
    }
}
