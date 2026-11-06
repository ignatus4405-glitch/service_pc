using System;

namespace PcCare.Models
{
    public sealed class ReleaseInfo
    {
        public string TagName { get; set; }
        public string Name { get; set; }
        public string ReleaseNotes { get; set; }
        public string ReleasePageUrl { get; set; }
        public string DownloadUrl { get; set; }
        public Version Version { get; set; }

        public bool HasInstallPackage
        {
            get { return !String.IsNullOrWhiteSpace(DownloadUrl); }
        }
    }

    public sealed class UpdateCheckResult
    {
        public ReleaseInfo Release { get; set; }
        public bool IsUpToDate { get; set; }
        public bool NoPublishedReleases { get; set; }
        public string ErrorMessage { get; set; }
    }
}
