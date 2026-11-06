using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

namespace PcCare.Updater
{
    /// <summary>
    /// Runs in the user's context, waits for PC Care to exit, replaces the executable,
    /// then starts PC Care again without inheriting an elevated token. If the destination
    /// is protected, only the file-copy phase is retried after an explicit UAC consent.
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            bool copyOnly = HasArgument(args, "--copy-only");
            string releaseUrl = String.Empty;

            try
            {
                string sourcePath = Path.GetFullPath(GetArgument(args, "--source"));
                string targetPath = Path.GetFullPath(GetArgument(args, "--target"));

                if (copyOnly)
                {
                    ReplaceExecutable(sourcePath, targetPath);
                    return 0;
                }

                int parentId = Int32.Parse(GetArgument(args, "--pid"));
                releaseUrl = GetArgument(args, "--release");
                WaitForParent(parentId);

                try
                {
                    ReplaceExecutable(sourcePath, targetPath);
                }
                catch (UnauthorizedAccessException)
                {
                    // Elevate only the small file-copy helper. This coordinator remains
                    // unelevated and therefore relaunches the app with the normal user token.
                    RunElevatedCopy(sourcePath, targetPath);
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = targetPath,
                    UseShellExecute = true
                });
                return 0;
            }
            catch (Exception ex)
            {
                if (!copyOnly)
                {
                    MessageBox.Show(
                        "Не удалось установить обновление. Текущая версия не была намеренно удалена.\r\n\r\n" + ex.Message +
                        (String.IsNullOrWhiteSpace(releaseUrl) ? String.Empty : "\r\n\r\nОткройте страницу релиза и скачайте ZIP вручную."),
                        "PC Care — обновление",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);

                    if (!String.IsNullOrWhiteSpace(releaseUrl))
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo(releaseUrl) { UseShellExecute = true });
                        }
                        catch { }
                    }
                }

                return 1;
            }
        }

        private static void ReplaceExecutable(string sourcePath, string targetPath)
        {
            if (!String.Equals(Path.GetFileName(sourcePath), "PcCare.exe", StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(Path.GetFileName(targetPath), "PcCare.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Помощник обновления может заменять только файл PcCare.exe.");
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException("Файл обновления не найден.", sourcePath);

            string targetDirectory = Path.GetDirectoryName(targetPath);
            if (!Directory.Exists(targetDirectory))
                Directory.CreateDirectory(targetDirectory);

            string stagingPath = targetPath + ".new";
            if (File.Exists(stagingPath))
                File.Delete(stagingPath);
            File.Copy(sourcePath, stagingPath, true);

            if (File.Exists(targetPath))
            {
                try
                {
                    File.Replace(stagingPath, targetPath, null);
                }
                catch (IOException)
                {
                    File.Copy(stagingPath, targetPath, true);
                    File.Delete(stagingPath);
                }
            }
            else
            {
                File.Move(stagingPath, targetPath);
            }
        }

        private static void RunElevatedCopy(string sourcePath, string targetPath)
        {
            string arguments = "--copy-only --source " + QuoteArgument(sourcePath) +
                " --target " + QuoteArgument(targetPath);
            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = Application.ExecutablePath,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas"
            };

            using (Process elevated = Process.Start(startInfo))
            {
                if (elevated == null)
                    throw new InvalidOperationException("Не удалось запустить помощник обновления с правами администратора.");
                if (!elevated.WaitForExit(120000))
                    throw new TimeoutException("Повышенный помощник обновления не завершился за отведённое время.");
                if (elevated.ExitCode != 0)
                    throw new IOException("Повышенный помощник не смог записать обновление (код " + elevated.ExitCode + ").");
            }
        }

        private static void WaitForParent(int processId)
        {
            try
            {
                using (Process parent = Process.GetProcessById(processId))
                {
                    if (!parent.HasExited && !parent.WaitForExit(60000))
                        throw new TimeoutException("Приложение PC Care не завершилось за отведённое время.");
                }
            }
            catch (ArgumentException)
            {
                // The application has already exited.
            }
        }

        private static bool HasArgument(string[] args, string name)
        {
            foreach (string arg in args)
            {
                if (String.Equals(arg, name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string GetArgument(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (String.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            }

            throw new ArgumentException("Не задан обязательный параметр " + name + ".");
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + (value ?? String.Empty).Replace("\"", "\\\"") + "\"";
        }
    }
}
