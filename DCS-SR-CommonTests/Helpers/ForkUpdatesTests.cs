using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Ciribob.DCS.SimpleRadio.Standalone.Common.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Ciribob.DCS.SimpleRadio.Standalone.Common.Tests.Helpers;

[TestClass]
public class ForkUpdatesTests
{
    private static readonly Version Base = new(2, 4, 1, 0);

    private static ForkRelease Release(int build, bool prerelease = false, string baseVersion = "2.4.1.0")
        => new($"455th-v{baseVersion}+{build}", Version.Parse(baseVersion), build, prerelease, $"+{build}", "",
            "https://example/page", "https://example/x.msi", "00", 1);

    [TestMethod]
    public void TryParseTag_ReadsBaseAndBuild()
    {
        Assert.IsTrue(ForkUpdates.TryParseTag("455th-v2.4.1.0+12", out var baseVersion, out var build));
        Assert.AreEqual(Base, baseVersion);
        Assert.AreEqual(12, build);

        Assert.IsFalse(ForkUpdates.TryParseTag("2.4.1.0", out _, out _));
        Assert.IsFalse(ForkUpdates.TryParseTag("v2.4.1.0", out _, out _));
        Assert.IsFalse(ForkUpdates.TryParseTag("455th-v2.4.1.0", out _, out _));
        Assert.IsFalse(ForkUpdates.TryParseTag("455th-v2.4.1.0+x", out _, out _));
        Assert.IsFalse(ForkUpdates.TryParseTag(null, out _, out _));
    }

    [TestMethod]
    public void SelectUpdate_PicksNewestBuildNumerically()
    {
        var update = ForkUpdates.SelectUpdate(new[] { Release(9), Release(12), Release(10) }, Base, 6, false);
        Assert.AreEqual(12, update.Build);
    }

    [TestMethod]
    public void SelectUpdate_NothingWhenCurrentIsNewest()
    {
        Assert.IsNull(ForkUpdates.SelectUpdate(new[] { Release(5), Release(6) }, Base, 6, false));
    }

    [TestMethod]
    public void SelectUpdate_NewerBaseVersionWinsOverHigherBuild()
    {
        var update = ForkUpdates.SelectUpdate(new[] { Release(20), Release(1, baseVersion: "2.4.2.0") }, Base, 6, false);
        Assert.AreEqual(new Version(2, 4, 2, 0), update.BaseVersion);
    }

    [TestMethod]
    public void SelectUpdate_PrereleasesOnlyOnTheBetaChannel()
    {
        var releases = new[] { Release(7), Release(8, prerelease: true) };
        Assert.AreEqual(7, ForkUpdates.SelectUpdate(releases, Base, 6, includePrereleases: false).Build);
        Assert.AreEqual(8, ForkUpdates.SelectUpdate(releases, Base, 6, includePrereleases: true).Build);
    }

    [TestMethod]
    public void SelectUpdate_SkippedVersionIsOfferedAgainOnlyWhenSomethingNewerShips()
    {
        Assert.IsNull(ForkUpdates.SelectUpdate(new[] { Release(7) }, Base, 6, false, "455th-v2.4.1.0+7"));
        Assert.AreEqual(8,
            ForkUpdates.SelectUpdate(new[] { Release(7), Release(8) }, Base, 6, false, "455th-v2.4.1.0+7").Build);
    }

    [TestMethod]
    public void ParseReleases_ReadsMsiAndDigestAndDropsDraftsAndForeignTags()
    {
        const string json = """
        [
          { "tag_name": "455th-v2.4.1.0+7", "draft": false, "prerelease": false, "name": "+7", "body": "notes",
            "html_url": "https://github.com/x/y/releases/tag/455th-v2.4.1.0%2B7",
            "assets": [
              { "name": "455th-SRS-2.4.1.0-win-x64.zip", "browser_download_url": "https://z", "size": 2, "digest": "sha256:aa" },
              { "name": "455th-SRS-2.4.1.0-win-x64.msi", "browser_download_url": "https://m", "size": 3, "digest": "sha256:ABCDEF" }
            ] },
          { "tag_name": "455th-v2.4.1.0+8", "draft": true, "prerelease": false, "assets": [
              { "name": "a.msi", "browser_download_url": "https://m", "size": 3 } ] },
          { "tag_name": "2.4.2.0", "draft": false, "prerelease": false, "assets": [
              { "name": "a.msi", "browser_download_url": "https://m", "size": 3 } ] },
          { "tag_name": "455th-v2.4.1.0+9", "draft": false, "prerelease": true, "assets": [] }
        ]
        """;

        var releases = ForkUpdates.ParseReleases(json);

        Assert.AreEqual(1, releases.Count);
        var release = releases.Single();
        Assert.AreEqual(7, release.Build);
        Assert.AreEqual("https://m", release.MsiUrl);
        Assert.AreEqual("abcdef", release.MsiSha256);
        Assert.AreEqual(3, release.MsiSize);
        Assert.AreEqual("notes", release.Notes);
    }

    [TestMethod]
    public async Task DownloadMsiAsync_KeepsAVerifiedInstaller()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var path = Path.Combine(Path.GetTempPath(), $"fork-update-test-{Guid.NewGuid()}", "a.msi");
        var release = Release(7) with { MsiSha256 = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant() };

        await ForkUpdates.DownloadMsiAsync(new HttpClient(new FixedHandler(payload)), release, path, null,
            CancellationToken.None);

        CollectionAssert.AreEqual(payload, File.ReadAllBytes(path));
        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    [TestMethod]
    public async Task DownloadMsiAsync_DeletesAndRefusesAMismatchedOrUnverifiableInstaller()
    {
        var path = Path.Combine(Path.GetTempPath(), $"fork-update-test-{Guid.NewGuid()}", "a.msi");
        var http = new HttpClient(new FixedHandler(new byte[] { 9, 9, 9 }));

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            ForkUpdates.DownloadMsiAsync(http, Release(7) with { MsiSha256 = "00" }, path, null, CancellationToken.None));
        Assert.IsFalse(File.Exists(path));

        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            ForkUpdates.DownloadMsiAsync(http, Release(7) with { MsiSha256 = null }, path, null, CancellationToken.None));
        Assert.IsFalse(File.Exists(path));

        Directory.Delete(Path.GetDirectoryName(path)!, true);
    }

    private sealed class FixedHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
    }
}
