using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Chronicle.Core.Models;
using Chronicle.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicle.Tests.Integration
{
    /// <summary>Deletion of shared media is open to every signed-in user until an administrator reserves it.</summary>
    public class MediaRightsTests : IClassFixture<ChronicleApiFactory>
    {
        private const string Password = "Password123!";
        private readonly ChronicleApiFactory _factory;

        public MediaRightsTests(ChronicleApiFactory factory)
        {
            factory.SeedDatabase();
            _factory = factory;
        }

        private async Task<HttpClient> ClientAsync(bool admin)
        {
            var username = $"mr_{Guid.NewGuid():N}";
            var reg = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new { username, password = Password });
            var token = JsonDocument.Parse(await reg.Content.ReadAsStringAsync()).RootElement.GetProperty("data").GetProperty("token").GetString()!;
            await _factory.SetAdminAsync(username, admin);
            var c = _factory.CreateClient();
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return c;
        }

        private async Task<int> NewItemAsync()
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var item = new MediaItem { Name = $"Doomed {Guid.NewGuid():N}", MediaTypeId = (await db.MediaTypes.FirstAsync()).Id, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
            db.MediaItems.Add(item);
            await db.SaveChangesAsync();
            return item.Id;
        }

        private static async Task<bool> ReadAsync(HttpClient c)
        {
            var response = await c.GetAsync("/api/v1/media-rights");
            response.EnsureSuccessStatusCode();
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("data").GetProperty("deleteAdminOnly").GetBoolean();
        }

        [Fact]
        public async Task Anonymous_CannotReadOrChangeIt()
        {
            var anon = _factory.CreateClient();
            (await anon.GetAsync("/api/v1/media-rights")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await anon.PutAsJsonAsync("/api/v1/media-rights", new { deleteAdminOnly = true })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task OrdinaryUsers_CanReadButNotChangeIt()
        {
            var user = await ClientAsync(admin: false);
            (await ReadAsync(user)).Should().BeFalse("deletion is open by default");
            (await user.PutAsJsonAsync("/api/v1/media-rights", new { deleteAdminOnly = true })).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        [Fact]
        public async Task WhileDeletionIsOpen_AnOrdinaryUserMayDelete()
        {
            var user = await ClientAsync(admin: false);
            var id = await NewItemAsync();
            (await user.DeleteAsync($"/api/v1/media/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        }

        [Fact]
        public async Task Reserved_AnOrdinaryUserIsRefused_AnAdministratorIsNot_AndOpeningItAgainRestoresIt()
        {
            var admin = await ClientAsync(admin: true);
            var user = await ClientAsync(admin: false);
            var id = await NewItemAsync();
            try
            {
                (await admin.PutAsJsonAsync("/api/v1/media-rights", new { deleteAdminOnly = true })).StatusCode.Should().Be(HttpStatusCode.OK);
                (await ReadAsync(user)).Should().BeTrue();

                var refused = await user.DeleteAsync($"/api/v1/media/{id}");
                refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
                JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetProperty("code").GetString()
                    .Should().Be("MEDIA_DELETE_RESTRICTED");
                using (var scope = _factory.Services.CreateScope())
                    (await scope.ServiceProvider.GetRequiredService<ChronicleDbContext>().MediaItems.AnyAsync(m => m.Id == id)).Should().BeTrue("nothing was deleted");

                (await admin.DeleteAsync($"/api/v1/media/{await NewItemAsync()}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

                await admin.PutAsJsonAsync("/api/v1/media-rights", new { deleteAdminOnly = false });
                (await user.DeleteAsync($"/api/v1/media/{id}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
            }
            finally { await admin.PutAsJsonAsync("/api/v1/media-rights", new { deleteAdminOnly = false }); }
        }
    }
}
