using System.Net.Http.Json;
using Chronicle.Core.Helpers;
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

/// <summary>
/// Covers ScraperController.ResolveEpisodesLockedAsync (backs Kodi's getepisodelist/
/// getepisodedetails contract via EnsureEpisodesResolvedAsync, called from GET
/// /api/v1/scraper/tv/episodes).
///
/// Root-caused live (2026-09-12), same day as the two fixes elsewhere in this class: the
/// original version treated "this season already has at least one local episode" as
/// "permanently resolved, never check the provider again" -- correct for a finished season a
/// real file scan already fully covers, but it meant a CURRENTLY AIRING show could never learn
/// about a newly released episode through this endpoint either, no matter how many times Kodi
/// asked. A user would have needed the whole show removed and rescanned from scratch every
/// time a new episode dropped. Fixed to re-check EVERY known season on every call and top each
/// one up with any episode NUMBER it doesn't already have -- deliberately not special-cased to
/// just the latest season (per-user direction, 2026-09-12: "don't do special handling for the
/// latest episode, just get any episodes that aren't in Kodi yet"). These tests exist to prove
/// both halves of that: new episodes get added wherever they appear, and nothing already-known
/// ever gets duplicated.
/// </summary>
public class ScraperResolveEpisodesLockedTests : IClassFixture<EpisodeProviderTestFactory>
{
    private readonly EpisodeProviderTestFactory _factory;

    public ScraperResolveEpisodesLockedTests(EpisodeProviderTestFactory factory)
    {
        factory.SeedDatabase();
        _factory = factory;
        _factory.Provider.Reset();
    }

