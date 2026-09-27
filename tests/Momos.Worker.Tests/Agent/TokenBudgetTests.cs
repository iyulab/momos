using IronHive.Agent.Tracking;
using Microsoft.Extensions.AI;
using Momos.Worker.Agent;

namespace Momos.Worker.Tests.Agent;

public sealed class TokenBudgetTests
{
    private static ChatResponse Reply(long tokens) =>
        new(new ChatMessage(ChatRole.Assistant, "ok")) { Usage = new UsageDetails { TotalTokenCount = tokens } };

    private static UsageLimitingChatClient Client(params ChatResponse[] replies) =>
        new(new FakeChatClient(replies[..^1], replies[^1]),
            new UsageLimiter(new UsageLimitsConfig { MaxSessionTokens = int.MaxValue, StopOnLimit = true }));

    [Fact]
    public async Task OutsideAnyScope_CallsGoThroughWithNoBudgetCurrent()
    {
        var client = Client(Reply(500), Reply(500));

        await client.GetResponseAsync("a");
        await client.GetResponseAsync("b");

        Assert.Null(TokenBudget.Current);
    }

    [Fact]
    public async Task InsideAScope_UsageIsChargedToItAndItsParent()
    {
        var total = new TokenBudget(1_000);
        var chapter = new TokenBudget(600, total);
        var client = Client(Reply(250));

        using (TokenBudget.Enter(chapter))
        {
            await client.GetResponseAsync("a");
        }

        Assert.Equal(250, chapter.Used);
        Assert.Equal(250, total.Used);
        Assert.Null(TokenBudget.Current);
    }

    [Fact]
    public async Task AnExhaustedScope_StopsTheNextCallBeforeItReachesTheModel()
    {
        var chapter = new TokenBudget(100);
        var client = Client(Reply(150), Reply(1));

        using (TokenBudget.Enter(chapter))
        {
            await client.GetResponseAsync("first");
            await Assert.ThrowsAsync<UsageLimitExceededException>(() => client.GetResponseAsync("second"));
        }
    }

    [Fact]
    public async Task ABudgetSpentAcrossScopes_StopsTheNextCall()
    {
        var total = new TokenBudget(300);
        var client = Client(Reply(200), Reply(200), Reply(1));

        using (TokenBudget.Enter(new TokenBudget(1_000, total)))
        {
            await client.GetResponseAsync("chapter one");
        }

        using (TokenBudget.Enter(new TokenBudget(1_000, total)))
        {
            await client.GetResponseAsync("chapter two");
            await Assert.ThrowsAsync<UsageLimitExceededException>(() => client.GetResponseAsync("chapter two, again"));
        }

        Assert.True(total.IsExhausted);
    }

    [Fact]
    public void ANonPositiveLimit_IsRefused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new TokenBudget(0));
}
