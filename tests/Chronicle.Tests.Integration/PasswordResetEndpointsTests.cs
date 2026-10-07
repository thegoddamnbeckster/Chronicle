using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Chronicle.API;
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
    public sealed class RecordingEmailSender : IEmailSender
    {
        public List<(EmailMessage Message, SmtpCredentials Credentials)> Sent { get; } = [];
        public Exception? Throw { get; set; }

        public Task SendAsync(EmailMessage message, SmtpCredentials credentials, CancellationToken ct = default)
        {
            if (Throw is not null) throw Throw;
            lock (Sent) Sent.Add((message, credentials));
            return Task.CompletedTask;
        }
    }

    /// <summary>Forgot-password, reset-password, admin-issued codes, mail settings and the recovery command, end to end.</summary>
    public class PasswordResetEndpointsTests : IClassFixture<ChronicleApiFactory>, IDisposable
    {
        private const string Password = "Password123!";
        private const string NewPassword = "A-brand-new-one-1";
        private readonly ChronicleApiFactory _root;
        private readonly InMemorySink _logs = new();
        private readonly List<IDisposable> _hosts = [];

        public PasswordResetEndpointsTests(ChronicleApiFactory root) { root.SeedDatabase(); _root = root; }
        public void Dispose() { foreach (var h in _hosts) h.Dispose(); }

        // ── helpers ───────────────────────────────────────────────────────────

        private (WebApplicationFactory<Program> Host, RecordingEmailSender Mail) Host(Action<TestAuthSettings>? limits = null)
        {
            var settings = TestAuthSettings.Permissive();
            limits?.Invoke(settings);
            var mail = new RecordingEmailSender();
            var captured = new LoggerConfiguration().WriteTo.Sink(_logs).CreateLogger();
            var host = _root.WithWebHostBuilder(b => b.ConfigureServices(s =>
            {
                s.AddSingleton<ICachedAppSettings>(settings);
                s.AddSingleton<IEmailSender>(mail);
                s.AddSingleton(new AuthAuditLog(() => captured));
            }));
            _hosts.Add(host);
            // The database is shared by every test in the class; start each one with no mail settings saved.
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
                db.AppSettings.RemoveRange(db.AppSettings.Where(x => x.Key.StartsWith("email.")));
                db.PasswordResetTokens.RemoveRange(db.PasswordResetTokens);
                db.SaveChanges();
            }
            return (host, mail);
        }

        private static async Task<JsonElement> Json(HttpResponseMessage r) =>
            JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

        private sealed record Account(int Id, string Username, string Key, string Email);

        private static async Task<Account> NewAccountAsync(WebApplicationFactory<Program> host, bool admin = false, bool withEmail = true, bool active = true)
        {
            var username = $"pr_{Guid.NewGuid():N}";
            var email = $"{username}@example.com";
            var reg = await host.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new { username, password = Password, email = withEmail ? email : null });
            reg.StatusCode.Should().Be(HttpStatusCode.OK);
            var data = (await Json(reg)).GetProperty("data");
            var id = data.GetProperty("user").GetProperty("id").GetInt32();
            using var scope = host.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            var u = await db.Users.FirstAsync(x => x.Id == id);
            u.IsAdmin = admin;
            u.IsActive = active;
            await db.SaveChangesAsync();
            return new Account(id, username, data.GetProperty("token").GetString()!, email);
        }

        private static HttpClient Session(WebApplicationFactory<Program> host, string key)
        {
            var c = host.CreateClient();
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            return c;
        }

        private static async Task ConfigureEmailAsync(WebApplicationFactory<Program> host, string publicUrl = "https://chronicle.example.com")
        {
            var admin = await NewAccountAsync(host, admin: true);
            var response = await Session(host, admin.Key).PutAsJsonAsync("/api/v1/settings/email", new
            {
                host = "smtp.example.com", port = 587, security = "starttls", username = "mailer", password = "mail-secret",
                fromAddress = "chronicle@example.com", fromName = "Chronicle", publicUrl,
            });
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        private static async Task WaitForMailAsync(WebApplicationFactory<Program> host, RecordingEmailSender mail, int count)
        {
            await host.Services.GetRequiredService<ResetMailDispatcher>().WhenIdleAsync();
            mail.Sent.Count.Should().Be(count);
        }

        private static string TokenFrom(EmailMessage m)
        {
            var body = m.TextBody;
            var i = body.IndexOf("#token=", StringComparison.Ordinal) + "#token=".Length;
            return body[i..body.IndexOfAny(['\n', '\r', ' '], i)];
        }

        private async Task<HttpResponseMessage> SignInAsync(WebApplicationFactory<Program> host, string username, string password) =>
            await host.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new { username, password });

        private string LogText() => string.Join("\n", _logs.LogEvents.Select(e => e.RenderMessage()));

        private static HttpRequestMessage Post(string url, object body, string? from = null)
        {
            var r = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
            if (from is not null) r.Headers.Add("X-Forwarded-For", from);
            return r;
        }

        // ══ forgot password ═══════════════════════════════════════════════════

        [Fact]
        public async Task TheAnswerIsTheSame_ForARealAccount_AnUnknownOne_AndOneWithNoAddress()
        {
            var (host, mail) = Host();
            await ConfigureEmailAsync(host);
            var real = await NewAccountAsync(host);
            var noMail = await NewAccountAsync(host, withEmail: false);
            var client = host.CreateClient();

            var answers = new List<string>();
            foreach (var id in new[] { real.Username, "nobody-at-all", noMail.Username, real.Email })
            {
                var response = await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { identifier = id });
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                answers.Add(await response.Content.ReadAsStringAsync());
            }

            answers.Distinct().Should().HaveCount(1, "nothing in the reply may reveal whether the account exists");
            await host.Services.GetRequiredService<ResetMailDispatcher>().WhenIdleAsync();
            mail.Sent.Should().HaveCount(2, "only the real account (asked for twice) had an address to write to");
        }

        [Fact]
        public async Task TheEmail_GoesToTheAccountsAddress_AndLinksToTheConfiguredAddressNeverTheHostHeader()
        {
            var (host, mail) = Host();
            await ConfigureEmailAsync(host, "https://chronicle.example.com");
            var u = await NewAccountAsync(host);
            var req = Post("/api/v1/auth/forgot-password", new { identifier = u.Username });
            req.Headers.Host = "evil.example.net";    // a hostile Host header

            (await host.CreateClient().SendAsync(req)).StatusCode.Should().Be(HttpStatusCode.OK);
            await WaitForMailAsync(host, mail, 1);

            var sent = mail.Sent[0];
            sent.Message.To.Should().Be(u.Email);
            sent.Message.TextBody.Should().Contain("https://chronicle.example.com/reset-password#token=").And.NotContain("evil.example.net");
            sent.Credentials.Settings.Host.Should().Be("smtp.example.com");
            sent.Credentials.Password.Should().Be("mail-secret");
        }

        [Fact]
        public async Task WithoutMailSetUp_NothingIsSent_AndTheReplySaysEmailIsOff()
        {
            var (host, mail) = Host();
            var u = await NewAccountAsync(host);

            var response = await host.CreateClient().PostAsJsonAsync("/api/v1/auth/forgot-password", new { identifier = u.Username });

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await Json(response)).GetProperty("data").GetProperty("emailEnabled").GetBoolean().Should().BeFalse();
            await host.Services.GetRequiredService<ResetMailDispatcher>().WhenIdleAsync();
            mail.Sent.Should().BeEmpty();
        }

        [Fact]
        public async Task AFailingMailServer_DoesNotChangeTheAnswer_OrReachTheCaller()
        {
            var (host, mail) = Host();
            await ConfigureEmailAsync(host);
            mail.Throw = new EmailSendException("Could not reach the mail server.");
            var u = await NewAccountAsync(host);

            var response = await host.CreateClient().PostAsJsonAsync("/api/v1/auth/forgot-password", new { identifier = u.Username });

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            await host.Services.GetRequiredService<ResetMailDispatcher>().WhenIdleAsync();
        }

        [Fact]
        public async Task EachRequest_IsLogged_WithoutSayingWhetherTheAccountExisted_ToTheCaller()
        {
            var (host, _) = Host();
            await ConfigureEmailAsync(host);
            var u = await NewAccountAsync(host);

            await host.CreateClient().PostAsJsonAsync("/api/v1/auth/forgot-password", new { identifier = u.Username });

            LogText().Should().Contain("password-reset requested").And.Contain(u.Username);
        }

        [Fact]
        public async Task OneAddressCannotAskForever_AndOnePersonCannotBeMailBombed()
        {
            var (host, mail) = Host(s => { s.Values[AuthController.ForgotPerAddressKey] = "100"; s.Values[AuthController.ForgotPerIdentifierKey] = "2"; });
            await ConfigureEmailAsync(host);
            var u = await NewAccountAsync(host);
            var client = host.CreateClient();

            // Different claimed addresses each time: only the per-person limit can stop this.
            var results = new List<HttpStatusCode>();
            for (var i = 0; i < 4; i++)
                results.Add((await client.SendAsync(Post("/api/v1/auth/forgot-password", new { identifier = u.Username }, $"10.7.0.{i}"))).StatusCode);

            results.Should().Equal(HttpStatusCode.OK, HttpStatusCode.OK, (HttpStatusCode)429, (HttpStatusCode)429);
            await host.Services.GetRequiredService<ResetMailDispatcher>().WhenIdleAsync();
            mail.Sent.Should().HaveCount(2);
            LogText().Should().Contain("THROTTLED");
        }

        [Fact]
        public async Task ThePerAddressLimit_AppliesEvenToDifferentPeople()
        {
            var (host, _) = Host(s => s.Values[AuthController.ForgotPerAddressKey] = "2");
            var client = host.CreateClient();

            var codes = new List<HttpStatusCode>();
            for (var i = 0; i < 3; i++)
                codes.Add((await client.SendAsync(Post("/api/v1/auth/forgot-password", new { identifier = $"person{i}" }, "10.7.1.1"))).StatusCode);

            codes.Should().Equal(HttpStatusCode.OK, HttpStatusCode.OK, (HttpStatusCode)429);
        }

        [Fact]
        public async Task ABlankIdentifier_IsABadRequest()
        {
            var (host, _) = Host();

            (await host.CreateClient().PostAsJsonAsync("/api/v1/auth/forgot-password", new { identifier = "" })).StatusCode
                .Should().Be(HttpStatusCode.BadRequest);
        }

        // ══ reset password ════════════════════════════════════════════════════

        [Fact]
        public async Task TheWholeJourney_ForgotEmailReset_OldPasswordDies_NewOneWorks_OldSessionsEnd()
        {
            var (host, mail) = Host();
            await ConfigureEmailAsync(host);
            var u = await NewAccountAsync(host);
            var oldSession = Session(host, u.Key);
            (await oldSession.GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.OK);

            await host.CreateClient().PostAsJsonAsync("/api/v1/auth/forgot-password", new { identifier = u.Email });
            await WaitForMailAsync(host, mail, 1);
            var token = TokenFrom(mail.Sent[0].Message);

            var reset = await host.CreateClient().PostAsJsonAsync("/api/v1/auth/reset-password", new { token, newPassword = NewPassword });

            reset.StatusCode.Should().Be(HttpStatusCode.OK);
            (await SignInAsync(host, u.Username, Password)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the old password no longer works");
            (await SignInAsync(host, u.Username, NewPassword)).StatusCode.Should().Be(HttpStatusCode.OK);
            (await oldSession.GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a reset ends every existing session");
            LogText().Should().Contain("password reset completed").And.Contain(u.Username);
        }

        [Fact]
        public async Task ATokenWorksOnce()
        {
            var (host, mail) = Host();
            await ConfigureEmailAsync(host);
            var u = await NewAccountAsync(host);
            await host.CreateClient().PostAsJsonAsync("/api/v1/auth/forgot-password", new { identifier = u.Username });
            await WaitForMailAsync(host, mail, 1);
            var token = TokenFrom(mail.Sent[0].Message);

            (await host.CreateClient().PostAsJsonAsync("/api/v1/auth/reset-password", new { token, newPassword = NewPassword })).StatusCode.Should().Be(HttpStatusCode.OK);
            var again = await host.CreateClient().PostAsJsonAsync("/api/v1/auth/reset-password", new { token, newPassword = "Someone-elses-pass-2" });

            again.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await Json(again)).GetProperty("error").GetProperty("code").GetString().Should().Be("INVALID_RESET_CODE");
            (await SignInAsync(host, u.Username, NewPassword)).StatusCode.Should().Be(HttpStatusCode.OK, "the second use changed nothing");
        }

        [Fact]
        public async Task AnExpiredToken_IsRefused()
        {
            var (host, _) = Host(s => s.Values[PasswordResetService.TokenMinutesKey] = "1");
            var admin = await NewAccountAsync(host, admin: true);
            var target = await NewAccountAsync(host);
            var issued = await (await Session(host, admin.Key).PostAsync($"/api/v1/users/{target.Id}/reset-token", null)).Content.ReadAsStringAsync();
            var token = JsonDocument.Parse(issued).RootElement.GetProperty("data").GetProperty("token").GetString()!;
            using (var scope = host.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
                foreach (var t in db.PasswordResetTokens) t.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
                await db.SaveChangesAsync();
            }

            (await host.CreateClient().PostAsJsonAsync("/api/v1/auth/reset-password", new { token, newPassword = NewPassword })).StatusCode
                .Should().Be(HttpStatusCode.BadRequest);
        }

        [Fact]
        public async Task GuessedCodes_AreRefusedIdentically_AndTheGuesserIsEventuallyBlocked()
        {
            var (host, _) = Host(s => s.Values[AuthController.ResetMissesKey] = "3");
            var client = host.CreateClient();

            var codes = new List<HttpStatusCode>();
            for (var i = 0; i < 5; i++)
                codes.Add((await client.SendAsync(Post("/api/v1/auth/reset-password", new { token = $"guess{i}", newPassword = NewPassword }, "10.7.2.2"))).StatusCode);

            codes.Take(3).Should().OnlyContain(c => c == HttpStatusCode.BadRequest);
            codes.Skip(3).Should().OnlyContain(c => c == (HttpStatusCode)429);
            LogText().Should().Contain("REJECTED");
        }

        [Fact]
        public async Task ACorrectCode_StillWorks_ForSomeoneElseWhileOneAddressIsBlocked()
        {
            var (host, _) = Host(s => s.Values[AuthController.ResetMissesKey] = "1");
            var admin = await NewAccountAsync(host, admin: true);
            var target = await NewAccountAsync(host);
            var token = JsonDocument.Parse(await (await Session(host, admin.Key).PostAsync($"/api/v1/users/{target.Id}/reset-token", null)).Content.ReadAsStringAsync())
                .RootElement.GetProperty("data").GetProperty("token").GetString()!;
            var client = host.CreateClient();
            await client.SendAsync(Post("/api/v1/auth/reset-password", new { token = "bad", newPassword = NewPassword }, "10.7.3.3"));

            (await client.SendAsync(Post("/api/v1/auth/reset-password", new { token, newPassword = NewPassword }, "10.7.3.4"))).StatusCode
                .Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task AWeakPassword_IsExplained_AndTheCodeSurvivesForAnotherTry()
        {
            var (host, _) = Host();
            var admin = await NewAccountAsync(host, admin: true);
            var target = await NewAccountAsync(host);
            var token = JsonDocument.Parse(await (await Session(host, admin.Key).PostAsync($"/api/v1/users/{target.Id}/reset-token", null)).Content.ReadAsStringAsync())
                .RootElement.GetProperty("data").GetProperty("token").GetString()!;
            var client = host.CreateClient();

            // Too short is caught by request validation before the code is even looked at.
            (await client.PostAsJsonAsync("/api/v1/auth/reset-password", new { token, newPassword = "short" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);

            (await client.PostAsJsonAsync("/api/v1/auth/reset-password", new { token, newPassword = NewPassword })).StatusCode
                .Should().Be(HttpStatusCode.OK, "the failed attempt did not use the code up");
        }

        [Fact]
        public async Task ResettingDoesNotSignAnyoneIn()
        {
            var (host, _) = Host();
            var admin = await NewAccountAsync(host, admin: true);
            var target = await NewAccountAsync(host);
            var token = JsonDocument.Parse(await (await Session(host, admin.Key).PostAsync($"/api/v1/users/{target.Id}/reset-token", null)).Content.ReadAsStringAsync())
                .RootElement.GetProperty("data").GetProperty("token").GetString()!;

            var response = await host.CreateClient().PostAsJsonAsync("/api/v1/auth/reset-password", new { token, newPassword = NewPassword });

            (await response.Content.ReadAsStringAsync()).Should().NotContain("chr_sess_");
            response.Headers.Contains("Set-Cookie").Should().BeFalse();
        }

        // ══ administrator-issued codes ════════════════════════════════════════

        [Fact]
        public async Task AnAdministrator_CanHandSomeoneACode_ShownOnce_WithAWorkingLink()
        {
            var (host, _) = Host();
            var admin = await NewAccountAsync(host, admin: true);
            var target = await NewAccountAsync(host, withEmail: false);

            var response = await Session(host, admin.Key).PostAsync($"/api/v1/users/{target.Id}/reset-token", null);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var data = (await Json(response)).GetProperty("data");
            var token = data.GetProperty("token").GetString()!;
            data.GetProperty("resetUrl").GetString().Should().EndWith($"/reset-password#token={token}");
            data.GetProperty("username").GetString().Should().Be(target.Username);
            data.GetProperty("expiresAt").GetDateTime().Should().BeAfter(DateTime.UtcNow.AddMinutes(30)).And.BeBefore(DateTime.UtcNow.AddMinutes(61));
            LogText().Should().Contain("token issued").And.Contain(target.Id.ToString());

            (await host.CreateClient().PostAsJsonAsync("/api/v1/auth/reset-password", new { token, newPassword = NewPassword })).StatusCode.Should().Be(HttpStatusCode.OK);
            (await SignInAsync(host, target.Username, NewPassword)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task OnlyAnAdministratorWithABrowserSession_CanIssueOne()
        {
            var (host, _) = Host();
            var admin = await NewAccountAsync(host, admin: true);
            var ordinary = await NewAccountAsync(host);
            var target = await NewAccountAsync(host);
            var minted = await Session(host, admin.Key).PostAsJsonAsync("/api/v1/tokens", new { name = "script", scope = "full" });
            var raw = (await Json(minted)).GetProperty("data").GetProperty("token").GetString()!;
            var byKey = host.CreateClient();
            byKey.DefaultRequestHeaders.Add("X-API-Key", raw);

            (await host.CreateClient().PostAsync($"/api/v1/users/{target.Id}/reset-token", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await Session(host, ordinary.Key).PostAsync($"/api/v1/users/{target.Id}/reset-token", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await byKey.PostAsync($"/api/v1/users/{target.Id}/reset-token", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                "an API key must not be able to take over accounts");
        }

        [Fact]
        public async Task AnAdministratorsApiKey_CannotEndOrReadOtherUsersSessions_EitherRouteOfTheSameFamily()
        {
            // Regression: these routes once sat inside a controller whose class-level [Authorize] was merged with the
            // method-level "session only" policy, which quietly let a full-scope admin API key through.
            var (host, _) = Host();
            var admin = await NewAccountAsync(host, admin: true);
            var victim = await NewAccountAsync(host);
            var minted = await Session(host, admin.Key).PostAsJsonAsync("/api/v1/tokens", new { name = "s", scope = "full" });
            var byKey = host.CreateClient();
            byKey.DefaultRequestHeaders.Add("X-API-Key", (await Json(minted)).GetProperty("data").GetProperty("token").GetString()!);

            (await byKey.GetAsync($"/api/v1/users/{victim.Id}/sessions")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await byKey.DeleteAsync($"/api/v1/users/{victim.Id}/sessions")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await byKey.PostAsync($"/api/v1/users/{victim.Id}/reset-token", null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await Session(host, victim.Key).GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.OK, "the victim's session was not ended");
            // The same key still works for what a key is for.
            (await byKey.GetAsync("/api/v1/users")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task IssuingForAnUnknownOrDeactivatedUser_IsRefusedClearly()
        {
            var (host, _) = Host();
            var admin = await NewAccountAsync(host, admin: true);
            var off = await NewAccountAsync(host, active: false);
            var client = Session(host, admin.Key);

            (await client.PostAsync("/api/v1/users/999999/reset-token", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await client.PostAsync($"/api/v1/users/{off.Id}/reset-token", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        }

        // ══ mail settings ═════════════════════════════════════════════════════

        [Fact]
        public async Task MailSettings_AreAdminOnly_AndSessionOnly()
        {
            var (host, _) = Host();
            var ordinary = await NewAccountAsync(host);
            var admin = await NewAccountAsync(host, admin: true);
            var minted = await Session(host, admin.Key).PostAsJsonAsync("/api/v1/tokens", new { name = "s", scope = "full" });
            var byKey = host.CreateClient();
            byKey.DefaultRequestHeaders.Add("X-API-Key", (await Json(minted)).GetProperty("data").GetProperty("token").GetString()!);

            foreach (var (method, url) in new[] { ("GET", "/api/v1/settings/email"), ("PUT", "/api/v1/settings/email"), ("POST", "/api/v1/settings/email/test") })
            {
                (await host.CreateClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), url))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
                (await Session(host, ordinary.Key).SendAsync(new HttpRequestMessage(new HttpMethod(method), url))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
                (await byKey.SendAsync(new HttpRequestMessage(new HttpMethod(method), url))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            }
        }

        [Fact]
        public async Task ThePassword_IsNeverReturned_ByAnyEndpoint()
        {
            var (host, _) = Host();
            await ConfigureEmailAsync(host);
            var admin = await NewAccountAsync(host, admin: true);
            var ordinary = await NewAccountAsync(host);

            var email = await (await Session(host, admin.Key).GetAsync("/api/v1/settings/email")).Content.ReadAsStringAsync();
            var general = await (await Session(host, ordinary.Key).GetAsync("/api/v1/settings/app")).Content.ReadAsStringAsync();

            email.Should().NotContain("mail-secret").And.Contain("\"hasPassword\":true");
            general.Should().NotContain("mail-secret").And.NotContain("email.");
        }

        [Fact]
        public async Task MailSettings_CannotBeWrittenThroughTheGeneralSettingsEndpoint()
        {
            var (host, _) = Host();
            var admin = await NewAccountAsync(host, admin: true);

            var response = await Session(host, admin.Key).PutAsJsonAsync("/api/v1/settings/app/email.password", new { value = "x" });

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Fact]
        public async Task SavingBadMailSettings_ExplainsWhat_AndKeepsTheOldOnes()
        {
            var (host, _) = Host();
            await ConfigureEmailAsync(host);
            var admin = await NewAccountAsync(host, admin: true);
            var client = Session(host, admin.Key);

            var bad = await client.PutAsJsonAsync("/api/v1/settings/email", new
            {
                host = "smtp.example.com:587", port = 587, security = "starttls", fromAddress = "a@b.co", publicUrl = "https://c.example.com",
            });

            bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await Json(bad)).GetProperty("error").GetProperty("message").GetString().Should().Contain("host");
            (await Json(await client.GetAsync("/api/v1/settings/email"))).GetProperty("data").GetProperty("host").GetString().Should().Be("smtp.example.com");
        }

        [Fact]
        public async Task TheTestMessage_GoesOut_AndAFailureIsExplainedToTheAdministrator()
        {
            var (host, mail) = Host();
            await ConfigureEmailAsync(host);
            var admin = await NewAccountAsync(host, admin: true);
            var client = Session(host, admin.Key);

            (await client.PostAsJsonAsync("/api/v1/settings/email/test", new { to = "me@example.com" })).StatusCode.Should().Be(HttpStatusCode.OK);
            mail.Sent.Should().ContainSingle().Which.Message.To.Should().Be("me@example.com");

            mail.Throw = new EmailSendException("The mail server rejected the user name or password.");
            var failed = await client.PostAsJsonAsync("/api/v1/settings/email/test", new { to = "me@example.com" });
            failed.StatusCode.Should().Be(HttpStatusCode.BadGateway);
            (await Json(failed)).GetProperty("error").GetProperty("message").GetString().Should().Contain("user name or password");
        }

        [Fact]
        public async Task TheTestMessage_RefusesABadAddress_AndMissingSettings()
        {
            var (host, mail) = Host();
            var admin = await NewAccountAsync(host, admin: true);
            var client = Session(host, admin.Key);

            (await client.PostAsJsonAsync("/api/v1/settings/email/test", new { to = "me@example.com" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);   // not configured yet
            await ConfigureEmailAsync(host);
            (await client.PostAsJsonAsync("/api/v1/settings/email/test", new { to = "not an address" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
            mail.Sent.Should().BeEmpty();
        }

        // ══ the local recovery command ════════════════════════════════════════

        [Theory]
        [InlineData(new[] { "--reset-admin-password", "bob" }, "bob")]
        [InlineData(new[] { "--RESET-ADMIN-PASSWORD", "bob" }, "bob")]
        [InlineData(new[] { "--urls", "http://x", "--reset-admin-password", "bob" }, "bob")]
        [InlineData(new[] { "--reset-admin-password" }, "")]
        [InlineData(new[] { "--reset-admin-password", "--other" }, "")]
        [InlineData(new string[0], null)]
        [InlineData(new[] { "--urls", "http://x" }, null)]
        public void TheRecoveryFlag_IsRecognised(string[] args, string? expected)
        {
            RecoveryCommand.ParseUsername(args).Should().Be(expected);
        }

        [Fact]
        public async Task TheRecoveryCommand_PrintsACodeThatWorks_WithoutAnyCredentials()
        {
            var (host, _) = Host();
            var u = await NewAccountAsync(host, admin: true);
            using var output = new StringWriter();

            var exit = await RecoveryCommand.RunAsync(host.Services, u.Username.ToUpperInvariant(), output);

            exit.Should().Be(0);
            var text = output.ToString();
            text.Should().Contain(u.Username).And.Contain("works once");
            var token = text.Split('\n').Select(l => l.Trim()).First(l => l.Length >= 43 && !l.Contains(' ') && !l.StartsWith("http"));
            (await host.CreateClient().PostAsJsonAsync("/api/v1/auth/reset-password", new { token, newPassword = NewPassword })).StatusCode.Should().Be(HttpStatusCode.OK);
            (await SignInAsync(host, u.Username, NewPassword)).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task TheRecoveryCommand_ExplainsUnknownAndInactiveUsers_AndMissingNames()
        {
            var (host, _) = Host();
            var off = await NewAccountAsync(host, active: false);

            foreach (var name in new[] { "nobody-here", off.Username, "" })
            {
                using var output = new StringWriter();
                (await RecoveryCommand.RunAsync(host.Services, name, output)).Should().Be(1, name);
                output.ToString().Should().NotBeNullOrWhiteSpace();
            }
        }
    }
}
