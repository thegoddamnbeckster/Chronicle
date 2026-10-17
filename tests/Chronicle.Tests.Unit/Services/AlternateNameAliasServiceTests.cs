using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chronicle.Tests.Unit.Services;

public class AlternateNameAliasServiceTests
{
    private static (ChronicleDbContext Db, AlternateNameAliasService Svc) Build()
    {
        var name = Guid.NewGuid().ToString();
        var services = new ServiceCollection();
        services.AddDbContext<ChronicleDbContext>(o => o.UseInMemoryDatabase(name));
        var sp = services.BuildServiceProvider();
        return (sp.GetRequiredService<ChronicleDbContext>(),
                new AlternateNameAliasService(sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<AlternateNameAliasService>.Instance));
    }

    private static async Task<MediaItem> AddAsync(ChronicleDbContext db, MediaType type, string name, int level, string? json, int? parent = null)
    {
        var item = new MediaItem { Name = name, MediaTypeId = type.Id, HierarchyLevel = level, ParentId = parent, MetadataJson = json, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.MediaItems.Add(item);
        await db.SaveChangesAsync();
        return item;
    }

    [Fact]
    public async Task AnAuthorsStoredAlternateNames_BecomeAliases_AndRunningAgainAddsNothing()
    {
        var (db, svc) = Build();
        var type = new MediaType { Name = "audiobooks", DisplayName = "Audiobooks", HierarchyLevels = 3 };
        db.MediaTypes.Add(type);
        await db.SaveChangesAsync();
        var author = await AddAsync(db, type, "Jeremy Robinson", 0,
            """{"hardcover":{"alternateNames":["Jeremy Bishop","Jeremiah Knight"]},"chronicle.plugin.openlibrary":{"alternateNames":["Jeremy Robinson","J. Robinson"]},"_resolved":{"title":"x"}}""");

        await svc.ExecuteAsync(default);
        await svc.ExecuteAsync(default);

        var aliases = await db.MediaItemAliases.AsNoTracking().ToListAsync();
        aliases.Select(a => a.Alias).Should().BeEquivalentTo(["Jeremy Bishop", "Jeremiah Knight", "J. Robinson"]);
        aliases.Should().OnlyContain(a => a.MediaItemId == author.Id);
        aliases.Single(a => a.Alias == "J. Robinson").Source.Should().Be("plugin:openlibrary");
    }

    [Fact]
    public async Task OnlyATopLevelContainer_GetsAliases()
    {
        var (db, svc) = Build();
        var multi = new MediaType { Name = "audiobooks", DisplayName = "Audiobooks", HierarchyLevels = 3 };
        var flat = new MediaType { Name = "movies", DisplayName = "Movies", HierarchyLevels = 1 };
        db.MediaTypes.AddRange(multi, flat);
        await db.SaveChangesAsync();
        const string json = """{"hardcover":{"alternateNames":["Other Name"]}}""";
        var author = await AddAsync(db, multi, "A", 0, null);
        await AddAsync(db, multi, "A Book", 2, json, author.Id);
        await AddAsync(db, flat, "A Movie", 0, json);

        await svc.ExecuteAsync(default);

        (await db.MediaItemAliases.CountAsync()).Should().Be(0);
    }
}
