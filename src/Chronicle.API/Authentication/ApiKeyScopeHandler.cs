using Chronicle.Services.Security;
using Microsoft.AspNetCore.Authorization;

namespace Chronicle.API.Authentication;

/// <summary>
/// Part of the default authorization policy: a request authenticated by an API key must be
/// inside that key's scope. Requests authenticated by a browser session are unaffected, and a
/// "full" key passes everything. Applies to every [Authorize] route that uses the default policy,
/// so a newly added route is closed to scoped keys until a scope rule names it.
/// </summary>
public sealed class ApiKeyScopeRequirement : IAuthorizationRequirement { }

public sealed class ApiKeyScopeHandler : AuthorizationHandler<ApiKeyScopeRequirement>
{
    private readonly AuthAuditLog _audit;

    public ApiKeyScopeHandler(AuthAuditLog audit) => _audit = audit;

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, ApiKeyScopeRequirement requirement)
    {
        var scopeClaim = context.User.FindFirst(ApiKeyAuthenticationHandler.ScopeClaimType)?.Value;

        // Not an API-key caller (a session, or nothing): nothing to restrict here.
        if (scopeClaim is null)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        var http = context.Resource as HttpContext;
        var method = http?.Request.Method ?? string.Empty;
        var path = http?.Request.Path.Value ?? string.Empty;

        if (ApiKeyScopes.IsAllowed(scopeClaim, method, path))
        {
            context.Succeed(requirement);
        }
        else if (http is not null)
        {
            int? userId = int.TryParse(context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value, out var u) ? u : null;
            _audit.ScopeDenied(http, userId, scopeClaim, method, path);
        }
        return Task.CompletedTask;
    }
}
