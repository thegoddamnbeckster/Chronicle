using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicle.Tests.Integration
{
    /// <summary>
    /// The duplicate cleanup finds shared file paths and shared external ids with queries the in-memory provider only emulates
    /// (GROUP BY with COUNT(DISTINCT), lower() keys in an IN list). Run here against a real SQLite file with the real migrations.
    /// </summary>
    public class DuplicateCleanupSqliteTests : IClassFixture<SqliteApiFactory>
    {
        private readonly SqliteApiFactory _factory;

        public DuplicateCleanupSqliteTests(SqliteApiFactory factory) => _factory = factory;

        private static string Scanned(string file) =>
            JsonSerializer.Serialize(new { fileScanner = new { filePaths = new[] { file }, folderPath = Path.GetDirectoryName(file) } });

        [Fact]
        public async Task TwoItemsSharingAFilePath_AndTwoSharingAnExternalId_AreEachMergedIntoOne_AndTheRestAreLeftAlone()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var type = await db.MediaTypes.FirstAsync();
            MediaItem Item(string name, string? json = null) => new()
            {
                Name = name, MediaTypeId = type.Id, HierarchyLevel = 0, Year = 2001, MetadataJson = json,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };

            var file = Path.Combine("E:", "Movies", "Same File (2001)", "same.mkv");
            var pathA = Item("Same File", Scanned(file));
            var pathB = Item("Same File (copy)", Scanned(file));
            var idA = Item("Shared Id Movie", Scanned(Path.Combine("E:", "Movies", "Shared Id Movie (2001)", "a.mkv")));
            var idB = Item("Shared Id Movie");
            var alone = Item("Standing Alone");
            var notShared1 = Item("Different One");
            var notShared2 = Item("Different Two");
            db.MediaItems.AddRange(pathA, pathB, idA, idB, alone, notShared1, notShared2);
            await db.SaveChangesAsync();
            db.MediaExternalIds.AddRange(
                new MediaExternalId { MediaItemId = idA.Id, Source = "tmdb", ExternalId = "movie:777" },
                new MediaExternalId { MediaItemId = idB.Id, Source = "TMDB", ExternalId = "MOVIE:777" },   // same id, different capitalisation
                new MediaExternalId { MediaItemId = notShared1.Id, Source = "tmdb", ExternalId = "movie:1" },
                new MediaExternalId { MediaItemId = notShared2.Id, Source = "tmdb", ExternalId = "movie:2" });
            await db.SaveChangesAsync();

            var removed = await _factory.Services.GetRequiredService<DuplicateCleanupService>().RunAsync();

            removed.Should().Be(2);
            using var check = _factory.Services.CreateScope();
            var names = await check.ServiceProvider.GetRequiredService<ChronicleDbContext>().MediaItems
                .AsNoTracking().Where(m => new[] { "Same File", "Same File (copy)", "Shared Id Movie", "Standing Alone", "Different One", "Different Two" }.Contains(m.Name))
                .Select(m => m.Name).ToListAsync();
            names.Count(n => n.StartsWith("Same File")).Should().Be(1);
            names.Count(n => n == "Shared Id Movie").Should().Be(1);
            names.Should().Contain(["Standing Alone", "Different One", "Different Two"]);
        }
    }
}
