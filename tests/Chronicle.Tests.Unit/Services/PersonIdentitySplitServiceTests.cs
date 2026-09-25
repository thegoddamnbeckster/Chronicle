using System.Text.Json.Nodes;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

/// <summary>
/// Root-caused live (2026-09-24): Chris Evans (actor, TMDB birthDate 1981) carried the Wikipedia
/// article of the 1966 English presenter, so the presenter's photo became the main photo. The
/// split task must move ONLY a provable conflict (both years known, gap >= 2) onto its own record.
/// </summary>
public class PersonIdentitySplitServiceTests
{
    private const string Wiki = PersonIdentitySplitService.WikipediaPluginId;

    private static string Json(string tmdbBirth, string? wikiTag, string wikiPoster = "https://w/x.jpg")
    {
        var tags = wikiTag is null ? "[]" : $"[\"{wikiTag}\", \"English male actors\"]";
        return $$$"""
        {
          "chronicle.plugin.tmdb": {"externalId":"person:1","extendedData":{"birthDate":"{{{tmdbBirth}}}"}},
          "{{{Wiki}}}": {"externalId":"wikipedia:en:X_(presenter)","title":"Chris Evans","posterUrl":"{{{wikiPoster}}}","tags":{{{tags}}}}
        }
        """;
    }

    [Fact]
    public void Detect_YearsFarApart_IsConflict()
    {
        Assert.True(PersonIdentitySplitService.TryDetectConflict(Json("1981-06-13", "1966 births"), out var t, out var w));
        Assert.Equal(1981, t);
        Assert.Equal(1966, w);
    }

    [Fact]
    public void Detect_OffByOneYear_IsNotConflict() =>
        Assert.False(PersonIdentitySplitService.TryDetectConflict(Json("1981-06-13", "1982 births"), out _, out _));

    [Fact]
    public void Detect_WikipediaYearUnknown_IsNotConflict() =>
        Assert.False(PersonIdentitySplitService.TryDetectConflict(Json("1981-06-13", null), out _, out _));

    [Fact]
    public void Detect_NoTrustedBirthDate_IsNotConflict()
    {
        var json = $$$"""{"chronicle.plugin.tmdb":{"externalId":"person:1"},"{{{Wiki}}}":{"tags":["1945 births"]}}""";
        Assert.False(PersonIdentitySplitService.TryDetectConflict(json, out _, out _));
    }

    [Fact]
    public void Detect_TrustedProvidersDisagreeAmongThemselves_IsNotConflict()
    {
        var json = $$$"""
        {"a":{"birthDate":"1981-01-01"},"b":{"birthDate":"1950-01-01"},"{{{Wiki}}}":{"tags":["1966 births"]}}
        """;
        Assert.False(PersonIdentitySplitService.TryDetectConflict(json, out _, out _));
    }

