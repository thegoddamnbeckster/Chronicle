using System.ComponentModel.DataAnnotations;
using Chronicle.API.Authentication;
using Chronicle.API.DTOs;
using Chronicle.Services.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chronicle.API.Controllers;

/// <summary>
/// Outgoing mail (Settings -> Email): the server password-reset links are sent through. Administrators with a browser
/// session only. The password can be set but is never returned; only whether one is saved.
/// </summary>
[ApiController]
[Route("api/v1/settings/email")]
[Authorize(Policy = AuthPolicies.SessionAdmin)]
public class EmailSettingsController : ControllerBase
{
    private readonly IEmailSettingsStore _store;
    private readonly IEmailSender _sender;
    private readonly ILogger<EmailSettingsController> _log;

    public EmailSettingsController(IEmailSettingsStore store, IEmailSender sender, ILogger<EmailSettingsController> log)
    {
        _store = store;
        _sender = sender;
        _log = log;
    }

    public sealed record EmailSettingsRequest(
        [Required] string Host, int Port, [Required] string Security, string? Username, string? Password,
        [Required] string FromAddress, string? FromName, string? PublicUrl);

    public sealed record TestEmailRequest([Required, MaxLength(320)] string To);

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct) =>
        Ok(ApiResponse<EmailSettings>.Ok(await _store.GetAsync(ct)));

    [HttpPut]
    public async Task<IActionResult> Put([FromBody] EmailSettingsRequest req, CancellationToken ct)
    {
        try
        {
            await _store.SaveAsync(new EmailSettingsUpdate(req.Host, req.Port, req.Security, req.Username, req.Password,
                req.FromAddress, req.FromName ?? "Chronicle", req.PublicUrl), ct);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ApiResponse<object>.Fail("INVALID_EMAIL_SETTINGS", ex.Message));
        }
        _log.LogInformation("EMAIL settings saved by an administrator (server {Host})", req.Host);
        return Ok(ApiResponse<EmailSettings>.Ok(await _store.GetAsync(ct)));
    }

    /// <summary>Sends a short message to the address given, using the saved settings, and reports exactly what went wrong if it fails.</summary>
    [HttpPost("test")]
    public async Task<IActionResult> Test([FromBody] TestEmailRequest req, CancellationToken ct)
    {
        var to = req.To.Trim();
        if (!EmailSettingsStore.IsPlausibleAddress(to))
            return BadRequest(ApiResponse<object>.Fail("INVALID_ADDRESS", "That does not look like an email address."));

        var credentials = await _store.GetCredentialsAsync(ct);
        if (!credentials.Settings.IsConfigured)
            return BadRequest(ApiResponse<object>.Fail("EMAIL_NOT_CONFIGURED", "Save the mail server, sender and public address first."));

        try
        {
            await _sender.SendAsync(new EmailMessage(to, "Chronicle test message",
                "This is a test message from Chronicle. If you can read it, password-reset emails will reach people."), credentials, ct);
            return Ok(ApiResponse<object>.Ok(new { sent = true }));
        }
        catch (EmailSendException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, ApiResponse<object>.Fail("EMAIL_FAILED", ex.Message));
        }
    }
}
