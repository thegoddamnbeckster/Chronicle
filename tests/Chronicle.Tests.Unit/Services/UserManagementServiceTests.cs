using Chronicle.Core.Exceptions;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Chronicle.Services.Security;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Chronicle.Tests.Unit.Services
{
    /// <summary>
    /// Covers the admin/profile/contact surface added for UI user management. Kept separate
    /// from <see cref="UserServiceTests"/>, which covers registration and preferences.
    /// </summary>
    public class UserManagementServiceTests : IDisposable
    {
        private readonly ChronicleDbContext _context;
        private readonly UserService _service;
        private readonly DeactivatedUserCache _deactivated = new();

        public UserManagementServiceTests()
        {
            var options = new DbContextOptionsBuilder<ChronicleDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options;

            _context = new ChronicleDbContext(options);

            var hasher = new Mock<IPasswordHasher>();
            hasher.Setup(h => h.HashPassword(It.IsAny<string>()))
                  .Returns<string>(p => "hashed:" + p);
            hasher.Setup(h => h.VerifyPassword(It.IsAny<string>(), It.IsAny<string>()))
                  .Returns<string, string>((plain, hash) => hash == "hashed:" + plain);

            _service = new UserService(_context, hasher.Object, _deactivated);
        }

        // ── Display name resolution ───────────────────────────────────────────

        [Fact]
        public void ResolveDisplayName_PrefersExplicitDisplayName()
        {
            var u = new User { Username = "jsmith", DisplayName = "Boss", Handle = "@jsmith", FirstName = "Jane", LastName = "Smith" };
            u.ResolveDisplayName().Should().Be("Boss");
        }

        [Fact]
        public void ResolveDisplayName_FallsBackToHandle()
        {
            var u = new User { Username = "jsmith", Handle = "@jsmith", FirstName = "Jane", LastName = "Smith" };
            u.ResolveDisplayName().Should().Be("@jsmith");
        }

        [Fact]
        public void ResolveDisplayName_FallsBackToFullName_WhenHandleMissing()
        {
            var u = new User { Username = "jsmith", FirstName = "Jane", LastName = "Smith" };
            u.ResolveDisplayName().Should().Be("Jane Smith");
        }

        [Fact]
        public void ResolveDisplayName_UsesPartialName_WhenOnlyOneHalfPresent()
        {
            new User { Username = "jsmith", FirstName = "Jane" }.ResolveDisplayName().Should().Be("Jane");
            new User { Username = "jsmith", LastName = "Smith" }.ResolveDisplayName().Should().Be("Smith");
        }

        [Fact]
        public void ResolveDisplayName_FallsBackToUsername_WhenEverythingElseEmpty()
        {
            var u = new User { Username = "jsmith", DisplayName = "  ", Handle = "", FirstName = null, LastName = " " };
            u.ResolveDisplayName().Should().Be("jsmith");
        }

        // ── Create / list ─────────────────────────────────────────────────────

        [Fact]
        public async Task CreateUserAsync_DoesNotAutoPromoteFirstAccount()
        {
            // RegisterAsync makes the very first account an admin; admin-created accounts must
            // not, or "add a normal user" on an empty install silently creates an admin.
            var user = await _service.CreateUserAsync("alice", "password1", null, null, null, null, isAdmin: false);
            user.IsAdmin.Should().BeFalse();
            user.IsActive.Should().BeTrue();
        }

        [Fact]
        public async Task CreateUserAsync_HonoursExplicitAdminFlag()
        {
            var user = await _service.CreateUserAsync("root", "password1", null, null, null, null, isAdmin: true);
            user.IsAdmin.Should().BeTrue();
        }

        [Fact]
        public async Task CreateUserAsync_DuplicateUsername_Throws()
        {
            await _service.CreateUserAsync("alice", "password1", null, null, null, null, false);
            await FluentActions.Invoking(() =>
                    _service.CreateUserAsync("alice", "password2", null, null, null, null, false))
                .Should().ThrowAsync<DuplicateUsernameException>();
        }

        [Fact]
        public async Task CreateUserAsync_BlankIdentityFields_StoredAsNull()
        {
            var user = await _service.CreateUserAsync("alice", "password1", "  ", "   ", "", "  ", false);
            user.Email.Should().BeNull();
            user.FirstName.Should().BeNull();
            user.LastName.Should().BeNull();
            user.Handle.Should().BeNull();
        }

        [Fact]
        public async Task ListUsersAsync_ReturnsAllUsersOrderedByUsername()
        {
            await _service.CreateUserAsync("zoe", "password1", null, null, null, null, false);
            await _service.CreateUserAsync("alice", "password1", null, null, null, null, false);
            await _service.CreateUserAsync("mike", "password1", null, null, null, null, false);

            var users = await _service.ListUsersAsync();
            users.Select(u => u.Username).Should().ContainInOrder("alice", "mike", "zoe");
        }

        // ── Promote / demote ──────────────────────────────────────────────────

        [Fact]
        public async Task SetAdminAsync_PromotesRegularUser()
        {
            var user = await _service.CreateUserAsync("alice", "password1", null, null, null, null, false);
            var updated = await _service.SetAdminAsync(user.Id, true);
            updated.IsAdmin.Should().BeTrue();
        }

        [Fact]
        public async Task SetAdminAsync_DemoteLastAdmin_Throws()
        {
            var admin = await _service.CreateUserAsync("root", "password1", null, null, null, null, true);
            await _service.CreateUserAsync("alice", "password1", null, null, null, null, false);

            await FluentActions.Invoking(() => _service.SetAdminAsync(admin.Id, false))
                .Should().ThrowAsync<LastAdminException>();

            (await _service.GetByIdAsync(admin.Id))!.IsAdmin.Should().BeTrue();
        }

        [Fact]
        public async Task SetAdminAsync_DemoteAllowed_WhenAnotherActiveAdminExists()
        {
            var first  = await _service.CreateUserAsync("root",  "password1", null, null, null, null, true);
            await _service.CreateUserAsync("second", "password1", null, null, null, null, true);

            var updated = await _service.SetAdminAsync(first.Id, false);
            updated.IsAdmin.Should().BeFalse();
        }

        [Fact]
        public async Task SetAdminAsync_InactiveAdminDoesNotCountAsBackup()
        {
            // A suspended admin can't log in, so it can't be the account that rescues you.
            var active   = await _service.CreateUserAsync("root", "password1", null, null, null, null, true);
            var inactive = await _service.CreateUserAsync("old",  "password1", null, null, null, null, true);
            await _service.SetActiveAsync(inactive.Id, false);

            await FluentActions.Invoking(() => _service.SetAdminAsync(active.Id, false))
                .Should().ThrowAsync<LastAdminException>();
        }

        [Fact]
        public async Task SetAdminAsync_UnknownUser_Throws()
        {
            await FluentActions.Invoking(() => _service.SetAdminAsync(9999, true))
                .Should().ThrowAsync<UserNotFoundException>();
        }

        // ── Activate / deactivate ─────────────────────────────────────────────

        [Fact]
        public async Task SetActiveAsync_DeactivateLastAdmin_Throws()
        {
            var admin = await _service.CreateUserAsync("root", "password1", null, null, null, null, true);

            await FluentActions.Invoking(() => _service.SetActiveAsync(admin.Id, false))
                .Should().ThrowAsync<LastAdminException>();
        }

        [Fact]
        public async Task SetActiveAsync_DeactivateRegularUser_Succeeds()
        {
            await _service.CreateUserAsync("root", "password1", null, null, null, null, true);
            var alice = await _service.CreateUserAsync("alice", "password1", null, null, null, null, false);

            var updated = await _service.SetActiveAsync(alice.Id, false);
            updated.IsActive.Should().BeFalse();
        }

        [Fact]
        public async Task SetActiveAsync_Deactivated_CannotAuthenticate_ButRowsSurvive()
        {
            await _service.CreateUserAsync("root", "password1", null, null, null, null, true);
            var alice = await _service.CreateUserAsync("alice", "password1", null, null, null, null, false);
            await _service.AddContactAsync(alice.Id, "email", null, "alice@example.com", true);

            await _service.SetActiveAsync(alice.Id, false);

            await FluentActions.Invoking(() => _service.AuthenticateAsync("alice", "password1"))
                .Should().ThrowAsync<InvalidCredentialsException>();

            // Suspension is reversible — nothing about the account is destroyed.
            (await _service.GetByIdAsync(alice.Id)).Should().NotBeNull();
            (await _service.ListContactsAsync(alice.Id)).Should().HaveCount(1);

            await _service.SetActiveAsync(alice.Id, true);
            (await _service.AuthenticateAsync("alice", "password1")).Username.Should().Be("alice");
        }

        // ── Delete ────────────────────────────────────────────────────────────

        [Fact]
        public async Task DeleteUserAsync_LastAdmin_Throws()
        {
            var admin = await _service.CreateUserAsync("root", "password1", null, null, null, null, true);

            await FluentActions.Invoking(() => _service.DeleteUserAsync(admin.Id))
                .Should().ThrowAsync<LastAdminException>();

            (await _service.GetByIdAsync(admin.Id)).Should().NotBeNull();
        }

        [Fact]
        public async Task DeleteUserAsync_RegularUser_RemovesAccount()
        {
            await _service.CreateUserAsync("root", "password1", null, null, null, null, true);
            var alice = await _service.CreateUserAsync("alice", "password1", null, null, null, null, false);

            await _service.DeleteUserAsync(alice.Id);
            (await _service.GetByIdAsync(alice.Id)).Should().BeNull();
        }

        [Fact]
        public async Task DeleteUserAsync_ClearsDanglingMergeAuditStamps()
        {
            // merged_by_user_id has no FK behind it, so without explicit clearing it would keep
            // pointing at an account that no longer exists.
            await _service.CreateUserAsync("root", "password1", null, null, null, null, true);
            var alice = await _service.CreateUserAsync("alice", "password1", null, null, null, null, false);

            _context.MediaItemMerges.Add(new MediaItemMerge
            {
                WinnerId = 1, LoserOriginalId = 2, LoserName = "Loser",
                LoserMediaTypeId = 1, LoserHierarchyLevel = 0,
                MergedAt = DateTime.UtcNow, MergedByUserId = alice.Id,
            });
            await _context.SaveChangesAsync();

            await _service.DeleteUserAsync(alice.Id);

            var merge = await _context.MediaItemMerges.SingleAsync();
            merge.MergedByUserId.Should().BeNull();
            // The merge record itself — and the shared media behind it — survives.
            merge.LoserName.Should().Be("Loser");
        }

        [Fact]
        public async Task DeleteUserAsync_LeavesOtherUsersMergeStampsAlone()
        {
            await _service.CreateUserAsync("root", "password1", null, null, null, null, true);
            var alice = await _service.CreateUserAsync("alice", "password1", null, null, null, null, false);
            var bob   = await _service.CreateUserAsync("bob",   "password1", null, null, null, null, false);

            _context.MediaItemMerges.AddRange(
                new MediaItemMerge { WinnerId = 1, LoserOriginalId = 2, LoserName = "A", LoserMediaTypeId = 1, MergedAt = DateTime.UtcNow, MergedByUserId = alice.Id },
                new MediaItemMerge { WinnerId = 3, LoserOriginalId = 4, LoserName = "B", LoserMediaTypeId = 1, MergedAt = DateTime.UtcNow, MergedByUserId = bob.Id });
            await _context.SaveChangesAsync();

            await _service.DeleteUserAsync(alice.Id);

            var kept = await _context.MediaItemMerges.SingleAsync(m => m.LoserName == "B");
            kept.MergedByUserId.Should().Be(bob.Id);
        }

        [Fact]
        public async Task DeleteUserAsync_UnknownUser_Throws()
        {
            await FluentActions.Invoking(() => _service.DeleteUserAsync(9999))
                .Should().ThrowAsync<UserNotFoundException>();
        }

        // ── Profile ───────────────────────────────────────────────────────────

        [Fact]
        public async Task UpdateProfileAsync_SetsAllIdentityFields()
        {
            var user = await _service.CreateUserAsync("jsmith", "password1", null, null, null, null, false);

            var updated = await _service.UpdateProfileAsync(user.Id, "jsmith@example.com", "Jane", "Smith", "@jsmith", null);

            updated.Email.Should().Be("jsmith@example.com");
            updated.FirstName.Should().Be("Jane");
            updated.LastName.Should().Be("Smith");
            updated.Handle.Should().Be("@jsmith");
            updated.ResolveDisplayName().Should().Be("@jsmith");
        }

        [Fact]
        public async Task UpdateProfileAsync_NullField_ClearsIt()
        {
            // A full replacement, not a patch — this is what lets a user remove a handle.
            var user = await _service.CreateUserAsync("jsmith", "password1", "jsmith@example.com", "Jane", "Smith", "@jsmith", false);

            var updated = await _service.UpdateProfileAsync(user.Id, "jsmith@example.com", "Jane", "Smith", null, null);

            updated.Handle.Should().BeNull();
            updated.ResolveDisplayName().Should().Be("Jane Smith");
        }

        [Fact]
        public async Task UpdateProfileAsync_TrimsWhitespace()
        {
            var user = await _service.CreateUserAsync("jsmith", "password1", null, null, null, null, false);
            var updated = await _service.UpdateProfileAsync(user.Id, "  jsmith@example.com  ", " Jane ", " Smith ", " @jsmith ", "  ");

            updated.Email.Should().Be("jsmith@example.com");
            updated.FirstName.Should().Be("Jane");
            updated.Handle.Should().Be("@jsmith");
            updated.DisplayName.Should().BeNull();
        }

        [Fact]
        public async Task UpdateProfileAsync_DoesNotTouchRoleOrActiveState()
        {
            var user = await _service.CreateUserAsync("root", "password1", null, null, null, null, true);
            var updated = await _service.UpdateProfileAsync(user.Id, "r@example.com", null, null, null, null);

            updated.IsAdmin.Should().BeTrue();
            updated.IsActive.Should().BeTrue();
        }

        [Fact]
        public async Task UpdateProfileAsync_UnknownUser_Throws()
        {
            await FluentActions.Invoking(() => _service.UpdateProfileAsync(9999, null, null, null, null, null))
                .Should().ThrowAsync<UserNotFoundException>();
        }

        // ── Cutting off live sessions ─────────────────────────────────────────

        [Fact]
        public async Task SetActiveAsync_Deactivate_BlocksExistingTokens()
        {
            // JWTs are stateless and live 24 h, so without this the suspended session would
            // keep working for up to a day after "Deactivate".
            await _service.CreateUserAsync("root", "password1", null, null, null, null, true);
            var alice = await _service.CreateUserAsync("alice", "password1", null, null, null, null, false);

            _deactivated.IsBlocked(alice.Id).Should().BeFalse();

            await _service.SetActiveAsync(alice.Id, false);
            _deactivated.IsBlocked(alice.Id).Should().BeTrue();

            await _service.SetActiveAsync(alice.Id, true);
            _deactivated.IsBlocked(alice.Id).Should().BeFalse("reactivating must restore access");
        }

        [Fact]
        public async Task DeleteUserAsync_BlocksExistingTokens()
        {
            await _service.CreateUserAsync("root", "password1", null, null, null, null, true);
            var alice = await _service.CreateUserAsync("alice", "password1", null, null, null, null, false);

            await _service.DeleteUserAsync(alice.Id);
            _deactivated.IsBlocked(alice.Id).Should().BeTrue();
        }

        [Fact]
        public async Task CreateUserAsync_ClearsAStaleBlockOnAReusedId()
        {
            // SQLite can hand a new row the id of a deleted one; inheriting the old block
            // would lock the new account out of an app it just joined.
            await _service.CreateUserAsync("root", "password1", null, null, null, null, true);
            var alice = await _service.CreateUserAsync("alice", "password1", null, null, null, null, false);
            await _service.DeleteUserAsync(alice.Id);
            _deactivated.IsBlocked(alice.Id).Should().BeTrue();

            _deactivated.Block(9999);
            _context.Users.Add(new User
            {
                Id = 9999, Username = "recycled", PasswordHash = "x",
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, IsActive = true,
            });
            await _context.SaveChangesAsync();

            await _service.RegisterAsync("fresh", "password1", null);
            var recreated = await _service.CreateUserAsync("recreated", "password1", null, null, null, null, false);
            _deactivated.IsBlocked(recreated.Id).Should().BeFalse();
        }

        [Fact]
        public void DeactivatedUserCache_ReplaceRebuildsTheWholeSet()
        {
            var cache = new DeactivatedUserCache();
            cache.Block(1);
            cache.Block(2);

            cache.Replace([3, 4]);

            cache.IsBlocked(1).Should().BeFalse();
            cache.IsBlocked(2).Should().BeFalse();
            cache.IsBlocked(3).Should().BeTrue();
            cache.IsBlocked(4).Should().BeTrue();
        }

        [Fact]
        public async Task VerifyPasswordAsync_DoesNotStampLastLogin()
        {
            // Changing your password proves the current one, but it isn't a sign-in — it must
            // not overwrite "last login" in the admin list.
            var user = await _service.CreateUserAsync("alice", "password1", null, null, null, null, false);
            (await _service.GetByIdAsync(user.Id))!.LastLoginAt.Should().BeNull();

            (await _service.VerifyPasswordAsync(user.Id, "password1")).Should().BeTrue();
            (await _service.VerifyPasswordAsync(user.Id, "wrong")).Should().BeFalse();

            (await _service.GetByIdAsync(user.Id))!.LastLoginAt.Should().BeNull();
        }

        [Fact]
        public async Task VerifyPasswordAsync_UnknownUser_ReturnsFalse()
        {
            (await _service.VerifyPasswordAsync(9999, "password1")).Should().BeFalse();
        }

        [Fact]
        public async Task ChangePasswordAsync_OldPasswordStopsWorking()
        {
            var user = await _service.CreateUserAsync("alice", "oldpassword", null, null, null, null, false);

            await _service.ChangePasswordAsync(user.Id, "newpassword");

            (await _service.AuthenticateAsync("alice", "newpassword")).Id.Should().Be(user.Id);
            await FluentActions.Invoking(() => _service.AuthenticateAsync("alice", "oldpassword"))
                .Should().ThrowAsync<InvalidCredentialsException>();
        }

        // ── Contacts ──────────────────────────────────────────────────────────

        [Fact]
        public async Task AddContactAsync_StoresContact()
        {
            var user = await _service.CreateUserAsync("alice", "password1", null, null, null, null, false);

            var contact = await _service.AddContactAsync(user.Id, "email", "work", "alice@work.example", true);

            contact.Kind.Should().Be("email");
            contact.Label.Should().Be("work");
            contact.Value.Should().Be("alice@work.example");
            contact.IsPrimary.Should().BeTrue();
            (await _service.ListContactsAsync(user.Id)).Should().HaveCount(1);
        }

        [Fact]
        public async Task AddContactAsync_NormalizesKindToLowercase()
        {
            // Kind is free-form so a new network needs no migration; lowercasing keeps
            // "Mastodon" and "mastodon" in the same group.
            var user = await _service.CreateUserAsync("alice", "password1", null, null, null, null, false);
            var contact = await _service.AddContactAsync(user.Id, "  MASTODON  ", null, "@alice@mas.to", false);
            contact.Kind.Should().Be("mastodon");
        }

        [Fact]
        public async Task AddContactAsync_AcceptsArbitraryKinds()
        {
            var user = await _service.CreateUserAsync("alice", "password1", null, null, null, null, false);

            foreach (var kind in new[] { "email", "phone", "discord", "matrix", "bluesky", "carrier-pigeon" })
                await _service.AddContactAsync(user.Id, kind, null, $"value-{kind}", false);

            (await _service.ListContactsAsync(user.Id)).Select(c => c.Kind)
                .Should().Contain(["email", "phone", "discord", "matrix", "bluesky", "carrier-pigeon"]);
        }

        [Fact]
        public async Task AddContactAsync_MultipleOfSameKind_AllKept()
        {
            var user = await _service.CreateUserAsync("alice", "password1", null, null, null, null, false);

            await _service.AddContactAsync(user.Id, "phone", "mobile", "555-0100", true);
            await _service.AddContactAsync(user.Id, "phone", "work",   "555-0200", false);
            await _service.AddContactAsync(user.Id, "phone", "home",   "555-0300", false);

            (await _service.ListContactsAsync(user.Id)).Where(c => c.Kind == "phone").Should().HaveCount(3);
        }

        [Fact]
        public async Task AddContactAsync_NewPrimary_DemotesPreviousPrimaryOfSameKind()
        {
            var user = await _service.CreateUserAsync("alice", "password1", null, null, null, null, false);
            var first = await _service.AddContactAsync(user.Id, "email", "old", "old@example.com", true);
            await _service.AddContactAsync(user.Id, "email", "new", "new@example.com", true);

            var contacts = await _service.ListContactsAsync(user.Id);
            contacts.Where(c => c.Kind == "email" && c.IsPrimary).Should().HaveCount(1);
            contacts.Single(c => c.Id == first.Id).IsPrimary.Should().BeFalse();
        }

        [Fact]
        public async Task AddContactAsync_PrimaryIsScopedPerKind()
        {
            var user = await _service.CreateUserAsync("alice", "password1", null, null, null, null, false);
            await _service.AddContactAsync(user.Id, "email", null, "alice@example.com", true);
            await _service.AddContactAsync(user.Id, "phone", null, "555-0100", true);

            var contacts = await _service.ListContactsAsync(user.Id);
            contacts.Count(c => c.IsPrimary).Should().Be(2, "one primary per kind, not one overall");
        }

        [Fact]
        public async Task AddContactAsync_UnknownUser_Throws()
        {
            await FluentActions.Invoking(() => _service.AddContactAsync(9999, "email", null, "x@example.com", false))
                .Should().ThrowAsync<UserNotFoundException>();
        }

        [Fact]
        public async Task ListContactsAsync_IsScopedToOneUser()
        {
            var alice = await _service.CreateUserAsync("alice", "password1", null, null, null, null, false);
            var bob   = await _service.CreateUserAsync("bob",   "password1", null, null, null, null, false);
            await _service.AddContactAsync(alice.Id, "email", null, "alice@example.com", true);
            await _service.AddContactAsync(bob.Id,   "email", null, "bob@example.com",   true);

            var contacts = await _service.ListContactsAsync(alice.Id);
            contacts.Should().HaveCount(1);
            contacts[0].Value.Should().Be("alice@example.com");
        }

        [Fact]
        public async Task UpdateContactAsync_ChangesValueAndKind()
        {
            var user = await _service.CreateUserAsync("alice", "password1", null, null, null, null, false);
            var contact = await _service.AddContactAsync(user.Id, "email", null, "old@example.com", false);

            var updated = await _service.UpdateContactAsync(user.Id, contact.Id, "Email", "personal", "new@example.com", true);

            updated.Kind.Should().Be("email");
            updated.Label.Should().Be("personal");
            updated.Value.Should().Be("new@example.com");
            updated.IsPrimary.Should().BeTrue();
        }

        [Fact]
        public async Task UpdateContactAsync_CannotTouchAnotherUsersContact()
        {
            // Scoping by (id, userId) rather than id alone is what stops one user editing
            // another's contacts by guessing an id.
            var alice = await _service.CreateUserAsync("alice", "password1", null, null, null, null, false);
            var bob   = await _service.CreateUserAsync("bob",   "password1", null, null, null, null, false);
            var bobsContact = await _service.AddContactAsync(bob.Id, "email", null, "bob@example.com", true);

            await FluentActions.Invoking(() =>
                    _service.UpdateContactAsync(alice.Id, bobsContact.Id, "email", null, "hijacked@example.com", true))
                .Should().ThrowAsync<UserContactNotFoundException>();

            (await _service.ListContactsAsync(bob.Id))[0].Value.Should().Be("bob@example.com");
        }

        [Fact]
        public async Task UpdateContactAsync_UnknownContact_Throws()
        {
            var user = await _service.CreateUserAsync("alice", "password1", null, null, null, null, false);
            await FluentActions.Invoking(() =>
                    _service.UpdateContactAsync(user.Id, 9999, "email", null, "x@example.com", false))
                .Should().ThrowAsync<UserContactNotFoundException>();
        }

        [Fact]
        public async Task DeleteContactAsync_RemovesOnlyThatContact()
        {
            var user = await _service.CreateUserAsync("alice", "password1", null, null, null, null, false);
            var keep = await _service.AddContactAsync(user.Id, "email", null, "keep@example.com", true);
            var drop = await _service.AddContactAsync(user.Id, "phone", null, "555-0100", true);

            await _service.DeleteContactAsync(user.Id, drop.Id);

            var contacts = await _service.ListContactsAsync(user.Id);
            contacts.Should().HaveCount(1);
            contacts[0].Id.Should().Be(keep.Id);
        }

        [Fact]
        public async Task DeleteContactAsync_CannotTouchAnotherUsersContact()
        {
            var alice = await _service.CreateUserAsync("alice", "password1", null, null, null, null, false);
            var bob   = await _service.CreateUserAsync("bob",   "password1", null, null, null, null, false);
            var bobsContact = await _service.AddContactAsync(bob.Id, "email", null, "bob@example.com", true);

            await FluentActions.Invoking(() => _service.DeleteContactAsync(alice.Id, bobsContact.Id))
                .Should().ThrowAsync<UserContactNotFoundException>();

            (await _service.ListContactsAsync(bob.Id)).Should().HaveCount(1);
        }

        public void Dispose() => _context.Dispose();
    }
}
