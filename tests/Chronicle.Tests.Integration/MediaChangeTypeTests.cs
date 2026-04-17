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

public class MediaChangeTypeTests : IClassFixture<ChronicleApiFactory>
{
    private readonly ChronicleApiFactory _factory;
    private static readonly JsonSerializerOptions _json = new() { PropertyNameCaseInsensitive = true };

    // Fixed admin credentials — first registration in this factory instance gets Admin role.
    private const string AdminUser = "changetype_admin";
    private const string AdminPass = "Password123!";

    public MediaChangeTypeTests(ChronicleApiFactory factory)
    {
        factory.SeedDatabase();
        _factory = factory;

        // Ensure the admin user exists (first call creates it as admin; subsequent calls are no-ops).
        EnsureAdminRegisteredAsync(factory).GetAwaiter().GetResult();
        EnsureMediaTypesAsync(factory).GetAwaiter().GetResult();
    }

    private static async Task EnsureAdminRegisteredAsync(ChronicleApiFactory factory)
    {
        var client = factory.CreateClient();
        await client.PostAsJsonAsync("/api/v1/auth/register",
            new { username = AdminUser, password = AdminPass });
    }

    private static async Task EnsureMediaTypesAsync(ChronicleApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();

        // Ensure movies type exists
        if (!db.Set<MediaType>().Any(t => t.Name == "movies"))
        {
            db.Set<MediaType>().Add(new MediaType
            {
                Name = "movies", DisplayName = "Movies",
                HierarchyLevels = 1, InteractionVerb = "watched",
                ProgressUnit = "minutes", IsActive = true, IsBuiltIn = true,
                CreatedAt = DateTime.UtcNow
            });
        }

        // Ensure fanedits type exists
        if (!db.Set<MediaType>().Any(t => t.Name == "fanedits"))
        {
            db.Set<MediaType>().Add(new MediaType
            {
                Name = "fanedits", DisplayName = "Fan Edits",
                HierarchyLevels = 1, InteractionVerb = "watched",
                ProgressUnit = "minutes", IsActive = true, IsBuiltIn = true,
                CreatedAt = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync();
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

    private int GetTypeId(string typeName)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        return db.Set<MediaType>().First(t => t.Name == typeName).Id;
    }

    [Fact]
    public async Task ChangeType_Returns200_AndUpdatesType()
    {
        var client = await AdminClientAsync();
        var movieTypeId   = GetTypeId("movies");
        var faneditTypeId = GetTypeId("fanedits");

        int itemId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var item = new MediaItem
            {
                MediaTypeId = movieTypeId, Name = "Test Movie", HierarchyLevel = 0,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
            };
            db.MediaItems.Add(item);
            await db.SaveChangesAsync();
            itemId = item.Id;
        }

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/media/{itemId}/change-type",
            new { mediaTypeId = faneditTypeId });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        // Use a fresh scope to avoid EF identity-cache returning the pre-update entity
        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var updated = await verifyDb.MediaItems.FindAsync(itemId);
        updated!.MediaTypeId.Should().Be(faneditTypeId);
    }

    [Fact]
    public async Task ChangeType_Returns400_WithParentId_WhenChildItem()
    {
        var client = await AdminClientAsync();
        var movieTypeId   = GetTypeId("movies");
        var faneditTypeId = GetTypeId("fanedits");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var parent = new MediaItem
        {
            MediaTypeId = movieTypeId, Name = "Parent", HierarchyLevel = 0,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.MediaItems.Add(parent);
        await db.SaveChangesAsync();
        var child = new MediaItem
        {
            MediaTypeId = movieTypeId, Name = "Child", HierarchyLevel = 1,
            ParentId = parent.Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow
        };
        db.MediaItems.Add(child);
        await db.SaveChangesAsync();

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/media/{child.Id}/change-type",
            new { mediaTypeId = faneditTypeId });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("error").GetProperty("code").GetString()
            .Should().Be("CHANGE_TYPE_USE_ROOT");
        body.RootElement.GetProperty("error").GetProperty("parentId").GetInt32()
            .Should().Be(parent.Id);
    }

    [Fact]
    public async Task ChangeType_Returns401_WhenNotAuthenticated()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/media/1/change-type", new { mediaTypeId = 4 });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
