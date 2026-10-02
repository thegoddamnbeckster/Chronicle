using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Chronicle.Services.Plugins;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// The one-time purge of NFO-derived data (2026-10-02). Real SQLite rather than the in-memory
/// provider, so JSON and cascade behaviour match production.
/// </summary>
public sealed class LegacyNfoPurgeServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ChronicleDbContext _db;
    private readonly Mock<IPluginService> _plugins = new();
    private readonly Mock<IMetadataResolutionService> _resolver = new();
    private readonly List<int> _resolvedIds = [];
    private readonly string _contentRoot;
    private readonly LegacyNfoPurgeService _svc;
    private readonly int _movieTypeId;

    public LegacyNfoPurgeServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _movieTypeId = _db.MediaTypes.Single(t => t.Name == "movies").Id;

        _resolver.Setup(r => r.ResolveAsync(It.IsAny<MediaItem>(), It.IsAny<ChronicleDbContext>(), It.IsAny<CancellationToken>()))
            .Callback<MediaItem, ChronicleDbContext, CancellationToken>((item, _, _) => _resolvedIds.Add(item.Id))
            .Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddSingleton(_db);
        services.AddSingleton(_plugins.Object);
        services.AddSingleton(_resolver.Object);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        _contentRoot = Directory.CreateTempSubdirectory("chronicle_nfo_purge_").FullName;
        var env = new Mock<IHostEnvironment>();
        env.SetupGet(e => e.ContentRootPath).Returns(_contentRoot);

        _svc = new LegacyNfoPurgeService(scopeFactory, env.Object, NullLogger<LegacyNfoPurgeService>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        Directory.Delete(_contentRoot, recursive: true);
    }

    private MediaItem AddItem(string name, string? metadataJson)
    {
        var item = new MediaItem
        {
            MediaTypeId = _movieTypeId, Name = name, MetadataJson = metadataJson,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _db.MediaItems.Add(item);
        _db.SaveChanges();
        return item;
    }

    private void AddCredit(int mediaItemId, int? personId, string source) =>
        _db.MediaCredits.Add(new MediaCredit
        {
            MediaItemId = mediaItemId, PersonMediaItemId = personId, PersonName = "Someone",
            Role = "Actor", Source = source,
        });

    private JsonElement Json(int id) =>
        JsonDocument.Parse(_db.MediaItems.AsNoTracking().Single(m => m.Id == id).MetadataJson!).RootElement;

    [Fact]
    public async Task StripsNfoData_KeepsEverythingElse_AndReResolvesOnlyItemsThatLostData()
    {
        var withNfo = AddItem("Control", """
            {"chronicle.plugin.tmdb":{"title":"Control"},
             "chronicle_scraper.legacy_nfo":{"title":"Control","year":2023},
             "fileScanner":{"filePaths":["N:\\Control.mkv"],"nfoPath":"N:\\Control.nfo","nfoRaw":"<movie/>","nfoParsed":null}}
            """);
        var nullKeysOnly = AddItem("Other", """{"fileScanner":{"filePaths":["N:\\Other.mkv"],"nfoPath":null}}""");
        var untouched = AddItem("Plain", """{"chronicle.plugin.tmdb":{"title":"Plain"}}""");

        await _svc.RunOnceAsync();

        var a = Json(withNfo.Id);
        Assert.False(a.TryGetProperty("chronicle_scraper.legacy_nfo", out _));
        Assert.True(a.TryGetProperty("chronicle.plugin.tmdb", out _));
        var fs = a.GetProperty("fileScanner");
        Assert.False(fs.TryGetProperty("nfoPath", out _));
        Assert.False(fs.TryGetProperty("nfoRaw", out _));
        Assert.False(fs.TryGetProperty("nfoParsed", out _));
        Assert.Equal(1, fs.GetProperty("filePaths").GetArrayLength());

        Assert.False(Json(nullKeysOnly.Id).GetProperty("fileScanner").TryGetProperty("nfoPath", out _));
        Assert.Equal([withNfo.Id], _resolvedIds); // the null-only and untouched items need no re-resolve
        Assert.True(Json(untouched.Id).TryGetProperty("chronicle.plugin.tmdb", out _));
    }

    [Fact]
    public async Task RemovesNfoCredits_AndOnlyPersonStubsLeftWithNothing()
    {
        var movie = AddItem("Control", """{"chronicle.plugin.tmdb":{"title":"Control"}}""");
        var nfoOnlyPerson = AddItem("Stub Person", null);
        var sharedPerson = AddItem("Real Person", """{"chronicle.plugin.tmdb":{"title":"Real Person"}}""");
        AddCredit(movie.Id, nfoOnlyPerson.Id, "legacy_nfo");
        AddCredit(movie.Id, sharedPerson.Id, "legacy_nfo");
        AddCredit(movie.Id, sharedPerson.Id, "tmdb");
        _db.SaveChanges();

        await _svc.RunOnceAsync();

        Assert.Empty(_db.MediaCredits.Where(c => c.Source == "legacy_nfo"));
        Assert.Single(_db.MediaCredits.Where(c => c.Source == "tmdb"));
        Assert.Null(_db.MediaItems.AsNoTracking().SingleOrDefault(m => m.Id == nfoOnlyPerson.Id));
        Assert.NotNull(_db.MediaItems.AsNoTracking().SingleOrDefault(m => m.Id == sharedPerson.Id));
    }

    [Fact]
    public async Task UninstallsTheSidecarPlugin_AndDeletesItsFolder()
    {
        _db.Plugins.Add(new Plugin
        {
            PluginId = "chronicle.plugin.kodi.nfo", Name = "Kodi NFO", Version = "1.0.1", Author = "x",
            DllPath = "x.dll", InstalledAt = DateTime.UtcNow,
        });
        _db.SaveChanges();
        var dir = Directory.CreateDirectory(Path.Combine(_contentRoot, "plugins", "chronicle.plugin.kodi.nfo"));
        File.WriteAllText(Path.Combine(dir.FullName, "manifest.json"), "{}");

        await _svc.RunOnceAsync();

        _plugins.Verify(p => p.UninstallPluginAsync(It.IsAny<int>()), Times.Once);
        Assert.False(Directory.Exists(dir.FullName));
    }

    [Fact]
    public async Task RunsOnlyOnce()
    {
        var item = AddItem("Control", """{"chronicle_scraper.legacy_nfo":{"title":"Control"}}""");

        await _svc.RunOnceAsync();
        var marker = _db.AppSettings.AsNoTracking().Single(s => s.Key == "maintenance.legacy_nfo_purge");
        Assert.NotNull(marker);

        // Data reappearing after the purge (e.g. restored by hand) is left alone on later starts.
        var tracked = _db.MediaItems.Single(m => m.Id == item.Id);
        tracked.MetadataJson = """{"chronicle_scraper.legacy_nfo":{"title":"Control"}}""";
        _db.SaveChanges();
        _resolvedIds.Clear();

        await _svc.RunOnceAsync();

        Assert.True(Json(item.Id).TryGetProperty("chronicle_scraper.legacy_nfo", out _));
        Assert.Empty(_resolvedIds);
    }
}
