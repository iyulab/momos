using System.ComponentModel.DataAnnotations;

namespace Momos.Host.Endpoints;

public sealed class WorkerAuthOptions
{
    public const string SectionName = "Momos:Host:WorkerAuth";

    /// <summary>
    /// Shared secret every Momos.Worker sends as <c>Authorization: Bearer {ApiKey}</c> on the
    /// claim-next/report/fail endpoints. One value for all workers rather than a key per
    /// worker — simplest option for a small worker fleet; per-worker identity can be layered
    /// on later without changing this header-based wire shape.
    /// </summary>
    [Required(ErrorMessage = "Momos:Host:WorkerAuth:ApiKey가 비어 있습니다 — Momos__Host__WorkerAuth__ApiKey 환경 변수로 주입하세요(README.md '설정' 참고).")]
    public required string ApiKey { get; set; }
}
