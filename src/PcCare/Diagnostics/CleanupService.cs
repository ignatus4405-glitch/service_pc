using System;
using System.Collections.Generic;
using System.IO;

namespace PcCare.Diagnostics
{
    public sealed class CleanupResult
    {
        public CleanupResult()
        {
            Warnings = new List<string>();
        }

        public long DeletedFiles { get; set; }
        public ulong BytesFreed { get; set; }
        public long SkippedFiles { get; set; }
        public List<string> Warnings { get; private set; }
    }

    /// <summary>
    /// Conservative cleanup limited to the current user's TEMP folder. Only files older
    /// than one day are removed; reparse points, recent files and locked files are skipped.
    /// </summary>
    public sealed class CleanupService
    {
        private const int MaximumDepth = 32;
        private static readonly TimeSpan MinimumFileAge = TimeSpan.FromHours(24);

        public string GetUserTempPath()
        {
            string path = Environment.GetEnvironmentVariable("TEMP");
            if (String.IsNullOrWhiteSpace(path))
                path = Path.GetTempPath();
            return Path.GetFullPath(path);
        }

        public CleanupResult CleanOldTemporaryFiles()
        {
            CleanupResult result = new CleanupResult();
            string root = GetUserTempPath();
            ValidateTempRoot(root);
            if (!Directory.Exists(root))
            {
                result.Warnings.Add("Папка временных файлов не найдена.");
                return result;
            }

            DateTime cutoff = DateTime.Now.Subtract(MinimumFileAge);
            CleanDirectory(root, root, cutoff, 0, result);
            return result;
        }

        private static void ValidateTempRoot(string path)
        {
            string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string driveRoot = (Path.GetPathRoot(Path.GetFullPath(path)) ?? String.Empty)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (String.Equals(fullPath, driveRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Очистка отменена: TEMP указывает на корень диска.");

            string leaf = new DirectoryInfo(fullPath).Name;
            string localTemp = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp");
            bool underStandardTemp = IsSameOrChild(fullPath, localTemp);
            bool namedAsTemp = String.Equals(leaf, "Temp", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(leaf, "Tmp", StringComparison.OrdinalIgnoreCase);
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            bool underUserProfile = !String.IsNullOrWhiteSpace(userProfile) && IsSameOrChild(fullPath, userProfile);
            if (!underStandardTemp && (!namedAsTemp || underUserProfile))
                throw new InvalidOperationException("Очистка отменена: пользовательский TEMP не похож на временную папку.");

            Environment.SpecialFolder[] protectedFolders =
            {
                Environment.SpecialFolder.Windows,
                Environment.SpecialFolder.System,
                Environment.SpecialFolder.ProgramFiles,
                Environment.SpecialFolder.ProgramFilesX86,
                Environment.SpecialFolder.CommonApplicationData,
                Environment.SpecialFolder.MyDocuments,
                Environment.SpecialFolder.Desktop
            };
            foreach (Environment.SpecialFolder folder in protectedFolders)
            {
                string protectedPath = Environment.GetFolderPath(folder);
                if (!String.IsNullOrWhiteSpace(protectedPath) && IsSameOrChild(fullPath, protectedPath))
                    throw new InvalidOperationException("Очистка отменена: TEMP указывает на системную или пользовательскую папку с данными.");
            }
        }

        private static bool IsSameOrChild(string path, string root)
        {
            if (String.IsNullOrWhiteSpace(path) || String.IsNullOrWhiteSpace(root))
                return false;

            string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return String.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase) ||
                fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static void CleanDirectory(string root, string current, DateTime cutoff, int depth, CleanupResult result)
        {
            if (depth > MaximumDepth)
            {
                result.Warnings.Add("Пропущена слишком глубокая вложенная папка.");
                return;
            }

            try
            {
                DirectoryInfo directory = new DirectoryInfo(current);
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    result.SkippedFiles++;
                    return;
                }

                FileInfo[] files = directory.GetFiles();
                foreach (FileInfo file in files)
                {
                    try
                    {
                        string fullPath = Path.GetFullPath(file.FullName);
                        if (!IsWithinRoot(root, fullPath) ||
                            (file.Attributes & FileAttributes.ReparsePoint) != 0 ||
                            file.LastWriteTime > cutoff)
                        {
                            result.SkippedFiles++;
                            continue;
                        }

                        ulong length = file.Length < 0 ? 0UL : (ulong)file.Length;
                        file.Delete();
                        result.BytesFreed += length;
                        result.DeletedFiles++;
                    }
                    catch (Exception)
                    {
                        result.SkippedFiles++;
                    }
                }

                DirectoryInfo[] children = directory.GetDirectories();
                foreach (DirectoryInfo child in children)
                {
                    string fullPath = Path.GetFullPath(child.FullName);
                    if (!IsWithinRoot(root, fullPath))
                    {
                        result.SkippedFiles++;
                        continue;
                    }

                    CleanDirectory(root, fullPath, cutoff, depth + 1, result);
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add("Часть временных файлов пропущена: " + ex.Message);
            }
        }

        private static bool IsWithinRoot(string root, string path)
        {
            string normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string normalizedPath = Path.GetFullPath(path);
            return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                String.Equals(normalizedPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
        }
    }
}
