using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Chronicle.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicle.Tests.Integration
{
    /// <summary>Deleting an account on a real SQLite file with the real migrations and enforced foreign keys.</summary>
    public class UserDeleteSqliteTests : IClassFixture<SqliteApiFactory>
    {
        private const string Password = "Password123!";
        private readonly SqliteApiFactory _factory;

        public UserDeleteSqliteTests(SqliteApiFactory factory) => _factory = factory;

        private static async Task<JsonElement> Json(HttpResponseMessage r) =>
            JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

        private async Task<(HttpClient Client, int Id)> AccountAsync(bool admin)
        {
            var response = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new { username = $"del_{Guid.NewGuid():N}", password = Password });
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            var data = (await Json(response)).GetProperty("data");
            var id = data.GetProperty("user").GetProperty("id").GetInt32();
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
                (await db.Users.FirstAsync(u => u.Id == id)).IsAdmin = admin;
                await db.SaveChangesAsync();
            }
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", data.GetProperty("token").GetString()!);
            return (client, id);
        }

        [Fact]
        public async Task AnAdmin_CanDeleteAnAccount_ThatHasASessionAndAKey()
        {
            var admin = await AccountAsync(admin: true);
            var victim = await AccountAsync(admin: false);
            var key = await victim.Client.PostAsJsonAsync("/api/v1/tokens", new { name = "scrobbler" });
            _ = key;

            var response = await admin.Client.DeleteAsync($"/api/v1/users/{victim.Id}");

            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            (await admin.Client.GetAsync($"/api/v1/users/{victim.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }
}
