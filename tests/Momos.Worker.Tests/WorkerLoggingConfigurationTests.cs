using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Momos.Worker.Tests;

/// <summary>
/// The Worker polls claim-next every few seconds and runs as a service, so whatever its shipped
/// <c>appsettings.json</c> lets through at Information accumulates around the clock. These tests
/// read that file, not a copy of its values.
/// </summary>
public sealed class WorkerLoggingConfigurationTests
{
    private static ILoggerFactory ShippedLoggerFactory()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(WorkerProjectDirectory().FullName, "appsettings.json"), optional: false)
            .Build();
        return new ServiceCollection()
            .AddLogging(builder => builder
                .AddConfiguration(configuration.GetSection("Logging"))
                .AddProvider(new EnabledLoggerProvider()))
            .BuildServiceProvider()
            .GetRequiredService<ILoggerFactory>();
    }

    [Theory]
    [InlineData("System.Net.Http.HttpClient.IHostApiClient.LogicalHandler")]
    [InlineData("System.Net.Http.HttpClient.IHostApiClient.ClientHandler")]
    public void HttpClientRequestLogging_IsSilentAtInformation_ButKeepsWarnings(string category)
    {
        // Four lines per poll at Information: an idle Worker wrote about 130,000 a day, burying
        // the lines that report an actual failed run.
        var logger = ShippedLoggerFactory().CreateLogger(category);

        Assert.False(logger.IsEnabled(LogLevel.Information));
        Assert.True(logger.IsEnabled(LogLevel.Warning));
    }

    [Fact]
    public void TheWorkersOwnLogging_StaysAtInformation() =>
        Assert.True(ShippedLoggerFactory().CreateLogger("Momos.Worker.Execution.PullExecutionBackgroundService").IsEnabled(LogLevel.Information));

    /// <summary>Answers every level as enabled, so only the configured filters decide.</summary>
    private sealed class EnabledLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new EnabledLogger();

        public void Dispose()
        {
        }

        private sealed class EnabledLogger : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
            }
        }
    }

    private static DirectoryInfo WorkerProjectDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Momos.Worker");
            if (File.Exists(Path.Combine(candidate, "appsettings.json")))
            {
                return new DirectoryInfo(candidate);
            }
        }

        throw new InvalidOperationException($"No ancestor of '{AppContext.BaseDirectory}' contains 'src/Momos.Worker/appsettings.json'.");
    }
}
