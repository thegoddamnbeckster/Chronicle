using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Chronicle.Tests.Integration;

/// <summary>
/// Clearing a provider's match must also make the provider forget the id it matched. The enrichment row re-uses its own
/// stored id on the next run instead of searching again, so a cleared match came straight back (2026-10-13: 15 wrong
/// Open Library matches were cleared, reset and re-run and returned identical).
/// The in-memory test database cannot run this endpoint (it uses bulk deletes), so this runs on a real SQLite file.
/// </summary>
public class ClearExternalIdTests : IClassFixture<SqliteApiFactory>
{
    private readonly SqliteApiFactory _factory;
    private const string AdminUser = "clear_ext_admin_fixture";
    private const string AdminPass = "Password123!";

    public ClearExternalIdTests(SqliteApiFactory factory)
    {
        _factory = factory;
        factory.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new { username = AdminUser, password = AdminPass }).GetAwaiter().GetResult();
    }

    private async Task<HttpClient> AdminClientAsync()
    {
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { username = AdminUser, password = AdminPass });
        var token = JsonDocument.Parse(await login.Content.ReadAsStringAsync()).RootElement.GetProperty("data").GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private int Seed(string source, string externalId, string pluginId, string otherPluginId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var item = new MediaItem { Name = "Sentienced to Death", MediaTypeId = 1, HierarchyLevel = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        db.MediaItems.Add(item);
        db.SaveChanges();
        db.MediaExternalIds.Add(new MediaExternalId { MediaItemId = item.Id, Source = source, ExternalId = externalId });
        db.MediaEnrichments.Add(new MediaItemEnrichment { MediaItemId = item.Id, PluginId = pluginId, ExternalId = externalId, Status = EnrichmentStatus.Completed, MaxRetries = 3 });
        db.MediaEnrichments.Add(new MediaItemEnrichment { MediaItemId = item.Id, PluginId = otherPluginId, ExternalId = "other:1", Status = EnrichmentStatus.Completed, MaxRetries = 3 });
        db.SaveChanges();
        return item.Id;
    }

    private string? EnrichmentId(int itemId, string pluginId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        return db.MediaEnrichments.AsNoTracking().Single(e => e.MediaItemId == itemId && e.PluginId == pluginId).ExternalId;
    }

    private bool HasExternalId(int itemId, string source)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        return db.MediaExternalIds.AsNoTracking().Any(e => e.MediaItemId == itemId && e.Source == source);
    }

    [Theory]
    [InlineData("openlibrary")]
    [InlineData("chronicle.plugin.openlibrary")]
    public async Task ClearingAMatch_MakesItsEnrichmentRowForgetTheId(string sourceInUrl)
    {
        var id = Seed("openlibrary", "OL19631710W", "chronicle.plugin.openlibrary", "chronicle.plugin.tmdb");
        var client = await AdminClientAsync();

        var resp = await client.DeleteAsync($"/api/v1/media/{id}/external-ids/{sourceInUrl}");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        HasExternalId(id, "openlibrary").Should().BeFalse();
        EnrichmentId(id, "chronicle.plugin.openlibrary").Should().BeNull("a stale id would bring the same match straight back");
    }

    [Fact]
    public async Task OtherProvidersEnrichmentRows_AreLeftAlone()
    {
        var id = Seed("openlibrary", "OL19631710W", "chronicle.plugin.openlibrary", "chronicle.plugin.tmdb");
        var client = await AdminClientAsync();

        await client.DeleteAsync($"/api/v1/media/{id}/external-ids/openlibrary");

        EnrichmentId(id, "chronicle.plugin.tmdb").Should().Be("other:1");
    }

    [Fact]
    public async Task RepeatingAHalfDoneClear_CompletesIt()
    {
        // The id row is already gone (an earlier clear), but the enrichment row still holds its copy.
        var id = Seed("openlibrary", "OL19631710W", "chronicle.plugin.openlibrary", "chronicle.plugin.tmdb");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            db.MediaExternalIds.RemoveRange(db.MediaExternalIds.Where(e => e.MediaItemId == id));
            db.SaveChanges();
        }
        var client = await AdminClientAsync();

        var resp = await client.DeleteAsync($"/api/v1/media/{id}/external-ids/openlibrary");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        EnrichmentId(id, "chronicle.plugin.openlibrary").Should().BeNull();
    }
}
