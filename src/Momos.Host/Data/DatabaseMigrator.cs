using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Momos.Host.Data;

/// <summary>
/// Retries a SQLite migration on "database is locked"/"database table is locked" errors.
/// A prior process holding the database file (e.g. a network file share's lock lingering past
/// what the orchestrator reports as a clean shutdown) should not crash the whole app on a single
/// failed attempt when the lock is expected to clear shortly.
/// </summary>
public static class DatabaseMigrator
{
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;

    public static void MigrateWithRetry(
        Action migrate,
        ILogger logger,
        IReadOnlyList<TimeSpan> retryDelays,
        Action<TimeSpan>? delay = null)
    {
        delay ??= Thread.Sleep;

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                migrate();
                return;
            }
            catch (SqliteException ex) when (IsLockContention(ex) && attempt < retryDelays.Count)
            {
                var wait = retryDelays[attempt];
                logger.LogWarning(
                    ex,
                    "Database migration hit a SQLite lock (attempt {Attempt}/{TotalAttempts}); retrying in {Delay}.",
                    attempt + 1,
                    retryDelays.Count + 1,
                    wait);
                delay(wait);
            }
        }
    }

    private static bool IsLockContention(SqliteException ex)
        => ex.SqliteErrorCode is SqliteBusy or SqliteLocked;
}
