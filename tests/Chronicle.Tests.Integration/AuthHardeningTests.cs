using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Chronicle.API.Authentication;
using Chronicle.API.Controllers;
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
    /// The 2026-10-07 hardening: login/registration/pairing throttling, API-key scopes, and the
    /// session cookie that lets image tags authenticate. Each test builds its own host so it can
    /// set its own limits and capture its own audit log.
    /// </summary>
    public class AuthHardeningTests : IClassFixture<ChronicleApiFactory>, IDisposable
    {
        private const string Password = "Password123!";
        private readonly ChronicleApiFactory _root;
        private readonly InMemorySink _logs = new();
        private readonly List<IDisposable> _hosts = new();

        public AuthHardeningTests(ChronicleApiFactory root)
        {
            root.SeedDatabase();
            _root = root;
        }

        public void Dispose() { foreach (var h in _hosts) h.Dispose(); }

        // ── helpers ───────────────────────────────────────────────────────────

        private WebApplicationFactory<Program> Host(Action<TestAuthSettings>? limits = null, Action<Microsoft.AspNetCore.Hosting.IWebHostBuilder>? more = null)
        {
            var settings = TestAuthSettings.Permissive();
            limits?.Invoke(settings);
            var captured = new LoggerConfiguration().WriteTo.Sink(_logs).CreateLogger();
            var host = _root.WithWebHostBuilder(b =>
            {
                b.ConfigureServices(s =>
                {
                    s.AddSingleton<ICachedAppSettings>(settings);
                    s.AddSingleton(new AuthAuditLog(() => captured));
                });
                more?.Invoke(b);
            });
            _hosts.Add(host);
            return host;
        }

        private static async Task<JsonElement> Json(HttpResponseMessage r) =>
            JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

        private sealed record Account(int Id, string Username, string Key);

        private static async Task<Account> RegisterAsync(WebApplicationFactory<Program> host, bool admin, HttpClient? client = null)
        {
            client ??= host.CreateClient();
            var username = $"h_{Guid.NewGuid():N}";
            var response = await client.PostAsJsonAsync("/api/v1/auth/register", new { username, password = Password });
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var data = (await Json(response)).GetProperty("data");
            var id = data.GetProperty("user").GetProperty("id").GetInt32();
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
                var u = await db.Users.FirstAsync(x => x.Id == id);
                u.IsAdmin = admin;
                await db.SaveChangesAsync();
            }
            return new Account(id, username, data.GetProperty("token").GetString()!);
        }

        private static HttpClient Bearer(WebApplicationFactory<Program> host, string key)
        {
            var c = host.CreateClient();
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            return c;
        }

        private static HttpClient ApiKey(WebApplicationFactory<Program> host, string rawKey)
        {
            var c = host.CreateClient();
            c.DefaultRequestHeaders.Add("X-API-Key", rawKey);
            return c;
        }

        private static async Task<(int Id, string Raw)> MintKeyAsync(HttpClient session, string scope)
        {
            var response = await session.PostAsJsonAsync("/api/v1/tokens", new { name = $"k-{scope}", scope });
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var data = (await Json(response)).GetProperty("data");
            data.GetProperty("scope").GetString().Should().Be(scope);
            return (data.GetProperty("id").GetInt32(), data.GetProperty("token").GetString()!);
        }

        /// <summary>The in-process test server reports no client address at all, which is not
        /// what the forwarded-headers middleware sees in production. This gives every request a
        /// real TCP peer address before that middleware runs.</summary>
        private sealed class PeerAddressFilter(string address) : Microsoft.AspNetCore.Hosting.IStartupFilter
        {
            public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(
                Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
            {
                Microsoft.AspNetCore.Builder.UseExtensions.Use(app, async (ctx, nxt) =>
                {
                    ctx.Connection.RemoteIpAddress = IPAddress.Parse(address);
                    ctx.Connection.RemotePort = 4242;
                    await nxt(ctx);
                });
                next(app);
            };
        }

        private string LogText() => string.Join("\n", _logs.LogEvents.Select(e => e.RenderMessage()));

        private static HttpRequestMessage From(string address, HttpMethod method, string url, object? body = null)
        {
            var req = new HttpRequestMessage(method, url);
            req.Headers.Add("X-Forwarded-For", address);
            if (body is not null) req.Content = JsonContent.Create(body);
            return req;
        }

        // ══ login throttling ══════════════════════════════════════════════════

        [Fact]
        public async Task RepeatedWrongPasswords_ThenEvenTheRightOneIsRefused_With429AndRetryAfter()
        {
            var host = Host(s => s.Values[LoginThrottle.MaxPerAddressAndUserKey] = "3");
            var user = await RegisterAsync(host, admin: false);
            var client = host.CreateClient();

            for (var i = 0; i < 3; i++)
            {
                var wrong = await client.SendAsync(From("10.8.0.1", HttpMethod.Post, "/api/v1/auth/login", new { user.Username, password = "wrong-password" }));
                wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            }

            var blocked = await client.SendAsync(From("10.8.0.1", HttpMethod.Post, "/api/v1/auth/login", new { user.Username, password = Password }));

            blocked.StatusCode.Should().Be((HttpStatusCode)429, "the correct password must not be tried while blocked");
            blocked.Headers.RetryAfter.Should().NotBeNull();
            (await Json(blocked)).GetProperty("error").GetProperty("code").GetString().Should().Be("TOO_MANY_ATTEMPTS");
            LogText().Should().Contain("login THROTTLED").And.Contain(user.Username);
        }

        [Fact]
        public async Task TheBlockIsPerAddress_ADifferentAddressCanStillSignIn()
        {
            var host = Host(s => s.Values[LoginThrottle.MaxPerAddressAndUserKey] = "3");
            var user = await RegisterAsync(host, admin: false);
            var client = host.CreateClient();
            for (var i = 0; i < 3; i++)
                await client.SendAsync(From("10.8.0.1", HttpMethod.Post, "/api/v1/auth/login", new { user.Username, password = "wrong-password" }));

            var other = await client.SendAsync(From("10.8.0.2", HttpMethod.Post, "/api/v1/auth/login", new { user.Username, password = Password }));

            other.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task ASuccessfulSignIn_ResetsTheFailureCount()
        {
            var host = Host(s => s.Values[LoginThrottle.MaxPerAddressAndUserKey] = "3");
            var user = await RegisterAsync(host, admin: false);
            var client = host.CreateClient();

            for (var round = 0; round < 3; round++)
            {
                for (var i = 0; i < 2; i++)
                    await client.SendAsync(From("10.8.0.3", HttpMethod.Post, "/api/v1/auth/login", new { user.Username, password = "wrong-password" }));
                var ok = await client.SendAsync(From("10.8.0.3", HttpMethod.Post, "/api/v1/auth/login", new { user.Username, password = Password }));
                ok.StatusCode.Should().Be(HttpStatusCode.OK, $"round {round}: two misses then a hit never reaches the limit of 3");
            }
        }

        [Fact]
        public async Task AddressLimit_BlocksOneMachineTryingManyUsernames()
        {
            var host = Host(s => s.Values[LoginThrottle.MaxPerAddressKey] = "4");
            var client = host.CreateClient();
            for (var i = 0; i < 4; i++)
                await client.SendAsync(From("10.8.0.4", HttpMethod.Post, "/api/v1/auth/login", new { username = $"nobody{i}", password = "xxxxxxxx" }));

            var next = await client.SendAsync(From("10.8.0.4", HttpMethod.Post, "/api/v1/auth/login", new { username = "yet-another", password = "xxxxxxxx" }));

            next.StatusCode.Should().Be((HttpStatusCode)429);
        }

        [Fact]
        public async Task Registration_IsLimitedPerAddress()
        {
            var host = Host(s => s.Values[LoginThrottle.RegisterMaxKey] = "2");
            var client = host.CreateClient();
            for (var i = 0; i < 2; i++)
            {
                var ok = await client.SendAsync(From("10.8.0.5", HttpMethod.Post, "/api/v1/auth/register", new { username = $"reg_{Guid.NewGuid():N}", password = Password }));
                ok.StatusCode.Should().Be(HttpStatusCode.OK);
            }

            var third = await client.SendAsync(From("10.8.0.5", HttpMethod.Post, "/api/v1/auth/register", new { username = $"reg_{Guid.NewGuid():N}", password = Password }));
            var elsewhere = await client.SendAsync(From("10.8.0.6", HttpMethod.Post, "/api/v1/auth/register", new { username = $"reg_{Guid.NewGuid():N}", password = Password }));

            third.StatusCode.Should().Be((HttpStatusCode)429);
            elsewhere.StatusCode.Should().Be(HttpStatusCode.OK);
            LogText().Should().Contain("register THROTTLED");
        }

        [Fact]
        public async Task ThrottleSettingsLiveInAppSettings_NotInCode()
        {
            // A setting read from the database changes the limit; the default of 5 does not apply.
            var host = Host(s => s.Values[LoginThrottle.MaxPerAddressAndUserKey] = "1");
            var user = await RegisterAsync(host, admin: false);
            var client = host.CreateClient();
            await client.SendAsync(From("10.8.0.7", HttpMethod.Post, "/api/v1/auth/login", new { user.Username, password = "wrong-password" }));

            var second = await client.SendAsync(From("10.8.0.7", HttpMethod.Post, "/api/v1/auth/login", new { user.Username, password = Password }));

            second.StatusCode.Should().Be((HttpStatusCode)429);
        }

        // ══ device pairing throttling ═════════════════════════════════════════

        [Fact]
        public async Task PairingInitiate_IsLimitedPerAddress()
        {
            var host = Host(s => s.Values[DeviceAuthController.InitiatesPerHourKey] = "2");
            var client = host.CreateClient();
            for (var i = 0; i < 2; i++)
                (await client.SendAsync(From("10.8.1.1", HttpMethod.Post, "/api/v1/auth/device", new { deviceName = "box" }))).StatusCode.Should().Be(HttpStatusCode.OK);

            var third = await client.SendAsync(From("10.8.1.1", HttpMethod.Post, "/api/v1/auth/device", new { deviceName = "box" }));

            third.StatusCode.Should().Be((HttpStatusCode)429);
            LogText().Should().Contain("device-pairing").And.Contain("THROTTLED");
        }

        [Fact]
        public async Task GuessingPairingCodes_IsLimitedPerAddress_ButPollingARealCodeIsNot()
        {
            var host = Host(s => s.Values[DeviceAuthController.MissesPer15MinKey] = "3");
            var client = host.CreateClient();
            var started = await client.SendAsync(From("10.8.1.2", HttpMethod.Post, "/api/v1/auth/device", new { deviceName = "box" }));
            var code = (await Json(started)).GetProperty("data").GetProperty("code").GetString()!;

            for (var i = 0; i < 10; i++)
                (await client.SendAsync(From("10.8.1.2", HttpMethod.Get, $"/api/v1/auth/device/{code}/poll"))).StatusCode
                    .Should().Be(HttpStatusCode.OK, "a device polling its own pending code every few seconds is normal");

            for (var i = 0; i < 3; i++)
                (await client.SendAsync(From("10.8.1.2", HttpMethod.Get, $"/api/v1/auth/device/ZZZZZ{i}/poll"))).StatusCode.Should().Be(HttpStatusCode.OK);

            (await client.SendAsync(From("10.8.1.2", HttpMethod.Get, "/api/v1/auth/device/ZZZZZ9/poll"))).StatusCode
                .Should().Be((HttpStatusCode)429, "three unknown codes is the limit");
            (await client.SendAsync(From("10.8.1.9", HttpMethod.Get, "/api/v1/auth/device/ZZZZZ8/poll"))).StatusCode
                .Should().Be(HttpStatusCode.OK, "another address is unaffected");
        }

        [Fact]
        public async Task PairingFlow_IssuesADeviceScopedKey_AndOnlyALoginCanApprove()
        {
            var host = Host();
            var user = await RegisterAsync(host, admin: true);
            var session = Bearer(host, user.Key);
            var anon = host.CreateClient();
            var code = (await Json(await anon.PostAsJsonAsync("/api/v1/auth/device", new { deviceName = "Living Room" })))
                .GetProperty("data").GetProperty("code").GetString()!;

            // An API key, even a full one owned by an admin, may not approve a pairing.
            var (_, fullRaw) = await MintKeyAsync(session, "full");
            (await ApiKey(host, fullRaw).PostAsync($"/api/v1/auth/device/{code}/approve", null)).StatusCode
                .Should().Be(HttpStatusCode.Unauthorized);

            (await session.PostAsync($"/api/v1/auth/device/{code}/approve", null)).StatusCode.Should().Be(HttpStatusCode.OK);
            var poll = await Json(await anon.GetAsync($"/api/v1/auth/device/{code}/poll"));
            var deviceKey = poll.GetProperty("data").GetProperty("apiKey").GetString()!;

            var device = ApiKey(host, deviceKey);
            (await device.GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await device.GetAsync("/api/v1/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "the key was paired by an admin but is not an admin");

            var list = await Json(await session.GetAsync("/api/v1/tokens"));
            list.GetProperty("data").EnumerateArray()
                .First(t => t.GetProperty("name").GetString()!.StartsWith("Living Room"))
                .GetProperty("scope").GetString().Should().Be("device");
        }

        // ══ API-key scopes ════════════════════════════════════════════════════

        [Fact]
        public async Task DeviceKey_WorksForItsClients_ButIsNeverAnAdmin_EvenWhenItsOwnerIs()
        {
            var host = Host();
            var admin = await RegisterAsync(host, admin: true);
            var (_, raw) = await MintKeyAsync(Bearer(host, admin.Key), "device");
            var device = ApiKey(host, raw);

            (await device.GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await device.GetAsync("/api/v1/scrobble/history")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await device.GetAsync("/api/v1/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await device.DeleteAsync("/api/v1/media/1")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await device.PostAsync("/api/v1/scrobble/admin/cleanup-cross-show-corruption", null)).StatusCode
                .Should().Be(HttpStatusCode.Forbidden, "an admin-only route inside an allowed prefix still needs the Admin role");
        }

        [Fact]
        public async Task DeviceKey_CannotManageKeys_OrPairings_OrSessions()
        {
            var host = Host();
            var admin = await RegisterAsync(host, admin: true);
            var (_, raw) = await MintKeyAsync(Bearer(host, admin.Key), "device");
            var device = ApiKey(host, raw);

            (await device.GetAsync("/api/v1/tokens")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await device.PostAsJsonAsync("/api/v1/tokens", new { name = "mine", scope = "full" })).StatusCode
                .Should().Be(HttpStatusCode.Unauthorized);
            (await device.GetAsync("/api/v1/auth/sessions")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task FullKey_ActsAsItsOwner_ButStillCannotManageKeys()
        {
            var host = Host();
            var admin = await RegisterAsync(host, admin: true);
            var (_, raw) = await MintKeyAsync(Bearer(host, admin.Key), "full");
            var full = ApiKey(host, raw);

            (await full.GetAsync("/api/v1/users")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await full.GetAsync("/api/v1/tokens")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await full.PostAsJsonAsync("/api/v1/tokens", new { name = "x" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task BridgeKey_MayOnlySearchCreateAndRefreshMedia()
        {
            var host = Host();
            var admin = await RegisterAsync(host, admin: true);
            var (_, raw) = await MintKeyAsync(Bearer(host, admin.Key), "bridge");
            var bridge = ApiKey(host, raw);

            (await bridge.GetAsync("/api/v1/media/types")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await bridge.GetAsync("/api/v1/scrobble/history")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await bridge.DeleteAsync("/api/v1/media/1")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await bridge.GetAsync("/api/v1/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        [Fact]
        public async Task ChangingAKeysScope_TakesEffectOnItsNextRequest()
        {
            var host = Host();
            var user = await RegisterAsync(host, admin: false);
            var session = Bearer(host, user.Key);
            var (id, raw) = await MintKeyAsync(session, "device");
            var key = ApiKey(host, raw);
            (await key.GetAsync("/api/v1/scrobble/history")).StatusCode.Should().Be(HttpStatusCode.OK);

            (await session.PutAsJsonAsync($"/api/v1/tokens/{id}/scope", new { scope = "bridge" })).StatusCode.Should().Be(HttpStatusCode.OK);

            (await key.GetAsync("/api/v1/scrobble/history")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await key.GetAsync("/api/v1/media/types")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task UnknownScope_IsRejected_OnCreateAndOnChange()
        {
            var host = Host();
            var user = await RegisterAsync(host, admin: false);
            var session = Bearer(host, user.Key);

            (await session.PostAsJsonAsync("/api/v1/tokens", new { name = "x", scope = "superuser" })).StatusCode
                .Should().Be(HttpStatusCode.BadRequest);
            var (id, _) = await MintKeyAsync(session, "device");
            (await session.PutAsJsonAsync($"/api/v1/tokens/{id}/scope", new { scope = "root" })).StatusCode
                .Should().Be(HttpStatusCode.BadRequest);
        }

        [Fact]
        public async Task OmittingTheScope_StaysFull_SoExistingClientsOfTheEndpointKeepWorking()
        {
            var host = Host();
            var user = await RegisterAsync(host, admin: false);

            var created = await Bearer(host, user.Key).PostAsJsonAsync("/api/v1/tokens", new { name = "legacy" });

            (await Json(created)).GetProperty("data").GetProperty("scope").GetString().Should().Be("full");
        }

        [Fact]
        public async Task ScopeDenials_AreLogged()
        {
            var host = Host();
            var user = await RegisterAsync(host, admin: false);
            var (_, raw) = await MintKeyAsync(Bearer(host, user.Key), "device");

            await ApiKey(host, raw).GetAsync("/api/v1/library");

            LogText().Should().Contain("api-key scope denied").And.Contain("/api/v1/library");
        }

        [Fact]
        public async Task DotSegments_CannotWalkOutOfTheAllowedPrefix()
        {
            var host = Host();
            var admin = await RegisterAsync(host, admin: true);
            var (_, raw) = await MintKeyAsync(Bearer(host, admin.Key), "device");

            var response = await ApiKey(host, raw).GetAsync("/api/v1/scrobble/../users");

            response.StatusCode.Should().NotBe(HttpStatusCode.OK);
        }

        // ══ session cookie for <img> tags ═════════════════════════════════════

        private static string? CookieValue(HttpResponseMessage r, out string raw)
        {
            raw = r.Headers.TryGetValues("Set-Cookie", out var values)
                ? values.FirstOrDefault(v => v.StartsWith(SessionCookie.Name + "=")) ?? string.Empty
                : string.Empty;
            if (raw.Length == 0) return null;
            var first = raw.Split(';')[0];
            return first[(SessionCookie.Name.Length + 1)..];
        }

        [Fact]
        public async Task Login_SetsAnHttpOnlySameSiteStrictCookie_ScopedToTheApi()
        {
            var host = Host();
            var user = await RegisterAsync(host, admin: false);

            var response = await host.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { user.Username, password = Password });

            var value = CookieValue(response, out var raw);
            value.Should().StartWith("chr_sess_");
            raw.ToLowerInvariant().Should().Contain("httponly").And.Contain("samesite=strict").And.Contain("path=/api/");
        }

        [Fact]
        public async Task ImageRoutes_AcceptTheCookie_AndRefuseAnonymousCallers()
        {
            var host = Host();
            var user = await RegisterAsync(host, admin: false);
            var login = await host.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { user.Username, password = Password });
            var cookie = CookieValue(login, out _)!;

            (await host.CreateClient().GetAsync("/api/v1/media/999999/local-poster")).StatusCode
                .Should().Be(HttpStatusCode.Unauthorized);
            (await host.CreateClient().GetAsync("/api/v1/plugins/999999/icon")).StatusCode
                .Should().Be(HttpStatusCode.Unauthorized);

            foreach (var url in new[] { "/api/v1/media/999999/local-poster", "/api/v1/plugins/999999/icon" })
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("Cookie", $"{SessionCookie.Name}={cookie}");
                (await host.CreateClient().SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.NotFound,
                    $"{url}: authenticated by the cookie, then genuinely not found");
            }
        }

        [Fact]
        public async Task TheCookie_IsNotAcceptedForAnythingButImages()
        {
            var host = Host();
            var user = await RegisterAsync(host, admin: true);
            var login = await host.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { user.Username, password = Password });
            var cookie = CookieValue(login, out _)!;

            foreach (var url in new[] { "/api/v1/users/me", "/api/v1/users", "/api/v1/library", "/api/v1/auth/sessions" })
            {
                var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("Cookie", $"{SessionCookie.Name}={cookie}");
                (await host.CreateClient().SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                    $"{url} must need the Authorization header, so a cross-site request riding the cookie reads nothing");
            }
        }

        [Fact]
        public async Task TheCookie_DiesWithTheSession_OnLogoutAndOnRestart()
        {
            var host = Host();
            var user = await RegisterAsync(host, admin: false);
            var login = await host.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { user.Username, password = Password });
            var cookie = CookieValue(login, out _)!;
            var key = (await Json(login)).GetProperty("data").GetProperty("token").GetString()!;

            HttpRequestMessage Image(WebApplicationFactory<Program> h)
            {
                var r = new HttpRequestMessage(HttpMethod.Get, "/api/v1/media/999999/local-poster");
                r.Headers.Add("Cookie", $"{SessionCookie.Name}={cookie}");
                return r;
            }

            (await host.CreateClient().SendAsync(Image(host))).StatusCode.Should().Be(HttpStatusCode.NotFound);

            // Restart: a new host over the same database has an empty session store.
            var restarted = host.WithWebHostBuilder(_ => { });
            _hosts.Add(restarted);
            (await restarted.CreateClient().SendAsync(Image(restarted))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            // Logout on the original host clears the cookie and kills the session.
            var logout = await Bearer(host, key).PostAsync("/api/v1/auth/logout", null);
            logout.Headers.GetValues("Set-Cookie").Should().Contain(v => v.StartsWith(SessionCookie.Name + "=;") || v.Contains("expires=Thu, 01 Jan 1970"));
            (await host.CreateClient().SendAsync(Image(host))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task ImageRequests_ByCookie_DoNotCountAsActivity()
        {
            var host = Host();
            var user = await RegisterAsync(host, admin: false);
            var login = await host.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { user.Username, password = Password });
            var cookie = CookieValue(login, out _)!;
            var store = host.Services.GetRequiredService<ISessionStore>();
            var before = store.Validate(cookie, touch: false).Session!.LastSeenAt;
            await Task.Delay(30);

            var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/media/999999/local-poster");
            req.Headers.Add("Cookie", $"{SessionCookie.Name}={cookie}");
            await host.CreateClient().SendAsync(req);

            store.Validate(cookie, touch: false).Session!.LastSeenAt.Should().Be(before);
        }

        // ══ forwarded-header trust ════════════════════════════════════════════

        [Fact]
        public async Task WithTrustedProxiesConfigured_AnUntrustedClientCannotClaimAnAddress()
        {
            var host = Host(more: b =>
            {
                b.UseSetting("Security:TrustedProxies", "10.99.99.99");
                b.ConfigureServices(s => s.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter>(_ => new PeerAddressFilter("10.1.2.3")));
            });
            var client = host.CreateClient();

            await client.SendAsync(From("6.6.6.6", HttpMethod.Post, "/api/v1/auth/login", new { username = "spoofer", password = "xxxxxxxx" }));

            LogText().Should().Contain("login FAILED").And.Contain("10.1.2.3").And.NotContain("6.6.6.6",
                "the sender is not a trusted proxy, so its X-Forwarded-For is ignored");
        }

        [Fact]
        public async Task WithoutTrustedProxies_ASpoofedAddressIsShown_NextToTheRealPeer()
        {
            var host = Host(more: b =>
                b.ConfigureServices(s => s.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter>(_ => new PeerAddressFilter("10.1.2.3"))));
            var client = host.CreateClient();

            await client.SendAsync(From("6.6.6.6", HttpMethod.Post, "/api/v1/auth/login", new { username = "spoofer2", password = "xxxxxxxx" }));

            LogText().Should().Contain("6.6.6.6").And.Contain("forwarded").And.Contain("10.1.2.3",
                "the real connection address is shown next to the claimed one");
        }
    }
}
