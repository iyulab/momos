using IronHive.Agent.Mcp;
using Microsoft.Extensions.Options;
using Momos.Worker.Agent;

namespace Momos.Worker.Tests.Agent;

public class McpPluginStartupServiceTests
{
    [Fact]
    public async Task StartAsync_WithNoPluginsConfigured_ConnectsNothing()
    {
        var manager = new FakeMcpPluginManager();
        var service = new McpPluginStartupService(manager, Options.Create(new McpPluginsConfig()));

        await service.StartAsync(CancellationToken.None);

        Assert.Empty(manager.ConnectedPluginNames);
    }

    [Fact]
    public async Task StartAsync_WithAConfiguredPlugin_ConnectsIt()
    {
        var manager = new FakeMcpPluginManager();
        var config = new McpPluginsConfig
        {
            Plugins = new Dictionary<string, McpPluginConfig>
            {
                ["filesystem"] = new McpPluginConfig
                {
                    Transport = McpTransportType.Stdio,
                    Command = "npx",
                    Arguments = ["-y", "@modelcontextprotocol/server-filesystem", "/tmp"],
                },
            },
        };
        var service = new McpPluginStartupService(manager, Options.Create(config));

        await service.StartAsync(CancellationToken.None);

        Assert.Contains("filesystem", manager.ConnectedPluginNames);
    }

    [Fact]
    public async Task StopAsync_DisconnectsAllPlugins()
    {
        var manager = new FakeMcpPluginManager();
        var service = new McpPluginStartupService(manager, Options.Create(new McpPluginsConfig()));

        await service.StopAsync(CancellationToken.None);

        Assert.True(manager.DisconnectAllCalled);
    }
}
