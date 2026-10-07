using System.Security.Claims;
using Chronicle.API.Authentication;
using Chronicle.API.DTOs;
using Chronicle.Services.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chronicle.API.Controllers;

[ApiController]
[Route("api/v1/tokens")]
// Credentials are managed with a real login only: an API key (even a "full" one) must not be
// able to mint, widen or revoke keys, or a single leaked key could entrench itself.
[Authorize(Policy = AuthPolicies.SessionOnly)]
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
            .Select(t => new ApiTokenDto(t.Id, t.Name, t.CreatedAt, t.LastUsedAt, t.ExpiresAt, t.Scope))
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
        var scope = string.IsNullOrWhiteSpace(request.Scope) ? ApiKeyScopes.Full : request.Scope.Trim().ToLowerInvariant();
        if (!ApiKeyScopes.IsKnown(scope))
            return BadRequest(ApiResponse<object>.Fail("UNKNOWN_SCOPE",
                $"Unknown scope '{request.Scope}'. Use one of: {string.Join(", ", ApiKeyScopes.All)}."));

        var (token, rawValue) = await _tokenService.CreateTokenAsync(
            userId, request.Name, request.ExpiresAt, HttpContext.RequestAborted, scope);

        var dto = new CreateApiTokenResponse(
            token.Id, token.Name, rawValue, token.CreatedAt, token.ExpiresAt, token.Scope);

        return Ok(ApiResponse<CreateApiTokenResponse>.Ok(dto));
    }

    /// <summary>The scopes a key can be given, with plain-language descriptions.</summary>
    [HttpGet("scopes")]
    public IActionResult GetScopes() =>
        Ok(ApiResponse<List<ApiKeyScopeDto>>.Ok(
            ApiKeyScopes.All.Select(s => new ApiKeyScopeDto(s, ApiKeyScopes.Describe(s))).ToList()));

    /// <summary>Changes what an existing key may do. Takes effect on the key's next request.</summary>
    [HttpPut("{id:int}/scope")]
    public async Task<IActionResult> SetScope(int id, [FromBody] SetApiTokenScopeRequest request)
    {
        var scope = request.Scope.Trim().ToLowerInvariant();
        if (!ApiKeyScopes.IsKnown(scope))
            return BadRequest(ApiResponse<object>.Fail("UNKNOWN_SCOPE",
                $"Unknown scope '{request.Scope}'. Use one of: {string.Join(", ", ApiKeyScopes.All)}."));

        var changed = await _tokenService.SetScopeAsync(id, GetUserId(), scope, HttpContext.RequestAborted);
        return changed
            ? Ok(ApiResponse<object>.Ok(new { id, scope }))
            : NotFound(ApiResponse<object>.Fail("TOKEN_NOT_FOUND", "Token not found or already revoked."));
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
