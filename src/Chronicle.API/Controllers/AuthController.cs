using Chronicle.API.DTOs;
using Chronicle.Core.Exceptions;
using Chronicle.Services;
using Chronicle.Services.Security;
using Microsoft.AspNetCore.Mvc;

namespace Chronicle.API.Controllers
{
    [ApiController]
    [Route("api/v1/auth")]
    public class AuthController : ControllerBase
    {
        private readonly IUserService _userService;
        private readonly IJwtTokenService _jwtService;

        public AuthController(IUserService userService, IJwtTokenService jwtService)
        {
            _userService = userService;
            _jwtService = jwtService;
        }

        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterRequest request)
        {
            try
            {
                var user = await _userService.RegisterAsync(request.Username, request.Password, request.Email);
                var token = _jwtService.GenerateToken(user);
                return Ok(ApiResponse<AuthResponse>.Ok(new AuthResponse(token, ToDto(user))));
            }
            catch (DuplicateUsernameException ex)
            {
                return Conflict(ApiResponse<AuthResponse>.Fail("USERNAME_TAKEN", ex.Message));
            }
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            try
            {
                var user = await _userService.AuthenticateAsync(request.Username, request.Password);
                var token = _jwtService.GenerateToken(user);
                return Ok(ApiResponse<AuthResponse>.Ok(new AuthResponse(token, ToDto(user))));
            }
            catch (InvalidCredentialsException)
            {
                return Unauthorized(ApiResponse<AuthResponse>.Fail("INVALID_CREDENTIALS", "Invalid username or password."));
            }
        }

        private static UserDto ToDto(Chronicle.Core.Models.User u) =>
            new(u.Id, u.Username, u.Email, u.DisplayName, u.IsAdmin);
    }
}