    private int EnsureTvType()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var existing = db.MediaTypes.FirstOrDefault(t => t.Name == "tv");
        if (existing is not null) return existing.Id;
        var mt = new MediaType
        {
            Name = "tv", DisplayName = "TV Shows", HierarchyLevels = 3,
            InteractionVerb = "watched", ProgressUnit = "minutes",
            IsBuiltIn = false, IsActive = true, CreatedAt = DateTime.UtcNow,
        };
        db.MediaTypes.Add(mt);
        db.SaveChanges();
        return mt.Id;
    }

    private int SeedShow(string showExternalId, string name = "Provider Episode Probe Show")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var mediaTypeId = EnsureTvType();

        var metadataJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [EpisodeProviderTestFactory.FakePluginId] = new Dictionary<string, object>
            {
                ["externalId"] = showExternalId,
            },
        });
        var show = new MediaItem
        {
            MediaTypeId = mediaTypeId, Name = name, HierarchyLevel = 0,
            MetadataJson = metadataJson,
            NormalizedName = MediaItemNormalizer.NormalizeName(name),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.MediaItems.Add(show);
        db.SaveChanges();
        return show.Id;
    }

    /// Seeds a season (with the given local episode numbers already present, each a plain stub)
    /// directly, bypassing this endpoint entirely -- simulates "already resolved by an earlier
    /// call or a real file scan" starting state.
    private int SeedSeasonWithEpisodes(int showId, int seasonNumber, params int[] episodeNumbers)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var mediaTypeId = EnsureTvType();

        var season = new MediaItem
        {
            MediaTypeId = mediaTypeId, Name = $"Season {seasonNumber}", ParentId = showId,
            HierarchyLevel = 1, Number = seasonNumber,
            NormalizedName = MediaItemNormalizer.NormalizeName($"Season {seasonNumber}"),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.MediaItems.Add(season);
        db.SaveChanges();

        foreach (var num in episodeNumbers)
        {
            db.MediaItems.Add(new MediaItem
            {
                MediaTypeId = mediaTypeId, Name = $"S{seasonNumber:D2}E{num:D2}", ParentId = season.Id,
                HierarchyLevel = 2, Number = num,
                NormalizedName = MediaItemNormalizer.NormalizeName($"S{seasonNumber:D2}E{num:D2}"),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
        }
        db.SaveChanges();
        return season.Id;
    }

    private async Task<HttpClient> AuthClientAsync()
    {
        var client = _factory.CreateClient();
        var username = $"scraper_epresolve_{Guid.NewGuid():N}";
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { username, password = "Password123!" });
        var token = System.Text.Json.JsonDocument.Parse(await reg.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task GetEpisodes_LatestKnownSeasonHasNewEpisodeUpstream_AddsOnlyTheNewOne()
    {
        var showId = SeedShow("show-1");
        var seasonId = SeedSeasonWithEpisodes(showId, seasonNumber: 4, 1, 2, 3);
        _factory.Provider.SeasonEpisodes[4] =
        [
            new ProviderEpisodeSummary(1, "Episode 1"),
            new ProviderEpisodeSummary(2, "Episode 2"),
            new ProviderEpisodeSummary(3, "Episode 3"),
            new ProviderEpisodeSummary(4, "Brand New Episode"),
        ];

        var client = await AuthClientAsync();
        var resp = await client.GetAsync($"/api/v1/scraper/tv/episodes?showId={showId}");
        resp.EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var episodeNumbers = db.MediaItems
            .Where(e => e.ParentId == seasonId && e.HierarchyLevel == 2)
            .Select(e => e.Number).ToList();
        episodeNumbers.Should().BeEquivalentTo(new int?[] { 1, 2, 3, 4 });

        // Exactly one season row for number 4 -- the existing container was reused, not
        // duplicated (the same class of bug already fixed in FileScanService today, just via
        // this endpoint's own separate code path).
        db.MediaItems.Count(s => s.ParentId == showId && s.HierarchyLevel == 1 && s.Number == 4)
            .Should().Be(1);
    }

    [Fact]
    public async Task GetEpisodes_LatestKnownSeasonUnchangedUpstream_AddsNothingAndDoesNotDuplicate()
    {
        var showId = SeedShow("show-2");
        var seasonId = SeedSeasonWithEpisodes(showId, seasonNumber: 1, 1, 2, 3);
        _factory.Provider.SeasonEpisodes[1] =
        [
            new ProviderEpisodeSummary(1, "Episode 1"),
            new ProviderEpisodeSummary(2, "Episode 2"),
            new ProviderEpisodeSummary(3, "Episode 3"),
        ];

        var client = await AuthClientAsync();
        var resp = await client.GetAsync($"/api/v1/scraper/tv/episodes?showId={showId}");
        resp.EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        db.MediaItems.Count(e => e.ParentId == seasonId && e.HierarchyLevel == 2).Should().Be(3);
        db.MediaItems.Count(s => s.ParentId == showId && s.HierarchyLevel == 1 && s.Number == 1)
            .Should().Be(1);
    }

    /// The behavior this whole fix exists for: NOT special-cased to only the latest season
    /// (per-user direction, 2026-09-12) -- an OLDER, already-known season that gained a new
    /// episode (e.g. a late-added special, or a provider correction) gets topped up exactly
    /// the same way the latest one does, even while a genuinely later season is also known.
    [Fact]
    public async Task GetEpisodes_OlderAlreadyKnownSeasonGainsAnEpisode_IsToppedUpToo()
    {
        var showId = SeedShow("show-3");
        var season1Id = SeedSeasonWithEpisodes(showId, seasonNumber: 1, 1, 2, 3);
        SeedSeasonWithEpisodes(showId, seasonNumber: 2, 1, 2);
        _factory.Provider.SeasonEpisodes[1] =
        [
            new ProviderEpisodeSummary(1, "Episode 1"),
            new ProviderEpisodeSummary(2, "Episode 2"),
            new ProviderEpisodeSummary(3, "Episode 3"),
            new ProviderEpisodeSummary(4, "Late-Added Episode"),
        ];
        _factory.Provider.SeasonEpisodes[2] =
        [
            new ProviderEpisodeSummary(1, "Episode 1"),
            new ProviderEpisodeSummary(2, "Episode 2"),
        ];

        var client = await AuthClientAsync();
        var resp = await client.GetAsync($"/api/v1/scraper/tv/episodes?showId={showId}");
        resp.EnsureSuccessStatusCode();

        _factory.Provider.QueriedSeasons.Should().Contain(1,
            "an older season must be re-checked too, not only the latest one");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var season1Numbers = db.MediaItems
            .Where(e => e.ParentId == season1Id && e.HierarchyLevel == 2)
            .Select(e => e.Number).ToList();
        season1Numbers.Should().BeEquivalentTo(new int?[] { 1, 2, 3, 4 });
    }

    [Fact]
    public async Task GetEpisodes_BrandNewSeasonUpstream_IsCreatedAlongsideExistingOnes()
    {
        var showId = SeedShow("show-4");
        SeedSeasonWithEpisodes(showId, seasonNumber: 1, 1, 2);
        _factory.Provider.SeasonEpisodes[1] = [];
        _factory.Provider.SeasonEpisodes[2] =
        [
            new ProviderEpisodeSummary(1, "Season 2 Premiere"),
        ];

        var client = await AuthClientAsync();
        var resp = await client.GetAsync($"/api/v1/scraper/tv/episodes?showId={showId}");
        resp.EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var season2 = db.MediaItems.FirstOrDefault(
            s => s.ParentId == showId && s.HierarchyLevel == 1 && s.Number == 2);
        season2.Should().NotBeNull();
        db.MediaItems.Count(e => e.ParentId == season2!.Id && e.HierarchyLevel == 2).Should().Be(1);
    }
}

/// <summary>Records every season number it was asked about, and returns whatever the test
/// configured for that number (defaulting to empty, per IMetadataProvider's own default
/// interface member) -- lets a test assert BOTH what got added and what was never even
/// queried.</summary>
public sealed class FakeEpisodeListProvider : IMetadataProvider
{
    public string PluginId => EpisodeProviderTestFactory.FakePluginId;
    public string Name => "Fake Episode Provider";
    public string Version => "1.0.0";
    public string Author => "Test";

    public Dictionary<int, List<ProviderEpisodeSummary>> SeasonEpisodes { get; } = new();
    public List<int> QueriedSeasons { get; } = [];

    public void Reset()
    {
        SeasonEpisodes.Clear();
        QueriedSeasons.Clear();
    }

    public MediaTypeSupport[] GetSupportedMediaTypes() => [];
    public PluginSettingsSchema GetSettingsSchema() => new();
    public void Configure(IReadOnlyDictionary<string, string> settings) { }

    public Task<IReadOnlyList<ScoredCandidate>> SearchAsync(MediaSearchContext context, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ScoredCandidate>>([]);

    public Task<MediaMetadata> GetByIdAsync(string externalId, CancellationToken ct = default) =>
        Task.FromResult(new MediaMetadata { ExternalId = externalId });

    public Task<byte[]> GetImageAsync(string url, CancellationToken ct = default) =>
        Task.FromResult(Array.Empty<byte>());

    public Task<bool> HealthCheckAsync(CancellationToken ct = default) => Task.FromResult(true);

    public Task<IReadOnlyList<ProviderEpisodeSummary>> GetEpisodeListAsync(
        string showExternalId, int seasonNumber, CancellationToken ct = default)
    {
        QueriedSeasons.Add(seasonNumber);
        var episodes = SeasonEpisodes.TryGetValue(seasonNumber, out var eps)
            ? eps
            : new List<ProviderEpisodeSummary>();
        return Task.FromResult<IReadOnlyList<ProviderEpisodeSummary>>(episodes);
    }
}

/// <summary>Wraps a single FakeEpisodeListProvider under EpisodeProviderTestFactory.FakePluginId
/// -- every other IPluginRegistry member is unused by ResolveEpisodesLockedAsync and just
/// returns empty/throws if ever reached, so a change accidentally depending on one fails loudly
/// rather than silently returning plausible-looking fake data.</summary>
public sealed class FakeSingleProviderPluginRegistry(FakeEpisodeListProvider provider) : IPluginRegistry
{
    public IReadOnlyList<IMetadataProvider> GetMetadataProviders() => [provider];

    public IReadOnlyList<(string PluginId, IMetadataProvider Provider, string? IconUrl)> GetMetadataProviderEntries() =>
        [(provider.PluginId, provider, null)];

    public IMetadataProvider? GetMetadataProvider(string pluginId) =>
        pluginId == provider.PluginId ? provider : null;

    public IReadOnlyList<IWidgetPlugin> GetWidgetPlugins() => [];
    public IReadOnlyList<IImportProvider> GetImportProviders() => [];
    public IImportProvider? GetImportProvider(string pluginId) => null;
    public IReadOnlyList<IReportPlugin> GetReportPlugins() => [];
    public IReadOnlyList<IFileScannerPlugin> GetFileScannerPlugins() => [];
    public IReadOnlyList<IThemePlugin> GetThemePlugins() => [];
    public IReadOnlyList<ISidecarFormatPlugin> GetSidecarFormatPlugins() => [];
    public ISidecarFormatPlugin? GetSidecarFormatPlugin(string pluginId) => null;
    public IReadOnlyList<LoadedPlugin> GetLoadedPlugins() => [];

    public Task<LoadedPlugin> LoadPluginAsync(
        int dbId, string dllPath, IReadOnlyDictionary<string, string> settings, CancellationToken ct = default) =>
        throw new NotSupportedException("Not used by ResolveEpisodesLockedAsync tests.");

    public void UnloadPlugin(int dbId) { }
}

/// <summary>Same InMemory-DB setup as ChronicleApiFactory, plus a fake single-provider
/// IPluginRegistry so ScraperController.EnsureEpisodesResolvedAsync has something real (if
/// scripted) to call instead of always seeing zero loaded plugins.</summary>
public sealed class EpisodeProviderTestFactory : ChronicleApiFactory
{
    public const string FakePluginId = "chronicle.plugin.fakeepisodeprovider";

    public FakeEpisodeListProvider Provider { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IPluginRegistry>();
            services.AddSingleton<IPluginRegistry>(new FakeSingleProviderPluginRegistry(Provider));
        });
    }
}
