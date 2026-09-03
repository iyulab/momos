using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Momos.Host.Data;

namespace Momos.Host.Tests.Data;

/// <summary>
/// Exercises the retry loop in isolation from EF Core/SQLite — <see cref="DatabaseMigrator.MigrateWithRetry"/>
/// takes the migrate operation and the delay action as delegates precisely so a "database is locked"
/// scenario can be simulated without a real multi-minute wait or a real file-share lock.
/// </summary>
public sealed class DatabaseMigratorTests
{
    private static SqliteException LockedException(int errorCode = 5 /* SQLITE_BUSY */)
        => new("database is locked", errorCode);

    [Fact]
    public void MigrateWithRetry_WhenMigrateSucceedsFirstTry_DoesNotRetry()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();

        DatabaseMigrator.MigrateWithRetry(
            migrate: () => attempts++,
            logger: NullLogger.Instance,
            retryDelays: [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)],
            delay: delays.Add);

        Assert.Equal(1, attempts);
        Assert.Empty(delays);
    }

    [Fact]
    public void MigrateWithRetry_WhenLockedThenSucceeds_RetriesUntilItSucceeds()
    {
        var attempts = 0;
        var delays = new List<TimeSpan>();
        var retryDelays = new[] { TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40) };

        DatabaseMigrator.MigrateWithRetry(
            migrate: () =>
            {
                attempts++;
                if (attempts < 3)
                {
                    throw LockedException();
                }
            },
            logger: NullLogger.Instance,
            retryDelays: retryDelays,
            delay: delays.Add);

        Assert.Equal(3, attempts);
        Assert.Equal(retryDelays, delays);
    }

    [Fact]
    public void MigrateWithRetry_WhenStillLockedAfterExhaustingRetryBudget_RethrowsTheSqliteException()
    {
        var attempts = 0;
        var retryDelays = new[] { TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(40) };

        var thrown = Assert.Throws<SqliteException>(() =>
            DatabaseMigrator.MigrateWithRetry(
                migrate: () =>
                {
                    attempts++;
                    throw LockedException();
                },
                logger: NullLogger.Instance,
                retryDelays: retryDelays,
                delay: _ => { }));

        // one initial attempt + one per configured retry delay, then give up.
        Assert.Equal(retryDelays.Length + 1, attempts);
        Assert.Equal(5, thrown.SqliteErrorCode);
    }

    [Fact]
    public void MigrateWithRetry_WhenTheExceptionIsNotALockContention_RethrowsImmediatelyWithoutRetrying()
    {
        var attempts = 0;
        var delayed = false;

        var thrown = Assert.Throws<SqliteException>(() =>
            DatabaseMigrator.MigrateWithRetry(
                migrate: () =>
                {
                    attempts++;
                    throw new SqliteException("no such table: Projects", errorCode: 1 /* SQLITE_ERROR */);
                },
                logger: NullLogger.Instance,
                retryDelays: [TimeSpan.FromSeconds(20)],
                delay: _ => delayed = true));

        Assert.Equal(1, attempts);
        Assert.False(delayed);
        Assert.Equal(1, thrown.SqliteErrorCode);
    }

    [Fact]
    public void MigrateWithRetry_WhenANonSqliteExceptionIsThrown_RethrowsImmediatelyWithoutRetrying()
    {
        var attempts = 0;

        Assert.Throws<InvalidOperationException>(() =>
            DatabaseMigrator.MigrateWithRetry(
                migrate: () =>
                {
                    attempts++;
                    throw new InvalidOperationException("unrelated failure");
                },
                logger: NullLogger.Instance,
                retryDelays: [TimeSpan.FromSeconds(20)],
                delay: _ => { }));

        Assert.Equal(1, attempts);
    }
}
