using Chronicle.Data;
using Chronicle.Services.Database;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.API;

/// <summary>
/// <c>Chronicle.API --copy-database-to-postgres "Host=...;Database=...;Username=...;Password=..." [--replace]</c>: copies the
/// current SQLite database into an empty PostgreSQL database (creating its tables), checks the row counts, and exits without
/// starting the web server. The SQLite database is only read, so nothing is lost if it fails; switching Chronicle over is a
/// separate, deliberate step (printed at the end). Stop Chronicle first for a copy that is a consistent snapshot.
/// </summary>
public static class DatabaseCopyCommand
{
    public const string Flag = "--copy-database-to-postgres";
    public const string ReplaceFlag = "--replace";

    /// <summary>The PostgreSQL connection string following the flag, or null when the flag is absent; "" when it is missing.</summary>
    public static string? ParseTarget(string[] args)
    {
        var i = Array.FindIndex(args, a => string.Equals(a, Flag, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return null;
        return i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1] : string.Empty;
    }

    public static bool WantsReplace(string[] args) => args.Any(a => string.Equals(a, ReplaceFlag, StringComparison.OrdinalIgnoreCase));

    public static async Task<int> RunAsync(string sqliteConnectionString, string postgresConnectionString, bool replace, TextWriter output)
    {
        if (string.IsNullOrWhiteSpace(postgresConnectionString))
        {
            await output.WriteLineAsync($"Usage: Chronicle.API {Flag} \"Host=...;Database=...;Username=...;Password=...\" [{ReplaceFlag}]");
            return 1;
        }

        // Read-only on purpose: this command can never change the database it copies from.
        var sqlite = new SqliteConnectionStringBuilder(sqliteConnectionString) { Mode = SqliteOpenMode.ReadOnly };
        await using var source = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>().UseSqlite(sqlite.ConnectionString).Options);
        // Emptying or filling a big table takes longer than the usual 30 seconds a single statement is allowed.
        var pg = new Npgsql.NpgsqlConnectionStringBuilder(postgresConnectionString);
        if (!postgresConnectionString.Contains("Command Timeout", StringComparison.OrdinalIgnoreCase)) pg.CommandTimeout = 0;
        await using var target = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseNpgsql(pg.ConnectionString, o => o.MigrationsAssembly(Chronicle.Data.Postgres.PostgresDesignTimeFactory.MigrationsAssembly))
            .Options);

        try
        {
            var behind = (await source.Database.GetPendingMigrationsAsync()).ToList();
            if (behind.Count > 0)
            {
                await output.WriteLineAsync($"The SQLite database is {behind.Count} update(s) behind this version of Chronicle (next: {behind[0]}).");
                await output.WriteLineAsync("Start Chronicle once so it can bring the database up to date, stop it, then run this command again.");
                return 1;
            }

            await output.WriteLineAsync($"Copying {sqlite.DataSource} into PostgreSQL...");
            await output.WriteLineAsync("Creating the PostgreSQL tables (migrations)...");
            await target.Database.MigrateAsync();

            var report = await DatabaseCopier.CopyAsync(source, target, replace, line => output.WriteLine(line));

            await output.WriteLineAsync();
            foreach (var t in report.Tables.Where(t => t.SourceRows > 0 || t.TargetRows > 0))
                await output.WriteLineAsync($"  {t.Table,-34} {t.SourceRows,10} -> {t.TargetRows,10} {(t.Matches ? (t.Skipped > 0 ? $"ok ({t.Skipped} left out)" : "ok") : "MISMATCH")}");
            await output.WriteLineAsync();

            if (!report.AllMatch)
            {
                await output.WriteLineAsync("Some row counts do not match, so the copy is NOT complete. The SQLite database was not touched.");
                return 1;
            }
            await output.WriteLineAsync($"Copied {report.TotalRows} rows in {report.Elapsed:hh\\:mm\\:ss}. Every table's row count matches.");
            if (report.TotalSkipped > 0)
                await output.WriteLineAsync($"{report.TotalSkipped} row(s) were left out because they pointed at items that no longer exist in the SQLite database (leftovers from older versions); nothing could use them.");
            await output.WriteLineAsync();
            await output.WriteLineAsync("To switch Chronicle to PostgreSQL, set these for the Chronicle process and restart it:");
            await output.WriteLineAsync("    DATABASE_PROVIDER=postgresql");
            await output.WriteLineAsync("    ConnectionStrings__DefaultConnection=<the PostgreSQL connection string you used>");
            await output.WriteLineAsync("Keep the SQLite file as your backup until you are sure. To go back, unset them.");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Npgsql.NpgsqlException or DbUpdateException or SqliteException)
        {
            await output.WriteLineAsync("Copy failed: " + (ex.InnerException?.Message ?? ex.Message));
            await output.WriteLineAsync("The SQLite database was not touched. Fix the problem and run the command again with " + ReplaceFlag + " to start over.");
            return 1;
        }
    }
}
