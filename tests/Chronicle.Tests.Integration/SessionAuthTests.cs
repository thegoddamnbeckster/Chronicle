using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Chronicle.API.Authentication;
using Chronicle.Data;
using Chronicle.Services.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Sinks.InMemory;

namespace Chronicle.Tests.Integration
{
    /// <summary>
    /// The session-key guarantees: opaque server-issued keys, checked on every request, one per
    /// login, ended by logout / password change / deactivation / restart, never logged.
    /// Design: docs/plans/2026-10-07-session-keys-design.md
    /// </summary>
    public class SessionAuthTests : IClassFixture<ChronicleApiFactory>, IDisposable
    {
        private const string Password = "Password123!";
        private readonly WebApplicationFactory<Program> _factory;
        private readonly InMemorySink _logs = new();

        public SessionAuthTests(ChronicleApiFactory factory)
        {
            factory.SeedDatabase();

            // A host whose audit log writes to this test's own sink: nothing global is touched,
            // so parallel test classes cannot see or disturb each other's output.
            var captured = new LoggerConfiguration().WriteTo.Sink(_logs).CreateLogger();
            _factory = factory.WithWebHostBuilder(b => b.ConfigureServices(services =>
                services.AddSingleton(new AuthAuditLog(() => captured))));
        }

        public void Dispose() => _factory.Dispose();

        // ── helpers ───────────────────────────────────────────────────────────

        private sealed record Login(int UserId, string Username, string Key);

        private static async Task<JsonElement> Json(HttpResponseMessage r) =>
            JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

        private async Task<Login> RegisterAsync(HttpClient? client = null)
        {
            client ??= _factory.CreateClient();
            var username = $"s_{Guid.NewGuid():N}";
            var response = await client.PostAsJsonAsync("/api/v1/auth/register", new { username, password = Password });
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var data = (await Json(response)).GetProperty("data");
            return new Login(data.GetProperty("user").GetProperty("id").GetInt32(), username,
                data.GetProperty("token").GetString()!);
        }

        private async Task<string> LoginAsync(string username, HttpClient? client = null)
        {
            client ??= _factory.CreateClient();
            var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { username, password = Password });
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            return (await Json(response)).GetProperty("data").GetProperty("token").GetString()!;
        }

        private HttpClient ClientWith(string key, WebApplicationFactory<Program>? factory = null)
        {
            var client = (factory ?? _factory).CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            return client;
        }

