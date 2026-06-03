using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicle.Tests.Integration;

public class MetadataResolutionTests : IClassFixture<ChronicleApiFactory>
{
    private readonly ChronicleApiFactory _factory;
    private const string AdminUser = "resolution_admin";
    private const string AdminPass  = "Password123!";

    public MetadataResolutionTests(ChronicleApiFactory factory)
    {
        _factory = factory;
        factory.SeedDatabase();
        EnsureAdminRegistered(factory).GetAwaiter().GetResult();
    }

    private static async Task EnsureAdminRegistered(ChronicleApiFactory factory)
    {
        var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/v1/auth/register",
            new { username = AdminUser, password = AdminPass });
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        var client = _factory.CreateClient();
        var login  = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = AdminUser, password = AdminPass });
        var token  = JsonDocument.Parse(await login.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task ResolvedMetadata_PosterUrl_RespectsAssignmentPriority()
    {
        // ── 1. Seed media type, item, assignment config, and run resolution ──────
        int itemId;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

            // Ensure an "audiobooks" media type exists
            var mt = db.MediaTypes.FirstOrDefault(t => t.Name == "audiobooks");
            if (mt == null)
            {
                mt = db.MediaTypes.Add(new MediaType
                {
                    Name             = "audiobooks",
                    DisplayName      = "Audiobooks",
                    HierarchyLevels  = 1,
                    InteractionVerb  = "listened",
                    ProgressUnit     = "minutes",
                    IsBuiltIn        = false,
                    IsActive         = true,
                    CreatedAt        = DateTime.UtcNow
                }).Entity;
                db.SaveChanges();
            }

            // Create a media item whose metadata_json has blobs from two plugins.
            // hardcover has poster A; chronicle.plugin.musicbrainz has poster B.
            var item = new MediaItem
            {
                MediaTypeId    = mt.Id,
                Name           = "Test Audiobook",
                HierarchyLevel = 0,
                CreatedAt      = DateTime.UtcNow,
                UpdatedAt      = DateTime.UtcNow,
                MetadataJson   = """
                {
                  "hardcover":                    {"posterUrl":"https://hardcover.app/cover.jpg","title":"Test Audiobook"},
                  "chronicle.plugin.musicbrainz": {"posterUrl":"https://mb.org/cover.jpg","title":"Test Audiobook"}
                }
                """
            };
            db.MediaItems.Add(item);
            db.SaveChanges();
            itemId = item.Id;

            // Write assignment config: hardcover > musicbrainz for poster_url on audiobooks
            const string configJson =
                """{"audiobooks":{"poster_url":["hardcover","chronicle.plugin.musicbrainz"],"title":["hardcover","chronicle.plugin.musicbrainz"]}}""";

            var existing = db.AppSettings.Find("metadata_assignment.config");
            if (existing != null)
                existing.Value = configJson;
            else
                db.AppSettings.Add(new AppSetting { Key = "metadata_assignment.config", Value = configJson });
            db.SaveChanges();

            // Invalidate the cache so it re-reads from the DB we just wrote
            var cache = scope.ServiceProvider.GetRequiredService<AssignmentConfigCache>();
            cache.Invalidate();

            // Resolve metadata for the item
            var resolver = scope.ServiceProvider.GetRequiredService<IMetadataResolutionService>();
            await resolver.ResolveAsync(item, db);
            db.SaveChanges();
        }

        // ── 2. Fetch the item via HTTP and assert resolvedMetadata ───────────────
        var client   = await AdminClientAsync();
        var response = await client.GetAsync($"/api/v1/media/{itemId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        var doc  = JsonDocument.Parse(body);

        doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();

        var data             = doc.RootElement.GetProperty("data");
        var resolvedMetadata = data.GetProperty("resolvedMetadata");

        resolvedMetadata.ValueKind.Should().NotBe(JsonValueKind.Null,
            "resolvedMetadata should be present after ResolveAsync ran");

        var posterUrl = resolvedMetadata.GetProperty("posterUrl").GetString();
        posterUrl.Should().Be("https://hardcover.app/cover.jpg",
            "hardcover has higher priority than chronicle.plugin.musicbrainz for poster_url");
    }
}
