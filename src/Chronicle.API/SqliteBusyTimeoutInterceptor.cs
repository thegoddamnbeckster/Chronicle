using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Chronicle.API;

/// <summary>
/// Sets PRAGMA busy_timeout, foreign_keys and synchronous on every SQLite connection opened by
/// EF Core.
///
/// busy_timeout: lets concurrent background tasks (enrichment, scheduled scan, library) wait
/// up to 5 seconds for a write lock rather than failing immediately with "database is locked".
/// journal_mode=WAL (set once at startup) is the primary concurrency fix; this is the
/// safety-net for the rare cases where WAL checkpointing briefly blocks writers.
///
/// foreign_keys: SQLite does NOT enforce declared foreign keys (or run ON DELETE CASCADE)
/// unless this pragma is explicitly turned on per-connection -- it was never set anywhere in
/// this codebase before, even though ChronicleDbContext's model declares
/// `DeleteBehavior.Cascade` at ~14 relationships (MediaItemEnrichment -> MediaItem among them).
/// EF Core's own in-memory cascade only fires for children already loaded into the change
/// tracker at delete time; anything else -- a plain `db.Remove(item)` without eager-loading its
/// dependents, a raw-SQL delete, a bulk `ExecuteDelete` -- silently left orphaned child rows
/// behind instead of cascading, which is exactly how the "Pending (3), only 2 render" bug
/// (orphaned MediaItemEnrichment rows) came to exist live. Turning this on makes SQLite itself
/// honor the cascade behavior the model already declares, closing off orphan creation at the
/// root instead of only making individual read paths resilient to orphans after the fact.
/// </summary>
internal sealed class SqliteBusyTimeoutInterceptor : DbConnectionInterceptor
{
    /// <summary>How long SQLite itself waits for a lock before reporting it busy (the EF command timeout, 30 s by default, applies on top).</summary>
    internal const int BusyTimeoutMs = 5000;

    private readonly string _pragmas;
    /// <param name="tempDirectory">Where SQLite may put scratch files (created if missing). Null leaves SQLite's default.</param>
    public SqliteBusyTimeoutInterceptor(string? tempDirectory = null)
    {
        // synchronous=NORMAL: with the write-ahead log (set at startup) SQLite then waits for the disk once per checkpoint instead of once
        // per commit. A power cut can lose the last few moments of writes but never corrupts the file; for a media tracker whose busiest
        // writers are scrobbles and enrichment, the shorter write lock is worth far more than the last second of durability.
        _pragmas = $"PRAGMA busy_timeout={BusyTimeoutMs}; PRAGMA foreign_keys=ON; PRAGMA synchronous=NORMAL;";
        if (tempDirectory is not null)
        {
            Directory.CreateDirectory(tempDirectory);
            _pragmas += $" PRAGMA temp_store_directory='{tempDirectory.Replace("'", "''")}';";
        }
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = _pragmas;
        cmd.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = _pragmas;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
