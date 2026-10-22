using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services.Export;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chronicle.Tests.Unit.Services.Export;

public class ExportScopeTests
{
    private static ChronicleDbContext NewDb() =>
        new(new DbContextOptionsBuilder<ChronicleDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    [Fact]
    public async Task WithNoSettings_TheShippedDefaultsApply()
    {
        await using var db = NewDb();

        var scope = await ExportScope.LoadAsync(db);

        Assert.True(scope.MovieTypes.Contains("movies") && scope.MovieTypes.Contains("anime_movies"));
        Assert.True(scope.ShowTypes.Contains("tv") && scope.ShowTypes.Contains("anime"));
        Assert.DoesNotContain("fanedits", scope.MovieTypes);   // fan edits carry the original film's ids; exporting one would mark the original watched
    }

    [Fact]
    public async Task ASettingReplacesTheDefault_AndMatchesNamesIgnoringCase()
    {
        await using var db = NewDb();
        db.AppSettings.Add(new AppSetting { Key = ExportScope.MovieTypesKey, Value = "[\"Films\",\"anime_movies\"]" });
        await db.SaveChangesAsync();

        var scope = await ExportScope.LoadAsync(db);

        Assert.Contains("films", scope.MovieTypes);
        Assert.DoesNotContain("movies", scope.MovieTypes);
        Assert.Contains("tv", scope.ShowTypes);                 // the others keep their defaults
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("")]
    public async Task AMalformedOrEmptySetting_FallsBackToTheDefaultInsteadOfExportingNothing(string raw)
    {
        await using var db = NewDb();
        db.AppSettings.Add(new AppSetting { Key = ExportScope.ShowTypesKey, Value = raw });
        await db.SaveChangesAsync();

        var scope = await ExportScope.LoadAsync(db);

        Assert.Contains("tv", scope.ShowTypes);
    }

    [Fact]
    public async Task TypeIds_AreResolvedFromTheNamesInOneQuery()
    {
        await using var db = NewDb();
        db.MediaTypes.AddRange(
            new MediaType { Id = 1, Name = "movies", DisplayName = "Movies", HierarchyLevels = 1, CreatedAt = DateTime.UtcNow },
            new MediaType { Id = 2, Name = "tv", DisplayName = "TV", HierarchyLevels = 3, CreatedAt = DateTime.UtcNow },
            new MediaType { Id = 3, Name = "fanedits", DisplayName = "Fan Edits", HierarchyLevels = 1, CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var (movies, shows) = await (await ExportScope.LoadAsync(db)).TypeIdsAsync(db);

        Assert.Equal([1], movies);
        Assert.Equal([2], shows);
    }
}
