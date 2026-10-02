using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Nitrox.Launcher.Models.Attributes;
using Nitrox.Launcher.Models.Design;
using Nitrox.Launcher.Models.Utils;
using Nitrox.Model.Core;

namespace Nitrox.Launcher.Models.Services;

[HttpService]
internal sealed class NitroxWebsiteApiService
{
    private const string RELEASES_PATH = "releases?per_page=20";
    private readonly HttpClient httpClient;
    private readonly HttpFileService httpFileService;

    public NitroxWebsiteApiService(HttpClient httpClient, HttpFileService httpFileService)
    {
        this.httpClient = httpClient;
        this.httpFileService = httpFileService;
        httpClient.BaseAddress = new Uri("https://api.github.com/repos/sinaosal/Nitrox-Modification/");
        httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        httpClient.DefaultRequestHeaders.CacheControl = null;
    }

    public async Task<NitroxChangelog[]?> GetChangeLogsAsync(CancellationToken cancellationToken = default)
    {
        GitHubRelease[] releases = await GetReleasesAsync(cancellationToken);
        return releases
            .Where(static release => !release.Draft && release.ParsedVersion != null)
            .Select(static release => new NitroxChangelog(release.TagName, release.PublishedAt.UtcDateTime, MarkdownRichTextConverter.Convert(release.Body ?? "No release notes provided.")))
            .ToArray();
    }

    public async Task<NitroxRelease?> GetNitroxLatestVersionAsync(CancellationToken cancellationToken = default)
    {
        GitHubRelease? release = await GetLatestReleaseAsync(cancellationToken);
        if (release?.ParsedVersion is not { } version)
        {
            return null;
        }

        Dictionary<string, PlatformInfo> platforms = [];
        if (release.CurrentPlatformAsset is { } asset)
        {
            platforms[NitroxEnvironment.PlatformName] = new PlatformInfo
            {
                Architectures = new Dictionary<string, ArchitectureInfo>
                {
                    [NitroxEnvironment.ArchitectureName] = new ArchitectureInfo
                    {
                        DownloadUrl = asset.DownloadUrl,
                        FileSize = asset.Size.ToString(CultureInfo.InvariantCulture),
                        Sha256Hash = asset.Sha256Hash
                    }
                }
            };
        }

        return new NitroxRelease { Version = version, DisplayVersion = release.DisplayVersion, Platforms = platforms };
    }

    /// <summary>
    ///     Gets the latest Nitrox for the platform of the current machine.
    /// </summary>
    public Task<HttpFileService.FileDownloader> GetLatestNitroxAsync(string downloadUrl, CancellationToken cancellationToken) =>
        httpFileService.GetFileAsync(downloadUrl, cancellationToken);

    private async Task<GitHubRelease[]> GetReleasesAsync(CancellationToken cancellationToken) =>
        await httpClient.GetFromJsonAsync<GitHubRelease[]>(RELEASES_PATH, cancellationToken) ?? [];

