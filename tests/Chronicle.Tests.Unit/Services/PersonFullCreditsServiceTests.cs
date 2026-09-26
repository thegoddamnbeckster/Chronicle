using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Chronicle.Services;
using Chronicle.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// "Show every credit" on the person page: the provider's whole filmography, with titles already in the
/// library linked, library-only credits kept, and no cap.
/// </summary>
public class PersonFullCreditsServiceTests
{
    private static ChronicleDbContext NewDb() => new(new DbContextOptionsBuilder<ChronicleDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static MediaItem Item(int typeId, string name, int? year = null) => new()
    {
        MediaTypeId = typeId, Name = name, Year = year, HierarchyLevel = 1,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task ReturnsEveryProviderCredit_LinkingLibraryTitles_AndKeepingLibraryOnlyCredits()
    {
        await using var db = NewDb();
        db.MediaTypes.AddRange(
            new MediaType { Id = 1, Name = "movies", DisplayName = "Movies", HierarchyLevels = 1, CreatedAt = DateTime.UtcNow },
            new MediaType { Id = 2, Name = "people", DisplayName = "People", HierarchyLevels = 1, CreatedAt = DateTime.UtcNow });
        var person = Item(2, "Keanu");
        var matrix = Item(1, "The Matrix", 1999);
        var libraryOnly = Item(1, "Fan Edit", 2020);
        db.MediaItems.AddRange(person, matrix, libraryOnly);
        await db.SaveChangesAsync();
        db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = matrix.Id, Source = "tmdb", ExternalId = "movie:603" });
        db.MediaEnrichments.Add(new MediaItemEnrichment { MediaItemId = person.Id, PluginId = "p", ExternalId = "person:6384" });
        db.MediaCredits.Add(new MediaCredit { MediaItemId = libraryOnly.Id, PersonMediaItemId = person.Id, PersonName = "Keanu", Role = "Cast" });
        await db.SaveChangesAsync();

        // 300 credits: far more than any plausible page size -- there is no limit.
        var provided = new List<ProviderPersonCredit> { new("movie:603", "movie", "The Matrix", 1999, null, "Actor", "Neo") };
        provided.AddRange(Enumerable.Range(1, 299).Select(i =>
            new ProviderPersonCredit($"movie:{10000 + i}", "movie", $"Other {i}", 1990 + i % 30, null, "Actor")));
        var provider = new Mock<IMetadataProvider>();
        provider.Setup(p => p.GetPersonCreditsAsync("person:6384", It.IsAny<CancellationToken>())).ReturnsAsync(provided);
        var registry = new Mock<IPluginRegistry>();
        registry.Setup(r => r.GetMetadataProvider("p")).Returns(provider.Object);

        var result = await new PersonFullCreditsService(registry.Object, NullLogger<PersonFullCreditsService>.Instance)
            .GetAsync(db, person.Id, default);

        Assert.Equal(301, result.Count);
        Assert.Contains(result, c => c.MediaItemId == matrix.Id && c.Role == "Actor" && c.CharacterName == "Neo");
        Assert.Equal(299, result.Count(c => c.MediaItemId == null));           // not in the library
        Assert.Contains(result, c => c.MediaItemId == libraryOnly.Id && c.Role == "Actor"); // "Cast" folded into "Actor"
    }

    [Fact]
    public async Task APersonWithNoProviderId_StillReturnsTheirLibraryCredits()
    {
        await using var db = NewDb();
        db.MediaTypes.AddRange(
            new MediaType { Id = 1, Name = "movies", DisplayName = "Movies", HierarchyLevels = 1, CreatedAt = DateTime.UtcNow },
            new MediaType { Id = 2, Name = "people", DisplayName = "People", HierarchyLevels = 1, CreatedAt = DateTime.UtcNow });
        var person = Item(2, "Someone");
        var movie = Item(1, "A Movie", 2001);
        db.MediaItems.AddRange(person, movie);
        await db.SaveChangesAsync();
        db.MediaCredits.Add(new MediaCredit { MediaItemId = movie.Id, PersonMediaItemId = person.Id, PersonName = "Someone", Role = "Director" });
        await db.SaveChangesAsync();

        var result = await new PersonFullCreditsService(Mock.Of<IPluginRegistry>(), NullLogger<PersonFullCreditsService>.Instance)
            .GetAsync(db, person.Id, default);

        var only = Assert.Single(result);
        Assert.Equal(("Director", movie.Id), (only.Role, only.MediaItemId));
    }
}
