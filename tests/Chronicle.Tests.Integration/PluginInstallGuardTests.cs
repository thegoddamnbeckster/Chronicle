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
    /// <summary>The manual plugin-install route can no longer be used to load arbitrary code.</summary>
    public class PluginInstallGuardTests : IClassFixture<ChronicleApiFactory>
    {
        private const string Password = "Password123!";
        private readonly ChronicleApiFactory _factory;

        public PluginInstallGuardTests(ChronicleApiFactory factory) { factory.SeedDatabase(); _factory = factory; }

        private static async Task<JsonElement> Json(HttpResponseMessage r) =>
            JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

        private async Task<HttpClient> SignInAsync(bool admin)
        {
            var reg = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new { username = $"pg_{Guid.NewGuid():N}", password = Password });
            var data = (await Json(reg)).GetProperty("data");
            var id = data.GetProperty("user").GetProperty("id").GetInt32();
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
                (await db.Users.FirstAsync(u => u.Id == id)).IsAdmin = admin;
                await db.SaveChangesAsync();
            }
            var c = _factory.CreateClient();
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", data.GetProperty("token").GetString()!);
            return c;
        }

        [Fact]
        public async Task ManualInstall_OfADllOutsideThePluginsFolder_Gets403_AndNothingIsInstalled()
        {
            var admin = await SignInAsync(admin: true);
            var dll = Path.Combine(Path.GetTempPath(), "evil-" + Guid.NewGuid().ToString("N") + ".dll");
            await File.WriteAllTextAsync(dll, "not really a plugin");
            try
            {
                var response = await admin.PostAsJsonAsync("/api/v1/plugins", new { dllPath = dll });

                response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
                (await Json(response)).GetProperty("error").GetProperty("code").GetString().Should().Be("PLUGIN_NOT_ALLOWED");
            }
            finally { File.Delete(dll); }
        }

        [Fact]
        public async Task ApprovingFilesOfAnUnknownPlugin_Is404()
        {
            var admin = await SignInAsync(admin: true);

            var response = await admin.PostAsync("/api/v1/plugins/no.such.plugin/accept-files", null);

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task ApprovingFiles_NeedsAnAdministrator()
        {
            var user = await SignInAsync(admin: false);

            var response = await user.PostAsync("/api/v1/plugins/chronicle.plugin.tmdb/accept-files", null);

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
    }
}
