using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services.Database;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Tests.Unit.Services;

/// <summary>Only runs when a PostgreSQL server is offered through CHRONICLE_TEST_PG (a connection string to its "postgres" database).</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public const string Variable = "CHRONICLE_TEST_PG";
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"Set {Variable} to a PostgreSQL connection string (database=postgres) to run this.";
    }
}

public sealed class DatabaseCopierTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "chronicle-copy-" + Guid.NewGuid().ToString("N"));

    public DatabaseCopierTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch { /* scratch */ }
    }

    private ChronicleDbContext NewSqlite(string name, bool migrate = true)
    {
        var db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_dir, name)}").Options);
        if (migrate) db.Database.Migrate();
        return db;
    }

    private static async Task SeedAsync(ChronicleDbContext db)
    {
        var now = DateTime.UtcNow;
        db.Users.Add(new User { Id = 1, Username = "alice", PasswordHash = "x", IsAdmin = true, IsActive = true, CreatedAt = now, UpdatedAt = now });
        db.MediaTypes.Add(new MediaType { Id = 50, Name = "comics", DisplayName = "Comics", HierarchyLevels = 3, CreatedAt = now, ScanHintsJson = "{\"extensions\":[\".cbz\"]}" });
        await db.SaveChangesAsync();

        // A child whose parent has a HIGHER id (items get reparented under containers created later).
        db.MediaItems.AddRange(
            new MediaItem { Id = 100, MediaTypeId = 50, Name = "Child issue", HierarchyLevel = 2, ParentId = 300, Number = 1, CreatedAt = now, UpdatedAt = now },
            new MediaItem { Id = 200, MediaTypeId = 50, Name = "Standalone", HierarchyLevel = 0, CreatedAt = now, UpdatedAt = now, MetadataJson = "{\"note\":\"café – 日本語\"}" },
            new MediaItem { Id = 300, MediaTypeId = 50, Name = "Series", HierarchyLevel = 1, ParentId = 400, CreatedAt = now, UpdatedAt = now },
            new MediaItem { Id = 400, MediaTypeId = 50, Name = "Publisher", HierarchyLevel = 0, CreatedAt = now, UpdatedAt = now });
        db.AppSettings.Add(new AppSetting { Key = "plugins.catalog_url", Value = "https://example.org/plugins.json" });
        db.UserLibraries.Add(new UserLibrary { UserId = 1, MediaItemId = 200, Status = LibraryStatus.Completed, AddedAt = now, UpdatedAt = now });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task CopiesEveryRow_ParentsBeforeChildren_AndTheCountsMatch()
    {
        await using var source = NewSqlite("source.db");
        await SeedAsync(source);
        await using var target = NewSqlite("target.db");

        var report = await DatabaseCopier.CopyAsync(source, target, replace: false);

        report.AllMatch.Should().BeTrue();
        report.Tables.Single(t => t.Table == "media_items").TargetRows.Should().Be(4);
        var items = await target.MediaItems.AsNoTracking().OrderBy(i => i.Id).ToListAsync();
        items.Select(i => (i.Id, i.ParentId)).Should().Equal((100, 300), (200, null), (300, 400), (400, null));
        items.Single(i => i.Id == 200).MetadataJson.Should().Contain("café").And.Contain("日本語");
        (await target.Users.SingleAsync()).Username.Should().Be("alice");
        (await target.AppSettings.SingleAsync()).Key.Should().Be("plugins.catalog_url");
        (await target.UserLibraries.SingleAsync()).Status.Should().Be(LibraryStatus.Completed);
    }

    [Fact]
    public async Task TheSourceIsOnlyRead()
    {
        await using var source = NewSqlite("source.db");
        await SeedAsync(source);
        await source.DisposeAsync();
        SqliteConnection.ClearAllPools();
        var before = ReadShared(Path.Combine(_dir, "source.db"));
        await using var readOnly = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseSqlite(new SqliteConnectionStringBuilder($"Data Source={Path.Combine(_dir, "source.db")}") { Mode = SqliteOpenMode.ReadOnly }.ConnectionString).Options);
        await using var target = NewSqlite("target.db");

        await DatabaseCopier.CopyAsync(readOnly, target, replace: false);

        await readOnly.DisposeAsync();
        SqliteConnection.ClearAllPools();
        ReadShared(Path.Combine(_dir, "source.db")).Should().Equal(before);
    }

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    [Fact]
    public async Task ATargetThatAlreadyHoldsData_IsRefused_UnlessReplacementIsAskedFor()
    {
        await using var source = NewSqlite("source.db");
        await SeedAsync(source);
        await using var target = NewSqlite("target.db");
        await SeedAsync(target);
        target.Users.Add(new User { Id = 2, Username = "bob", PasswordHash = "x", IsActive = true, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
        await target.SaveChangesAsync();

        var refuse = () => DatabaseCopier.CopyAsync(source, target, replace: false);
        (await refuse.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("already contains data");
        (await target.Users.CountAsync()).Should().Be(2);                    // nothing was touched

        var report = await DatabaseCopier.CopyAsync(source, target, replace: true);

        report.AllMatch.Should().BeTrue();
        (await target.Users.AsNoTracking().Select(u => u.Username).ToListAsync()).Should().Equal("alice");   // bob is gone
    }

    [Fact]
    public async Task TheRowsAMigrationSeeds_AreReplacedByTheSourcesNotDuplicated()
    {
        await using var source = NewSqlite("source.db");
        await SeedAsync(source);
        var seededInSource = await source.MediaTypes.CountAsync();
        await using var target = NewSqlite("target.db");   // already holds the built-in types

        var report = await DatabaseCopier.CopyAsync(source, target, replace: false);

        report.Tables.Single(t => t.Table == "media_types").TargetRows.Should().Be(seededInSource);
        (await target.MediaTypes.AnyAsync(t => t.Name == "comics")).Should().BeTrue();
    }

    [Fact]
    public async Task Rows_ThatPointAtNothing_AreLeftOutAndCounted_TheRestStillCopies()
    {
        await using var source = NewSqlite("source.db");
        await SeedAsync(source);
        // Leftovers an older version could leave behind: SQLite does not stop a row pointing at a deleted item.
        await source.Database.OpenConnectionAsync();
        await source.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF");
        await source.Database.ExecuteSqlRawAsync(
            "INSERT INTO user_libraries (UserId, MediaItemId, Status, AddedAt, UpdatedAt) VALUES (1, 999999, 0, '2026-01-01', '2026-01-01')");
        await source.Database.CloseConnectionAsync();
        await using var target = NewSqlite("target.db");

        var report = await DatabaseCopier.CopyAsync(source, target, replace: false, batchSize: 1);

        var libraries = report.Tables.Single(t => t.Table == "user_libraries");
        libraries.SourceRows.Should().Be(2);
        libraries.TargetRows.Should().Be(1);
        libraries.Skipped.Should().Be(1);
        report.AllMatch.Should().BeTrue();
        report.TotalSkipped.Should().Be(1);
        (await target.MediaItems.CountAsync()).Should().Be(4);
    }

    [Fact]
    public async Task AnEmptySource_CopiesNothing_AndSaysSo()
    {
        await using var source = NewSqlite("source.db");
        await source.Database.ExecuteSqlRawAsync("DELETE FROM media_types");
        await using var target = NewSqlite("target.db");

        var report = await DatabaseCopier.CopyAsync(source, target, replace: false);

        report.AllMatch.Should().BeTrue();
        report.TotalRows.Should().Be(0);
    }

    [Fact]
    public async Task Progress_IsReportedTable_ByTable()
    {
        await using var source = NewSqlite("source.db");
        await SeedAsync(source);
        await using var target = NewSqlite("target.db");
        var lines = new List<string>();

        await DatabaseCopier.CopyAsync(source, target, replace: false, log: lines.Add);

        lines.Should().Contain(l => l.StartsWith("Copying media_items (4 rows)"));
    }

    [Fact]
    public void TheModelHasNoCircleOfTablesTheCopierCannotOrder()
    {
        using var db = NewSqlite("model.db", migrate: false);

        var act = () => DatabaseCopier.Order(db.Model.GetEntityTypes().Where(t => !t.IsOwned() && t.GetTableName() is not null).ToList());

        act.Should().NotThrow();
    }

    // ── against a real PostgreSQL server ──────────────────────────────────────

    [PostgresFact]
    public async Task IntoPostgres_EveryRowArrives_WithItsIdsDatesAndText_AndNewRowsGetFreshIds()
    {
        var admin = Environment.GetEnvironmentVariable(PostgresFactAttribute.Variable)!;
        var name = "chronicle_test_" + Guid.NewGuid().ToString("N")[..10];
        var targetConn = new Npgsql.NpgsqlConnectionStringBuilder(admin) { Database = name }.ConnectionString;
        await using (var adminDb = new Npgsql.NpgsqlConnection(admin))
        {
            await adminDb.OpenAsync();
            await using var create = adminDb.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{name}\"";
            await create.ExecuteNonQueryAsync();
        }
        try
        {
            await using var source = NewSqlite("source.db");
            await SeedAsync(source);
            await using var target = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
                .UseNpgsql(targetConn, o => o.MigrationsAssembly(Chronicle.Data.Postgres.PostgresDesignTimeFactory.MigrationsAssembly)).Options);
            await target.Database.MigrateAsync();

            var report = await DatabaseCopier.CopyAsync(source, target, replace: false);

            report.AllMatch.Should().BeTrue();
            var items = await target.MediaItems.AsNoTracking().OrderBy(i => i.Id).ToListAsync();
            items.Select(i => (i.Id, i.ParentId)).Should().Equal((100, 300), (200, null), (300, 400), (400, null));
            items.Single(i => i.Id == 200).MetadataJson.Should().Contain("café").And.Contain("日本語");
            items[0].CreatedAt.Kind.Should().Be(DateTimeKind.Utc);

            // The sequence continues after the copied ids instead of colliding with them.
            var fresh = new MediaItem { MediaTypeId = 50, Name = "New after copy", HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            target.MediaItems.Add(fresh);
            await target.SaveChangesAsync();
            fresh.Id.Should().BeGreaterThan(400);
        }
        finally
        {
            Npgsql.NpgsqlConnection.ClearAllPools();
            await using var adminDb = new Npgsql.NpgsqlConnection(admin);
            await adminDb.OpenAsync();
            await using var drop = adminDb.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)";
            await drop.ExecuteNonQueryAsync();
        }
    }
}

public class SearchCaseFoldingTests
{
    [Theory]
    [InlineData("The MATRIX", false, "the matrix")]
    [InlineData("The MATRIX", true, "the matrix")]
    [InlineData("CAFÉ", false, "cafÉ")]      // SQLite's lower() leaves non-ASCII capitals alone, and so must the pattern
    [InlineData("CAFÉ", true, "café")]       // PostgreSQL's lower() folds them
    [InlineData("100%_done", false, "100%_done")]
    [InlineData("", false, "")]
    public void FoldsTheTermTheWayTheDatabasesOwnLowerDoes(string term, bool postgres, string expected) =>
        Chronicle.Services.MediaService.FoldCase(term, postgres).Should().Be(expected);
}