        private async Task SetAdminAsync(int userId, bool isAdmin)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var user = await db.Users.FirstAsync(u => u.Id == userId);
            user.IsAdmin = isAdmin;
            await db.SaveChangesAsync();
        }

        private async Task SetActiveAsync(int userId, bool isActive)
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var user = await db.Users.FirstAsync(u => u.Id == userId);
            user.IsActive = isActive;
            await db.SaveChangesAsync();
        }

        private string AllLogText() =>
            string.Join("\n", _logs.LogEvents.Select(e => e.RenderMessage()));

        // ── the key itself ────────────────────────────────────────────────────

        [Fact]
        public async Task Login_ReturnsAServerIssuedSessionKey_NotAJwt()
        {
            var login = await RegisterAsync();

            login.Key.Should().StartWith("chr_sess_");
            login.Key.Should().NotContain(".", "a JWT has dots; a session key is opaque");
        }

        [Fact]
        public async Task ValidKey_IsAccepted()
        {
            var login = await RegisterAsync();

            (await ClientWith(login.Key).GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Theory]
        [InlineData("chr_sess_thiskeywasneverissued")]
        [InlineData("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxIn0.c2ln")]   // a leftover JWT
        [InlineData("garbage")]
        public async Task UnknownOrWrongShapeKey_Returns401_NeverA500(string key)
        {
            (await ClientWith(key).GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task NoHeader_Returns401()
        {
            (await _factory.CreateClient().GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        // ── one key per login ─────────────────────────────────────────────────

        [Fact]
        public async Task TwoLogins_GetDistinctKeys_AndEndingOneLeavesTheOther()
        {
            var first = await RegisterAsync();
            var secondKey = await LoginAsync(first.Username);

            secondKey.Should().NotBe(first.Key);
            (await ClientWith(first.Key).GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await ClientWith(secondKey).GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.OK);

            (await ClientWith(first.Key).PostAsync("/api/v1/auth/logout", null)).StatusCode.Should().Be(HttpStatusCode.OK);

            (await ClientWith(first.Key).GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await ClientWith(secondKey).GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // ── logout ────────────────────────────────────────────────────────────

        [Fact]
        public async Task Logout_MakesTheSameKeyFailImmediately()
        {
            var login = await RegisterAsync();
            var client = ClientWith(login.Key);

            (await client.PostAsync("/api/v1/auth/logout", null)).StatusCode.Should().Be(HttpStatusCode.OK);

            (await client.GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task LogoutAll_EndsEverySessionOfTheUser()
        {
            var first = await RegisterAsync();
            var secondKey = await LoginAsync(first.Username);

            (await ClientWith(first.Key).PostAsync("/api/v1/auth/logout-all", null)).StatusCode.Should().Be(HttpStatusCode.OK);

            (await ClientWith(first.Key).GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await ClientWith(secondKey).GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        // ── restart ───────────────────────────────────────────────────────────

        [Fact]
        public async Task Restart_EndsEverySession_ButCredentialsStillWork()
        {
            var login = await RegisterAsync();
            (await ClientWith(login.Key).GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.OK);

            // A new host over the SAME database is a restarted API: the in-memory store starts empty.
            using var restarted = _factory.WithWebHostBuilder(_ => { });

            (await ClientWith(login.Key, restarted).GetAsync("/api/v1/users/me"))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            var newKey = await LoginAsync(login.Username, restarted.CreateClient());
            newKey.Should().NotBe(login.Key);
            (await ClientWith(newKey, restarted).GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // ── live account state ────────────────────────────────────────────────

        [Fact]
        public async Task DemotingAnAdmin_TakesEffectOnTheVeryNextRequest()
        {
            var login = await RegisterAsync();
            await SetAdminAsync(login.UserId, true);
            var client = ClientWith(login.Key);
            (await client.GetAsync("/api/v1/users")).StatusCode.Should().Be(HttpStatusCode.OK);

            await SetAdminAsync(login.UserId, false);

            (await client.GetAsync("/api/v1/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        [Fact]
        public async Task PromotingAUser_TakesEffectWithoutReLogin()
        {
            var login = await RegisterAsync();
            await SetAdminAsync(login.UserId, false);
            var client = ClientWith(login.Key);
            (await client.GetAsync("/api/v1/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

            await SetAdminAsync(login.UserId, true);

            (await client.GetAsync("/api/v1/users")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task DeactivatedAccount_KeyStopsWorking_EvenWithoutAnExplicitRevoke()
        {
            var login = await RegisterAsync();
            var client = ClientWith(login.Key);
            (await client.GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.OK);

            await SetActiveAsync(login.UserId, false);

            (await client.GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        // ── password changes end other sessions ───────────────────────────────

        [Fact]
        public async Task SelfPasswordChange_EndsOtherSessions_ButKeepsTheCurrentOne()
        {
            var login = await RegisterAsync();
            var otherKey = await LoginAsync(login.Username);
            var current = ClientWith(login.Key);

            var change = await current.PutAsJsonAsync("/api/v1/users/me/password",
                new { currentPassword = Password, newPassword = "Brand-new-Pass-9!" });
            change.StatusCode.Should().Be(HttpStatusCode.OK);

            (await current.GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await ClientWith(otherKey).GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task AdminPasswordReset_EndsAllOfThatUsersSessions()
        {
            var admin = await RegisterAsync();
            await SetAdminAsync(admin.UserId, true);
            var target = await RegisterAsync();

            var reset = await ClientWith(admin.Key).PutAsJsonAsync($"/api/v1/users/{target.UserId}/password",
                new { newPassword = "Reset-by-admin-9!" });
            reset.StatusCode.Should().Be(HttpStatusCode.OK);

            (await ClientWith(target.Key).GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await ClientWith(admin.Key).GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task AdminDeactivatingAUser_EndsTheirSessions()
        {
            var admin = await RegisterAsync();
            await SetAdminAsync(admin.UserId, true);
            var target = await RegisterAsync();

            (await ClientWith(admin.Key).PutAsJsonAsync($"/api/v1/users/{target.UserId}/active", new { isActive = false }))
                .StatusCode.Should().Be(HttpStatusCode.OK);

            (await ClientWith(target.Key).GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        // ── sessions list ─────────────────────────────────────────────────────

        [Fact]
        public async Task SessionsList_ShowsOnlyTheCallersSessions_AndMarksTheCurrentOne()
        {
            var login = await RegisterAsync();
            await LoginAsync(login.Username);
            var other = await RegisterAsync();

            var response = await ClientWith(login.Key).GetAsync("/api/v1/auth/sessions");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var sessions = (await Json(response)).GetProperty("data").EnumerateArray().ToList();

            sessions.Should().HaveCount(2);
            sessions.Count(s => s.GetProperty("isCurrent").GetBoolean()).Should().Be(1);
            (await response.Content.ReadAsStringAsync()).Should().NotContain(login.Key).And.NotContain(other.Key);
        }

        [Fact]
        public async Task EndingAnotherUsersSessionById_Returns404_AndDoesNotEndIt()
        {
            var mine = await RegisterAsync();
            var theirs = await RegisterAsync();
            var theirSessions = await Json(await ClientWith(theirs.Key).GetAsync("/api/v1/auth/sessions"));
            var theirId = theirSessions.GetProperty("data")[0].GetProperty("id").GetGuid();

            var response = await ClientWith(mine.Key).DeleteAsync($"/api/v1/auth/sessions/{theirId}");

            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await ClientWith(theirs.Key).GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task EndingOneOfMyOtherSessions_Works()
        {
            var login = await RegisterAsync();
            var otherKey = await LoginAsync(login.Username);
            var list = await Json(await ClientWith(login.Key).GetAsync("/api/v1/auth/sessions"));
            var otherId = list.GetProperty("data").EnumerateArray()
                .First(s => !s.GetProperty("isCurrent").GetBoolean()).GetProperty("id").GetGuid();

            (await ClientWith(login.Key).DeleteAsync($"/api/v1/auth/sessions/{otherId}")).StatusCode.Should().Be(HttpStatusCode.OK);

            (await ClientWith(otherKey).GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await ClientWith(login.Key).GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task AdminCanListAndEndAnotherUsersSessions_ButANonAdminCannot()
        {
            var admin = await RegisterAsync();
            await SetAdminAsync(admin.UserId, true);
            var target = await RegisterAsync();
            var nobody = await RegisterAsync();
            await SetAdminAsync(nobody.UserId, false);

            (await ClientWith(nobody.Key).GetAsync($"/api/v1/users/{target.UserId}/sessions"))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);

            var list = await ClientWith(admin.Key).GetAsync($"/api/v1/users/{target.UserId}/sessions");
            list.StatusCode.Should().Be(HttpStatusCode.OK);
            (await Json(list)).GetProperty("data").GetArrayLength().Should().Be(1);

            (await ClientWith(admin.Key).DeleteAsync($"/api/v1/users/{target.UserId}/sessions"))
                .StatusCode.Should().Be(HttpStatusCode.OK);
            (await ClientWith(target.Key).GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        // ── API keys are separate ─────────────────────────────────────────────

        [Fact]
        public async Task ApiKey_StillAuthenticates_AndCannotManageBrowserSessions()
        {
            var login = await RegisterAsync();
            var created = await ClientWith(login.Key).PostAsJsonAsync("/api/v1/tokens", new { name = "kodi" });
            created.StatusCode.Should().Be(HttpStatusCode.OK, "the token endpoint is part of the existing API");
            var raw = (await Json(created)).GetProperty("data").GetProperty("token").GetString()!;

            var apiKeyClient = _factory.CreateClient();
            apiKeyClient.DefaultRequestHeaders.Add("X-API-Key", raw);

            (await apiKeyClient.GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await apiKeyClient.GetAsync("/api/v1/auth/sessions")).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                "session management needs a real login");
            (await apiKeyClient.PostAsync("/api/v1/auth/logout-all", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            // Ending every session does not touch the API key.
            await ClientWith(login.Key).PostAsync("/api/v1/auth/logout-all", null);
            (await apiKeyClient.GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // ── idle polling ──────────────────────────────────────────────────────

        [Fact]
        public async Task BackgroundPolling_DoesNotCountAsActivity()
        {
            var login = await RegisterAsync();
            var store = _factory.Services.GetRequiredService<ISessionStore>();

            string? lastSeenOf() => store.ListForUser(login.UserId).Single().LastSeenAt.ToString("O");
            var before = lastSeenOf();
            await Task.Delay(30);

            var polling = ClientWith(login.Key);
            polling.DefaultRequestHeaders.Add(SessionAuthenticationHandler.BackgroundHeader, "1");
            (await polling.GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.OK);
            lastSeenOf().Should().Be(before, "a background poll must not restart the idle clock");

            (await ClientWith(login.Key).GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.OK);
            lastSeenOf().Should().NotBe(before, "a user-triggered request does");
        }

        // ── audit logging ─────────────────────────────────────────────────────

        [Fact]
        public async Task SuccessfulLogin_IsLogged_WithUserAndSession_ButNeverTheKeyOrPassword()
        {
            var login = await RegisterAsync();
            var key = await LoginAsync(login.Username);

            var text = AllLogText();
            text.Should().Contain("login OK").And.Contain(login.Username);
            text.Should().NotContain(key).And.NotContain(login.Key).And.NotContain(Password);
        }

        [Fact]
        public async Task FailedLogin_IsLogged_WithTheClaimedUsername_ButNotThePassword()
        {
            var username = $"ghost_{Guid.NewGuid():N}";

            var response = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login",
                new { username, password = "super-secret-wrong-password" });
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            var text = AllLogText();
            text.Should().Contain("login FAILED").And.Contain(username);
            text.Should().NotContain("super-secret-wrong-password");
        }

        [Fact]
        public async Task RejectedSessionKey_IsLogged_ByTagOnly()
        {
            var login = await RegisterAsync();
            await ClientWith(login.Key).PostAsync("/api/v1/auth/logout", null);

            (await ClientWith(login.Key).GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            var text = AllLogText();
            text.Should().Contain("rejected \"Session\" credential").And.Contain(SessionStore.LogTag(login.Key));
            text.Should().NotContain(login.Key);
        }

        [Fact]
        public async Task RejectedApiKey_IsLogged()
        {
            var client = _factory.CreateClient();
            var fake = "chr_live_" + Guid.NewGuid().ToString("N");
            client.DefaultRequestHeaders.Add("X-API-Key", fake);

            (await client.GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            var text = AllLogText();
            text.Should().Contain("rejected \"ApiKey\" credential").And.Contain(SessionStore.LogTag(fake));
            text.Should().NotContain(fake);
        }

        [Fact]
        public async Task Registration_IsLogged()
        {
            var login = await RegisterAsync();

            AllLogText().Should().Contain("register OK").And.Contain(login.Username);
        }

        [Fact]
        public async Task Logout_IsLogged()
        {
            var login = await RegisterAsync();
            await ClientWith(login.Key).PostAsync("/api/v1/auth/logout", null);

            AllLogText().Should().Contain("logout");
        }
    }
}
