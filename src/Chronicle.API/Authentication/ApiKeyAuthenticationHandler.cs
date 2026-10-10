using System.Security.Claims;
using System.Text.Encodings.Web;
using Chronicle.Services.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Chronicle.API.Authentication;

/// <summary>
/// ASP.NET Core authentication handler for the <c>X-API-Key</c> header.
/// Validates keys against the database via <see cref="IApiTokenService"/> and builds
/// a claims principal identical in shape to the JWT one so all controllers work
/// with either auth scheme transparently.
/// </summary>
public class ApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "ApiKey";
    private const string ApiKeyHeader = "X-API-Key";

    /// <summary>Carries the calling ApiToken's own Id -- distinct from the user id every other
    /// claim here already carries, since one user can have several API keys/devices (e.g. two
    /// Shields) and some features (KodiDevice registration) need to know exactly which device
    /// is calling, not just which user owns it. Absent on a JWT-authenticated request (the web
    /// UI has no single "device" to attribute); callers that need this must be reached only via
    /// an API key, same as every other scraper/device-facing endpoint already is.</summary>
    public const string ApiTokenIdClaimType = "chronicle:api_token_id";

    /// <summary>The key's scope (see ApiKeyScopes). Enforced by <see cref="ApiKeyScopeHandler"/>.</summary>
    public const string ScopeClaimType = "chronicle:api_key_scope";

    private readonly IApiTokenService _tokenService;
    private readonly AuthAuditLog _audit;

    public ApiKeyAuthenticationHandler(
        IApiTokenService tokenService,
        AuthAuditLog audit,
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
        _tokenService = tokenService;
        _audit = audit;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(ApiKeyHeader, out var values))
            return AuthenticateResult.NoResult();

        var rawKey = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(rawKey))
            return AuthenticateResult.NoResult();

        var token = await _tokenService.ValidateTokenAsync(rawKey, Context.RequestAborted);
        if (token is null)
        {
            _audit.RejectedCredential(Context, "ApiKey", "invalid or expired API key", SessionStore.LogTag(rawKey));
            return AuthenticateResult.Fail("Invalid or expired API key.");
        }

        var user = token.User!;

        // A deactivated account's keys must stop working too, not just its password.
        if (!user.IsActive)
        {
            _audit.RejectedCredential(Context, "ApiKey", "account is deactivated", SessionStore.LogTag(rawKey), user.Id);
            return AuthenticateResult.Fail("Account is deactivated.");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(ApiTokenIdClaimType, token.Id.ToString()),
            new(ScopeClaimType, token.Scope),
        };

        // Only a full-access key acts as an administrator. A scoped key (a Kodi box, a bridge)
        // never does, even when its owner is one: that is the point of scoping it.
        if (user.IsAdmin && token.Scope == ApiKeyScopes.Full)
            claims.Add(new Claim(ClaimTypes.Role, "Admin"));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return AuthenticateResult.Success(ticket);
    }
}
