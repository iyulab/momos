using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Momos.Worker.Execution;
using Momos.Worker.SelfUpdate;

namespace Momos.Worker.Tests.SelfUpdate;

public sealed class WorkerSelfUpdaterTests : IDisposable
{
    private readonly string _installRoot = Directory.CreateTempSubdirectory("momos-worker-selfupdate-test").FullName;

    public void Dispose() => Directory.Delete(_installRoot, recursive: true);

    private static byte[] BuildZipAsset(string entryContent)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("Momos.Worker.exe");
            using var writer = new StreamWriter(entry.Open());
            writer.Write(entryContent);
        }
        return ms.ToArray();
    }

    /// <summary>Routes GitHub Releases API + asset download URLs to canned in-memory responses.</summary>
    private sealed class FakeGitHubHandler(string tagName, byte[] zipBytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/releases", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse($$"""[{"tag_name":"{{tagName}}","assets":[{"name":"momos-worker-win-x64.zip","browser_download_url":"https://assets.invalid/archive.zip"},{"name":"momos-worker-win-x64.zip.sha256","browser_download_url":"https://assets.invalid/archive.zip.sha256"}]}]"""));
            }
            if (path.Contains("/releases/tags/", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse($$"""{"tag_name":"{{tagName}}","assets":[{"name":"momos-worker-win-x64.zip","browser_download_url":"https://assets.invalid/archive.zip"},{"name":"momos-worker-win-x64.zip.sha256","browser_download_url":"https://assets.invalid/archive.zip.sha256"}]}"""));
            }
            if (path.EndsWith("/archive.zip", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zipBytes) });
            }
            if (path.EndsWith("/archive.zip.sha256", StringComparison.Ordinal))
            {
                var hash = Convert.ToHexString(SHA256.HashData(zipBytes)).ToLowerInvariant();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($"{hash}  archive.zip") });
            }
            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }

        private static HttpResponseMessage JsonResponse(string json) =>
            new(HttpStatusCode.OK) { Content = JsonContent.Create(System.Text.Json.JsonDocument.Parse(json).RootElement) };
    }

    [Fact]
    public async Task StageUpdateAsync_WithValidChecksum_SwapsCurrentPointerAndReSwapsOnASubsequentUpdate()
    {
        var updater1 = new WorkerSelfUpdater(
            new HttpClient(new FakeGitHubHandler("worker-v0.2.0", BuildZipAsset("v0.2.0 binary"))),
            Options.Create(new WorkerSelfUpdateOptions { Repo = "iyulab/momos", InstallRoot = _installRoot, Rid = "win-x64" }),
            NullLogger<WorkerSelfUpdater>.Instance);

        await updater1.StageUpdateAsync("0.2.0", CancellationToken.None);

        await AssertCurrentResolvesToAsync("0.2.0", "v0.2.0 binary");

        // Re-swap onto a second, newer version — Directory.CreateSymbolicLink always creates a
        // directory-type reparse point, so "current" always reports Directory.Exists == true;
        // deleting it with File.Delete (rather than Directory.Delete) throws
        // UnauthorizedAccessException, which would make every update after the first fail
        // forever. That regression is exactly what this second stage call catches.
        var updater2 = new WorkerSelfUpdater(
            new HttpClient(new FakeGitHubHandler("worker-v0.3.0", BuildZipAsset("v0.3.0 binary"))),
            Options.Create(new WorkerSelfUpdateOptions { Repo = "iyulab/momos", InstallRoot = _installRoot, Rid = "win-x64" }),
            NullLogger<WorkerSelfUpdater>.Instance);

        await updater2.StageUpdateAsync("0.3.0", CancellationToken.None);

        await AssertCurrentResolvesToAsync("0.3.0", "v0.3.0 binary");
    }

    /// <summary>Verifies "current" is actually a symlink pointing at installs/&lt;version&gt;
    /// (not just that some path exists somewhere), and that reading through it reaches the
    /// staged file with the expected content.</summary>
    private async Task AssertCurrentResolvesToAsync(string version, string expectedContent)
    {
        var currentLink = Path.Combine(_installRoot, "current");
        var expectedTarget = Path.Combine(_installRoot, "installs", version);

        var resolved = Directory.ResolveLinkTarget(currentLink, returnFinalTarget: true);
        Assert.NotNull(resolved);
        Assert.Equal(
            Path.GetFullPath(expectedTarget).TrimEnd(Path.DirectorySeparatorChar),
            resolved!.FullName.TrimEnd(Path.DirectorySeparatorChar));

        var resolvedFile = Path.Combine(currentLink, "Momos.Worker.exe");
        Assert.Equal(expectedContent, await File.ReadAllTextAsync(resolvedFile));
    }

    [Fact]
    public async Task StageUpdateAsync_WithChecksumMismatch_LeavesNoInstallsDirAndThrows()
    {
        var zipBytes = BuildZipAsset("corrupted");
        // handler가 돌려주는 sha256은 실제 zipBytes 해시와 다르게 만든다 — 의도적 불일치.
        var handler = new TamperedChecksumHandler(zipBytes);
        var httpClient = new HttpClient(handler);
        var updater = new WorkerSelfUpdater(
            httpClient,
            Options.Create(new WorkerSelfUpdateOptions { Repo = "iyulab/momos", InstallRoot = _installRoot, Rid = "win-x64" }),
            NullLogger<WorkerSelfUpdater>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => updater.StageUpdateAsync("0.2.0", CancellationToken.None));

        Assert.False(Directory.Exists(Path.Combine(_installRoot, "installs", "0.2.0")));
    }

    private sealed class TamperedChecksumHandler(byte[] zipBytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/releases/tags/", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(System.Text.Json.JsonDocument.Parse($$"""{"tag_name":"worker-v0.2.0","assets":[{"name":"momos-worker-win-x64.zip","browser_download_url":"https://assets.invalid/archive.zip"},{"name":"momos-worker-win-x64.zip.sha256","browser_download_url":"https://assets.invalid/archive.zip.sha256"}]}""").RootElement),
                });
            }
            if (path.EndsWith("/archive.zip", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zipBytes) });
            }
            if (path.EndsWith("/archive.zip.sha256", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("0000000000000000000000000000000000000000000000000000000000000000  archive.zip"),
                });
            }
            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }
    }
}
