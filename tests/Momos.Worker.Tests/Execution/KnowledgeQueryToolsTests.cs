using Microsoft.Extensions.Logging.Abstractions;
using Momos.Worker.Execution;

namespace Momos.Worker.Tests.Execution;

public class KnowledgeQueryToolsTests
{
    [Fact]
    public async Task QueryProjectKnowledge_Success_RecordsAToolCallEntry()
    {
        var hostApiClient = new FakeHostApiClient([], queryKnowledgeResult: ["snippet one"]);
        var trace = new ToolCallTraceSink();
        var tools = new KnowledgeQueryTools(hostApiClient, Guid.NewGuid(), trace, NullLogger<KnowledgeQueryTools>.Instance);

        await tools.QueryProjectKnowledge("login flow");

        var entry = Assert.Single(trace.Calls);
        Assert.Equal(nameof(KnowledgeQueryTools.QueryProjectKnowledge), entry.Tool);
        Assert.Equal("login flow", entry.Summary);
        Assert.True(entry.Success);
    }

    [Fact]
    public async Task QueryProjectKnowledge_HostThrows_RecordsAFailedToolCallEntryAndStillThrows()
    {
        var hostApiClient = new FakeHostApiClient([], queryKnowledgeThrows: true);
        var trace = new ToolCallTraceSink();
        var tools = new KnowledgeQueryTools(hostApiClient, Guid.NewGuid(), trace, NullLogger<KnowledgeQueryTools>.Instance);

        await Assert.ThrowsAsync<HttpRequestException>(() => tools.QueryProjectKnowledge("login flow"));

        var entry = Assert.Single(trace.Calls);
        Assert.False(entry.Success);
    }
}
