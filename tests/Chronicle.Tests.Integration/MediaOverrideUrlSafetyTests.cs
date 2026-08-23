using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Chronicle.Core.Helpers;
using Chronicle.Core.Models;
using Chronicle.Data;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicle.Tests.Integration;

/// <summary>
/// SetOverride ("pin this URL as the poster/backdrop/etc.") is the endpoint a manual
/// image-URL feature would submit through, and its value is later fetched server-side
/// (poster-proxy) -- so it's the choke point that must reject anything other than a genuine,
/// publicly-reachable http(s) URL, guarding against SSRF (an internal address, a cloud
/// metadata endpoint) as well as non-URL schemes.
/// </summary>
public class MediaOverrideUrlSafetyTests : IClassFixture<ChronicleApiFactory>
{
    private readonly ChronicleApiFactory _factory;

    public MediaOverrideUrlSafetyTests(ChronicleApiFactory factory)
    {
        factory.SeedDatabase();
        _factory = factory;
    }

    private int MoviesTypeId()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var existing = db.MediaTypes.FirstOrDefault(t => t.Name == "movies");
        if (existing is not null) return existing.Id;

        var mt = new MediaType
        {
            Name = "movies", DisplayName = "Movies", HierarchyLevels = 1,
            InteractionVerb = "watched", ProgressUnit = "minutes",
            IsBuiltIn = false, IsActive = true, CreatedAt = DateTime.UtcNow,
        };
        db.MediaTypes.Add(mt);
        db.SaveChanges();
        return mt.Id;
    }

    private int SeedMovie(string name)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var item = new MediaItem
        {
            MediaTypeId = MoviesTypeId(), Name = name, HierarchyLevel = 0,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        };
        db.MediaItems.Add(item);
        db.SaveChanges();
        return item.Id;
    }

    private async Task<HttpClient> AuthClientAsync()
    {
        var client = _factory.CreateClient();
        var username = $"urlsafety_{Guid.NewGuid():N}";
        var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
            new { username, password = "Password123!" });
        var token = JsonDocument.Parse(await reg.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")] // cloud metadata endpoint
    [InlineData("http://127.0.0.1:6379/")]                    // loopback
    [InlineData("http://10.0.0.5/internal")]                  // RFC1918
    [InlineData("javascript:alert(1)")]                       // not http(s) at all
    [InlineData("not a url")]
    public async Task SetOverride_RejectsUnsafeOrMalformedUrl_OnAnImageField(string badUrl)
    {
        var itemId = SeedMovie("SSRF Test Movie " + Guid.NewGuid());
        var client = await AuthClientAsync();

        var resp = await client.PutAsJsonAsync(
            $"/api/v1/media/{itemId}/overrides/poster_url", new { url = badUrl });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SetOverride_AcceptsAWellFormedPublicUrl_OnAnImageField()
    {
        var itemId = SeedMovie("Good Poster Movie " + Guid.NewGuid());
        var client = await AuthClientAsync();

        var resp = await client.PutAsJsonAsync(
            $"/api/v1/media/{itemId}/overrides/poster_url",
            new { url = "https://image.tmdb.org/t/p/original/example.jpg" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task SetOverride_DoesNotUrlValidateNonImageFields()
    {
        // "title" pins a plain string through the same generic endpoint -- it must not be
        // rejected just because it isn't a URL.
        var itemId = SeedMovie("Retitle Me " + Guid.NewGuid());
        var client = await AuthClientAsync();

        var resp = await client.PutAsJsonAsync(
            $"/api/v1/media/{itemId}/overrides/title", new { url = "A Brand New Title" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("not a url")]
    public async Task PosterProxy_RejectsUnsafeOrMalformedUrl(string badUrl)
    {
        var client = await AuthClientAsync();

        var resp = await client.GetAsync(
            $"/api/v1/media/poster-proxy?url={Uri.EscapeDataString(badUrl)}");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
