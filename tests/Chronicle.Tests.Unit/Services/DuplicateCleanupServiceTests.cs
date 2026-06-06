using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicle.Tests.Unit.Services;

public class DuplicateCleanupServiceTests : IDisposable
{
    private readonly ChronicleDbContext _context;
    private readonly DuplicateCleanupService _service;
    private readonly MediaType _moviesType;
    private readonly MediaType _faneditsType;

    public DuplicateCleanupServiceTests()
    {
        var options = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _context = new ChronicleDbContext(options);

        // Seed media types
        _moviesType  = new MediaType { Name = "movies",   DisplayName = "Movies",    HierarchyLevels = 1 };
        _faneditsType = new MediaType { Name = "fanedits", DisplayName = "Fan Edits", HierarchyLevels = 1 };
        _context.MediaTypes.AddRange(_moviesType, _faneditsType);
        _context.SaveChanges();

        var services = new ServiceCollection();
        services.AddSingleton(_context);
        services.AddDbContext<ChronicleDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));
        var provider = services.BuildServiceProvider();

        var scopeFactory = new DirectScopeFactory(_context);
        _service = new DuplicateCleanupService(scopeFactory);
    }

    public void Dispose() => _context.Dispose();

    // ── BUG-009: folderPath must NOT be used as the duplicate key ─────────────

    [Fact]
    public async Task RunAsync_DoesNotGroupItemsBySharedFolderPath()
    {
        // Items in the same folder (e.g. TV episodes) share a folderPath but each
        // has a distinct filePaths entry — they must NOT be treated as duplicates.
        var sharedFolder = "/media/TV/Breaking Bad/Season 1";
        _context.MediaItems.AddRange(
            MakeItem("s01e01.mkv", sharedFolder, "Pilot"),
            MakeItem("s01e02.mkv", sharedFolder, "Cat's in the Bag"),
            MakeItem("s01e03.mkv", sharedFolder, "...And the Bag's in the River")
        );
        await _context.SaveChangesAsync();

        var removed = await _service.RunAsync();

        removed.Should().Be(0, "items sharing a folder but with distinct filePaths are not duplicates");
        _context.MediaItems.Count().Should().Be(3);
    }

    [Fact]
    public async Task RunAsync_DetectsRealDuplicate_SameFilePath()
    {
        // Two items pointing at the exact same file are genuine duplicates.
        // The OLDER item (lower Id) survives regardless of metadata richness.
        const string folder = "/media/Movies/Inception (2010)";

        // Older item added first (will get a lower Id) — no metadata.
        var older = MakeItem("Inception.mkv", folder, "Inception");
        _context.MediaItems.Add(older);
        await _context.SaveChangesAsync();

        // Newer item added after (higher Id) — richer metadata, but should still lose.
        var newer = MakeItem("Inception.mkv", folder, "Inception", posterUrl: "/img/poster.jpg", overview: "A thief...");
        _context.MediaItems.Add(newer);
        await _context.SaveChangesAsync();

        var removed = await _service.RunAsync();

        removed.Should().Be(1);
        _context.MediaItems.Count().Should().Be(1);
        // The OLDER item (lower Id) survives — age beats metadata richness.
        _context.MediaItems.Single().Id.Should().Be(older.Id);
    }

    [Fact]
    public async Task RunAsync_DetectsCrossTypeDuplicate_MovieAndFanEdit()
    {
        // BUG-010: same physical file tracked under "movies" and "fanedits" types.
        const string folder = "/media/FanEdits/Apocalypse Now";

        var asMovie   = MakeItem("ApocalypseNow_Redux.mkv", folder, "Apocalypse Now", typeId: _moviesType.Id,
                                 posterUrl: "/img/p.jpg", overview: "War film...");
        var asFanEdit = MakeItem("ApocalypseNow_Redux.mkv", folder, "Apocalypse Now Redux", typeId: _faneditsType.Id);
        _context.MediaItems.AddRange(asMovie, asFanEdit);
        await _context.SaveChangesAsync();

        var removed = await _service.RunAsync();

        removed.Should().Be(1, "same physical file should be detected as a duplicate regardless of media type");
        _context.MediaItems.Count().Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_ItemsWithoutFileScannerMetadata_AreIgnored()
    {
        // Items created purely through TMDB (no file scanner data) have no filePaths
        // and must never be falsely matched as duplicates.
        var tmdbOnly = new MediaItem
        {
            Name         = "The Matrix",
            MediaTypeId  = _moviesType.Id,
            MetadataJson = JsonSerializer.Serialize(new { tmdb = new { id = 603 } }),
            CreatedAt    = DateTime.UtcNow,
            UpdatedAt    = DateTime.UtcNow,
        };
        var tmdbOnly2 = new MediaItem
        {
            Name         = "The Matrix",
            MediaTypeId  = _moviesType.Id,
            MetadataJson = JsonSerializer.Serialize(new { tmdb = new { id = 603 } }),
            CreatedAt    = DateTime.UtcNow,
            UpdatedAt    = DateTime.UtcNow,
        };
        _context.MediaItems.AddRange(tmdbOnly, tmdbOnly2);
        await _context.SaveChangesAsync();

        var removed = await _service.RunAsync();

        removed.Should().Be(0, "items without fileScanner metadata are excluded from file-path duplicate detection");
    }

    [Fact]
    public async Task RunAsync_LargeSeasonFolder_AllEpisodesPreserved()
    {
        // Regression for BUG-009: 20 episodes in the same folder must all survive.
        const string folder = "/media/TV/The Wire/Season 3";
        var episodes = Enumerable.Range(1, 20)
            .Select(i => MakeItem($"s03e{i:D2}.mkv", folder, $"Episode {i}"))
            .ToList();
        _context.MediaItems.AddRange(episodes);
        await _context.SaveChangesAsync();

        var removed = await _service.RunAsync();

        removed.Should().Be(0);
        _context.MediaItems.Count().Should().Be(20);
    }

    [Fact]
    public async Task RunAsync_UserLibraryReassignedToWinner_BeforeLoserDeleted()
    {
        // User data on the loser must be migrated to the winner (older item), not lost.
        const string folder = "/media/Movies/Dune";
        // winner = older item (added and saved first → lower Id)
        var winner = MakeItem("Dune.mkv", folder, "Dune");
        _context.MediaItems.Add(winner);
        await _context.SaveChangesAsync();
        // loser = newer item (higher Id)
        var loser = MakeItem("Dune.mkv", folder, "Dune", posterUrl: "/p.jpg");
        _context.MediaItems.Add(loser);
        await _context.SaveChangesAsync();

        var user = new User { Username = "alice", PasswordHash = "x", CreatedAt = DateTime.UtcNow };
        _context.Users.Add(user);
        await _context.SaveChangesAsync();

        _context.UserLibraries.Add(new UserLibrary
        {
            UserId      = user.Id,
            MediaItemId = loser.Id,
            Status      = LibraryStatus.Completed,
            AddedAt     = DateTime.UtcNow,
        });
        await _context.SaveChangesAsync();

        var removed = await _service.RunAsync();

        removed.Should().Be(1);
        var lib = _context.UserLibraries.Single();
        lib.MediaItemId.Should().NotBe(loser.Id, "library entry should have been re-pointed to the winner");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private MediaItem MakeItem(
        string fileName,
        string folder,
        string name,
        int?   typeId    = null,
        string? posterUrl = null,
        string? overview  = null)
    {
        var fullPath = $"{folder}/{fileName}";
        return new MediaItem
        {
            Name         = name,
            MediaTypeId  = typeId ?? _moviesType.Id,
            PosterUrl    = posterUrl,
            Overview     = overview,
            MetadataJson = JsonSerializer.Serialize(new
            {
                fileScanner = new
                {
                    importedAt = DateTime.UtcNow,
                    filePaths  = new[] { fullPath },
                    folderPath = folder,
                }
            }),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
    }
}

/// <summary>
/// Minimal <see cref="IServiceScopeFactory"/> that resolves a pre-built
/// <see cref="ChronicleDbContext"/> — avoids full DI container in unit tests.
/// </summary>
file sealed class DirectScopeFactory : IServiceScopeFactory
{
    private readonly ChronicleDbContext _ctx;
    public DirectScopeFactory(ChronicleDbContext ctx) => _ctx = ctx;

    public IServiceScope CreateScope() => new DirectScope(_ctx);

    private sealed class DirectScope : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; }
        public DirectScope(ChronicleDbContext ctx)
            => ServiceProvider = new DirectServiceProvider(ctx);
        public void Dispose() { }
    }

    private sealed class DirectServiceProvider : IServiceProvider
    {
        private readonly ChronicleDbContext _ctx;
        private static readonly IMetadataResolutionService _noopResolution = new NoopResolutionService();
        public DirectServiceProvider(ChronicleDbContext ctx) => _ctx = ctx;
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(ChronicleDbContext))          return _ctx;
            if (serviceType == typeof(IMetadataResolutionService))  return _noopResolution;
            return null;
        }
    }

    /// <summary>
    /// No-op resolution service for unit tests — ResolveAsync is a side effect we don't
    /// need to verify in DuplicateCleanupService tests.
    /// </summary>
    private sealed class NoopResolutionService : IMetadataResolutionService
    {
        public Task ResolveAsync(Chronicle.Core.Models.MediaItem item, ChronicleDbContext db, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task ResolveAllForMediaTypeAsync(string mediaTypeName, CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
