using System.Data.Common;
using Npgsql;

namespace Momos.Host.Data;

/// <summary>
/// Npgsql's connection pool defaults to 100 connections per connection string. The Host opens two
/// pools (the application database and the project knowledge store), and a shared PostgreSQL
/// server serves several applications, so an unset ceiling is a way to exhaust it. A ceiling the
/// operator set in the connection string is left alone.
/// </summary>
public static class ConnectionPoolDefaults
{
    /// <summary>Connections one pool may hold when the connection string does not say.</summary>
    public const int DefaultMaximumPoolSize = 10;

    public static string WithMaximumPoolSize(string connectionString, int maximum = DefaultMaximumPoolSize)
    {
        // NpgsqlConnectionStringBuilder reports every known keyword as present, so whether the
        // operator wrote one is read from a plain builder that only holds what was given.
        var given = new DbConnectionStringBuilder { ConnectionString = connectionString };
        if (given.ContainsKey("Maximum Pool Size") || given.ContainsKey("MaxPoolSize"))
        {
            return connectionString;
        }

        return new NpgsqlConnectionStringBuilder(connectionString) { MaxPoolSize = maximum }.ConnectionString;
    }
}
