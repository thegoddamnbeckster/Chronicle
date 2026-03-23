using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chronicle.Tests.Integration;

public class EnrichmentTests : IClassFixture<ChronicleApiFactory>
{
    private readonly ChronicleApiFactory _factory;

    private const string AdminUser = "enrich_admin_fixture";
    private const string AdminPass = "Password123!";

    public EnrichmentTests(ChronicleApiFactory factory)
    {
        factory.SeedDatabase();
        _factory = factory;
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
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { username = AdminUser, password = AdminPass });
        var token = JsonDocument.Parse(await login.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task GetStats_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/v1/enrichment/stats");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetStats_Authenticated_ReturnsSuccess()
    {
        var client = await AdminClientAsync();
        var resp = await client.GetAsync("/api/v1/enrichment/stats");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        doc.GetProperty("success").GetBoolean().Should().BeTrue();
        doc.TryGetProperty("data", out var data).Should().BeTrue();
        data.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task RunEnrichment_Authenticated_Returns202()
    {
        var client = await AdminClientAsync();
        var resp = await client.PostAsync("/api/v1/enrichment/chronicle.plugin.tmdb/run", null);
        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        doc.GetProperty("success").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task RunEnrichment_Unauthenticated_Returns401()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsync("/api/v1/enrichment/chronicle.plugin.tmdb/run", null);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Reset_AllScope_Authenticated_ReturnsSuccess()
    {
        var client = await AdminClientAsync();
        var resp = await client.PostAsJsonAsync(
            "/api/v1/enrichment/chronicle.plugin.tmdb/reset",
            new { Scope = "all", MediaItemId = (int?)null });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        doc.GetProperty("success").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Reset_InvalidScope_Returns400()
    {
        var client = await AdminClientAsync();
        var resp = await client.PostAsJsonAsync(
            "/api/v1/enrichment/chronicle.plugin.tmdb/reset",
            new { Scope = "bogus", MediaItemId = (int?)null });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        doc.GetProperty("success").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task GetEnrichmentItems_ReturnsPagedItems()
    {
        // Arrange: seed a media item + enrichment row
        const string pluginId = "chronicle.plugin.tmdb";
        SeedEnrichmentRow(pluginId, "Test Movie", EnrichmentStatus.Pending);

        var client = await AdminClientAsync();

        // Act
        var resp = await client.GetAsync($"/api/v1/enrichment/{pluginId}/items?page=1&pageSize=10");

        // Assert
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        doc.GetProperty("success").GetBoolean().Should().BeTrue();
        var data = doc.GetProperty("data");
        data.TryGetProperty("items", out var items).Should().BeTrue();
        items.ValueKind.Should().Be(JsonValueKind.Array);
        data.GetProperty("totalPages").GetInt32().Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task GetEnrichmentItems_FiltersByStatus()
    {
        // Arrange: seed a NotFound enrichment row
        const string pluginId = "chronicle.plugin.tmdb";
        SeedEnrichmentRow(pluginId, "Not Found Movie", EnrichmentStatus.NotFound);

        var client = await AdminClientAsync();

        // Act
        var resp = await client.GetAsync($"/api/v1/enrichment/{pluginId}/items?status=NotFound&page=1&pageSize=50");

        // Assert
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync()).RootElement;
        doc.GetProperty("success").GetBoolean().Should().BeTrue();
        var items = doc.GetProperty("data").GetProperty("items").EnumerateArray().ToList();
        items.Should().NotBeEmpty();
        items.Should().OnlyContain(i => i.GetProperty("status").GetString() == "NotFound");
    }

    private void SeedEnrichmentRow(string pluginId, string itemName, EnrichmentStatus status)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        // Ensure a media type exists
        var mediaType = db.MediaTypes.FirstOrDefault();
        if (mediaType is null)
        {
            mediaType = new MediaType
            {
                Name = "movies", DisplayName = "Movies",
                HierarchyLevels = 1, HierarchyLabels = "Movie",
                InteractionVerb = "watched", ProgressUnit = "minutes",
                IsBuiltIn = true, IsActive = true,
                CreatedAt = DateTime.UtcNow
            };
            db.MediaTypes.Add(mediaType);
            db.SaveChanges();
        }

        var item = new MediaItem
        {
            Name = itemName,
            MediaTypeId = mediaType.Id,
            HierarchyLevel = 0,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.MediaItems.Add(item);
        db.SaveChanges();

        db.EnrichmentStatuses.Add(new MediaItemEnrichmentStatus
        {
            MediaItemId = item.Id,
            PluginId    = pluginId,
            Status      = status,
            MaxRetries  = 3
        });
        db.SaveChanges();
    }
}
