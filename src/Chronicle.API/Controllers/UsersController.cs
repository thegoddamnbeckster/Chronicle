using System.Security.Claims;
using Chronicle.API.DTOs;
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

        [HttpGet("me")]
        public async Task<IActionResult> GetMe()
        {
            var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var user = await _userService.GetByIdAsync(userId);

            if (user == null)
                return NotFound(ApiResponse<UserDto>.Fail("USER_NOT_FOUND", "User not found."));

            var prefs = await _userService.GetPreferencesAsync(userId);
            return Ok(ApiResponse<UserDto>.Ok(new UserDto(user.Id, user.Username, user.Email, user.DisplayName, user.IsAdmin,
                prefs.ShowDiagnostics ?? user.IsAdmin)));
        }

        [HttpGet("me/preferences")]
        public async Task<IActionResult> GetMyPreferences()
        {
            var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var prefs = await _userService.GetPreferencesAsync(userId);
            return Ok(ApiResponse<object>.Ok(new
            {
                showDiagnostics        = prefs.ShowDiagnostics,
                defaultFoldsOpen       = prefs.DefaultFoldsOpen,
                folds                  = prefs.Folds ?? new Dictionary<string, bool>(),
                createCollectionStubs  = prefs.CreateCollectionStubs ?? true,
                theme                  = prefs.Theme,
            }));
        }

        [HttpPatch("me/preferences")]
        public async Task<IActionResult> PatchMyPreferences([FromBody] PatchPreferencesRequest req)
        {
            var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var patch = new UserPreferences
            {
                ShowDiagnostics       = req.ShowDiagnostics,
                DefaultFoldsOpen      = req.DefaultFoldsOpen,
                Folds                 = req.Folds,
                CreateCollectionStubs = req.CreateCollectionStubs,
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
                theme                 = prefs.Theme,
            }));
        }
    }

    public record PatchPreferencesRequest(
        bool? ShowDiagnostics,
        bool? DefaultFoldsOpen,
        Dictionary<string, bool>? Folds,
        bool? CreateCollectionStubs = null,
        string? Theme = null
    );
}
