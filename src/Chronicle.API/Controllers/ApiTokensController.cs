using System.Security.Claims;
using Chronicle.API.DTOs;
using Chronicle.Services.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chronicle.API.Controllers;

[ApiController]
[Route("api/v1/tokens")]
[Authorize]
public class ApiTokensController : ControllerBase
{
    private readonly IApiTokenService _tokenService;

    public ApiTokensController(IApiTokenService tokenService)
    {
        _tokenService = tokenService;
    }

    /// <summary>Lists all active API tokens for the authenticated user.</summary>
    [HttpGet]
    public async Task<IActionResult> GetTokens()
    {
        var userId = GetUserId();
        var tokens = await _tokenService.GetTokensForUserAsync(userId, HttpContext.RequestAborted);

        var dtos = tokens
            .Select(t => new ApiTokenDto(t.Id, t.Name, t.CreatedAt, t.LastUsedAt, t.ExpiresAt))
            .ToList();

        return Ok(ApiResponse<List<ApiTokenDto>>.Ok(dtos));
    }

    /// <summary>
    /// Creates a new API token. The raw token value is returned once in the response
    /// and is never retrievable again.
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> CreateToken([FromBody] CreateApiTokenRequest request)
    {
        var userId = GetUserId();
        var (token, rawValue) = await _tokenService.CreateTokenAsync(
            userId, request.Name, request.ExpiresAt, HttpContext.RequestAborted);

        var dto = new CreateApiTokenResponse(
            token.Id, token.Name, rawValue, token.CreatedAt, token.ExpiresAt);

        return Ok(ApiResponse<CreateApiTokenResponse>.Ok(dto));
    }

    /// <summary>Revokes (soft-deletes) an API token owned by the authenticated user.</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> RevokeToken(int id)
    {
        var userId = GetUserId();
        var revoked = await _tokenService.RevokeTokenAsync(id, userId, HttpContext.RequestAborted);

        if (!revoked)
            return NotFound(ApiResponse<object>.Fail("TOKEN_NOT_FOUND", "Token not found or already revoked."));

        return Ok(ApiResponse<object>.Ok(new { }));
    }

    private int GetUserId() =>
        int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
}
