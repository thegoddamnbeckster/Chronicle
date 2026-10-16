using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Tests.Unit.Services;

public class FolderPathBackfillTests
{
    private static ChronicleDbContext NewDb() => new(new DbContextOptionsBuilder<ChronicleDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static string FileJson(string file) =>
        System.Text.Json.JsonSerializer.Serialize(new { fileScanner = new { filePaths = new[] { file }, folderPath = (string?)null } });

    private static string? FolderOf(string? json) =>
        System.Text.Json.JsonDocument.Parse(json!).RootElement.GetProperty("fileScanner").GetProperty("folderPath").GetString();

    private static MediaItem Item(string json) => new()
    {
        Name = "x", MediaTypeId = 1, MetadataJson = json, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task FillsInTheFolderFromTheFirstFile_AndRunsOnlyOnce()
    {
        await using var db = NewDb();
        const string file = "E:/Music/A/01.flac";
        var first = Item(FileJson(file));
        db.MediaItems.Add(first);
        await db.SaveChangesAsync();
        var service = new FileScanService(db, null!, null!, null!, null!, null!);

        await service.BackfillFolderPathsAsync();
        FolderOf((await db.MediaItems.FindAsync(first.Id))!.MetadataJson).Should().Be(Path.GetDirectoryName(file));
        (await db.AppSettings.AnyAsync(s => s.Key == FileScanService.BackfillFolderPathsCompleteKey)).Should().BeTrue();

        // A later row in the same shape is left alone: the pass is a one-time repair, not a standing scan.
        var later = Item(FileJson("E:/Music/B/01.flac"));
        db.MediaItems.Add(later);
        await db.SaveChangesAsync();
        await service.BackfillFolderPathsAsync();
        FolderOf((await db.MediaItems.FindAsync(later.Id))!.MetadataJson).Should().BeNull();
    }
}
