using System.Security.Claims;
using Chronicle.API.Authentication;
using Chronicle.API.DTOs;
using Chronicle.Data;
using Chronicle.Services.Database;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.API.Controllers;

/// <summary>
/// Database backups, restore, maintenance and size reporting (Settings -> Database). Administrators
/// signed in with a browser session only: an API key, even a full one, cannot download, replace or
/// delete the database. Design: docs/plans/2026-10-07-backlog-designs.md section 1.
/// </summary>
[ApiController]
[Route("api/v1/database")]
[Authorize(Policy = AuthPolicies.SessionAdmin)]
public class DatabaseController : ControllerBase
{
    /// <summary>The exact word an admin must type to restore. Restoring replaces every user's data.</summary>
    public const string RestoreConfirmation = "RESTORE";

    private const long MaxUploadBytes = 16L * 1024 * 1024 * 1024;

    private readonly IDatabaseAdminService _admin;
    private readonly IAppRestart _restart;
    private readonly ChronicleDbContext _db;
    private readonly ILogger<DatabaseController> _log;

    public DatabaseController(IDatabaseAdminService admin, IAppRestart restart, ChronicleDbContext db, ILogger<DatabaseController> log)
    {
        _admin = admin;
        _restart = restart;
        _db = db;
        _log = log;
    }

    private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private IActionResult Fail(DatabaseAdminException ex)
    {
        var status = ex.Code switch
        {
            "UNSUPPORTED"    => StatusCodes.Status400BadRequest,
            "NOT_FOUND"      => StatusCodes.Status404NotFound,
            "BUSY"           => StatusCodes.Status409Conflict,
            "LOW_DISK"       => StatusCodes.Status409Conflict,
            "INVALID_BACKUP" => StatusCodes.Status422UnprocessableEntity,
            _                => StatusCodes.Status400BadRequest,
        };
        return StatusCode(status, ApiResponse<object>.Fail(ex.Code, ex.Message));
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken ct) =>
        Ok(ApiResponse<DatabaseStatus>.Ok(await _admin.GetStatusAsync(ct)));

    // ── settings ──────────────────────────────────────────────────────────────

    public sealed record DatabaseSettingsRequest(int? BackupsToKeep, int? WarnSizeMb);

    /// <summary>How many scheduled/manual backups to keep, and the size at which the Database page warns.</summary>
    [HttpPut("settings")]
    public async Task<IActionResult> PutSettings([FromBody] DatabaseSettingsRequest req, CancellationToken ct)
    {
        if (req.BackupsToKeep is { } keep)
        {
            if (keep < 1 || keep > 365)
                return BadRequest(ApiResponse<object>.Fail("INVALID_SETTING", "Keep between 1 and 365 backups."));
            await UpsertAsync(DatabaseAdminService.RetainKey, keep.ToString(), ct);
        }
        if (req.WarnSizeMb is { } warn)
        {
            if (warn < 1 || warn > 100_000_000)
                return BadRequest(ApiResponse<object>.Fail("INVALID_SETTING", "Enter a size in megabytes of at least 1."));
            await UpsertAsync(DatabaseAdminService.WarnSizeMbKey, warn.ToString(), ct);
        }
        return Ok(ApiResponse<DatabaseStatus>.Ok(await _admin.GetStatusAsync(ct)));
    }

    private async Task UpsertAsync(string key, string value, CancellationToken ct)
    {
        var row = await _db.AppSettings.FindAsync([key], ct);
        if (row is null) _db.AppSettings.Add(new Chronicle.Core.Models.AppSetting { Key = key, Value = value });
        else row.Value = value;
        await _db.SaveChangesAsync(ct);
    }

    // ── backups ───────────────────────────────────────────────────────────────

    [HttpGet("backups")]
    public IActionResult ListBackups() => Ok(ApiResponse<IReadOnlyList<BackupInfo>>.Ok(_admin.ListBackups()));

    [HttpPost("backups")]
    public async Task<IActionResult> CreateBackup(CancellationToken ct)
    {
        try
        {
            var info = await _admin.CreateBackupAsync(BackupKinds.Manual, ct);
            _log.LogInformation("DATABASE backup requested by user id {UserId}: {File}", CurrentUserId, info.FileName);
            return Ok(ApiResponse<BackupInfo>.Ok(info));
        }
        catch (DatabaseAdminException ex) { return Fail(ex); }
    }

