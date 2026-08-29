using IronHive.Agent.Mcp;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Momos.Worker.Agent;

/// <summary>
/// Loads MCP plugins from operator configuration at startup and disconnects them at
/// shutdown. With no plugins configured — the default — this is a no-op: it
/// only connects what an operator has explicitly listed in
/// <see cref="McpPluginsConfig"/>, never anything Momos decides on its own.
/// Deciding *which* tools to enable (e.g. a Computer Use tool server) and
/// their permission/isolation posture is a separate, human decision this
/// service does not make.
/// </summary>
public sealed class McpPluginStartupService(
    IMcpPluginManager pluginManager,
    IOptions<McpPluginsConfig> options) : IHostedService
{
    public const string SectionName = "Momos:Worker:Mcp";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var config = options.Value;
        return config.Plugins.Count == 0
            ? Task.CompletedTask
            : pluginManager.LoadFromConfigAsync(config, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) =>
        pluginManager.DisconnectAllAsync();
}
