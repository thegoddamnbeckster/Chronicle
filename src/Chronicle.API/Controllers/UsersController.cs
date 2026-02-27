using System.Security.Claims;
using Chronicle.API.DTOs;
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

            return Ok(ApiResponse<UserDto>.Ok(new UserDto(user.Id, user.Username, user.Email, user.DisplayName, user.IsAdmin)));
        }
    }
}
