using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Momos.Worker.Execution;

namespace Momos.Worker.SelfUpdate;

/// <summary>
/// Applies an update the same way install-worker.sh/.ps1 do — resolve the release, download the
/// asset, verify its sha256, unpack it into its own version directory — but implemented directly
/// against .NET rather than by shelling out to those scripts, so a running Worker never depends on
/// bash or pwsh being present on the machine it was deployed to.
/// </summary>
public sealed class WorkerSelfUpdater(HttpClient httpClient, IOptions<WorkerSelfUpdateOptions> options, ILogger<WorkerSelfUpdater> logger) : IWorkerSelfUpdater
{
    private string? _lastSuppressedNotice;

    public async Task UpdateAsync(string? targetVersion, CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            // Say it, but only when the answer changes. The caller asks again every poll interval,
            // so an unacted-on update would otherwise repeat the same warning every few seconds for
            // as long as the Worker runs — which buries it rather than surfacing it.
            var notice = targetVersion ?? "latest";
            if (_lastSuppressedNotice != notice)
            {
                _lastSuppressedNotice = notice;
                logger.LogWarning(
                    "Worker update to {TargetVersion} is available or required, but self-update is disabled ({SettingKey}:Enabled) — install it manually, or the Worker stays on this version and Host may keep refusing it work",
                    notice,
                    WorkerSelfUpdateOptions.SectionName);
            }

            return;
        }

        // Failures propagate to the caller's poll loop on purpose: it already counts consecutive
        // failed iterations and stops logging each one past a threshold, and handling them here
        // instead would leave that counter at zero and log every retry forever.
        await StageUpdateAsync(targetVersion, cancellationToken);

