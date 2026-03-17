using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

public class ScanFolderServiceTests
{
    private static ChronicleDbContext MakeDb()
    {
        var opts = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ChronicleDbContext(opts);
    }

    private static async Task<MediaType> SeedMediaTypeAsync(ChronicleDbContext db)
    {
        var mt = new MediaType
        {
            Name = "Movies",
            DisplayName = "Movies",
            HierarchyLevels = 1,
            CreatedAt = DateTime.UtcNow
        };
        db.MediaTypes.Add(mt);
        await db.SaveChangesAsync();
        return mt;
    }

    [Fact]
    public async Task CreateAsync_ReturnsSavedFolderWithCorrectValues()
    {
        var db = MakeDb();
        var mt = await SeedMediaTypeAsync(db);
        var svc = new ScanFolderService(db);

        var folder = await svc.CreateAsync(new CreateScanFolderRequest("/tmp/movies", mt.Id, true));

        Assert.Equal("/tmp/movies", folder.Path);
        Assert.Equal(mt.Id, folder.MediaTypeId);
        Assert.True(folder.Recursive);
        Assert.True(folder.IsEnabled);
        Assert.True(folder.Id > 0);
    }

    [Fact]
    public async Task ValidatePathAsync_ReturnsFalseForNonExistentPath()
    {
        var db = MakeDb();
        var svc = new ScanFolderService(db);

        var result = await svc.ValidatePathAsync("/this/path/does/not/exist/chronicle-test-xyz");

        Assert.False(result.Valid);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task DeleteAsync_ThrowsForNonExistentId()
    {
        var db = MakeDb();
        var svc = new ScanFolderService(db);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.DeleteAsync(9999));
    }
}
