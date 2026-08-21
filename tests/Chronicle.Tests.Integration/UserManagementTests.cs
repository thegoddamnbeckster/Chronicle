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
    /// <summary>
    /// End-to-end coverage of the user-management surface: admin CRUD, promote/demote, the
    /// self-service profile, and contacts — including the authorization boundaries, which are
    /// only observable through the HTTP pipeline.
    /// </summary>
    public class UserManagementTests : IClassFixture<ChronicleApiFactory>
    {
        private readonly ChronicleApiFactory _factory;

        public UserManagementTests(ChronicleApiFactory factory)
        {
            factory.SeedDatabase();
            _factory = factory;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private record Account(int Id, string Username, HttpClient Client);

        /// <summary>Registers a user and returns a client already carrying its bearer token.
        /// Admin is granted directly in the DB rather than via the API, so a test never depends
        /// on being the first registration in a shared fixture.</summary>
        private async Task<Account> NewAccountAsync(bool admin = false, string password = "Password123!")
        {
            var username = $"u_{Guid.NewGuid():N}";
            var client = _factory.CreateClient();

            var reg = await client.PostAsJsonAsync("/api/v1/auth/register", new { username, password });
            reg.StatusCode.Should().Be(HttpStatusCode.OK);

            var data = (await Json(reg)).GetProperty("data");
            var token = data.GetProperty("token").GetString()!;
            var id = data.GetProperty("user").GetProperty("id").GetInt32();

            if (admin)
            {
                using var scope = _factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
                var user = await db.Users.FirstAsync(u => u.Id == id);
                user.IsAdmin = true;
                await db.SaveChangesAsync();

                // The role lives in the JWT, so re-login to pick up the promotion.
                var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { username, password });
                token = (await Json(login)).GetProperty("data").GetProperty("token").GetString()!;
            }
            else
            {
                // RegisterAsync auto-promotes the very first account on an empty install; strip
                // that here so "non-admin" tests are honest regardless of execution order.
                using var scope = _factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
                var user = await db.Users.FirstAsync(u => u.Id == id);
                if (user.IsAdmin)
                {
                    user.IsAdmin = false;
                    await db.SaveChangesAsync();
                    var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { username, password });
                    token = (await Json(login)).GetProperty("data").GetProperty("token").GetString()!;
                }
            }

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return new Account(id, username, client);
        }

        private static async Task<JsonElement> Json(HttpResponseMessage r) =>
            JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

        // ── Authorization boundaries ──────────────────────────────────────────

        [Fact]
        public async Task ListUsers_WithoutToken_Returns401()
        {
            var response = await _factory.CreateClient().GetAsync("/api/v1/users");
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task ListUsers_AsNonAdmin_Returns403()
        {
            var user = await NewAccountAsync();
            var response = await user.Client.GetAsync("/api/v1/users");
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        [Fact]
        public async Task CreateUser_AsNonAdmin_Returns403()
        {
            var user = await NewAccountAsync();
            var response = await user.Client.PostAsJsonAsync("/api/v1/users",
                new { username = $"x_{Guid.NewGuid():N}", password = "Password123!" });
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        [Fact]
        public async Task SetAdmin_AsNonAdmin_Returns403()
        {
            var attacker = await NewAccountAsync();
            var response = await attacker.Client.PutAsJsonAsync(
                $"/api/v1/users/{attacker.Id}/admin", new { isAdmin = true });

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

            // And the account really wasn't promoted.
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            (await db.Users.FirstAsync(u => u.Id == attacker.Id)).IsAdmin.Should().BeFalse();
        }

        [Fact]
        public async Task DeleteUser_AsNonAdmin_Returns403()
        {
            var admin  = await NewAccountAsync(admin: true);
            var victim = await NewAccountAsync();
            var other  = await NewAccountAsync();

            var response = await other.Client.DeleteAsync($"/api/v1/users/{victim.Id}");
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

            (await admin.Client.GetAsync($"/api/v1/users/{victim.Id}"))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }

        // ── Admin CRUD ────────────────────────────────────────────────────────

        [Fact]
        public async Task ListUsers_AsAdmin_ReturnsAccounts()
        {
            var admin = await NewAccountAsync(admin: true);

            var response = await admin.Client.GetAsync("/api/v1/users");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var users = (await Json(response)).GetProperty("data").EnumerateArray().ToList();
            users.Should().NotBeEmpty();
            users.Select(u => u.GetProperty("username").GetString()).Should().Contain(admin.Username);
            users[0].TryGetProperty("resolvedDisplayName", out _).Should().BeTrue();
            users[0].TryGetProperty("contacts", out _).Should().BeTrue();
        }

        [Fact]
        public async Task CreateUser_AsAdmin_CreatesAccountThatCanLogIn()
        {
            var admin = await NewAccountAsync(admin: true);
            var username = $"created_{Guid.NewGuid():N}";

            var response = await admin.Client.PostAsJsonAsync("/api/v1/users", new
            {
                username,
                password  = "Password123!",
                email     = "created@example.com",
                firstName = "Created",
                lastName  = "User",
                handle    = "@created",
                isAdmin   = false,
            });

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var created = (await Json(response)).GetProperty("data");
            created.GetProperty("isAdmin").GetBoolean().Should().BeFalse();
            created.GetProperty("resolvedDisplayName").GetString().Should().Be("@created");

            var login = await _factory.CreateClient()
                .PostAsJsonAsync("/api/v1/auth/login", new { username, password = "Password123!" });
            login.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task CreateUser_DuplicateUsername_Returns409()
        {
            var admin = await NewAccountAsync(admin: true);
            var username = $"dup_{Guid.NewGuid():N}";

            await admin.Client.PostAsJsonAsync("/api/v1/users", new { username, password = "Password123!" });
            var second = await admin.Client.PostAsJsonAsync("/api/v1/users", new { username, password = "Password123!" });

            second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        }

        [Fact]
        public async Task CreateUser_ShortPassword_Returns400()
        {
            var admin = await NewAccountAsync(admin: true);
            var response = await admin.Client.PostAsJsonAsync("/api/v1/users",
                new { username = $"short_{Guid.NewGuid():N}", password = "abc" });

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }

        [Fact]
        public async Task DeleteUser_AsAdmin_RemovesAccount()
        {
            var admin  = await NewAccountAsync(admin: true);
            var victim = await NewAccountAsync();

            var response = await admin.Client.DeleteAsync($"/api/v1/users/{victim.Id}");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            (await admin.Client.GetAsync($"/api/v1/users/{victim.Id}"))
                .StatusCode.Should().Be(HttpStatusCode.NotFound);
        }

        [Fact]
        public async Task DeleteUser_Self_Returns400()
        {
            var admin = await NewAccountAsync(admin: true);
            var response = await admin.Client.DeleteAsync($"/api/v1/users/{admin.Id}");

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await Json(response)).GetProperty("error").GetProperty("code").GetString()
                .Should().Be("CANNOT_DELETE_SELF");
        }

        [Fact]
        public async Task DeactivateUser_Self_Returns400()
        {
            var admin = await NewAccountAsync(admin: true);
            var response = await admin.Client.PutAsJsonAsync($"/api/v1/users/{admin.Id}/active", new { isActive = false });

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await Json(response)).GetProperty("error").GetProperty("code").GetString()
                .Should().Be("CANNOT_DEACTIVATE_SELF");
        }

        // ── Promote / demote ──────────────────────────────────────────────────

        [Fact]
        public async Task PromoteAndDemote_RoundTrips()
        {
            var admin  = await NewAccountAsync(admin: true);
            var target = await NewAccountAsync();

            var promote = await admin.Client.PutAsJsonAsync($"/api/v1/users/{target.Id}/admin", new { isAdmin = true });
            promote.StatusCode.Should().Be(HttpStatusCode.OK);
            (await Json(promote)).GetProperty("data").GetProperty("isAdmin").GetBoolean().Should().BeTrue();

            var demote = await admin.Client.PutAsJsonAsync($"/api/v1/users/{target.Id}/admin", new { isAdmin = false });
            demote.StatusCode.Should().Be(HttpStatusCode.OK);
            (await Json(demote)).GetProperty("data").GetProperty("isAdmin").GetBoolean().Should().BeFalse();
        }

        [Fact]
        public async Task PromotedUser_GainsAdminAccessAfterReLogin()
        {
            var admin  = await NewAccountAsync(admin: true);
            var target = await NewAccountAsync();

            (await target.Client.GetAsync("/api/v1/users")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

            await admin.Client.PutAsJsonAsync($"/api/v1/users/{target.Id}/admin", new { isAdmin = true });

            // The role is carried in the JWT, so it takes effect on the next login.
            var login = await _factory.CreateClient()
                .PostAsJsonAsync("/api/v1/auth/login", new { username = target.Username, password = "Password123!" });
            var token = (await Json(login)).GetProperty("data").GetProperty("token").GetString()!;

            var promoted = _factory.CreateClient();
            promoted.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            (await promoted.GetAsync("/api/v1/users")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task DeactivatedUser_CannotLogIn_AndReactivationRestoresAccess()
        {
            var admin  = await NewAccountAsync(admin: true);
            var target = await NewAccountAsync();

            await admin.Client.PutAsJsonAsync($"/api/v1/users/{target.Id}/active", new { isActive = false });

            var blocked = await _factory.CreateClient()
                .PostAsJsonAsync("/api/v1/auth/login", new { username = target.Username, password = "Password123!" });
            blocked.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            await admin.Client.PutAsJsonAsync($"/api/v1/users/{target.Id}/active", new { isActive = true });

            var restored = await _factory.CreateClient()
                .PostAsJsonAsync("/api/v1/auth/login", new { username = target.Username, password = "Password123!" });
            restored.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task DeactivatingUser_ImmediatelyKillsTheirExistingSession()
        {
            // The token is stateless and valid for 24 h — blocking a new login isn't enough,
            // the session already in the browser has to stop working too.
            var admin  = await NewAccountAsync(admin: true);
            var target = await NewAccountAsync();

            (await target.Client.GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.OK);

            await admin.Client.PutAsJsonAsync($"/api/v1/users/{target.Id}/active", new { isActive = false });

            // Same client, same unexpired token.
            (await target.Client.GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

            await admin.Client.PutAsJsonAsync($"/api/v1/users/{target.Id}/active", new { isActive = true });
            (await target.Client.GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task DeletingUser_ImmediatelyKillsTheirExistingSession()
        {
            var admin  = await NewAccountAsync(admin: true);
            var target = await NewAccountAsync();

            (await target.Client.GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.OK);

            await admin.Client.DeleteAsync($"/api/v1/users/{target.Id}");

            (await target.Client.GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        // ── Self-service profile ──────────────────────────────────────────────

        [Fact]
        public async Task GetMyProfile_ReturnsIdentityFieldsAndContacts()
        {
            var user = await NewAccountAsync();

            var response = await user.Client.GetAsync("/api/v1/users/me/profile");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var data = (await Json(response)).GetProperty("data");
            data.GetProperty("username").GetString().Should().Be(user.Username);
            data.GetProperty("resolvedDisplayName").GetString().Should().Be(user.Username);
            data.GetProperty("contacts").GetArrayLength().Should().Be(0);
        }

        [Fact]
        public async Task UpdateMyProfile_PersistsAndDrivesDisplayName()
        {
            var user = await NewAccountAsync();

            var response = await user.Client.PutAsJsonAsync("/api/v1/users/me/profile", new
            {
                email     = "me@example.com",
                firstName = "Jane",
                lastName  = "Smith",
                handle    = "@jsmith",
            });

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var data = (await Json(response)).GetProperty("data");
            data.GetProperty("handle").GetString().Should().Be("@jsmith");
            data.GetProperty("resolvedDisplayName").GetString().Should().Be("@jsmith");

            // Clearing the handle falls through to the first/last name.
            var cleared = await user.Client.PutAsJsonAsync("/api/v1/users/me/profile", new
            {
                email     = "me@example.com",
                firstName = "Jane",
                lastName  = "Smith",
            });
            (await Json(cleared)).GetProperty("data").GetProperty("resolvedDisplayName").GetString()
                .Should().Be("Jane Smith");
        }

        [Fact]
        public async Task UpdateMyProfile_CannotSelfPromote()
        {
            // The profile endpoint deliberately has no role field; sending one must be ignored.
            var user = await NewAccountAsync();

            await user.Client.PutAsJsonAsync("/api/v1/users/me/profile",
                new { email = "x@example.com", isAdmin = true });

            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            (await db.Users.FirstAsync(u => u.Id == user.Id)).IsAdmin.Should().BeFalse();
        }

        [Fact]
        public async Task UpdateAnotherUsersProfile_AsNonAdmin_Returns403()
        {
            var victim   = await NewAccountAsync();
            var attacker = await NewAccountAsync();

            var response = await attacker.Client.PutAsJsonAsync($"/api/v1/users/{victim.Id}/profile",
                new { firstName = "Hijacked" });

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        [Fact]
        public async Task ChangeMyPassword_RequiresCurrentPassword()
        {
            var user = await NewAccountAsync();

            var noCurrent = await user.Client.PutAsJsonAsync("/api/v1/users/me/password",
                new { newPassword = "NewPassword123!" });
            noCurrent.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            var wrongCurrent = await user.Client.PutAsJsonAsync("/api/v1/users/me/password",
                new { currentPassword = "WRONG", newPassword = "NewPassword123!" });
            wrongCurrent.StatusCode.Should().Be(HttpStatusCode.BadRequest);

            // Original password still works.
            (await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login",
                new { username = user.Username, password = "Password123!" }))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task ChangeMyPassword_WithCorrectCurrent_Succeeds()
        {
            var user = await NewAccountAsync();

            var response = await user.Client.PutAsJsonAsync("/api/v1/users/me/password",
                new { currentPassword = "Password123!", newPassword = "NewPassword123!" });
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            var anon = _factory.CreateClient();
            (await anon.PostAsJsonAsync("/api/v1/auth/login",
                new { username = user.Username, password = "NewPassword123!" }))
                .StatusCode.Should().Be(HttpStatusCode.OK);
            (await anon.PostAsJsonAsync("/api/v1/auth/login",
                new { username = user.Username, password = "Password123!" }))
                .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        [Fact]
        public async Task AdminResetPassword_NeedsNoCurrentPassword()
        {
            var admin  = await NewAccountAsync(admin: true);
            var target = await NewAccountAsync();

            var response = await admin.Client.PutAsJsonAsync($"/api/v1/users/{target.Id}/password",
                new { newPassword = "ResetPassword123!" });
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            (await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login",
                new { username = target.Username, password = "ResetPassword123!" }))
                .StatusCode.Should().Be(HttpStatusCode.OK);
        }

        [Fact]
        public async Task AdminResetPassword_OnSelf_IsRejected()
        {
            // Own password always goes through the current-password check.
            var admin = await NewAccountAsync(admin: true);
            var response = await admin.Client.PutAsJsonAsync($"/api/v1/users/{admin.Id}/password",
                new { newPassword = "Whatever123!" });

            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await Json(response)).GetProperty("error").GetProperty("code").GetString()
                .Should().Be("USE_SELF_ENDPOINT");
        }

        // ── Contacts ──────────────────────────────────────────────────────────

        [Fact]
        public async Task Contacts_FullLifecycle()
        {
            var user = await NewAccountAsync();

            var added = await user.Client.PostAsJsonAsync("/api/v1/users/me/contacts",
                new { kind = "Email", label = "work", value = "me@work.example", isPrimary = true });
            added.StatusCode.Should().Be(HttpStatusCode.OK);

            var contact = (await Json(added)).GetProperty("data");
            contact.GetProperty("kind").GetString().Should().Be("email");
            var contactId = contact.GetProperty("id").GetInt32();

            var updated = await user.Client.PutAsJsonAsync($"/api/v1/users/me/contacts/{contactId}",
                new { kind = "email", label = "personal", value = "me@home.example", isPrimary = true });
            updated.StatusCode.Should().Be(HttpStatusCode.OK);
            (await Json(updated)).GetProperty("data").GetProperty("value").GetString()
                .Should().Be("me@home.example");

            var deleted = await user.Client.DeleteAsync($"/api/v1/users/me/contacts/{contactId}");
            deleted.StatusCode.Should().Be(HttpStatusCode.OK);

            var list = await user.Client.GetAsync("/api/v1/users/me/contacts");
            (await Json(list)).GetProperty("data").GetArrayLength().Should().Be(0);
        }

        [Fact]
        public async Task Contacts_SupportArbitraryKinds()
        {
            var user = await NewAccountAsync();

            foreach (var kind in new[] { "phone", "mastodon", "discord", "bluesky", "website", "signal" })
            {
                var r = await user.Client.PostAsJsonAsync("/api/v1/users/me/contacts",
                    new { kind, value = $"value-{kind}" });
                r.StatusCode.Should().Be(HttpStatusCode.OK);
            }

            var list = await user.Client.GetAsync("/api/v1/users/me/contacts");
            (await Json(list)).GetProperty("data").GetArrayLength().Should().Be(6);
        }

        [Fact]
        public async Task Contacts_CannotEditAnotherUsersContact()
        {
            var victim   = await NewAccountAsync();
            var attacker = await NewAccountAsync();

            var added = await victim.Client.PostAsJsonAsync("/api/v1/users/me/contacts",
                new { kind = "email", value = "victim@example.com", isPrimary = true });
            var contactId = (await Json(added)).GetProperty("data").GetProperty("id").GetInt32();

            // The self route scopes by the caller's own id, so the attacker's own (empty)
            // contact set is searched — the id simply isn't there.
            var hijack = await attacker.Client.PutAsJsonAsync($"/api/v1/users/me/contacts/{contactId}",
                new { kind = "email", value = "attacker@example.com", isPrimary = true });
            hijack.StatusCode.Should().Be(HttpStatusCode.NotFound);

            var deleteAttempt = await attacker.Client.DeleteAsync($"/api/v1/users/me/contacts/{contactId}");
            deleteAttempt.StatusCode.Should().Be(HttpStatusCode.NotFound);

            var list = await victim.Client.GetAsync("/api/v1/users/me/contacts");
            var kept = (await Json(list)).GetProperty("data").EnumerateArray().Single();
            kept.GetProperty("value").GetString().Should().Be("victim@example.com");
        }

        [Fact]
        public async Task Contacts_AdminCanManageOnBehalfOfAnotherUser()
        {
            var admin  = await NewAccountAsync(admin: true);
            var target = await NewAccountAsync();

            var added = await admin.Client.PostAsJsonAsync($"/api/v1/users/{target.Id}/contacts",
                new { kind = "phone", label = "mobile", value = "555-0100", isPrimary = true });
            added.StatusCode.Should().Be(HttpStatusCode.OK);

            var seenByUser = await target.Client.GetAsync("/api/v1/users/me/contacts");
            (await Json(seenByUser)).GetProperty("data").EnumerateArray().Single()
                .GetProperty("value").GetString().Should().Be("555-0100");
        }

        [Fact]
        public async Task Contacts_NonAdminCannotUseTheOnBehalfRoute()
        {
            var victim   = await NewAccountAsync();
            var attacker = await NewAccountAsync();

            var response = await attacker.Client.PostAsJsonAsync($"/api/v1/users/{victim.Id}/contacts",
                new { kind = "email", value = "attacker@example.com" });

            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }

        [Fact]
        public async Task Contacts_DeletingUserRemovesTheirContacts()
        {
            var admin  = await NewAccountAsync(admin: true);
            var target = await NewAccountAsync();

            await target.Client.PostAsJsonAsync("/api/v1/users/me/contacts",
                new { kind = "email", value = "gone@example.com", isPrimary = true });

            await admin.Client.DeleteAsync($"/api/v1/users/{target.Id}");

            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
            (await db.UserContacts.CountAsync(c => c.UserId == target.Id)).Should().Be(0);
        }
    }
}
