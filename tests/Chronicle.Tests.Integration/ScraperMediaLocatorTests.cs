using System.Net.Http.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Chronicle.Services.Plugins;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Chronicle.Tests.Integration;

/// <summary>A locator whose answer the test sets: stands in for the Radarr/Sonarr plugin.</summary>
public sealed class FakeLocator : IMediaLocator
{
    public Func<LocatorFamily, LocatorQuery, LocatedMedia?> Answer { get; set; } = (_, _) => null;
    public List<(LocatorFamily Family, LocatorQuery Query)> Asked { get; } = [];

    public string PluginId => "chronicle.plugin.fakearr";
    public string Name => "Fake Arr";
    public string Version => "1.0.0";
    public PluginSettingsSchema GetSettingsSchema() => new();
    public void Configure(IReadOnlyDictionary<string, string> settings) { }
    public bool Handles(LocatorFamily family) => true;
    public Task<bool> HealthCheckAsync(CancellationToken ct = default) => Task.FromResult(true);

    public Task<LocatedMedia?> LocateAsync(LocatorFamily family, LocatorQuery query, CancellationToken ct = default)
    {
        Asked.Add((family, query));
        return Task.FromResult(Answer(family, query));
    }
}

/// <summary>An IPluginRegistry that holds nothing but one locator.</summary>
public sealed class FakeLocatorPluginRegistry(FakeLocator locator) : IPluginRegistry
{
    public IReadOnlyList<IMediaLocator> GetMediaLocators() => [locator];

    public IReadOnlyList<IMetadataProvider> GetMetadataProviders() => [];
    public IReadOnlyList<(string PluginId, IMetadataProvider Provider, string? IconUrl)> GetMetadataProviderEntries() => [];
    public IMetadataProvider? GetMetadataProvider(string pluginId) => null;
    public IReadOnlyList<IWidgetPlugin> GetWidgetPlugins() => [];
    public IReadOnlyList<IImportProvider> GetImportProviders() => [];
    public IImportProvider? GetImportProvider(string pluginId) => null;
    public IReadOnlyList<IReportPlugin> GetReportPlugins() => [];
    public IReadOnlyList<IFileScannerPlugin> GetFileScannerPlugins() => [];
    public IReadOnlyList<IThemePlugin> GetThemePlugins() => [];
    public IReadOnlyList<IPluginTask> GetPluginTasks() => [];
    public IPluginTask? GetPluginTask(string pluginId, string taskId) => null;
    public IReadOnlyList<LoadedPlugin> GetLoadedPlugins() => [];

    public Task<LoadedPlugin> LoadPluginAsync(
        int dbId, string dllPath, IReadOnlyDictionary<string, string> settings, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public void UnloadPlugin(int dbId) { }
}

public sealed class LocatorTestFactory : ChronicleApiFactory
{
    public FakeLocator Locator { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPluginRegistry>();
            services.AddSingleton<IPluginRegistry>(new FakeLocatorPluginRegistry(Locator));
        });
    }
}

/// <summary>
/// Live 2026-11-18: "The Beast (2026).mkv" was shown in Kodi as the unrelated "Heart of the Beast". When a managing application
/// (Radarr, Sonarr) reports the exact id of a file or title, the scraper must use that id and never guess from the name.
/// </summary>
public class ScraperMediaLocatorTests : IClassFixture<LocatorTestFactory>
{
    private readonly LocatorTestFactory _factory;

    public ScraperMediaLocatorTests(LocatorTestFactory factory)
    {
        factory.SeedDatabase();
        _factory = factory;
        factory.Locator.Answer = (_, _) => null;
        factory.Locator.Asked.Clear();
    }

