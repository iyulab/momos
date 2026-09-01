using System.Reflection;

namespace Momos.Worker.Execution;

/// <summary>
/// Worker가 매 claim-next poll마다 Host에 보내는 자기 식별 정보. <see cref="ProtocolVersion"/>은
/// Host↔Worker 통신 계약이 실제로 바뀔 때만 사람이 올리는 값(Worker 릴리스 주기와 독립) —
/// PLAN-momos-20260901-worker-host-update-strategy.md 설계 섹션 1.
/// </summary>
public static class WorkerVersionInfo
{
    public const int ProtocolVersion = 1;

    /// <summary>이 빌드가 대응하는 worker-v* 릴리스 태그(예: "0.1.0"). csproj의 Version 프로퍼티에서
    /// 옴 — release-worker.yml은 `-p:Version=`으로 이를 채우고, 그 값이 없는 로컬 빌드는
    /// csproj 기본값 "0.0.0-dev"로 떨어진다.</summary>
    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0-dev";
}
