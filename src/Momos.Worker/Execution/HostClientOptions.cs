using System.ComponentModel.DataAnnotations;

namespace Momos.Worker.Execution;

public sealed class HostClientOptions
{
    public const string SectionName = "Momos:Worker:Host";

    [Required(ErrorMessage = "Momos:Worker:Host:BaseUrl이 비어 있습니다 — Momos__Worker__Host__BaseUrl 환경 변수로 주입하세요(README.md '설정' 참고).")]
    public required string BaseUrl { get; set; }

    /// <summary>
    /// Sent as <c>Authorization: Bearer {ApiKey}</c> on every Host request — must match the
    /// Host's own configured worker key or claim-next/report/fail reject with 401.
    /// </summary>
    [Required(ErrorMessage = "Momos:Worker:Host:ApiKey가 비어 있습니다 — Momos__Worker__Host__ApiKey 환경 변수로 주입하세요(README.md '설정' 참고).")]
    public required string ApiKey { get; set; }
}