    private int EnsureMovieType()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var existing = db.MediaTypes.FirstOrDefault(t => t.Name == "movies");
        if (existing is not null) return existing.Id;
        var mt = new MediaType
        {
            Name = "movies", DisplayName = "Movies", HierarchyLevels = 1,
            InteractionVerb = "watched", ProgressUnit = "minutes",
            IsBuiltIn = false, IsActive = true, CreatedAt = DateTime.UtcNow,
        };
        db.MediaTypes.Add(mt);
        db.SaveChanges();
        return mt.Id;
    }

    private int SeedMovie(string name, int year, params (string Source, string Id)[] ids)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var item = new MediaItem
        {
            MediaTypeId = EnsureMovieType(), Name = name, Year = year, HierarchyLevel = 0,
            NormalizedName = Chronicle.Core.Helpers.MediaItemNormalizer.NormalizeName(name),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.MediaItems.Add(item);
        db.SaveChanges();
        foreach (var (source, id) in ids)
            db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = item.Id, Source = source, ExternalId = id });
        db.SaveChanges();
        return item.Id;
    }

    private async Task<HttpClient> AuthClientAsync()
    {
        var client = _factory.CreateClient();
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { username = $"locator_{Guid.NewGuid():N}", password = "Password123!" });
        var token = System.Text.Json.JsonDocument.Parse(await reg.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<int> SearchMovieAsync(string title, int year, string fileName)
    {
        var client = await AuthClientAsync();
        var resp = await client.GetAsync(
            $"/api/v1/scraper/movies/search?title={Uri.EscapeDataString(title)}&year={year}&fileName={Uri.EscapeDataString(fileName)}");
        resp.EnsureSuccessStatusCode();
        return System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("id").GetInt32();
    }

    private static LocatedMedia Beast() => new("The Beast", 2026,
        new Dictionary<string, string> { ["tmdb"] = "movie:1185344", ["imdb"] = "tt1185344" }, Reason: "Radarr holds the file");

    [Fact]
    public async Task ARadarrAnswerWithNoItemYet_CreatesTheRightFilm_WithItsIdsSeeded_NotTheLookAlike()
    {
        var heart = SeedMovie("Scraper Locator Heart of the Beast", 2026, ("tmdb", "movie:1263337"));
        _factory.Locator.Answer = (_, _) => new LocatedMedia("Scraper Locator Beast", 2026,
            new Dictionary<string, string> { ["tmdb"] = "movie:7700001", ["imdb"] = "tt7700001" });

        var id = await SearchMovieAsync("Scraper Locator Beast", 2026, "Scraper Locator Beast (2026).mkv");

        id.Should().NotBe(heart);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var item = await db.MediaItems.SingleAsync(m => m.Id == id);
        item.Name.Should().Be("Scraper Locator Beast");
        var ids = await db.MediaExternalIds.Where(x => x.MediaItemId == id).Select(x => x.Source + "=" + x.ExternalId).ToListAsync();
        ids.Should().Contain("tmdb=movie:7700001").And.Contain("imdb=tt7700001");
    }

    [Fact]
    public async Task AnItemAlreadyHoldingTheReportedId_IsUsed_EvenWhenItsNameDiffers()
    {
        var existing = SeedMovie("Scraper Locator Renamed Film", 2026, ("tmdb", "movie:7700002"));
        _factory.Locator.Answer = (_, _) => new LocatedMedia("Scraper Locator Other Name", 2026,
            new Dictionary<string, string> { ["tmdb"] = "movie:7700002" });

        var id = await SearchMovieAsync("Scraper Locator Other Name", 2026, "Scraper Locator Other Name (2026).mkv");

        id.Should().Be(existing);
    }

    [Fact]
    public async Task ANameMatchCarryingADifferentId_IsALookAlike_AndIsNotUsed()
    {
        var lookAlike = SeedMovie("Scraper Locator Twin", 2026, ("tmdb", "movie:7700003"));
        _factory.Locator.Answer = (_, _) => new LocatedMedia("Scraper Locator Twin", 2026,
            new Dictionary<string, string> { ["tmdb"] = "movie:7700004" });

        var id = await SearchMovieAsync("Scraper Locator Twin", 2026, "Scraper Locator Twin (2026).mkv");

        id.Should().NotBe(lookAlike);
    }

    [Fact]
    public async Task ANameMatchWithNoIdOfItsOwn_StillMatches_SoNoDuplicateIsMade()
    {
        var plain = SeedMovie("Scraper Locator Plain", 2026);
        _factory.Locator.Answer = (_, _) => new LocatedMedia("Scraper Locator Plain", 2026,
            new Dictionary<string, string> { ["tmdb"] = "movie:7700005" });

        var id = await SearchMovieAsync("Scraper Locator Plain", 2026, "Scraper Locator Plain (2026).mkv");

        id.Should().Be(plain);
    }

    // Live 2026-11-18: "11 Flowers (2012).mkv" was recorded against "11/11/11". A recorded file name can be wrong, so the id the
    // managing application reports must outrank it.
    [Fact]
    public async Task AWrongRecordedFileName_YieldsToTheIdTheManagingApplicationReports()
    {
        var wrong = SeedMovie("Scraper Locator Wrong Owner", 2011, ("tmdb", "movie:7700010"));
        var right = SeedMovie("Scraper Locator Right Owner", 2012, ("tmdb", "movie:7700011"));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            db.MediaItemKnownFileNames.Add(new MediaItemKnownFileName { MediaItemId = wrong, FileName = "Scraper Locator Right Owner (2012).mkv" });
            db.SaveChanges();
        }
        _factory.Locator.Answer = (_, _) => new LocatedMedia("Scraper Locator Right Owner", 2012,
            new Dictionary<string, string> { ["tmdb"] = "movie:7700011" });

        var id = await SearchMovieAsync("Scraper Locator Right Owner", 2012, "Scraper Locator Right Owner (2012).mkv");

        id.Should().Be(right);
        id.Should().NotBe(wrong);
    }

    [Fact]
    public async Task ARecordedFileName_WithNoLocatorAnswer_IsStillTrusted()
    {
        var owner = SeedMovie("Scraper Locator Trusted Owner", 2012);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            db.MediaItemKnownFileNames.Add(new MediaItemKnownFileName { MediaItemId = owner, FileName = "Scraper Locator Trusted Owner (2012).mkv" });
            db.SaveChanges();
        }

        var id = await SearchMovieAsync("Scraper Locator Trusted Owner", 2012, "Scraper Locator Trusted Owner (2012).mkv");

        id.Should().Be(owner);
    }

    [Fact]
    public async Task ALocatorThatKnowsNothing_LeavesNameMatchingExactlyAsItWas()
    {
        var existing = SeedMovie("Scraper Locator Unknown", 2026);

        var id = await SearchMovieAsync("Scraper Locator Unknown", 2026, "Scraper Locator Unknown (2026).mkv");

        id.Should().Be(existing);
        _factory.Locator.Asked.Should().ContainSingle(a => a.Family == LocatorFamily.Movie && a.Query.FileName == "Scraper Locator Unknown (2026).mkv");
    }

    [Fact]
    public async Task ALocatorThatThrows_IsSkipped_AndTheSearchStillSucceeds()
    {
        var existing = SeedMovie("Scraper Locator Throws", 2026);
        _factory.Locator.Answer = (_, _) => throw new InvalidOperationException("Radarr is down");

        var id = await SearchMovieAsync("Scraper Locator Throws", 2026, "Scraper Locator Throws (2026).mkv");

        id.Should().Be(existing);
    }

    [Fact]
    public async Task Sonarr_SeedsTheShowsId_WhenTheShowIsNew()
    {
        _factory.Locator.Answer = (family, _) => family == LocatorFamily.Tv
            ? new LocatedMedia("Scraper Locator Show", 2024, new Dictionary<string, string> { ["tmdb"] = "tv:7700006" })
            : null;
        var client = await AuthClientAsync();

        var resp = await client.GetAsync("/api/v1/scraper/tv/search?title=Scraper%20Locator%20Show&year=2024");

        resp.EnsureSuccessStatusCode();
        var id = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("id").GetInt32();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        (await db.MediaExternalIds.AnyAsync(x => x.MediaItemId == id && x.Source == "tmdb" && x.ExternalId == "tv:7700006")).Should().BeTrue();
    }
}
