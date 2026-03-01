using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace Chronicle.Tests.Integration
{
    public class MediaListTests : IClassFixture<ChronicleApiFactory>
    {
        private readonly ChronicleApiFactory _factory;

        public MediaListTests(ChronicleApiFactory factory)
        {
            factory.SeedDatabase();
            _factory = factory;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private async Task<(HttpClient client, int mediaId)> SetupAsync()
        {
            var client   = _factory.CreateClient();
            var username = $"lst_{Guid.NewGuid():N}";

            var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
                new { username, password = "Password123!" });

            var token = JsonDocument.Parse(await reg.Content.ReadAsStringAsync())
                .RootElement.GetProperty("data").GetProperty("token").GetString()!;

            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);

            var mediaResp = await client.PostAsJsonAsync("/api/v1/media", new
            {
                mediaTypeId    = 1,
                name           = "List Test Show",
                hierarchyLevel = 0
            });

            var mediaId = JsonDocument.Parse(await mediaResp.Content.ReadAsStringAsync())
                .RootElement.GetProperty("data").GetProperty("id").GetInt32();

            return (client, mediaId);
        }

        // ── GET /api/v1/lists ─────────────────────────────────────────────────

        [Fact]
        public async Task GetLists_NewUser_ReturnsEmptyArray()
        {
            var (client, _) = await SetupAsync();

            var response = await client.GetAsync("/api/v1/lists");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            doc.GetProperty("success").GetBoolean().Should().BeTrue();
            doc.GetProperty("data").GetArrayLength().Should().Be(0);
        }

        [Fact]
        public async Task GetLists_WithoutAuth_Returns401()
        {
            var client   = _factory.CreateClient();
            var response = await client.GetAsync("/api/v1/lists");
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        // ── POST /api/v1/lists ────────────────────────────────────────────────

        [Fact]
        public async Task CreateList_ValidRequest_Returns201WithList()
        {
            var (client, _) = await SetupAsync();

            var response = await client.PostAsJsonAsync("/api/v1/lists", new
            {
                name        = "My Watchlist",
                description = "Films to watch this weekend",
                isOrdered   = true
            });

            response.StatusCode.Should().Be(HttpStatusCode.Created);

            var doc  = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            var data = doc.GetProperty("data");
            data.GetProperty("name").GetString().Should().Be("My Watchlist");
            data.GetProperty("isOrdered").GetBoolean().Should().BeTrue();
            data.GetProperty("id").GetInt32().Should().BeGreaterThan(0);
        }

        [Fact]
        public async Task CreateList_WithoutAuth_Returns401()
        {
            var client   = _factory.CreateClient();
            var response = await client.PostAsJsonAsync("/api/v1/lists",
                new { name = "No Auth List" });
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        // ── GET /api/v1/lists/{id} ────────────────────────────────────────────

        [Fact]
        public async Task GetList_ExistingList_Returns200WithDetail()
        {
            var (client, _) = await SetupAsync();

            var createResp = await client.PostAsJsonAsync("/api/v1/lists",
                new { name = "Detail Test List", isOrdered = false });
            var listId = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync())
                .RootElement.GetProperty("data").GetProperty("id").GetInt32();

            var response = await client.GetAsync($"/api/v1/lists/{listId}");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var doc  = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            var data = doc.GetProperty("data");
            data.GetProperty("id").GetInt32().Should().Be(listId);
            data.GetProperty("name").GetString().Should().Be("Detail Test List");
            data.GetProperty("items").GetArrayLength().Should().Be(0);
        }

        [Fact]
        public async Task GetList_OtherUsersListId_Returns404()
        {
            // User A creates a list
            var (clientA, _) = await SetupAsync();
            var createResp   = await clientA.PostAsJsonAsync("/api/v1/lists",
                new { name = "User A's Private List" });
            var listId = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync())
                .RootElement.GetProperty("data").GetProperty("id").GetInt32();

            // User B tries to fetch it
            var (clientB, _) = await SetupAsync();
            var response     = await clientB.GetAsync($"/api/v1/lists/{listId}");
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task GetList_NonExistentId_Returns404()
        {
            var (client, _) = await SetupAsync();
            var response    = await client.GetAsync("/api/v1/lists/999999");
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        // ── PUT /api/v1/lists/{id} ────────────────────────────────────────────

        [Fact]
        public async Task UpdateList_ValidRequest_Returns200WithUpdatedList()
        {
            var (client, _) = await SetupAsync();

            var createResp = await client.PostAsJsonAsync("/api/v1/lists",
                new { name = "Original Name" });
            var listId = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync())
                .RootElement.GetProperty("data").GetProperty("id").GetInt32();

            var putResp = await client.PutAsJsonAsync($"/api/v1/lists/{listId}", new
            {
                name        = "Updated Name",
                description = "New description",
                isOrdered   = true
            });

            putResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var doc  = JsonDocument.Parse(await putResp.Content.ReadAsStringAsync()).RootElement;
            var data = doc.GetProperty("data");
            data.GetProperty("name").GetString().Should().Be("Updated Name");
            data.GetProperty("isOrdered").GetBoolean().Should().BeTrue();
        }

        [Fact]
        public async Task UpdateList_NonExistentId_Returns404()
        {
            var (client, _) = await SetupAsync();
            var response    = await client.PutAsJsonAsync("/api/v1/lists/999999",
                new { name = "Ghost List" });
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        // ── DELETE /api/v1/lists/{id} ─────────────────────────────────────────

        [Fact]
        public async Task DeleteList_ExistingList_Returns204()
        {
            var (client, _) = await SetupAsync();

            var createResp = await client.PostAsJsonAsync("/api/v1/lists",
                new { name = "To Delete" });
            var listId = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync())
                .RootElement.GetProperty("data").GetProperty("id").GetInt32();

            var deleteResp = await client.DeleteAsync($"/api/v1/lists/{listId}");
            deleteResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

            // Confirm gone
            var getResp = await client.GetAsync($"/api/v1/lists/{listId}");
            getResp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task DeleteList_NonExistentId_Returns404()
        {
            var (client, _) = await SetupAsync();
            var response    = await client.DeleteAsync("/api/v1/lists/999999");
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        // ── POST /api/v1/lists/{id}/items ─────────────────────────────────────

        [Fact]
        public async Task AddItem_ValidMediaItem_Returns200WithItem()
        {
            var (client, mediaId) = await SetupAsync();

            var createResp = await client.PostAsJsonAsync("/api/v1/lists",
                new { name = "Item Test List" });
            var listId = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync())
                .RootElement.GetProperty("data").GetProperty("id").GetInt32();

            var addResp = await client.PostAsJsonAsync($"/api/v1/lists/{listId}/items",
                new { mediaItemId = mediaId });

            addResp.StatusCode.Should().Be(HttpStatusCode.OK);
            var doc  = JsonDocument.Parse(await addResp.Content.ReadAsStringAsync()).RootElement;
            var data = doc.GetProperty("data");
            data.GetProperty("id").GetInt32().Should().BeGreaterThan(0);
            data.GetProperty("mediaItem").GetProperty("id").GetInt32().Should().Be(mediaId);
        }

        [Fact]
        public async Task AddItem_DuplicateItem_Returns409()
        {
            var (client, mediaId) = await SetupAsync();

            var createResp = await client.PostAsJsonAsync("/api/v1/lists",
                new { name = "Duplicate Test List" });
            var listId = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync())
                .RootElement.GetProperty("data").GetProperty("id").GetInt32();

            await client.PostAsJsonAsync($"/api/v1/lists/{listId}/items",
                new { mediaItemId = mediaId });

            // Add same item again
            var dupResp = await client.PostAsJsonAsync($"/api/v1/lists/{listId}/items",
                new { mediaItemId = mediaId });
            dupResp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }

        [Fact]
        public async Task AddItem_NonExistentList_Returns404()
        {
            var (client, mediaId) = await SetupAsync();

            var response = await client.PostAsJsonAsync("/api/v1/lists/999999/items",
                new { mediaItemId = mediaId });
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        // ── DELETE /api/v1/lists/{id}/items/{itemId} ──────────────────────────

        [Fact]
        public async Task RemoveItem_ExistingItem_Returns204()
        {
            var (client, mediaId) = await SetupAsync();

            var createResp = await client.PostAsJsonAsync("/api/v1/lists",
                new { name = "Remove Item Test" });
            var listId = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync())
                .RootElement.GetProperty("data").GetProperty("id").GetInt32();

            var addResp = await client.PostAsJsonAsync($"/api/v1/lists/{listId}/items",
                new { mediaItemId = mediaId });
            var itemId = JsonDocument.Parse(await addResp.Content.ReadAsStringAsync())
                .RootElement.GetProperty("data").GetProperty("id").GetInt32();

            var deleteResp = await client.DeleteAsync($"/api/v1/lists/{listId}/items/{itemId}");
            deleteResp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        [Fact]
        public async Task RemoveItem_NonExistentItem_Returns404()
        {
            var (client, _) = await SetupAsync();

            var createResp = await client.PostAsJsonAsync("/api/v1/lists",
                new { name = "Remove Ghost Item" });
            var listId = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync())
                .RootElement.GetProperty("data").GetProperty("id").GetInt32();

            var response = await client.DeleteAsync($"/api/v1/lists/{listId}/items/999999");
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        // ── PUT /api/v1/lists/{id}/items/reorder ─────────────────────────────

        [Fact]
        public async Task ReorderItems_ValidPositions_Returns200()
        {
            var (client, mediaId) = await SetupAsync();

            // Create a second media item for reordering
            var mediaResp2 = await client.PostAsJsonAsync("/api/v1/media", new
            {
                mediaTypeId    = 1,
                name           = "Reorder Test Show 2",
                hierarchyLevel = 0
            });
            var mediaId2 = JsonDocument.Parse(await mediaResp2.Content.ReadAsStringAsync())
                .RootElement.GetProperty("data").GetProperty("id").GetInt32();

            var createResp = await client.PostAsJsonAsync("/api/v1/lists",
                new { name = "Reorder Test", isOrdered = true });
            var listId = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync())
                .RootElement.GetProperty("data").GetProperty("id").GetInt32();

            var add1Resp = await client.PostAsJsonAsync($"/api/v1/lists/{listId}/items",
                new { mediaItemId = mediaId });
            var itemId1 = JsonDocument.Parse(await add1Resp.Content.ReadAsStringAsync())
                .RootElement.GetProperty("data").GetProperty("id").GetInt32();

            var add2Resp = await client.PostAsJsonAsync($"/api/v1/lists/{listId}/items",
                new { mediaItemId = mediaId2 });
            var itemId2 = JsonDocument.Parse(await add2Resp.Content.ReadAsStringAsync())
                .RootElement.GetProperty("data").GetProperty("id").GetInt32();

            var reorderResp = await client.PutAsJsonAsync($"/api/v1/lists/{listId}/items/reorder",
                new
                {
                    items = new[]
                    {
                        new { itemId = itemId1, position = 2 },
                        new { itemId = itemId2, position = 1 }
                    }
                });

            reorderResp.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task GetLists_ReturnsOnlyOwnLists()
        {
            var (clientA, _) = await SetupAsync();
            var (clientB, _) = await SetupAsync();

            await clientA.PostAsJsonAsync("/api/v1/lists", new { name = "A's List" });
            await clientB.PostAsJsonAsync("/api/v1/lists", new { name = "B's List" });

            var responseA = await clientA.GetAsync("/api/v1/lists");
            var docA      = JsonDocument.Parse(await responseA.Content.ReadAsStringAsync()).RootElement;
            docA.GetProperty("data").GetArrayLength().Should().Be(1);

            var responseB = await clientB.GetAsync("/api/v1/lists");
            var docB      = JsonDocument.Parse(await responseB.Content.ReadAsStringAsync()).RootElement;
            docB.GetProperty("data").GetArrayLength().Should().Be(1);
        }
    }
}