        logger.LogInformation("Staged Worker update to {TargetVersion} — exiting for the residency mechanism to relaunch", targetVersion);
        Environment.Exit(WorkerExitCodes.UpdateStaged);
    }

    /// <summary>내부 테스트 훅 — 실제 프로세스 종료 없이 스테이징 단계만 검증할 수 있게 분리했다.</summary>
    internal async Task StageUpdateAsync(string? targetVersion, CancellationToken cancellationToken)
    {
        var opts = options.Value;
        var tag = await ResolveReleaseTagAsync(opts.Repo, targetVersion, cancellationToken);
        var (assetUrl, checksumUrl) = await ResolveAssetUrlsAsync(opts.Repo, tag, opts.Rid, cancellationToken);

        var assetBytes = await httpClient.GetByteArrayAsync(assetUrl, cancellationToken);
        var checksumLine = await httpClient.GetStringAsync(checksumUrl, cancellationToken);
        var expectedHash = checksumLine.Split(' ', 2)[0].Trim();
        var actualHash = Convert.ToHexString(SHA256.HashData(assetBytes)).ToLowerInvariant();
        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Checksum mismatch for Worker update asset (tag={tag}) — refusing to stage it.");
        }

        var version = tag.StartsWith("worker-v", StringComparison.Ordinal) ? tag["worker-v".Length..] : tag;
        var versionDir = Path.Combine(opts.InstallRoot, WorkerInstallLayout.VersionsDirectoryName, version);

        // 압축을 임시 디렉터리에 풀고, 성공한 뒤에만 최종 위치로 옮긴다 — 중간에 실패해도
        // installs/<version>/이 반쯤 채워진 채 남지 않게(원자성).
        var stagingDir = Path.Combine(Path.GetTempPath(), $"momos-worker-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);
        try
        {
            await ExtractAsync(assetBytes, opts.Rid, stagingDir, cancellationToken);

            // The install root's settings file is what the new version actually boots from
            // (WorkerInstallLayout.AddInstalledSettings reads it there directly). This copy is
            // belt-and-braces only: it keeps a version directory self-contained enough to launch on
            // its own, e.g. when diagnosing one outside the install layout.
            var persistentConfig = Path.Combine(opts.InstallRoot, WorkerInstallLayout.SettingsFileName);
            if (File.Exists(persistentConfig))
            {
                File.Copy(persistentConfig, Path.Combine(stagingDir, WorkerInstallLayout.SettingsFileName), overwrite: true);
            }

            if (Directory.Exists(versionDir))
            {
                Directory.Delete(versionDir, recursive: true);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(versionDir)!);
            Directory.Move(stagingDir, versionDir);
        }
        catch
        {
            if (Directory.Exists(stagingDir))
            {
                Directory.Delete(stagingDir, recursive: true);
            }
            throw;
        }

        SwapCurrentPointer(opts.InstallRoot, versionDir);
    }

    /// <summary>
    /// Points "current" at <paramref name="versionDir"/>, in an order chosen so that a failure can
    /// never leave the install without a "current" at all — losing it strands the machine with no
    /// launchable Worker and no way back short of a reinstall.
    /// <para>
    /// Creating the link is the one step here that can fail on an otherwise healthy machine: on
    /// Windows <see cref="Directory.CreateSymbolicLink"/> needs a privilege that is not granted by
    /// default (the install script sidesteps it by making a junction, which .NET has no API to
    /// create). So the new link is built under a scratch name first, while the old pointer is still
    /// in place — if that throws, the old version simply keeps running, which is the same fail-safe
    /// posture as refusing to swap on a checksum mismatch.
    /// </para>
    /// </summary>
    private static void SwapCurrentPointer(string installRoot, string versionDir)
    {
        var currentLink = Path.Combine(installRoot, WorkerInstallLayout.PointerDirectoryName);
        var stagedLink = currentLink + ".new";

        // Only ever a leftover link from an interrupted swap, cleared because the creation below
        // refuses an occupied path. Non-recursive, so it removes the reparse point and not the
        // version directory behind it — and it refuses a non-empty directory rather than delete
        // something this code did not put there. A file here is left alone entirely, and fails the
        // creation below instead, which is the safe outcome for a path this code does not own.
        if (Directory.Exists(stagedLink))
        {
            Directory.Delete(stagedLink);
        }

        Directory.CreateSymbolicLink(stagedLink, versionDir);

        try
        {
            if (Directory.Exists(currentLink))
            {
                // A directory-type reparse point reports as a directory, so File.Delete throws
                // UnauthorizedAccessException here; non-recursive Directory.Delete removes the
                // pointer itself and leaves the version directory it referenced alone.
                Directory.Delete(currentLink);
            }

            // A rename, not a second CreateSymbolicLink: the link provably exists now, so putting it
            // in place no longer depends on the privilege that could have failed. Directory.Move
            // refuses an existing destination, which is why the old pointer is removed first — the
            // gap between the two is a metadata-only operation within one directory.
            Directory.Move(stagedLink, currentLink);
        }
        catch
        {
            // Reached only if the rename itself failed, which can leave nothing at "current".
            // Nothing later in the process will retry, so make the one attempt that could restore a
            // launchable install before the failure propagates.
            try
            {
                if (!Directory.Exists(currentLink))
                {
                    Directory.CreateSymbolicLink(currentLink, versionDir);
                }
            }
            catch
            {
                // A failed recovery has nothing to add: rethrowing it here would replace the
                // exception that says what actually went wrong with one about the attempt to undo it.
            }

            throw;
        }
    }

    private async Task<string> ResolveReleaseTagAsync(string repo, string? targetVersion, CancellationToken cancellationToken)
    {
        if (targetVersion is not null)
        {
            return $"worker-v{targetVersion}";
        }

        var releases = await httpClient.GetFromJsonAsync<JsonElement>($"https://api.github.com/repos/{repo}/releases", cancellationToken);
        foreach (var release in releases.EnumerateArray())
        {
            var tagName = release.GetProperty("tag_name").GetString();
            if (tagName is not null && tagName.StartsWith("worker-v", StringComparison.Ordinal))
            {
                return tagName;
            }
        }

        throw new InvalidOperationException($"No worker-v* release found for repo={repo}.");
    }

    private async Task<(string AssetUrl, string ChecksumUrl)> ResolveAssetUrlsAsync(string repo, string tag, string rid, CancellationToken cancellationToken)
    {
        var release = await httpClient.GetFromJsonAsync<JsonElement>($"https://api.github.com/repos/{repo}/releases/tags/{tag}", cancellationToken);
        var assetName = rid == "win-x64" ? $"momos-worker-{rid}.zip" : $"momos-worker-{rid}.tar.gz";

        string? assetUrl = null, checksumUrl = null;
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            var url = asset.GetProperty("browser_download_url").GetString();
            if (name == assetName)
            {
                assetUrl = url;
            }
            else if (name == $"{assetName}.sha256")
            {
                checksumUrl = url;
            }
        }

        if (assetUrl is null || checksumUrl is null)
        {
            throw new InvalidOperationException($"Release '{tag}' is missing asset '{assetName}' or its checksum.");
        }

        return (assetUrl, checksumUrl);
    }

    private static async Task ExtractAsync(byte[] assetBytes, string rid, string destinationDir, CancellationToken cancellationToken)
    {
        using var ms = new MemoryStream(assetBytes);
        if (rid == "win-x64")
        {
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            zip.ExtractToDirectory(destinationDir, overwriteFiles: true);
        }
        else
        {
            await TarFile.ExtractToDirectoryAsync(ms, destinationDir, overwriteFiles: true, cancellationToken);
        }
    }
}

public static class WorkerExitCodes
{
    /// <summary>A clean exit that means "an update was staged, relaunch me" rather than a crash.
    /// Diagnostic only: the residency mechanism is expected to restart the Worker on any exit, so
    /// nothing branches on this value — it exists so an operator reading the logs can tell the two
    /// kinds of exit apart.</summary>
    public const int UpdateStaged = 75;
}
