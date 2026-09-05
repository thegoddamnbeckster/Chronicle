using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Chronicle.Tests.Unit.Services;

public class TitleSanitizationServiceTests : IDisposable
{
    private readonly ChronicleDbContext _context;
    private readonly Mock<IMetadataResolutionService> _resolutionMock = new();
    private readonly TitleSanitizationService _task;

    public TitleSanitizationServiceTests()
    {
        var options = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new ChronicleDbContext(options);

        var services = new ServiceCollection();
        services.AddSingleton(_context);
        services.AddSingleton(_resolutionMock.Object);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        _task = new TitleSanitizationService(scopeFactory, NullLogger<TitleSanitizationService>.Instance);

        _context.MediaTypes.Add(new MediaType { Id = 1, Name = "movies", DisplayName = "Movies", IsActive = true });
        _context.SaveChanges();
    }

    public void Dispose() => _context.Dispose();

    private static string WikipediaMetadataJson(string title) =>
        "{\"chronicle.plugin.wikipedia\":{\"title\":\"" + title + "\"}}";

    [Fact]
    public async Task ExecuteAsync_DefaultConfig_StripsWikipediaDisambiguatorSuffix()
    {
        // Regression coverage for the real, repeated bug this task exists to stop recurring:
        // "F9 (film)", "Kryptonite (3 Doors Down song)", etc. left in the stored partition.
        var item = new MediaItem
        {
            Id = 1, Name = "F9", MediaTypeId = 1, HierarchyLevel = 0,
            MetadataJson = WikipediaMetadataJson("F9 (film)"),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _context.MediaItems.Add(item);
        await _context.SaveChangesAsync();

        await _task.ExecuteAsync(CancellationToken.None);

        var reloaded = await _context.MediaItems.FirstAsync(m => m.Id == 1);
        reloaded.MetadataJson.Should().Contain("\"title\":\"F9\"");
        reloaded.MetadataJson.Should().NotContain("(film)");
        _resolutionMock.Verify(r => r.ResolveAsync(
            It.Is<MediaItem>(m => m.Id == 1), _context, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_TitleWithoutSuffix_LeavesItUntouchedAndDoesNotResolve()
    {
        var item = new MediaItem
        {
            Id = 2, Name = "Clean Title", MediaTypeId = 1, HierarchyLevel = 0,
            MetadataJson = WikipediaMetadataJson("Clean Title"),
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _context.MediaItems.Add(item);
        await _context.SaveChangesAsync();

        await _task.ExecuteAsync(CancellationToken.None);

        _resolutionMock.Verify(r => r.ResolveAsync(
            It.IsAny<MediaItem>(), It.IsAny<ChronicleDbContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ItemWithNoWikipediaPartition_IsSkipped()
    {
        var item = new MediaItem
        {
            Id = 3, Name = "No Wikipedia Data", MediaTypeId = 1, HierarchyLevel = 0,
            MetadataJson = """{"chronicle.plugin.tmdb":{"title":"Something (2021)"}}""",
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _context.MediaItems.Add(item);
        await _context.SaveChangesAsync();

        var act = async () => await _task.ExecuteAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
        _resolutionMock.Verify(r => r.ResolveAsync(
            It.IsAny<MediaItem>(), It.IsAny<ChronicleDbContext>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_MultipleAffectedItems_FixesAllOfThem()
    {
        _context.MediaItems.AddRange(
            new MediaItem
            {
                Id = 10, Name = "Loser", MediaTypeId = 1, HierarchyLevel = 0,
                MetadataJson = WikipediaMetadataJson("Loser (3 Doors Down song)"),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            },
            new MediaItem
            {
                Id = 11, Name = "Predator", MediaTypeId = 1, HierarchyLevel = 0,
                MetadataJson = WikipediaMetadataJson("Predator (film)"),
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
        await _context.SaveChangesAsync();

        await _task.ExecuteAsync(CancellationToken.None);

        (await _context.MediaItems.FirstAsync(m => m.Id == 10)).MetadataJson.Should().Contain("\"title\":\"Loser\"");
        (await _context.MediaItems.FirstAsync(m => m.Id == 11)).MetadataJson.Should().Contain("\"title\":\"Predator\"");
        _resolutionMock.Verify(r => r.ResolveAsync(
            It.IsAny<MediaItem>(), It.IsAny<ChronicleDbContext>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }
}
