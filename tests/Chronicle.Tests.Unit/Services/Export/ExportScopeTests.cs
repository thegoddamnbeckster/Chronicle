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
        Assert.True(scope.IdSources.Contains("imdb") && scope.IdSources.Contains("tmdb") && scope.IdSources.Contains("tvdb") && scope.IdSources.Contains("simkl"));
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
}