    private static (ChronicleDbContext db, PersonIdentitySplitService svc, Mock<IMetadataResolutionService> res, MediaType type)
        Setup()
    {
        var db = new ChronicleDbContext(new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var type = new MediaType { Id = 1, Name = "people", DisplayName = "People", HierarchyLevels = 1, CreatedAt = DateTime.UtcNow };
        db.MediaTypes.Add(type);
        db.SaveChanges();
        var svc = new PersonIdentitySplitService(null!, NullLogger<PersonIdentitySplitService>.Instance);
        return (db, svc, new Mock<IMetadataResolutionService>(), type);
    }

    private static async Task<MediaItem> SeedPersonAsync(ChronicleDbContext db, string json, string wikiPoster = "https://w/x.jpg")
    {
        var p = new MediaItem { MediaTypeId = 1, Name = "Chris Evans", NormalizedName = "chris evans", HierarchyLevel = 0,
                                MetadataJson = json, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.MediaItems.Add(p);
        await db.SaveChangesAsync();
        db.MediaExternalIds.AddRange(
            new MediaExternalId { MediaItemId = p.Id, Source = "tmdb", ExternalId = "tmdb:16828" },
            new MediaExternalId { MediaItemId = p.Id, Source = "wikipedia", ExternalId = "wikipedia:en:Chris_Evans_(presenter)" });
        db.PersonHeadshots.AddRange(
            new PersonHeadshot { PersonMediaItemId = p.Id, Url = "https://t/a.jpg", Source = "tmdb" },
            new PersonHeadshot { PersonMediaItemId = p.Id, Url = wikiPoster, Source = "wikipedia" });
        db.MediaEnrichments.Add(new MediaItemEnrichment { MediaItemId = p.Id, PluginId = Wiki, Status = EnrichmentStatus.Completed,
                                                          ExternalId = "wikipedia:en:Chris_Evans_(presenter)" });
        await db.SaveChangesAsync();
        return p;
    }

    [Fact]
    public async Task Split_MovesArticlePhotosAndEnrichmentOntoNewRecord_LeavesTmdbSideAlone()
    {
        var (db, svc, res, _) = Setup();
        await using var _ = db;
        var person = await SeedPersonAsync(db, Json("1981-06-13", "1966 births"));

        var created = await svc.SplitAsync(db, res.Object, person.Id, 1981, 1966, default);

        Assert.True(created);
        var stub = await db.MediaItems.SingleAsync(m => m.Id != person.Id);
        Assert.Equal("Chris Evans", stub.Name);
        Assert.Contains(Wiki, stub.MetadataJson!);

        var orig = await db.MediaItems.SingleAsync(m => m.Id == person.Id);
        Assert.DoesNotContain(Wiki, orig.MetadataJson!);
        Assert.Contains("chronicle.plugin.tmdb", orig.MetadataJson!);

        Assert.Equal(stub.Id, (await db.MediaExternalIds.SingleAsync(e => e.Source == "wikipedia")).MediaItemId);
        Assert.Equal(person.Id, (await db.MediaExternalIds.SingleAsync(e => e.Source == "tmdb")).MediaItemId);
        Assert.Equal(stub.Id, (await db.PersonHeadshots.SingleAsync(h => h.Source == "wikipedia")).PersonMediaItemId);
        Assert.Equal(person.Id, (await db.PersonHeadshots.SingleAsync(h => h.Source == "tmdb")).PersonMediaItemId);

        // The completed Wikipedia enrichment travelled with the article; the original gets a fresh Pending one.
        Assert.Equal(EnrichmentStatus.Completed, (await db.MediaEnrichments.SingleAsync(e => e.MediaItemId == stub.Id)).Status);
        Assert.Equal(EnrichmentStatus.Pending, (await db.MediaEnrichments.SingleAsync(e => e.MediaItemId == person.Id && e.PluginId == Wiki)).Status);

        res.Verify(r => r.ResolveAsync(It.IsAny<MediaItem>(), db, It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task Split_PosterPinOnMovedPhoto_TravelsWithThePhoto()
    {
        var (db, svc, res, _) = Setup();
        await using var _ = db;
        var json = JsonNode.Parse(Json("1981-06-13", "1966 births"))!.AsObject();
        json["_overrides"] = new JsonObject { ["poster_url"] = new JsonObject { ["url"] = "https://w/x.jpg" } };
        var person = await SeedPersonAsync(db, json.ToJsonString());

        await svc.SplitAsync(db, res.Object, person.Id, 1981, 1966, default);

        var stub = await db.MediaItems.SingleAsync(m => m.Id != person.Id);
        Assert.Contains("_overrides", stub.MetadataJson!);
        Assert.DoesNotContain("_overrides", (await db.MediaItems.SingleAsync(m => m.Id == person.Id)).MetadataJson!);
    }

    [Fact]
    public async Task Split_ArticleAlreadyOwnedElsewhere_DetachesWithoutCreatingASecondStub()
    {
        var (db, svc, res, _) = Setup();
        await using var _ = db;
        var person = await SeedPersonAsync(db, Json("1981-06-13", "1966 births"));
        var home = new MediaItem { MediaTypeId = 1, Name = "Chris Evans", NormalizedName = "chris evans", HierarchyLevel = 0,
                                   CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.MediaItems.Add(home);
        await db.SaveChangesAsync();
        db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = home.Id, Source = "wikipedia", ExternalId = "wikipedia:en:Chris_Evans_(presenter)" });
        await db.SaveChangesAsync();

        var created = await svc.SplitAsync(db, res.Object, person.Id, 1981, 1966, default);

        Assert.False(created);
        Assert.Equal(2, await db.MediaItems.CountAsync());
        Assert.DoesNotContain(await db.MediaExternalIds.ToListAsync(), e => e.MediaItemId == person.Id && e.Source == "wikipedia");
        Assert.Equal(EnrichmentStatus.Skipped, (await db.MediaEnrichments.SingleAsync(e => e.MediaItemId == person.Id)).Status);
    }
}