    [HttpGet("backups/{fileName}/download")]
    public IActionResult Download(string fileName)
    {
        try
        {
            var path = _admin.GetBackupPath(fileName);
            _log.LogInformation("DATABASE backup downloaded by user id {UserId}: {File}", CurrentUserId, fileName);
            return PhysicalFile(path, "application/zip", fileName);
        }
        catch (DatabaseAdminException ex) { return Fail(ex); }
    }

    [HttpDelete("backups/{fileName}")]
    public IActionResult Delete(string fileName)
    {
        try
        {
            _admin.DeleteBackup(fileName);
            _log.LogInformation("DATABASE backup deleted by user id {UserId}: {File}", CurrentUserId, fileName);
            return Ok(ApiResponse<object>.Ok(new { deleted = fileName }));
        }
        catch (DatabaseAdminException ex) { return Fail(ex); }
    }

    /// <summary>
    /// Uploads a backup zip as the raw request body (not multipart: that would buffer the whole file in
    /// the system temp folder, and nothing Chronicle handles may sit there). The file is checked before it
    /// is kept; a bad one is deleted and the reason returned.
    /// </summary>
    [HttpPost("backups/upload")]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<IActionResult> Upload(CancellationToken ct)
    {
        try
        {
            var info = await _admin.SaveUploadAsync(Request.Body, ct);
            _log.LogInformation("DATABASE backup uploaded by user id {UserId}: {File}", CurrentUserId, info.FileName);
            return Ok(ApiResponse<BackupInfo>.Ok(info));
        }
        catch (DatabaseAdminException ex) { return Fail(ex); }
    }

    [HttpPost("backups/{fileName}/validate")]
    public async Task<IActionResult> Validate(string fileName, CancellationToken ct)
    {
        try { return Ok(ApiResponse<BackupValidation>.Ok(await _admin.ValidateBackupAsync(fileName, ct))); }
        catch (DatabaseAdminException ex) { return Fail(ex); }
    }

    public sealed record RestoreRequest(string? Confirm);

    /// <summary>
    /// Replaces the live database with a backup. Requires typing <c>RESTORE</c>. The current database is
    /// backed up first, the restore is staged, and the application restarts to swap the file in - so the
    /// response arrives just before Chronicle goes down. Everyone is signed out by the restart.
    /// </summary>
    [HttpPost("backups/{fileName}/restore")]
    public async Task<IActionResult> Restore(string fileName, [FromBody] RestoreRequest req, CancellationToken ct)
    {
        if (!string.Equals(req.Confirm, RestoreConfirmation, StringComparison.Ordinal))
            return BadRequest(ApiResponse<object>.Fail("CONFIRMATION_REQUIRED", $"Type {RestoreConfirmation} to confirm."));

        try
        {
            await _admin.StageRestoreAsync(fileName, ct);
        }
        catch (DatabaseAdminException ex) { return Fail(ex); }

        _log.LogWarning("DATABASE restore of {File} requested by user id {UserId}; restarting to apply it", fileName, CurrentUserId);
        _restart.Request($"database restore from {fileName}");
        return Ok(ApiResponse<object>.Ok(new { restarting = true }));
    }

    // ── maintenance ───────────────────────────────────────────────────────────

    /// <param name="kind"><c>quick</c> (statistics + log trim) or <c>full</c> (rebuild indexes, compact).</param>
    [HttpPost("maintenance/{kind}")]
    public async Task<IActionResult> Maintenance(string kind, CancellationToken ct)
    {
        if (kind is not ("quick" or "full"))
            return BadRequest(ApiResponse<object>.Fail("UNKNOWN_KIND", "Use 'quick' or 'full'."));
        try
        {
            var result = await _admin.RunMaintenanceAsync(heavy: kind == "full", ct);
            _log.LogInformation("DATABASE {Kind} maintenance run by user id {UserId}", kind, CurrentUserId);
            return Ok(ApiResponse<MaintenanceResult>.Ok(result));
        }
        catch (DatabaseAdminException ex) { return Fail(ex); }
    }
}
