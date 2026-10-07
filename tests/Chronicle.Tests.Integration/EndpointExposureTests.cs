using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Chronicle.API.Authentication;
using Chronicle.API.Helpers;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;

namespace Chronicle.Tests.Integration
{
    /// <summary>
    /// Regression tests for the 2026-10-07 API audit (docs/plans/2026-10-07-session-keys-design.md §6.4):
    /// endpoints that disclosed information or acted as a proxy to anonymous callers.
    /// </summary>
    public class EndpointExposureTests : IClassFixture<ChronicleApiFactory>
    {
        private readonly ChronicleApiFactory _factory;

        public EndpointExposureTests(ChronicleApiFactory factory)
        {
            factory.SeedDatabase();
            _factory = factory;
        }

        /// <summary>A signed-in client. The first account ever registered in a fresh database is made
        /// admin automatically, so the role is set explicitly rather than left to test order.</summary>
        private async Task<HttpClient> SignedInClientAsync(bool admin = false)
        {
            var client = _factory.CreateClient();
            var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
                new { username = $"x_{Guid.NewGuid():N}", password = "Password123!" });
            reg.StatusCode.Should().Be(HttpStatusCode.OK);
            var data = JsonDocument.Parse(await reg.Content.ReadAsStringAsync()).RootElement.GetProperty("data");
            var key = data.GetProperty("token").GetString()!;
            var id = data.GetProperty("user").GetProperty("id").GetInt32();
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
                var user = await db.Users.FirstAsync(u => u.Id == id);
                user.IsAdmin = admin;
                await db.SaveChangesAsync();
            }
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            return client;
        }

        // ── F-1 diagnostics ───────────────────────────────────────────────────

        [Fact]
        public async Task Diagnostics_RequiresASession()
        {
            (await _factory.CreateClient().GetAsync("/api/v1/diagnostics"))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task Diagnostics_AnonymousResponseNeverContainsPaths()
        {
            var body = await (await _factory.CreateClient().GetAsync("/api/v1/diagnostics")).Content.ReadAsStringAsync();

            body.Should().NotContain("repoRoot", because: "no server paths may reach an anonymous caller");
            body.Should().NotContain("dbPath");
        }

        [Fact]
        public async Task Diagnostics_IsAdminOnly_BecauseRegistrationIsOpen()
        {
            var ordinary = await SignedInClientAsync(admin: false);
            var admin = await SignedInClientAsync(admin: true);

            (await ordinary.GetAsync("/api/v1/diagnostics")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await admin.GetAsync("/api/v1/diagnostics")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // ── F-3 progress endpoints ────────────────────────────────────────────

        [Theory]
        [InlineData("/api/v1/scan/status")]
        [InlineData("/api/v1/scan/import-progress")]
        [InlineData("/api/v1/scan/progress")]
        [InlineData("/api/v1/media/overrides/reset-progress")]
        public async Task ProgressEndpoints_RequireASession(string url)
        {
            (await _factory.CreateClient().GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Theory]
        [InlineData("/api/v1/scan/status")]
        [InlineData("/api/v1/scan/import-progress")]
        [InlineData("/api/v1/scan/progress")]
        [InlineData("/api/v1/media/overrides/reset-progress")]
        public async Task ProgressEndpoints_StillWorkForASignedInUser(string url)
        {
            var client = await SignedInClientAsync();

            (await client.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // ── F-2 poster proxy ──────────────────────────────────────────────────

        [Fact]
        public async Task PosterProxy_RequiresASession()
        {
            var response = await _factory.CreateClient()
                .GetAsync("/api/v1/media/poster-proxy?url=" + Uri.EscapeDataString("https://example.com/a.jpg"));

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Theory]
        [InlineData("")]
        [InlineData("not a url")]
        [InlineData("file:///c:/windows/win.ini")]
        [InlineData("ftp://example.com/a.jpg")]
        [InlineData("https://user:pass@example.com/a.jpg")]
        public async Task PosterProxy_RejectsNonHttpAndCredentialUrls(string url)
        {
            var client = await SignedInClientAsync();

            var response = await client.GetAsync("/api/v1/media/poster-proxy?url=" + Uri.EscapeDataString(url));

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Theory]
        [InlineData("http://127.0.0.1/a.jpg")]
        [InlineData("http://localhost/a.jpg")]
        [InlineData("http://10.0.0.162/a.jpg")]
        [InlineData("http://192.168.1.1/a.jpg")]
        [InlineData("http://172.16.0.1/a.jpg")]
        [InlineData("http://169.254.169.254/latest/meta-data/")]
        [InlineData("http://[::1]/a.jpg")]
        [InlineData("http://[fd00::1]/a.jpg")]
        [InlineData("http://0.0.0.0/a.jpg")]
        public async Task PosterProxy_RefusesPrivateAndLoopbackDestinations(string url)
        {
            var client = await SignedInClientAsync();

            var response = await client.GetAsync("/api/v1/media/poster-proxy?url=" + Uri.EscapeDataString(url));

            // Refused before any request leaves the machine: indistinguishable from "no image".
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Theory]
        [InlineData("127.0.0.1", false)]
        [InlineData("127.255.255.254", false)]
        [InlineData("10.0.0.1", false)]
        [InlineData("10.255.255.255", false)]
        [InlineData("172.15.255.255", true)]
        [InlineData("172.16.0.0", false)]
        [InlineData("172.31.255.255", false)]
        [InlineData("172.32.0.0", true)]
        [InlineData("192.168.0.1", false)]
        [InlineData("192.169.0.1", true)]
        [InlineData("169.254.169.254", false)]
        [InlineData("100.64.0.1", false)]
        [InlineData("100.127.255.255", false)]
        [InlineData("100.128.0.1", true)]
        [InlineData("0.0.0.0", false)]
        [InlineData("198.18.0.1", false)]
        [InlineData("224.0.0.1", false)]
        [InlineData("255.255.255.255", false)]
        [InlineData("8.8.8.8", true)]
        [InlineData("151.101.1.1", true)]
        [InlineData("::1", false)]
        [InlineData("::", false)]
        [InlineData("fe80::1", false)]
        [InlineData("fc00::1", false)]
        [InlineData("fd12:3456::1", false)]
        [InlineData("ff02::1", false)]
        [InlineData("::ffff:127.0.0.1", false)]        // IPv4-mapped loopback
        [InlineData("::ffff:10.0.0.5", false)]         // IPv4-mapped private
        [InlineData("::ffff:8.8.8.8", true)]
        [InlineData("2606:4700:4700::1111", true)]
        public void IsPublicAddress_ClassifiesCorrectly(string address, bool expected)
        {
            SafeImageFetcher.IsPublicAddress(System.Net.IPAddress.Parse(address)).Should().Be(expected);
        }

        // ── audit-log hygiene ─────────────────────────────────────────────────

        [Fact]
        public void AuthAudit_Clean_RemovesControlCharactersAndCapsLength()
        {
            var forged = "bob" + (char)13 + (char)10 + "AUTH login OK: user admin";
            var cleaned = AuthAudit.Clean(forged)!;
            cleaned.Should().NotContain(((char)10).ToString()).And.NotContain(((char)13).ToString());
            cleaned.Should().Contain("AUTH login OK", "the text is kept, only the line break is neutralised");
            AuthAudit.Clean(new string('x', 500))!.Length.Should().Be(51);   // 50 + ellipsis
            AuthAudit.Clean(null).Should().BeNull();
            AuthAudit.Clean("plain-name").Should().Be("plain-name");
        }

        [Fact]
        public void AuthAudit_Ip_ShowsTheRealPeerWhenAForwardedAddressWasApplied()
        {
            var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("1.2.3.4");   // claimed via X-Forwarded-For
            ctx.Request.Headers["X-Original-For"] = "10.0.0.7:5555";                  // set by the forwarded-headers middleware

            var ip = AuthAudit.Ip(ctx);

            ip.Should().Contain("1.2.3.4").And.Contain("10.0.0.7");
        }

        [Fact]
        public void AuthAudit_Ip_IsJustTheAddressWhenNothingWasForwarded()
        {
            var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.7");

            AuthAudit.Ip(ctx).Should().Be("10.0.0.7");
        }
    }
}
