using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Chronicle.API;

/// <summary>
/// Sets PRAGMA busy_timeout on every SQLite connection opened by EF Core so that
/// concurrent background tasks (enrichment, scheduled scan, library) wait up to
/// 5 seconds for a write lock rather than failing immediately with "database is locked".
/// journal_mode=WAL (set once at startup) is the primary concurrency fix; this is
/// the safety-net for the rare cases where WAL checkpointing briefly blocks writers.
/// </summary>
internal sealed class SqliteBusyTimeoutInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=5000;";
        cmd.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout=5000;";
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
