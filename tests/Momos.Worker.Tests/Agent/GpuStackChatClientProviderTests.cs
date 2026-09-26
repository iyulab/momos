using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Momos.Worker.Agent;

namespace Momos.Worker.Tests.Agent;

public class GpuStackChatClientProviderTests
{
    private static GpuStackLlmOptions ValidOptions() => new()
    {
        Endpoint = "http://gpustack.example.internal:9443",
        ApiKey = "gpustack-test-key",
        Model = "qwen2.5-coder",
    };

    [Fact]
    public async Task GetChatClientAsync_WithMissingEndpoint_ThrowsInvalidOperationException()
    {
        var options = ValidOptions();
        options.Endpoint = string.Empty;
        var provider = new GpuStackChatClientProvider(Options.Create(options));

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetChatClientAsync());
    }

    [Fact]
    public async Task GetChatClientAsync_WithMissingApiKey_ThrowsInvalidOperationException()
    {
        var options = ValidOptions();
        options.ApiKey = string.Empty;
        var provider = new GpuStackChatClientProvider(Options.Create(options));

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetChatClientAsync());
    }

    [Fact]
    public async Task GetChatClientAsync_WithMissingModel_ThrowsInvalidOperationException()
    {
        var options = ValidOptions();
        options.Model = string.Empty;
        var provider = new GpuStackChatClientProvider(Options.Create(options));

        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetChatClientAsync());
    }

    [Fact]
    public async Task GetChatClientAsync_WithValidConfig_ReturnsChatClient()
    {
        // No network call happens here — this only proves the IronHive.Core →
        // ChatClientAdapter wiring assembles correctly.
        var provider = new GpuStackChatClientProvider(Options.Create(ValidOptions()));

        var chatClient = await provider.GetChatClientAsync();

        Assert.NotNull(chatClient);
    }

    [Fact]
    public async Task GetResponseAsync_WithReasoningNone_SendsAnExplicitReasoningOffOnTheWire()
    {
        // The Worker pins ChatOptions.Reasoning to None (MomosAgentLoopFactoryTests). This pins
        // the other half: what that becomes in the request body. A provider update once changed
        // "unset" from off to on without a compile error — the wire is the only place it shows.
        var port = GetFreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();
        var captured = CaptureOneRequestAsync(listener);

        var options = ValidOptions();
        options.Endpoint = $"http://localhost:{port}";
        await using var provider = new GpuStackChatClientProvider(Options.Create(options));
        var chatClient = await provider.GetChatClientAsync();
        await chatClient.GetResponseAsync(
            "hello",
            new ChatOptions { Reasoning = new ReasoningOptions { Effort = ReasoningEffort.None } });

        var body = await captured;
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        Assert.Equal("none", root.GetProperty("reasoning_effort").GetString());
        Assert.Equal(0, root.GetProperty("thinking_token_budget").GetInt32());
        Assert.False(root.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
    }

    private static int GetFreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    private static async Task<string> CaptureOneRequestAsync(HttpListener listener)
    {
        var context = await listener.GetContextAsync();
        using var reader = new StreamReader(context.Request.InputStream);
        var body = await reader.ReadToEndAsync();
        var reply = Encoding.UTF8.GetBytes(
            """{"id":"c1","object":"chat.completion","created":0,"model":"qwen2.5-coder","choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}""");
        context.Response.ContentType = "application/json";
        await context.Response.OutputStream.WriteAsync(reply);
        context.Response.Close();
        return body;
    }

    [Fact]
    public void IsAvailable_ReflectsWhetherEndpointAndApiKeyAreConfigured()
    {
        var withBoth = new GpuStackChatClientProvider(Options.Create(ValidOptions()));

        var missingEndpoint = ValidOptions();
        missingEndpoint.Endpoint = string.Empty;
        var withoutEndpoint = new GpuStackChatClientProvider(Options.Create(missingEndpoint));

        var missingKey = ValidOptions();
        missingKey.ApiKey = string.Empty;
        var withoutKey = new GpuStackChatClientProvider(Options.Create(missingKey));

        Assert.True(withBoth.IsAvailable);
        Assert.False(withoutEndpoint.IsAvailable);
        Assert.False(withoutKey.IsAvailable);
    }
}
