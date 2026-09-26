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
/// "Show every credit" on the person page: the provider's whole filmography, stored and served from the
/// database until stale, with titles already in the library linked, library-only credits kept, no cap, and
/// an explicit "incomplete" signal when the provider cannot be reached.
/// </summary>
public class PersonFullCreditsServiceTests
{
    private sealed class Fixture
    {
        public ChronicleDbContext Db { get; } = new(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public Mock<IMetadataProvider> Provider { get; } = new();
        public PersonFullCreditsService Service { get; }
        public MediaItem Person { get; }

        public Fixture()
        {
            Db.MediaTypes.AddRange(
                new MediaType { Id = 1, Name = "movies", DisplayName = "Movies", HierarchyLevels = 1, CreatedAt = DateTime.UtcNow },
                new MediaType { Id = 2, Name = "people", DisplayName = "People", HierarchyLevels = 1, CreatedAt = DateTime.UtcNow });
            Person = Item(2, "Keanu");
            Db.MediaItems.Add(Person);
            Db.SaveChanges();
            Db.MediaEnrichments.Add(new MediaItemEnrichment { MediaItemId = Person.Id, PluginId = "p", ExternalId = "person:6384" });
            Db.SaveChanges();

            var registry = new Mock<IPluginRegistry>();
            registry.Setup(r => r.GetMetadataProvider("p")).Returns(Provider.Object);
            Service = new PersonFullCreditsService(registry.Object, NullLogger<PersonFullCreditsService>.Instance);
        }

        public MediaItem AddItem(int typeId, string name, int? year = null, bool stub = false)
        {
            var m = Item(typeId, name, year);
            m.IsStub = stub;
            Db.MediaItems.Add(m);
            Db.SaveChanges();
            return m;
        }

        public void Provides(IReadOnlyList<ProviderPersonCredit> credits) =>
            Provider.Setup(p => p.GetPersonCreditsAsync("person:6384", It.IsAny<CancellationToken>())).ReturnsAsync(credits);
    }

    private static MediaItem Item(int typeId, string name, int? year = null) => new()
    {
        MediaTypeId = typeId, Name = name, Year = year, HierarchyLevel = 1,
        CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    private static ProviderPersonCredit Credit(string id, string title, string role = "Actor", string source = "tmdb") =>
        new(source, id, id.StartsWith("tv:") ? "tv" : "movie", title, 2000, null, role);

    [Fact]
    public async Task ReturnsEveryProviderCredit_LinkingLibraryTitles_AndKeepingLibraryOnlyCredits_NoCap()
    {
        var f = new Fixture();
        var matrix = f.AddItem(1, "The Matrix", 1999);
        var libraryOnly = f.AddItem(1, "Fan Edit", 2020);
        f.Db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = matrix.Id, Source = "tmdb", ExternalId = "movie:603" });
        f.Db.MediaCredits.Add(new MediaCredit { MediaItemId = libraryOnly.Id, PersonMediaItemId = f.Person.Id, PersonName = "Keanu", Role = "Cast" });
        await f.Db.SaveChangesAsync();

        // 300 credits: far more than any plausible page size -- there is no limit.
        var provided = new List<ProviderPersonCredit> { new("tmdb", "movie:603", "movie", "The Matrix", 1999, null, "Actor", "Neo") };
        provided.AddRange(Enumerable.Range(1, 299).Select(i => Credit($"movie:{10000 + i}", $"Other {i}")));
        f.Provides(provided);

        var result = await f.Service.GetAsync(f.Db, f.Person.Id, default);

        Assert.False(result.Incomplete);
        Assert.Equal(301, result.Credits.Count);
        Assert.Contains(result.Credits, c => c.MediaItemId == matrix.Id && c.Role == "Actor" && c.CharacterName == "Neo");
        Assert.Equal(299, result.Credits.Count(c => c.MediaItemId == null));
        Assert.Contains(result.Credits, c => c.MediaItemId == libraryOnly.Id && c.Role == "Actor"); // "Cast" folded into "Actor"
    }

    [Fact]
    public async Task TwoDifferentTitlesWithTheSameNameAndYear_AreBothKept()
    {
        var f = new Fixture();
        f.Provides([Credit("movie:1", "Fargo"), Credit("tv:2", "Fargo")]);

        var result = await f.Service.GetAsync(f.Db, f.Person.Id, default);

        Assert.Equal(2, result.Credits.Count);
    }

    [Fact]
    public async Task LibraryLinkIsScopedToTheProvidersSource_AndPrefersARealItemOverAStub()
    {
        var f = new Fixture();
        var stub = f.AddItem(1, "Stub Matrix", stub: true);
        var real = f.AddItem(1, "The Matrix", 1999);
        var otherSource = f.AddItem(1, "Unrelated");
        f.Db.MediaExternalIds.AddRange(
            new MediaExternalId { MediaItemId = stub.Id, Source = "tmdb", ExternalId = "movie:603" },
            new MediaExternalId { MediaItemId = real.Id, Source = "tmdb", ExternalId = "movie:603" },
            new MediaExternalId { MediaItemId = otherSource.Id, Source = "other", ExternalId = "movie:777" });
        await f.Db.SaveChangesAsync();
        f.Provides([Credit("movie:603", "The Matrix"), Credit("movie:777", "Seven Seven Seven")]);

        var result = await f.Service.GetAsync(f.Db, f.Person.Id, default);

        Assert.Contains(result.Credits, c => c.MediaItemId == real.Id);
        Assert.DoesNotContain(result.Credits, c => c.MediaItemId == stub.Id);
        Assert.Contains(result.Credits, c => c.MediaItemId == null && c.Name == "Seven Seven Seven"); // other source's id is not a match
    }

