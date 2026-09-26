using FluxIndex.SDK;

namespace Momos.Host.Knowledge;

/// <summary>
/// Builds the knowledge store while the Host starts. Building it connects to the database,
/// creates the pgvector extension and checks the vector size, so a store that is unreachable,
/// lacks pgvector or was provisioned for another embedding size stops startup here — instead of
/// the Host starting cleanly and the first request that touches knowledge failing with a 500.
/// </summary>
internal sealed class KnowledgeIndexInitializer(IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            services.GetRequiredService<IFluxIndexContext>();
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"The project knowledge store ({KnowledgeOptions.SectionName}:ConnectionString) could not be initialized: {ex.Message}",
                ex);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
