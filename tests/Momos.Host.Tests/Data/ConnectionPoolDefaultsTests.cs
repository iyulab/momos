using Momos.Host.Data;
using Npgsql;

namespace Momos.Host.Tests.Data;

public sealed class ConnectionPoolDefaultsTests
{
    [Fact]
    public void WithoutAnExplicitCeiling_TheDefaultIsApplied()
    {
        var result = ConnectionPoolDefaults.WithMaximumPoolSize("Host=db;Database=momos;Username=u;Password=p");

        Assert.Equal(ConnectionPoolDefaults.DefaultMaximumPoolSize, new NpgsqlConnectionStringBuilder(result).MaxPoolSize);
    }

    [Theory]
    [InlineData("Maximum Pool Size=3")]
    [InlineData("MaxPoolSize=3")]
    public void AnOperatorCeiling_IsKept(string setting)
    {
        var result = ConnectionPoolDefaults.WithMaximumPoolSize($"Host=db;Database=momos;{setting}");

        Assert.Equal(3, new NpgsqlConnectionStringBuilder(result).MaxPoolSize);
    }

    [Fact]
    public void TheRestOfTheConnectionString_IsPreserved()
    {
        var result = new NpgsqlConnectionStringBuilder(
            ConnectionPoolDefaults.WithMaximumPoolSize("Host=db;Port=5433;Database=momos;Username=u;Password=p"));

        Assert.Equal("db", result.Host);
        Assert.Equal(5433, result.Port);
        Assert.Equal("momos", result.Database);
    }
}
