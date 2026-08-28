using System.Security.Claims;
using Chronicle.API.DTOs;
using Chronicle.Core.Exceptions;
using Chronicle.Core.Models;
using Chronicle.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chronicle.API.Controllers
{
    [ApiController]
    [Route("api/v1/users")]
    [Authorize]
    public class UsersController : ControllerBase
    {
        private readonly IUserService _userService;

        public UsersController(IUserService userService)
        {
            _userService = userService;
        }

        // ── Self-service ──────────────────────────────────────────────────────

        [HttpGet("me")]
        public async Task<IActionResult> GetMe()
        {
            var userId = CurrentUserId;
            var user = await _userService.GetByIdAsync(userId);

            if (user == null)
                return NotFound(ApiResponse<UserDto>.Fail("USER_NOT_FOUND", "User not found."));

            var prefs = await _userService.GetPreferencesAsync(userId);
            return Ok(ApiResponse<UserDto>.Ok(new UserDto(user.Id, user.Username, user.Email, user.DisplayName, user.IsAdmin,
                prefs.ShowDiagnostics ?? user.IsAdmin,
                prefs.ShowNowPlayingBanner ?? true)));
        }

        /// <summary>The full profile, including contacts — the slim /me shape is fixed by the
        /// auth flow, so the account editor gets its own richer endpoint.</summary>
        [HttpGet("me/profile")]
        public async Task<IActionResult> GetMyProfile(CancellationToken ct)
        {
            var user = await _userService.GetByIdAsync(CurrentUserId);
            if (user == null)
                return NotFound(ApiResponse<UserAccountDto>.Fail("USER_NOT_FOUND", "User not found."));

            var contacts = await _userService.ListContactsAsync(user.Id, ct);
            return Ok(ApiResponse<UserAccountDto>.Ok(UserAccountDto.From(user, contacts)));
        }

        [HttpPut("me/profile")]
        public async Task<IActionResult> UpdateMyProfile([FromBody] UpdateProfileRequest req, CancellationToken ct) =>
            await UpdateProfileCoreAsync(CurrentUserId, req, ct);

        [HttpPut("me/password")]
        public async Task<IActionResult> ChangeMyPassword([FromBody] ChangePasswordRequest req, CancellationToken ct)
        {
            var user = await _userService.GetByIdAsync(CurrentUserId);
            if (user == null)
                return NotFound(ApiResponse<object>.Fail("USER_NOT_FOUND", "User not found."));

            // Changing your own password always proves you know the current one, even for an
            // admin — otherwise a borrowed session becomes a permanent account takeover.
            if (string.IsNullOrEmpty(req.CurrentPassword))
                return BadRequest(ApiResponse<object>.Fail("CURRENT_PASSWORD_REQUIRED", "Current password is required."));

            if (!await _userService.VerifyPasswordAsync(user.Id, req.CurrentPassword, ct))
                return BadRequest(ApiResponse<object>.Fail("INVALID_CREDENTIALS", "Current password is incorrect."));

            await _userService.ChangePasswordAsync(user.Id, req.NewPassword, ct);
            return Ok(ApiResponse<object>.Ok(new { changed = true }));
        }

        // ── Self-service contacts ─────────────────────────────────────────────

        [HttpGet("me/contacts")]
        public async Task<IActionResult> GetMyContacts(CancellationToken ct) =>
            await ListContactsCoreAsync(CurrentUserId, ct);

        [HttpPost("me/contacts")]
        public async Task<IActionResult> AddMyContact([FromBody] ContactRequest req, CancellationToken ct) =>
            await AddContactCoreAsync(CurrentUserId, req, ct);

        [HttpPut("me/contacts/{contactId:int}")]
        public async Task<IActionResult> UpdateMyContact(int contactId, [FromBody] ContactRequest req, CancellationToken ct) =>
            await UpdateContactCoreAsync(CurrentUserId, contactId, req, ct);

        [HttpDelete("me/contacts/{contactId:int}")]
        public async Task<IActionResult> DeleteMyContact(int contactId, CancellationToken ct) =>
            await DeleteContactCoreAsync(CurrentUserId, contactId, ct);

        // ── Preferences ───────────────────────────────────────────────────────

        [HttpGet("me/preferences")]
        public async Task<IActionResult> GetMyPreferences()
        {
            var userId = CurrentUserId;
            var prefs = await _userService.GetPreferencesAsync(userId);
            return Ok(ApiResponse<object>.Ok(new
            {
                showDiagnostics        = prefs.ShowDiagnostics,
                defaultFoldsOpen       = prefs.DefaultFoldsOpen,
                folds                  = prefs.Folds ?? new Dictionary<string, bool>(),
                createCollectionStubs  = prefs.CreateCollectionStubs ?? true,
                showNowPlayingBanner   = prefs.ShowNowPlayingBanner ?? true,
                theme                  = prefs.Theme,
            }));
        }

        [HttpPatch("me/preferences")]
        public async Task<IActionResult> PatchMyPreferences([FromBody] PatchPreferencesRequest req)
        {
            var userId = CurrentUserId;
            var patch = new UserPreferences
            {
                ShowDiagnostics       = req.ShowDiagnostics,
                DefaultFoldsOpen      = req.DefaultFoldsOpen,
                Folds                 = req.Folds,
                CreateCollectionStubs = req.CreateCollectionStubs,
                ShowNowPlayingBanner  = req.ShowNowPlayingBanner,
                Theme                 = req.Theme,
            };
            await _userService.UpdatePreferencesAsync(userId, patch);
            var prefs = await _userService.GetPreferencesAsync(userId);
            var user = await _userService.GetByIdAsync(userId);
            return Ok(ApiResponse<object>.Ok(new
            {
                showDiagnostics       = prefs.ShowDiagnostics ?? (user?.IsAdmin ?? false),
                defaultFoldsOpen      = prefs.DefaultFoldsOpen,
                folds                 = prefs.Folds ?? new Dictionary<string, bool>(),
                createCollectionStubs = prefs.CreateCollectionStubs ?? true,
                showNowPlayingBanner  = prefs.ShowNowPlayingBanner ?? true,
                theme                 = prefs.Theme,
            }));
        }

        // ── Administration ────────────────────────────────────────────────────

        [HttpGet]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> ListUsers(CancellationToken ct)
        {
            var users = await _userService.ListUsersAsync(ct);
            var result = new List<UserAccountDto>(users.Count);
            foreach (var u in users)
                result.Add(UserAccountDto.From(u, await _userService.ListContactsAsync(u.Id, ct)));

            return Ok(ApiResponse<List<UserAccountDto>>.Ok(result));
        }

        [HttpGet("{id:int}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetUser(int id, CancellationToken ct)
        {
            var user = await _userService.GetByIdAsync(id);
            if (user == null)
                return NotFound(ApiResponse<UserAccountDto>.Fail("USER_NOT_FOUND", $"User {id} was not found."));

            var contacts = await _userService.ListContactsAsync(id, ct);
            return Ok(ApiResponse<UserAccountDto>.Ok(UserAccountDto.From(user, contacts)));
        }

        [HttpPost]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest req, CancellationToken ct)
        {
            try
            {
                var user = await _userService.CreateUserAsync(req.Username, req.Password, req.Email,
                    req.FirstName, req.LastName, req.Handle, req.IsAdmin, ct);
                return Ok(ApiResponse<UserAccountDto>.Ok(UserAccountDto.From(user)));
            }
            catch (DuplicateUsernameException ex)
            {
                return Conflict(ApiResponse<UserAccountDto>.Fail("USERNAME_TAKEN", ex.Message));
            }
        }

        [HttpPut("{id:int}/profile")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> UpdateUserProfile(int id, [FromBody] UpdateProfileRequest req, CancellationToken ct) =>
            await UpdateProfileCoreAsync(id, req, ct);

        /// <summary>Admin password reset — no current password, because the point is that the
        /// user has lost it.</summary>
        [HttpPut("{id:int}/password")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> ResetUserPassword(int id, [FromBody] ChangePasswordRequest req, CancellationToken ct)
        {
            if (id == CurrentUserId)
                return BadRequest(ApiResponse<object>.Fail("USE_SELF_ENDPOINT",
                    "Use /users/me/password to change your own password."));

            try
            {
                await _userService.ChangePasswordAsync(id, req.NewPassword, ct);
                return Ok(ApiResponse<object>.Ok(new { changed = true }));
            }
            catch (UserNotFoundException ex)
            {
                return NotFound(ApiResponse<object>.Fail("USER_NOT_FOUND", ex.Message));
            }
        }

        [HttpPut("{id:int}/admin")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> SetAdmin(int id, [FromBody] SetAdminRequest req, CancellationToken ct)
        {
            try
            {
                var user = await _userService.SetAdminAsync(id, req.IsAdmin, ct);
                return Ok(ApiResponse<UserAccountDto>.Ok(UserAccountDto.From(user)));
            }
            catch (UserNotFoundException ex)
            {
                return NotFound(ApiResponse<UserAccountDto>.Fail("USER_NOT_FOUND", ex.Message));
            }
            catch (LastAdminException ex)
            {
                return Conflict(ApiResponse<UserAccountDto>.Fail("LAST_ADMIN", ex.Message));
            }
        }

        [HttpPut("{id:int}/active")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> SetActive(int id, [FromBody] SetActiveRequest req, CancellationToken ct)
        {
            // Deactivating yourself locks you out of the session you're using, and the
            // last-admin guard doesn't catch it while another admin exists.
            if (id == CurrentUserId && !req.IsActive)
                return BadRequest(ApiResponse<UserAccountDto>.Fail("CANNOT_DEACTIVATE_SELF",
                    "You cannot deactivate your own account."));

            try
            {
                var user = await _userService.SetActiveAsync(id, req.IsActive, ct);
                return Ok(ApiResponse<UserAccountDto>.Ok(UserAccountDto.From(user)));
            }
            catch (UserNotFoundException ex)
            {
                return NotFound(ApiResponse<UserAccountDto>.Fail("USER_NOT_FOUND", ex.Message));
            }
            catch (LastAdminException ex)
            {
                return Conflict(ApiResponse<UserAccountDto>.Fail("LAST_ADMIN", ex.Message));
            }
        }

        /// <summary>
        /// Irreversible. Removes the account's own rows (library, watch history, API tokens,
        /// lists) — shared media is untouched. Prefer deactivation when the history matters.
        /// </summary>
        [HttpDelete("{id:int}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> DeleteUser(int id, CancellationToken ct)
        {
            if (id == CurrentUserId)
                return BadRequest(ApiResponse<object>.Fail("CANNOT_DELETE_SELF",
                    "You cannot delete your own account."));

            try
            {
                await _userService.DeleteUserAsync(id, ct);
                return Ok(ApiResponse<object>.Ok(new { deleted = true }));
            }
            catch (UserNotFoundException ex)
            {
                return NotFound(ApiResponse<object>.Fail("USER_NOT_FOUND", ex.Message));
            }
            catch (LastAdminException ex)
            {
                return Conflict(ApiResponse<object>.Fail("LAST_ADMIN", ex.Message));
            }
        }

        // ── Admin-on-behalf contacts ──────────────────────────────────────────

        [HttpGet("{id:int}/contacts")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetUserContacts(int id, CancellationToken ct) =>
            await ListContactsCoreAsync(id, ct);

        [HttpPost("{id:int}/contacts")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> AddUserContact(int id, [FromBody] ContactRequest req, CancellationToken ct) =>
            await AddContactCoreAsync(id, req, ct);

        [HttpPut("{id:int}/contacts/{contactId:int}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> UpdateUserContact(int id, int contactId, [FromBody] ContactRequest req, CancellationToken ct) =>
            await UpdateContactCoreAsync(id, contactId, req, ct);

        [HttpDelete("{id:int}/contacts/{contactId:int}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> DeleteUserContact(int id, int contactId, CancellationToken ct) =>
            await DeleteContactCoreAsync(id, contactId, ct);

        // ── Shared implementations ────────────────────────────────────────────
        // Self and admin-on-behalf routes differ only in whose id they act on, so the body
        // lives once here and [Authorize(Roles = "Admin")] stays on the routes that need it.

        private async Task<IActionResult> UpdateProfileCoreAsync(int userId, UpdateProfileRequest req, CancellationToken ct)
        {
            try
            {
                var user = await _userService.UpdateProfileAsync(userId, req.Email, req.FirstName,
                    req.LastName, req.Handle, req.DisplayName, ct);
                var contacts = await _userService.ListContactsAsync(userId, ct);
                return Ok(ApiResponse<UserAccountDto>.Ok(UserAccountDto.From(user, contacts)));
            }
            catch (UserNotFoundException ex)
            {
                return NotFound(ApiResponse<UserAccountDto>.Fail("USER_NOT_FOUND", ex.Message));
            }
        }

        private async Task<IActionResult> ListContactsCoreAsync(int userId, CancellationToken ct)
        {
            var contacts = await _userService.ListContactsAsync(userId, ct);
            return Ok(ApiResponse<List<UserContactDto>>.Ok(contacts.Select(UserContactDto.From).ToList()));
        }

        private async Task<IActionResult> AddContactCoreAsync(int userId, ContactRequest req, CancellationToken ct)
        {
            try
            {
                var contact = await _userService.AddContactAsync(userId, req.Kind, req.Label, req.Value, req.IsPrimary, ct);
                return Ok(ApiResponse<UserContactDto>.Ok(UserContactDto.From(contact)));
            }
            catch (UserNotFoundException ex)
            {
                return NotFound(ApiResponse<UserContactDto>.Fail("USER_NOT_FOUND", ex.Message));
            }
        }

        private async Task<IActionResult> UpdateContactCoreAsync(int userId, int contactId, ContactRequest req, CancellationToken ct)
        {
            try
            {
                var contact = await _userService.UpdateContactAsync(userId, contactId, req.Kind, req.Label,
                    req.Value, req.IsPrimary, ct);
                return Ok(ApiResponse<UserContactDto>.Ok(UserContactDto.From(contact)));
            }
            catch (UserContactNotFoundException ex)
            {
                return NotFound(ApiResponse<UserContactDto>.Fail("CONTACT_NOT_FOUND", ex.Message));
            }
        }

        private async Task<IActionResult> DeleteContactCoreAsync(int userId, int contactId, CancellationToken ct)
        {
            try
            {
                await _userService.DeleteContactAsync(userId, contactId, ct);
                return Ok(ApiResponse<object>.Ok(new { deleted = true }));
            }
            catch (UserContactNotFoundException ex)
            {
                return NotFound(ApiResponse<object>.Fail("CONTACT_NOT_FOUND", ex.Message));
            }
        }

        private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
    }

    public record PatchPreferencesRequest(
        bool? ShowDiagnostics,
        bool? DefaultFoldsOpen,
        Dictionary<string, bool>? Folds,
        bool? CreateCollectionStubs = null,
        bool? ShowNowPlayingBanner = null,
        string? Theme = null
    );
}
