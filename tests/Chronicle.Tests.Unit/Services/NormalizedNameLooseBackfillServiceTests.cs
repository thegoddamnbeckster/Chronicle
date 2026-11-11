using Chronicle.Core.Helpers;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// Rows saved before NormalizeNameLoose folded diacritics keep an accented key, so
/// "Ismael Cruz Córdova" never matched a credit for "Ismael Cruz Cordova" (2026-11-11). The
/// backfill recomputes the key; the merge that follows must still display the name of the
/// provider with the higher metadata-assignment precedence.
/// </summary>
public sealed class NormalizedNameLooseBackfillServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ChronicleDbContext _db;
    private readonly NormalizedNameLooseBackfillService _svc;
    private readonly int _peopleTypeId;

    public NormalizedNameLooseBackfillServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        var people = new MediaType { Name = "people", IsPersonType = true, DisplayName = "People", HierarchyLevels = 1, IsTrackable = false };
        _db.MediaTypes.Add(people);
        _db.SaveChanges();
        _peopleTypeId = people.Id;

        var services = new ServiceCollection();
        services.AddSingleton(_db);
        _svc = new NormalizedNameLooseBackfillService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<NormalizedNameLooseBackfillService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task RunOnce_FoldsStaleAccentedKey_LeavesNameAndUpdatedAtAlone_AndRunsOnlyOnce()
    {
        var stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var item = new MediaItem
        {
            MediaTypeId = _peopleTypeId, Name = "Ismael Cruz Córdova", HierarchyLevel = 0,
            CreatedAt = stamp, UpdatedAt = stamp,
        };
        _db.MediaItems.Add(item);
        await _db.SaveChangesAsync();
        // Simulate the pre-fix state: the stored key still carries the accent.
        await _db.MediaItems.Where(m => m.Id == item.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.NormalizedNameLoose, "ismaelcruzcórdova"));

        await _svc.RunOnceAsync();

        var row = await _db.MediaItems.AsNoTracking().SingleAsync(m => m.Id == item.Id);
        row.NormalizedNameLoose.Should().Be(MediaItemNormalizer.NormalizeNameLoose("Ismael Cruz Cordova"));
        row.Name.Should().Be("Ismael Cruz Córdova", "which spelling is displayed is precedence's call, not the backfill's");
        row.UpdatedAt.Should().Be(stamp, "a key recompute must not tell Kodi the item changed");

        // Marker set: a second run does nothing, even if a key is wrong again.
        await _db.MediaItems.Where(m => m.Id == item.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.NormalizedNameLoose, "stale"));
        await _svc.RunOnceAsync();
        (await _db.MediaItems.AsNoTracking().SingleAsync(m => m.Id == item.Id)).NormalizedNameLoose.Should().Be("stale");
    }
}

/// <summary>After the backfill exposes a Cordova/Córdova pair, merging it shows the higher-precedence provider's spelling.</summary>
public class AccentDuplicateMergePrecedenceTests
{
    private const int PeopleType = 1;
    private const string Tmdb = "chronicle.plugin.tmdb";
    private const string Wikipedia = "chronicle.plugin.wikipedia";

    private static async Task<string> MergeAndGetNameAsync(string[] titlePriority)
    {
        var db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
        await using var _ = db;
        db.MediaTypes.Add(new MediaType { Id = PeopleType, Name = "people", IsPersonType = true, DisplayName = "People", HierarchyLevels = 1, CreatedAt = DateTime.UtcNow });

        // Winner: the richer record (TMDB + IMDb ids), as DuplicatePersonAutoResolveService picks it.
        var tmdb = new MediaItem
        {
            MediaTypeId = PeopleType, Name = "Ismael Cruz Cordova", HierarchyLevel = 0,
            MetadataJson = $$$"""{"{{{Tmdb}}}":{"title":"Ismael Cruz Cordova"}}""",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        var wiki = new MediaItem
        {
            MediaTypeId = PeopleType, Name = "Ismael Cruz Córdova", HierarchyLevel = 0,
            MetadataJson = $$$"""{"{{{Wikipedia}}}":{"title":"Ismael Cruz Córdova"}}""",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.MediaItems.AddRange(tmdb, wiki);
        await db.SaveChangesAsync();

        var cache = new AssignmentConfigCache(null!);
        cache.InjectForTest(new Dictionary<string, Dictionary<string, List<string>>>
        {
            ["people"] = new() { ["title"] = [.. titlePriority] },
        });
        var aliases = new FieldAliasCache(null!);
        aliases.InjectForTest(new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase));
        var resolution = new MetadataResolutionService(cache, aliases, null!, NullLogger<MetadataResolutionService>.Instance);

        var svc = new MergeService(db, resolution, Mock.Of<IMovieCollectionService>(),
            Mock.Of<IFileScanService>(), NullLogger<MergeService>.Instance);
        await svc.MergeAsync(tmdb.Id, wiki.Id, mergedByUserId: null);

        return (await db.MediaItems.SingleAsync(m => m.Id == tmdb.Id)).Name;
    }

    [Fact]
    public async Task Merge_DisplaysTheHigherPrecedenceProvidersName_WikipediaFirst() =>
        (await MergeAndGetNameAsync([Wikipedia, Tmdb])).Should().Be("Ismael Cruz Córdova");

    [Fact]
    public async Task Merge_DisplaysTheHigherPrecedenceProvidersName_TmdbFirst() =>
        (await MergeAndGetNameAsync([Tmdb, Wikipedia])).Should().Be("Ismael Cruz Cordova");
}
