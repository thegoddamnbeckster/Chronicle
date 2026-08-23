using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Services.Import;
using Chronicle.Services.Plugins;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Chronicle.Tests.Unit.Services
{
    public class ImportServiceTests : IDisposable
    {
        private readonly ChronicleDbContext _db;
        private readonly Mock<IPluginRegistry> _registry = new();
        private readonly Mock<IPluginService> _pluginService = new();
        private readonly ImportService _service;

        public ImportServiceTests()
        {
            var options = new DbContextOptionsBuilder<ChronicleDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;
            _db = new ChronicleDbContext(options);
            _service = new ImportService(_db, _registry.Object, _pluginService.Object);

            _db.MediaTypes.Add(new MediaType { Id = 1, Name = "tv", DisplayName = "TV Shows", CreatedAt = DateTime.UtcNow });
            _db.MediaTypes.Add(new MediaType { Id = 2, Name = "movie", DisplayName = "Movies", CreatedAt = DateTime.UtcNow });
            // A TV item that shares its exact name+year with an in-flight movie import below —
            // the title fallback must not cross media types just because these collide.
            _db.MediaItems.Add(new MediaItem
            {
                Id = 1, MediaTypeId = 1, Name = "Chronicle", Year = 2026,
                HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            });
            _db.SaveChanges();
        }

        [Fact]
        public async Task ImportHistoryAsync_TitleMatchesAcrossTypes_ScopesMatchToImportedMediaType()
        {
            var provider = new Mock<IImportProvider>();
            provider.Setup(p => p.GetWatchHistoryAsync(It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<ImportedWatchEvent>
                {
                    new ImportedWatchEvent(
                        ExternalId: "trakt:999",
                        AdditionalIds: new Dictionary<string, string>(),
                        MediaType: "movie",
                        Title: "Chronicle",
                        Year: 2026,
                        WatchedAt: DateTimeOffset.UtcNow)
                });
            _registry.Setup(r => r.GetImportProvider("chronicle.plugin.trakt")).Returns(provider.Object);

            var result = await _service.ImportHistoryAsync("chronicle.plugin.trakt", userId: 1);

            result.Imported.Should().Be(1);
            result.Errors.Should().BeEmpty();

            var evt = await _db.InteractionEvents.SingleAsync();
            evt.MediaItemId.Should().NotBe(1); // must not land on the TV item with the same name/year
            var matched = await _db.MediaItems.FindAsync(evt.MediaItemId);
            matched!.MediaTypeId.Should().Be(2); // a movie, not the TV item
        }

        public void Dispose() => _db.Dispose();
    }
}