    [Fact]
    public async Task RoleCaseDifferences_DoNotDuplicateALibraryCredit()
    {
        var f = new Fixture();
        var movie = f.AddItem(1, "A Movie", 2001);
        f.Db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = movie.Id, Source = "tmdb", ExternalId = "movie:5" });
        f.Db.MediaCredits.Add(new MediaCredit { MediaItemId = movie.Id, PersonMediaItemId = f.Person.Id, PersonName = "Keanu", Role = "Executive producer" });
        await f.Db.SaveChangesAsync();
        f.Provides([Credit("movie:5", "A Movie", "Executive Producer")]);

        var result = await f.Service.GetAsync(f.Db, f.Person.Id, default);

        Assert.Single(result.Credits);
    }

    [Fact]
    public async Task TheFilmographyIsStored_AndServedWithoutAnotherProviderCallWhileFresh()
    {
        var f = new Fixture();
        f.Provides([Credit("movie:1", "One"), Credit("movie:2", "Two")]);

        await f.Service.GetAsync(f.Db, f.Person.Id, default);
        var second = await f.Service.GetAsync(f.Db, f.Person.Id, default);

        Assert.Equal(2, await f.Db.PersonProviderCredits.CountAsync());
        Assert.Equal(2, second.Credits.Count);
        f.Provider.Verify(p => p.GetPersonCreditsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AStaleCopyIsRefreshed_ReplacingTheOldRows()
    {
        var f = new Fixture();
        f.Db.PersonProviderCredits.Add(new PersonProviderCredit
        {
            PersonMediaItemId = f.Person.Id, Source = "tmdb", ExternalId = "movie:old", MediaType = "movie", Title = "Old",
            Role = "Actor", FetchedAt = DateTime.UtcNow - PersonFullCreditsService.MaxAge - TimeSpan.FromDays(1),
        });
        await f.Db.SaveChangesAsync();
        f.Provides([Credit("movie:new", "New")]);

        var result = await f.Service.GetAsync(f.Db, f.Person.Id, default);

        Assert.Equal(["New"], result.Credits.Select(c => c.Name));
        Assert.Equal(1, await f.Db.PersonProviderCredits.CountAsync());
    }

    [Fact]
    public async Task ProviderFailureWithNothingStored_IsReportedAsIncomplete()
    {
        var f = new Fixture();
        var libraryMovie = f.AddItem(1, "Library Movie", 2001);
        f.Db.MediaCredits.Add(new MediaCredit { MediaItemId = libraryMovie.Id, PersonMediaItemId = f.Person.Id, PersonName = "Keanu", Role = "Actor" });
        await f.Db.SaveChangesAsync();
        f.Provider.Setup(p => p.GetPersonCreditsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("429"));

        var result = await f.Service.GetAsync(f.Db, f.Person.Id, default);

        Assert.True(result.Incomplete);
        Assert.Single(result.Credits); // still the library credits
    }

    [Fact]
    public async Task ProviderFailureWithAStaleCopy_UsesTheStaleCopy_NotIncomplete()
    {
        var f = new Fixture();
        f.Db.PersonProviderCredits.Add(new PersonProviderCredit
        {
            PersonMediaItemId = f.Person.Id, Source = "tmdb", ExternalId = "movie:old", MediaType = "movie", Title = "Old",
            Role = "Actor", FetchedAt = DateTime.UtcNow - PersonFullCreditsService.MaxAge - TimeSpan.FromDays(1),
        });
        await f.Db.SaveChangesAsync();
        f.Provider.Setup(p => p.GetPersonCreditsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("down"));

        var result = await f.Service.GetAsync(f.Db, f.Person.Id, default);

        Assert.False(result.Incomplete);
        Assert.Equal(["Old"], result.Credits.Select(c => c.Name));
    }

    [Fact]
    public async Task APersonWithNoProviderId_StillReturnsTheirLibraryCredits()
    {
        var f = new Fixture();
        f.Db.MediaEnrichments.RemoveRange(f.Db.MediaEnrichments);
        var movie = f.AddItem(1, "A Movie", 2001);
        f.Db.MediaCredits.Add(new MediaCredit { MediaItemId = movie.Id, PersonMediaItemId = f.Person.Id, PersonName = "Keanu", Role = "Director" });
        await f.Db.SaveChangesAsync();

        var result = await f.Service.GetAsync(f.Db, f.Person.Id, default);

        Assert.False(result.Incomplete);
        var only = Assert.Single(result.Credits);
        Assert.Equal(("Director", movie.Id), (only.Role, only.MediaItemId));
    }

    [Fact]
    public void GroupByRole_OrdersRolesAlphabetically_AndEachGroupNewestFirst_WithNoLimit()
    {
        var credits = Enumerable.Range(0, 250)
            .Select(i => new PersonFullCredit(null, $"T{i}", null, 1900 + i % 100, "movies", null, i % 2 == 0 ? "Actor" : "Director"))
            .Append(new PersonFullCredit(null, "NoYear", null, null, "movies", null, "Actor"))
            .ToList();

        var groups = PersonFullCreditsService.GroupByRole(credits);

        Assert.Equal(["Actor", "Director"], groups.Select(g => g.Role));
        Assert.Equal(251, groups.Sum(g => g.Items.Count));
        Assert.All(groups, g => Assert.Equal(
            g.Items.OrderByDescending(c => c.Year ?? int.MinValue).Select(c => c.Name), g.Items.Select(c => c.Name)));
        Assert.Equal("NoYear", groups[0].Items[^1].Name); // unknown year sorts last
    }
}
