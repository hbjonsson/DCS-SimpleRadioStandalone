using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Ciribob.DCS.SimpleRadio.Standalone.Common.Helpers;

/// <summary>A published 455th fork release with its MSI installer.</summary>
public sealed record ForkRelease(
    string Tag,
    Version BaseVersion,
    int Build,
    bool Prerelease,
    string Name,
    string Notes,
    string PageUrl,
    string MsiUrl,
    string MsiSha256,
    long MsiSize)
{
    public string DisplayVersion => $"{BaseVersion}+{Build}";

    public bool IsNewerThan(Version baseVersion, int build)
        => BaseVersion > baseVersion || (BaseVersion == baseVersion && Build > build);
}

/// <summary>
/// Reads the fork's GitHub releases (tags <c>455th-v{base}+{build}</c>, one MSI asset each) and downloads
/// an installer, checking it against the SHA-256 digest GitHub publishes for the asset.
/// </summary>
public static class ForkUpdates
{
    private static readonly Regex TagPattern = new(@"^455th-v(?<base>\d+(\.\d+){1,3})\+(?<build>\d+)$",
        RegexOptions.CultureInvariant);

    public static bool TryParseTag(string tag, out Version baseVersion, out int build)
    {
        baseVersion = null;
        build = 0;
        var match = TagPattern.Match(tag ?? string.Empty);
        return match.Success
               && Version.TryParse(match.Groups["base"].Value, out baseVersion)
               && int.TryParse(match.Groups["build"].Value, out build);
    }

    /// <summary>Parses the GitHub "list releases" JSON. Drafts, unknown tags and releases without an MSI are left out.</summary>
    public static List<ForkRelease> ParseReleases(string json)
    {
        var releases = new List<ForkRelease>();
        using var doc = JsonDocument.Parse(json);
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (Bool(release, "draft"))
                continue;
            var tag = Str(release, "tag_name");
            if (!TryParseTag(tag, out var baseVersion, out var build))
                continue;
            if (!release.TryGetProperty("assets", out var assets))
                continue;

            var msi = assets.EnumerateArray()
                .FirstOrDefault(a => Str(a, "name").EndsWith(".msi", StringComparison.OrdinalIgnoreCase));
            if (msi.ValueKind != JsonValueKind.Object)
                continue;

            var digest = Str(msi, "digest");
            releases.Add(new ForkRelease(
                tag, baseVersion, build, Bool(release, "prerelease"), Str(release, "name"), Str(release, "body"),
                Str(release, "html_url"), Str(msi, "browser_download_url"),
                digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null,
                msi.TryGetProperty("size", out var size) ? size.GetInt64() : 0));
        }

        return releases;
    }

    /// <summary>
    /// The newest release above the running version, or null. Prereleases count only on the beta channel;
    /// a skipped tag is offered again only once something newer comes out.
    /// </summary>
    public static ForkRelease SelectUpdate(IEnumerable<ForkRelease> releases, Version currentBase, int currentBuild,
        bool includePrereleases, string skippedTag = null)
    {
        var newest = releases
            .Where(r => includePrereleases || !r.Prerelease)
            .Where(r => r.IsNewerThan(currentBase, currentBuild))
            .OrderByDescending(r => r.BaseVersion)
            .ThenByDescending(r => r.Build)
            .FirstOrDefault();
        return newest != null && newest.Tag == skippedTag ? null : newest;
    }

    public static async Task<List<ForkRelease>> FetchReleasesAsync(HttpClient http, string owner, string repository,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{owner}/{repository}/releases?per_page=30");
        request.Headers.UserAgent.ParseAdd($"{owner}-{repository}-updater");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return ParseReleases(await response.Content.ReadAsStringAsync(cancellationToken));
    }

    /// <summary>
    /// Downloads the release MSI to <paramref name="destination"/> and checks its SHA-256. A missing digest or a
    /// mismatch deletes the file and throws, so an unverified installer is never run.
    /// </summary>
    public static async Task DownloadMsiAsync(HttpClient http, ForkRelease release, string destination,
        IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(release.MsiSha256))
            throw new InvalidDataException($"Release {release.Tag} publishes no SHA-256 for its installer.");

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using (var response = await http.GetAsync(release.MsiUrl, HttpCompletionOption.ResponseHeadersRead,
                   cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? release.MsiSize;
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = File.Create(destination);
            var buffer = new byte[81920];
            long received = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;
                if (total > 0)
                    progress?.Report((double)received / total);
            }
        }

        if (!string.Equals(await Sha256Async(destination, cancellationToken), release.MsiSha256,
                StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(destination);
            throw new InvalidDataException($"Installer for {release.Tag} failed its SHA-256 check.");
        }
    }

    public static async Task<string> Sha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static string Str(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static bool Bool(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
