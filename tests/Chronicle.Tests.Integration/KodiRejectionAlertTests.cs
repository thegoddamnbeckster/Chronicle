using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Chronicle.API.Authentication;
using Chronicle.Core.Models;
using Chronicle.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicle.Tests.Integration
{
    /// <summary>A Kodi add-on whose API key is turned away is an alert the administrators see, naming the add-on, not only a log line.</summary>
    public class KodiRejectionAlertTests : IClassFixture<ChronicleApiFactory>
    {
        private readonly ChronicleApiFactory _factory;

        public KodiRejectionAlertTests(ChronicleApiFactory factory) { factory.SeedDatabase(); _factory = factory; }

        [Theory]
        [InlineData("Kodi/Chronicle-Scrobbler/1.0", "Chronicle Scrobbler")]
        [InlineData("Kodi/Chronicle-Rating/1.0", "Chronicle Rating")]
        [InlineData("Kodi/Chronicle-Scraper/1.0", "Chronicle Scraper")]
        public void TheAddOnIsNamedFromItsUserAgent(string agent, string expected) =>
            KodiRejectionAlert.AddOnName(agent).Should().Be(expected);

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Mozilla/5.0 (Windows NT 10.0)")]
        [InlineData("Python-urllib/3.14")]
        public void AnythingThatIsNotAKodiAddOn_HasNoName(string? agent) => KodiRejectionAlert.AddOnName(agent).Should().BeNull();

        private async Task<int> AdminAsync()
        {
            var reg = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new { username = $"kr_{Guid.NewGuid():N}", password = "Password123!" });
            var id = JsonDocument.Parse(await reg.Content.ReadAsStringAsync()).RootElement.GetProperty("data").GetProperty("user").GetProperty("id").GetInt32();
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            (await db.Users.FirstAsync(u => u.Id == id)).IsAdmin = true;
            await db.SaveChangesAsync();
            return id;
        }

        private async Task<List<Notification>> AlertsAsync(int adminId)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            return await db.Notifications.AsNoTracking().Where(n => n.UserId == adminId && n.Kind == NotificationKinds.DeviceRejected).ToListAsync();
        }

        private async Task<HttpStatusCode> CallAsync(string agent)
        {
            var c = _factory.CreateClient();
            c.DefaultRequestHeaders.Add("X-API-Key", "not-a-key-" + Guid.NewGuid().ToString("N"));
            c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", agent);
            return (await c.PostAsJsonAsync("/api/v1/scrobble/rating", new { title = "x", rating = 7 })).StatusCode;
        }

        [Fact]
        public async Task ARejectedKodiAddOn_RaisesOneAlertNamingIt_AndRepeatsDoNotPileUp()
        {
            var admin = await AdminAsync();

            (await CallAsync("Kodi/Chronicle-Rating/1.0")).Should().Be(HttpStatusCode.Unauthorized);
            (await CallAsync("Kodi/Chronicle-Rating/1.0")).Should().Be(HttpStatusCode.Unauthorized);

            var alerts = await AlertsAsync(admin);
            var alert = alerts.Should().ContainSingle().Subject;
            alert.Title.Should().Contain("Chronicle Rating").And.Contain("turned away");
            alert.Body.Should().Contain("Edit Connection");
            alert.Link.Should().Be("/settings/api-keys");
        }

        [Fact]
        public async Task ARejectedKeyFromAnythingElse_RaisesNoKodiAlert()
        {
            var admin = await AdminAsync();

            (await CallAsync("Mozilla/5.0 (Windows NT 10.0)")).Should().Be(HttpStatusCode.Unauthorized);

            (await AlertsAsync(admin)).Should().BeEmpty();
        }
    }
}
