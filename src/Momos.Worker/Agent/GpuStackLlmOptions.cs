using System.ComponentModel.DataAnnotations;

namespace Momos.Worker.Agent;

public sealed class GpuStackLlmOptions
{
    public const string SectionName = "Momos:Llm:GpuStack";

    [Required(ErrorMessage = "Momos:Llm:GpuStack:Endpoint가 비어 있습니다 — Momos__Llm__GpuStack__Endpoint 환경 변수로 주입하세요(README.md '설정' 참고).")]
    public required string Endpoint { get; set; }

    [Required(ErrorMessage = "Momos:Llm:GpuStack:ApiKey가 비어 있습니다 — Momos__Llm__GpuStack__ApiKey 환경 변수로 주입하세요(README.md '설정' 참고).")]
    public required string ApiKey { get; set; }

    [Required(ErrorMessage = "Momos:Llm:GpuStack:Model이 비어 있습니다 — Momos__Llm__GpuStack__Model 환경 변수로 주입하세요(README.md '설정' 참고).")]
    public required string Model { get; set; }
}