    private async Task<GitHubRelease?> GetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        GitHubRelease[] releases = await GetReleasesAsync(cancellationToken);
        return releases
            .Where(static release => !release.Draft && release.ParsedVersion != null)
            .OrderByDescending(static release => release.PublishedAt)
            .FirstOrDefault();
    }

    public sealed record GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public required string TagName { get; init; }

        [JsonPropertyName("published_at")]
        public DateTimeOffset PublishedAt { get; init; }

        [JsonPropertyName("draft")]
        public bool Draft { get; init; }

        [JsonPropertyName("body")]
        public string? Body { get; init; }

        [JsonPropertyName("assets")]
        public GitHubReleaseAsset[]? Assets { get; init; }

        [JsonIgnore]
        public Version? ParsedVersion => ParseReleaseVersion(TagName);

        [JsonIgnore]
        public string DisplayVersion => TagName.TrimStart('v', 'V').Split('+')[0];

        [JsonIgnore]
        public GitHubReleaseAsset? CurrentPlatformAsset => OperatingSystem.IsWindows()
            ? Assets?.FirstOrDefault(static asset => asset.Name.EndsWith("Win64.zip", StringComparison.OrdinalIgnoreCase))
                ?? Assets?.FirstOrDefault(static asset => asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            : null;

        public bool IsNewerThan(string currentVersion)
            => IsReleaseVersionNewer(DisplayVersion, currentVersion);

        public static bool IsReleaseVersionNewer(string releaseVersion, string currentVersion)
        {
            Version? releaseNumericVersion = ParseReleaseVersion(releaseVersion);
            Version? installedVersion = ParseReleaseVersion(currentVersion);
            if (releaseNumericVersion == null || installedVersion == null)
            {
                return false;
            }

            int numericComparison = releaseNumericVersion.CompareTo(installedVersion);
            if (numericComparison != 0)
            {
                return numericComparison > 0;
            }

            string releasePrerelease = GetPrerelease(releaseVersion);
            string installedPrerelease = GetPrerelease(currentVersion);
            if (releasePrerelease.Length == 0)
            {
                return installedPrerelease.Length > 0;
            }

            return installedPrerelease.Length == 0 || string.Compare(releasePrerelease, installedPrerelease, StringComparison.OrdinalIgnoreCase) > 0;
        }

        private static Version? ParseReleaseVersion(string tagName)
        {
            string numericVersion = tagName.TrimStart('v', 'V').Split('-', '+')[0];
            if (!Version.TryParse(numericVersion, out Version? parsedVersion) || parsedVersion.Build < 0)
            {
                return null;
            }

            return new Version(parsedVersion.Major, parsedVersion.Minor, parsedVersion.Build, 0);
        }

        private static string GetPrerelease(string version)
        {
            string withoutBuildMetadata = version.Split('+')[0];
            int separatorIndex = withoutBuildMetadata.IndexOf('-');
            return separatorIndex < 0 ? "" : withoutBuildMetadata[(separatorIndex + 1)..];
        }
    }

    public sealed record GitHubReleaseAsset
    {
        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("browser_download_url")]
        public required string DownloadUrl { get; init; }

        [JsonPropertyName("size")]
        public long Size { get; init; }

        [JsonPropertyName("digest")]
        public string? Digest { get; init; }

        [JsonIgnore]
        public string? Sha256Hash => Digest?.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) == true ? Digest[7..] : null;
    }

    public sealed record NitroxRelease
    {
        [JsonPropertyName("version")]
        public required Version Version { get; init; }

        [JsonIgnore]
        public required string DisplayVersion { get; init; }

        public bool IsNewerThan(string currentVersion) => GitHubRelease.IsReleaseVersionNewer(DisplayVersion, currentVersion);

        [JsonPropertyName("platforms")]
        public Dictionary<string, PlatformInfo>? Platforms { get; init; }

        /// <summary>
        ///     Gets the download info for the current platform and architecture.
        /// </summary>
        public ArchitectureInfo? CurrentPlatformInfo
        {
            get
            {
                if (Platforms == null)
                {
                    return null;
                }

                if (Platforms.TryGetValue(NitroxEnvironment.PlatformName, out PlatformInfo? platformInfo) &&
                    platformInfo?.Architectures != null &&
                    platformInfo.Architectures.TryGetValue(NitroxEnvironment.ArchitectureName, out ArchitectureInfo? archInfo))
                {
                    return archInfo;
                }

                return null;
            }
        }
    }

    public sealed record PlatformInfo
    {
        [JsonPropertyName("filesize")]
        public string? FileSize { get; init; }

        [JsonPropertyName("architectures")]
        public Dictionary<string, ArchitectureInfo>? Architectures { get; init; }
    }

    public sealed record ArchitectureInfo
    {
        [JsonPropertyName("url")]
        public required string DownloadUrl { get; init; }

        public string? Md5Hash { get; init; }

        public string? Sha256Hash { get; init; }

        [JsonPropertyName("filesize")]
        public required string FileSize { get; init; }

        public float FileSizeMegaBytes => float.TryParse(FileSize, out float size) ? size : 0;
    }
}
