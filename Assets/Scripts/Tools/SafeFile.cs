using System;
using System.IO;
using UnityEngine;

namespace Freehand.Tools
{
    /// <summary>
    /// V0.15: writing the player's saves so that no interruption (power off, crash, a full disk mid-write) can leave
    /// them with neither the old version nor the new one, and setting files aside instead of deleting them.
    ///
    ///   Write:   the new bytes go to "file.tmp"; the old file becomes "file.bak"; the .tmp becomes the file; the
    ///            .bak is removed. At every moment one complete version exists on disk.
    ///   Recover: the file is missing but "file.bak" is there (interrupted between the two moves): the previous
    ///            complete version is put back.
    ///   SetAside: the file is renamed with a label and a timestamp - kept, never overwritten.
    /// </summary>
    public static class SafeFile
    {
        public static void Write(string path, byte[] bytes)
        {
            var temporary = path + ".tmp";
            var backup = path + ".bak";
            File.WriteAllBytes(temporary, bytes);
            if (File.Exists(path))
            {
                if (File.Exists(backup))
                    File.Delete(backup);
                File.Move(path, backup);
            }

            File.Move(temporary, path);
            if (File.Exists(backup))
                File.Delete(backup);
        }

        /// <summary>True if `path` exists (after putting its previous copy back when only that was left).</summary>
        public static bool Recover(string path)
        {
            if (File.Exists(path))
                return true;

            var backup = path + ".bak";
            if (!File.Exists(backup))
                return false;

            try
            {
                File.Move(backup, path);
                Debug.LogWarning($"[SafeFile] '{Path.GetFileName(path)}' was missing (an interrupted save): its previous copy was restored.");
                return true;
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[SafeFile] could not restore '{Path.GetFileName(path)}': {e.Message}");
                return false;
            }
        }

        /// <summary>Renames `path` to "path.label-yyyyMMddHHmmssfff" and returns the new path (null if there was nothing to move).</summary>
        public static string SetAside(string path, string label)
        {
            if (!File.Exists(path))
                return null;

            var aside = $"{path}.{label}-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            File.Move(path, aside);
            return aside;
        }

        /// <summary>Removes a file and the leftovers of its safe writes.</summary>
        public static void Delete(string path)
        {
            foreach (var candidate in new[] { path, path + ".tmp", path + ".bak" })
            {
                if (File.Exists(candidate))
                    File.Delete(candidate);
            }
        }
    }
}
