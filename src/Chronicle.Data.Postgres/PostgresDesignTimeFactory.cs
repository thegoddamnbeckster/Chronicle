using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Chronicle.Data.Postgres;

/// <summary>Used only by <c>dotnet ef</c> to build PostgreSQL migrations (no connection is opened when adding one).</summary>
public sealed class PostgresDesignTimeFactory : IDesignTimeDbContextFactory<ChronicleDbContext>
{
    public const string MigrationsAssembly = "Chronicle.Data.Postgres";

    public ChronicleDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ChronicleDbContext>();
        options.UseNpgsql(
            Environment.GetEnvironmentVariable("CHRONICLE_PG_DESIGN") ?? "Host=localhost;Database=chronicle_design;Username=postgres",
            o => o.MigrationsAssembly(MigrationsAssembly));
        return new ChronicleDbContext(options.Options);
    }
}
