using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Chronicle.API;
using Chronicle.Data;
using Chronicle.Services.Security;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicle.Tests.Integration
{
    /// <summary>An administrator can close self-service sign-up; it is open by default and can be reopened from the server console.</summary>
    public class RegistrationLockTests : IClassFixture<ChronicleApiFactory>
    {
        private const string Password = "Password123!";
        private readonly ChronicleApiFactory _factory;

        public RegistrationLockTests(ChronicleApiFactory factory)
        {
            factory.SeedDatabase();
            _factory = factory;
        }

        private static async Task<JsonElement> Json(HttpResponseMessage r) =>
            JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

        private async Task<HttpClient> AdminAsync()
        {
            var client = _factory.CreateClient();
            var response = await client.PostAsJsonAsync("/api/v1/auth/register", new { username = $"adm_{Guid.NewGuid():N}", password = Password });
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var data = (await Json(response)).GetProperty("data");
            var id = data.GetProperty("user").GetProperty("id").GetInt32();
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
                (await db.Users.FirstAsync(u => u.Id == id)).IsAdmin = true;
                await db.SaveChangesAsync();
            }
            var admin = _factory.CreateClient();
            admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", data.GetProperty("token").GetString());
            return admin;
        }

        private Task<HttpResponseMessage> TryRegisterAsync() =>
            _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/register", new { username = $"u_{Guid.NewGuid():N}", password = Password });

        [Fact]
        public async Task Registration_IsOpenByDefault_AndTheStatusEndpointSaysSo()
        {
            var status = await _factory.CreateClient().GetAsync("/api/v1/auth/registration");
            status.StatusCode.Should().Be(HttpStatusCode.OK);
            (await Json(status)).GetProperty("data").GetProperty("locked").GetBoolean().Should().BeFalse();
            (await TryRegisterAsync()).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task AnAdmin_CanLockAndUnlockRegistration_AndLockedSignUpIsRefused()
        {
            var admin = await AdminAsync();
            try
            {
                (await admin.PutAsJsonAsync("/api/v1/auth/registration", new { locked = true })).StatusCode.Should().Be(HttpStatusCode.OK);

                var refused = await TryRegisterAsync();
                refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
                (await Json(refused)).GetProperty("error").GetProperty("code").GetString().Should().Be("REGISTRATION_LOCKED");
                (await Json(await _factory.CreateClient().GetAsync("/api/v1/auth/registration")))
                    .GetProperty("data").GetProperty("locked").GetBoolean().Should().BeTrue();

                (await admin.PutAsJsonAsync("/api/v1/auth/registration", new { locked = false })).StatusCode.Should().Be(HttpStatusCode.OK);
                (await TryRegisterAsync()).StatusCode.Should().Be(HttpStatusCode.OK);
            }
            finally { await admin.PutAsJsonAsync("/api/v1/auth/registration", new { locked = false }); }
        }

        [Fact]
        public async Task OrdinaryUsersAndVisitors_CannotChangeTheLock()
        {
            (await _factory.CreateClient().PutAsJsonAsync("/api/v1/auth/registration", new { locked = true })).StatusCode
                .Should().Be(HttpStatusCode.Unauthorized);

            var response = await TryRegisterAsync();
            var user = _factory.CreateClient();
            user.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await Json(response)).GetProperty("data").GetProperty("token").GetString());
            // a non-admin (the seeded database already has users, so this account is not the first)
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            (await db.Users.AnyAsync(u => !u.IsAdmin)).Should().BeTrue();
            (await user.PutAsJsonAsync("/api/v1/auth/registration", new { locked = true })).StatusCode.Should().NotBe(HttpStatusCode.OK);
            (await TryRegisterAsync()).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task TheUnlockCommand_ReopensRegistration_WithoutAnyLogin()
        {
            var admin = await AdminAsync();
            await admin.PutAsJsonAsync("/api/v1/auth/registration", new { locked = true });
            (await TryRegisterAsync()).StatusCode.Should().Be(HttpStatusCode.Forbidden);

            UnlockRegistrationCommand.IsRequested(["--unlock-registration"]).Should().BeTrue();
            var output = new StringWriter();
            (await UnlockRegistrationCommand.RunAsync(_factory.Services, output)).Should().Be(0);

            output.ToString().Should().Contain("unlocked");
            (await TryRegisterAsync()).StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }
}
