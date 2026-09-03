using System.Reflection;

namespace Momos.Worker.Execution;

/// <summary>
/// What the Worker tells Host about itself on every claim-next poll. <see cref="ProtocolVersion"/>
/// is bumped by hand, and only when the Host↔Worker request/response contract actually changes —
/// keeping it separate from <see cref="Version"/> is what lets Host ship unrelated changes without
/// forcing every Worker to update.
/// </summary>
public static class WorkerVersionInfo
{
    public const int ProtocolVersion = 2;

    /// <summary>이 빌드가 대응하는 worker-v* 릴리스 태그(예: "0.1.0"). csproj의 Version 프로퍼티에서
    /// 옴 — release-worker.yml은 `-p:Version=`으로 이를 채우고, 그 값이 없는 로컬 빌드는
    /// csproj 기본값 "0.0.0-dev"로 떨어진다.</summary>
    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0-dev";
}
