using IronHive.Agent.Mcp;
using Microsoft.Extensions.AI;

namespace Momos.Worker.Tests.Agent;

/// <summary>Records <see cref="ConnectAsync"/>/<see cref="DisconnectAllAsync"/> calls without spawning any real MCP process.</summary>
public sealed class FakeMcpPluginManager : IMcpPluginManager
{
    public List<string> ConnectedPluginNames { get; } = [];
    public bool DisconnectAllCalled { get; private set; }

    public IReadOnlyCollection<string> ConnectedPlugins => ConnectedPluginNames;

    // The interface carries these, but a fake that spawns no process has nothing to
    // announce. Empty accessors say that outright, and are indistinguishable to a
    // subscriber from a field-like event nothing ever raises — minus the dead backing
    // field the compiler warns about. A test that needs one of these to fire should add
    // a raise method here rather than resurrect the field.
    public event EventHandler<McpPluginEventArgs>? PluginConnected { add { } remove { } }
    public event EventHandler<McpPluginEventArgs>? PluginDisconnected { add { } remove { } }
    public event EventHandler<McpPluginEventArgs>? ToolsChanged { add { } remove { } }

    public Task ConnectAsync(string name, McpPluginConfig config, CancellationToken cancellationToken = default)
    {
        ConnectedPluginNames.Add(name);
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(string name, CancellationToken cancellationToken = default)
    {
        ConnectedPluginNames.Remove(name);
        return Task.CompletedTask;
    }

    public Task DisconnectAllAsync(CancellationToken cancellationToken = default)
    {
        DisconnectAllCalled = true;
        ConnectedPluginNames.Clear();
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AITool>> GetToolsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AITool>>([]);

    public Task<IReadOnlyList<AITool>> GetToolsAsync(string pluginName, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AITool>>([]);

    public Task<McpToolResult> CallToolAsync(string pluginName, string toolName, IDictionary<string, object?>? arguments, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Not needed for these tests.");

    public Task<bool> IsHealthyAsync(string pluginName, CancellationToken cancellationToken = default) =>
        Task.FromResult(true);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
