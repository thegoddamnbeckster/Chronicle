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

    private readonly IApiTokenService _tokenService;

    public ApiKeyAuthenticationHandler(
        IApiTokenService tokenService,
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
        _tokenService = tokenService;
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
            return AuthenticateResult.Fail("Invalid or expired API key.");

        var user = token.User!;

        // A deactivated account's keys must stop working too, not just its password.
        if (!user.IsActive)
            return AuthenticateResult.Fail("Account is deactivated.");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
        };

        if (user.IsAdmin)
            claims.Add(new Claim(ClaimTypes.Role, "Admin"));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);

        return AuthenticateResult.Success(ticket);
    }
}
