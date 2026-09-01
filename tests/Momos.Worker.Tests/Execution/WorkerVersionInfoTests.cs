using Momos.Worker.Execution;

namespace Momos.Worker.Tests.Execution;

public sealed class WorkerVersionInfoTests
{
    [Fact]
    public void Version_WithNoInformationalVersionAttribute_FallsBackToDevPlaceholder()
    {
        // 로컬 dotnet build/test는 -p:Version을 안 넘기므로 csproj의 <Version>0.0.0-dev</Version>
        // 기본값이 InformationalVersionAttribute로 그대로 흘러들어간다 — release-worker.yml만
        // -p:Version=<tag>로 이 값을 덮어쓴다.
        Assert.Equal("0.0.0-dev", WorkerVersionInfo.Version);
    }

    [Fact]
    public void ProtocolVersion_IsAPositiveInteger()
    {
        Assert.True(WorkerVersionInfo.ProtocolVersion >= 1);
    }
}
