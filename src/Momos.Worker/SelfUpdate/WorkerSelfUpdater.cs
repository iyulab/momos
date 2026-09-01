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
/// PLAN-momos-20260901-worker-host-update-strategy.md 설계 섹션 2 구현체. install-worker.sh/.ps1과
/// 같은 프로토콜(GitHub Releases API → 자산 다운로드 → sha256 검증)을 셸아웃 없이 .NET 네이티브로
/// 재구현한다 — bash/pwsh 존재를 전제하는 런타임 의존성을 만들지 않기 위함.
/// </summary>
public sealed class WorkerSelfUpdater(HttpClient httpClient, IOptions<WorkerSelfUpdateOptions> options, ILogger<WorkerSelfUpdater> logger) : IWorkerSelfUpdater
{
    public async Task UpdateAsync(string? targetVersion, CancellationToken cancellationToken)
    {
        try
        {
            await StageUpdateAsync(targetVersion, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // D-51과 같은 태도: self-update 실패로 Worker 프로세스 자체가 죽으면 안 된다 — 실패는
            // 로깅만 하고 다음 poll에서 다시 시도한다(호출부 PullExecutionBackgroundService의
            // 기존 catch 블록이 이 예외를 그대로 삼켜 로그 임계치 패턴을 적용한다).
            logger.LogError(ex, "Self-update to {TargetVersion} failed — will retry on next poll", targetVersion ?? "latest");
            return;
        }

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
        var versionDir = Path.Combine(opts.InstallRoot, "installs", version);

        // 압축을 임시 디렉터리에 풀고, 성공한 뒤에만 최종 위치로 옮긴다 — 중간에 실패해도
        // installs/<version>/이 반쯤 채워진 채 남지 않게(원자성).
        var stagingDir = Path.Combine(Path.GetTempPath(), $"momos-worker-update-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDir);
        try
        {
            await ExtractAsync(assetBytes, opts.Rid, stagingDir, cancellationToken);

            // 지속 설정(appsettings.Production.json)은 InstallRoot에만 있다 — 새 버전 디렉터리에도
            // 복사해야 그 디렉터리를 ContentRootPath로 기동했을 때 그대로 읽힌다(WorkerProgram.cs).
            var persistentConfig = Path.Combine(opts.InstallRoot, "appsettings.Production.json");
            if (File.Exists(persistentConfig))
            {
                File.Copy(persistentConfig, Path.Combine(stagingDir, "appsettings.Production.json"), overwrite: true);
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

    private static void SwapCurrentPointer(string installRoot, string versionDir)
    {
        var currentLink = Path.Combine(installRoot, "current");
        // 검증까지 끝난 뒤에만 여기 도달한다 — 실패 시 포인터를 안 건드려 구버전이 계속
        // 돈다(fail-safe, 스펙 섹션 2).
        if (Directory.Exists(currentLink))
        {
            // Directory.CreateSymbolicLink 아래에서 항상 디렉터리 타입 reparse point를 만드므로
            // "current"는 항상 디렉터리로 보인다(File.Exists는 결코 true가 안 됨) — File.Delete를
            // 쓰면 UnauthorizedAccessException. non-recursive Directory.Delete는 reparse point
            // 자체만 지우고 그 대상 디렉터리(versionDir)는 건드리지 않는다.
            Directory.Delete(currentLink);
        }
        Directory.CreateSymbolicLink(currentLink, versionDir);
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
    /// <summary>정상 종료지만 "크래시가 아니라 업데이트 완료 후 재기동해 달라"는 의도를 로그에서
    /// 구분하기 위한 값 — 상주 메커니즘(systemd/Windows Service)이 재시작 여부를 판단하는 데는
    /// 안 쓰인다(항상 재시작이 기본 정책이므로, 스펙 섹션 2). 값 자체는 진단용.</summary>
    public const int UpdateStaged = 75;
}
