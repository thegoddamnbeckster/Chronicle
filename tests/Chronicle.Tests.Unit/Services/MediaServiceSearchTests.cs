using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Chronicle.Services.Plugins;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// The global search box (GET /media/search, allLevels). Runs on real SQLite, since the ranking
/// is SQL (LIKE patterns in ORDER BY) and the in-memory provider wouldn't prove it translates.
/// </summary>
public sealed class MediaServiceSearchTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ChronicleDbContext _db;
    private readonly MediaService _svc;

    public MediaServiceSearchTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _svc = new MediaService(_db, Mock.Of<IPluginRegistry>());
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private int TypeId(string name)
    {
        var existing = _db.MediaTypes.SingleOrDefault(t => t.Name == name);
        if (existing is not null) return existing.Id;
        var t = new MediaType { Name = name, DisplayName = name, HierarchyLevels = 3, CreatedAt = DateTime.UtcNow };
        _db.MediaTypes.Add(t);
        _db.SaveChanges();
        return t.Id;
    }

    private MediaItem Add(string name, string type, int level = 0)
    {
        var item = new MediaItem
        {
            Name = name, MediaTypeId = TypeId(type), HierarchyLevel = level,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        _db.MediaItems.Add(item);
        _db.SaveChanges();
        return item;
    }

    [Fact]
    public async Task ExactTitlesComeFirst_EvenBehindManyEarlierPartialMatches()
    {
        // The live case (2026-10-02): partial matches added first used to fill the whole page.
        Add("(2000) Invincible", "music", 1);
        Add("Feel Invincible", "music", 2);
        for (var i = 0; i < 30; i++) Add($"Feel Invincible (remix {i:D2})", "music", 2);
        for (var i = 0; i < 30; i++) Add($"Invincible #{i}", "books", 2);
        var track = Add("Invincible", "music", 2);
        var film = Add("Invincible", "movies");
        var show = Add("INVINCIBLE", "tv");

        var results = (await _svc.SearchAsync("invincible", perPage: 20, allLevels: true)).ToList();

        // Exact titles first (ignoring case), top-level items before the track.
        results.Take(2).Select(r => r.Id).Should().BeEquivalentTo([film.Id, show.Id]);
        results[2].Id.Should().Be(track.Id);
    }

    [Fact]
    public async Task ThenTitlesStartingWithIt_ThenWordMatches_ThenAnywhere()
    {
        var anywhere = Add("Unconquerable", "movies");   // contains "conquer" mid-word
        var word = Add("The Conquerors", "movies");
        var prefix = Add("Conquest of Space", "movies");
        var exact = Add("Conquer", "movies");

        var results = (await _svc.SearchAsync("conquer", allLevels: true)).Select(r => r.Id).ToList();

        // "Conquest" doesn't contain "conquer", so it isn't a match at all.
        results.Should().Equal(exact.Id, word.Id, anywhere.Id);
        results.Should().NotContain(prefix.Id);
    }

    [Fact]
    public async Task PrefixBeatsWordStart()
    {
        var word = Add("The Matrix Reloaded", "movies");
        var prefix = Add("Matrix Revolutions", "movies");

        var results = (await _svc.SearchAsync("matrix", allLevels: true)).Select(r => r.Id).ToList();

        results.Should().Equal(prefix.Id, word.Id);
    }

    [Fact]
    public async Task AnExactAliasCountsAsAnExactMatch()
    {
        var other = Add("Amelie Goes to Paris", "movies");
        var amelie = Add("Le Fabuleux Destin d'Amélie Poulain", "movies");
        _db.Set<MediaItemAlias>().Add(new MediaItemAlias { MediaItemId = amelie.Id, Alias = "Amelie" });
        await _db.SaveChangesAsync();

        var results = (await _svc.SearchAsync("amelie", allLevels: true)).Select(r => r.Id).ToList();

        results.Should().Equal(amelie.Id, other.Id);
    }

    [Fact]
    public async Task PercentAndUnderscoreAreLiteral()
    {
        var wolf = Add("100% Wolf", "movies");
        Add("100 Wolves", "movies");
        Add("100x Wolf", "movies");

        (await _svc.SearchAsync("100% wolf", allLevels: true)).Select(r => r.Id).Should().Equal(wolf.Id);
        (await _svc.SearchAsync("100_wolf", allLevels: true)).Should().BeEmpty();
    }

    [Fact]
    public async Task WithoutAllLevels_OnlyTopLevelItems()
    {
        Add("Invincible", "music", 2);
        var show = Add("Invincible", "tv");

        (await _svc.SearchAsync("invincible")).Select(r => r.Id).Should().Equal(show.Id);
    }
}
