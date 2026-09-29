using Microsoft.Extensions.AI;
using Momos.Worker.Agent;

namespace Momos.Worker.Tests.Agent;

public sealed class AnalysisToolMonitorTests
{
    private static string RunCommand(string command) => $"ran {command}";

    private static string ProposeClaim(int count) => $"Recorded {count}.";

    private static string Explode() => throw new InvalidOperationException("boom");

    private static string Cancel() => throw new OperationCanceledException();

    private static (AIFunction Read, AIFunction Propose) Tools(AnalysisToolMonitor monitor)
    {
        var tools = monitor.Watch(
        [
            AIFunctionFactory.Create(RunCommand, "RunCommand"),
            AIFunctionFactory.Create(ProposeClaim, "ProposeClaim"),
        ]);
        return (tools[0], tools[1]);
    }

    private static async Task<string> Text(AIFunction tool, AIFunctionArguments args) =>
        (await tool.InvokeAsync(args)) switch
        {
            string s => s,
            System.Text.Json.JsonElement e => e.GetString()!,
            var other => other?.ToString() ?? string.Empty,
        };

    private static AIFunctionArguments Read(string command) => new() { ["command"] = command };

    [Fact]
    public async Task EveryCall_IsCountedByTool()
    {
        var monitor = new AnalysisToolMonitor(readsBeforeNudge: 10);
        var (read, propose) = Tools(monitor);

        await Text(read, Read("ls"));
        await Text(read, Read("cat"));
        await Text(propose, new() { ["count"] = 1 });

        Assert.Equal(2, monitor.CallsTo("RunCommand"));
        Assert.Equal(1, monitor.CallsTo("ProposeClaim"));
        Assert.Equal("ProposeClaim 1, RunCommand 2", monitor.Summary());
    }

    [Fact]
    public async Task ACallWhoseArgumentsCannotBeBound_IsAnsweredWithWhy_AndCountedAsFailed()
    {
        var monitor = new AnalysisToolMonitor(readsBeforeNudge: 10);
        var (_, propose) = Tools(monitor);

        var answer = await Text(propose, new() { ["count"] = "not a number" });

        Assert.StartsWith("Rejected: this call could not be carried out (", answer, StringComparison.Ordinal);
        Assert.Equal(1, monitor.FailedCallsTo("ProposeClaim"));
        Assert.Equal("ProposeClaim 1 (1 failed)", monitor.Summary());
    }

    [Fact]
    public async Task AToolThatThrows_IsAnsweredInsteadOfFailingTheTurn()
    {
        var monitor = new AnalysisToolMonitor(readsBeforeNudge: 10);
        var tool = Assert.Single(monitor.Watch([AIFunctionFactory.Create(Explode, "Explode")]));

        var answer = await Text(tool, []);

        Assert.Contains("InvalidOperationException", answer, StringComparison.Ordinal);
        Assert.Equal(1, monitor.Failed);
    }

    [Fact]
    public async Task ACancelledCall_StillCancels()
    {
        var monitor = new AnalysisToolMonitor(readsBeforeNudge: 10);
        var tool = Assert.Single(monitor.Watch([AIFunctionFactory.Create(Cancel, "Slow")]));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tool.InvokeAsync([]).AsTask());
    }

    [Fact]
    public async Task ReadsInARow_GetAReminderToPropose_AndAProposalResetsTheCount()
    {
        var monitor = new AnalysisToolMonitor(readsBeforeNudge: 3);
        var (read, propose) = Tools(monitor);

        Assert.Equal("ran a", await Text(read, Read("a")));
        Assert.Equal("ran b", await Text(read, Read("b")));
        var third = await Text(read, Read("c"));
        Assert.StartsWith("ran c", third, StringComparison.Ordinal);
        Assert.Contains("3 reads in a row without a proposal", third, StringComparison.Ordinal);

        Assert.Equal("ran d", await Text(read, Read("d"))); // the reminder restarts the count
        await Text(propose, new() { ["count"] = 1 });
        Assert.Equal("ran e", await Text(read, Read("e")));
        Assert.Equal("ran f", await Text(read, Read("f")));
        Assert.Contains("without a proposal", await Text(read, Read("g")), StringComparison.Ordinal);
    }
}
