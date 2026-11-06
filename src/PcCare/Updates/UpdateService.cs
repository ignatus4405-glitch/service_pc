using System;
using System.Collections.Generic;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using PcCare.Models;

namespace PcCare.Updates
{
    /// <summary>
    /// Checks the repository's latest stable GitHub Release and, with the user's approval,
    /// downloads the portable package and starts its small restart helper.
    /// </summary>
    public sealed class UpdateService
    {
        private const string Repository = "ignatus4405-glitch/service_pc";
        private const string LatestReleaseApi = "https://api.github.com/repos/" + Repository + "/releases/latest";
        private const string UserAgent = "PC-Care-Windows/1.0";

        public Version CurrentVersion
        {
            get { return Assembly.GetExecutingAssembly().GetName().Version; }
        }

        public async Task<UpdateCheckResult> CheckAsync()
        {
            return await Task.Run(delegate { return CheckLatestRelease(); });
        }

        private UpdateCheckResult CheckLatestRelease()
        {
            UpdateCheckResult result = new UpdateCheckResult();
            HttpWebRequest request = null;

            try
            {
                // GitHub requires TLS 1.2. The numeric value keeps this build compatible
                // with the .NET Framework 4.5 reference assemblies used for Windows 7/8.
                ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
                request = (HttpWebRequest)WebRequest.Create(LatestReleaseApi);
                request.Method = "GET";
                request.Accept = "application/vnd.github+json";
                request.UserAgent = UserAgent;
                request.Timeout = 10000;
                request.ReadWriteTimeout = 10000;
                request.Headers["X-GitHub-Api-Version"] = "2022-11-28";

                string json;
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                using (Stream stream = response.GetResponseStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                {
                    json = reader.ReadToEnd();
                }

                JavaScriptSerializer serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = 4 * 1024 * 1024;
                IDictionary<string, object> releaseData = serializer.DeserializeObject(json) as IDictionary<string, object>;
                if (releaseData == null)
                    throw new InvalidDataException("GitHub вернул неизвестный формат ответа.");

                string tagName = GetString(releaseData, "tag_name");
                string versionText = tagName.TrimStart('v', 'V');
                Version releaseVersion;
                if (!Version.TryParse(versionText, out releaseVersion))
                    throw new InvalidDataException("Тег версии релиза «" + tagName + "» не соответствует формату X.Y.Z.");

                ReleaseInfo release = new ReleaseInfo();
                release.TagName = tagName;
                release.Name = GetString(releaseData, "name");
                release.ReleaseNotes = GetString(releaseData, "body");
                release.ReleasePageUrl = GetString(releaseData, "html_url");
                release.Version = releaseVersion;
                release.DownloadUrl = FindWindowsPackage(releaseData);
                result.Release = release;
                result.IsUpToDate = CompareThreePartVersions(releaseVersion, CurrentVersion) <= 0;
            }
            catch (WebException ex)
            {
                HttpWebResponse response = ex.Response as HttpWebResponse;
                if (response != null && response.StatusCode == HttpStatusCode.NotFound)
                {
                    result.NoPublishedReleases = true;
                    response.Close();
                }
                else
                {
                    result.ErrorMessage = "Не удалось проверить обновления: " + ex.Message;
                    if (response != null)
                        response.Close();
                }
            }
            catch (Exception ex)
            {
                result.ErrorMessage = "Не удалось проверить обновления: " + ex.Message;
            }

            return result;
        }

        public async Task DownloadAndStartUpdateAsync(ReleaseInfo release, int parentProcessId, string targetExecutable)
        {
            if (release == null)
                throw new ArgumentNullException("release");
            if (!release.HasInstallPackage)
                throw new InvalidOperationException("Для этого релиза не опубликован установочный ZIP-пакет.");
            if (!File.Exists(targetExecutable))
                throw new FileNotFoundException("Не найден исполняемый файл текущей версии.", targetExecutable);

            Uri downloadUri;
            if (!Uri.TryCreate(release.DownloadUrl, UriKind.Absolute, out downloadUri) ||
                !String.Equals(downloadUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Ссылка на пакет обновления должна использовать HTTPS.");

            string updatesRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PcCare", "Updates");
            if (String.IsNullOrWhiteSpace(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)))
                updatesRoot = Path.Combine(Path.GetTempPath(), "PcCare", "Updates");

            string safeTag = Regex.Replace(release.TagName ?? release.Version.ToString(), "[^A-Za-z0-9._-]", "_");
            string workDirectory = Path.Combine(updatesRoot, safeTag);
            if (Directory.Exists(workDirectory))
                Directory.Delete(workDirectory, true);
            Directory.CreateDirectory(workDirectory);

            string zipPath = Path.Combine(workDirectory, "release.zip");
            using (WebClient client = new WebClient())
            {
                client.Headers[HttpRequestHeader.UserAgent] = UserAgent;
                await client.DownloadFileTaskAsync(downloadUri, zipPath);
            }

            string extractedDirectory = Path.Combine(workDirectory, "package");
            Directory.CreateDirectory(extractedDirectory);
            string newExecutable = ExtractRootFile(zipPath, extractedDirectory, "PcCare.exe");
            string updaterExecutable = ExtractRootFile(zipPath, extractedDirectory, "PcCare.Updater.exe");

            string arguments = "--pid " + parentProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " --source " + QuoteArgument(newExecutable) +
                " --target " + QuoteArgument(Path.GetFullPath(targetExecutable)) +
                " --release " + QuoteArgument(release.ReleasePageUrl ?? String.Empty);

            ProcessStartInfo startInfo = new ProcessStartInfo
            {
                FileName = updaterExecutable,
                Arguments = arguments,
                UseShellExecute = true
            };

            Process.Start(startInfo);
        }

        public static string FormatVersion(Version version)
        {
            if (version == null)
                return "неизвестно";
            return version.Major + "." + Math.Max(0, version.Minor) + "." + Math.Max(0, version.Build);
        }

        private static string ExtractRootFile(string zipPath, string destinationDirectory, string expectedName)
        {
            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                ZipArchiveEntry selected = null;
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (String.Equals(entry.FullName, expectedName, StringComparison.Ordinal))
                    {
                        if (selected != null)
                            throw new InvalidDataException("В ZIP-пакете найден дублирующийся файл " + expectedName + ".");
                        selected = entry;
                    }
                }

                if (selected == null)
                    throw new InvalidDataException("В ZIP-пакете отсутствует корневой файл " + expectedName + ".");

                string destination = Path.Combine(destinationDirectory, expectedName);
                using (Stream source = selected.Open())
                using (FileStream target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    source.CopyTo(target);
                return destination;
            }
        }

        private static string FindWindowsPackage(IDictionary<string, object> releaseData)
        {
            object value;
            if (!releaseData.TryGetValue("assets", out value))
                return String.Empty;

            IEnumerable assets = value as IEnumerable;
            if (assets == null)
                return String.Empty;

            foreach (object item in assets)
            {
                IDictionary<string, object> asset = item as IDictionary<string, object>;
                if (asset == null)
                    continue;

                string name = GetString(asset, "name");
                if (name.EndsWith("-windows.zip", StringComparison.OrdinalIgnoreCase))
                    return GetString(asset, "browser_download_url");
            }

            return String.Empty;
        }

        private static string GetString(IDictionary<string, object> data, string key)
        {
            object value;
            if (!data.TryGetValue(key, out value) || value == null)
                return String.Empty;
            return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static int CompareThreePartVersions(Version left, Version right)
        {
            Version normalizedLeft = new Version(left.Major, Math.Max(0, left.Minor), Math.Max(0, left.Build));
            Version normalizedRight = new Version(right.Major, Math.Max(0, right.Minor), Math.Max(0, right.Build));
            return normalizedLeft.CompareTo(normalizedRight);
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + (value ?? String.Empty).Replace("\"", "\\\"") + "\"";
        }
    }
}
