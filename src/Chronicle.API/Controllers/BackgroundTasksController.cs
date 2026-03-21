using Chronicle.Data;
using Chronicle.Services;
using Cronos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.API.Controllers;

[ApiController]
[Route("api/v1/background-tasks")]
[Authorize]
public class BackgroundTasksController : ControllerBase
{
    private readonly ChronicleDbContext _db;
    private readonly ITaskSchedulerService _scheduler;

    public BackgroundTasksController(
        ChronicleDbContext db,
        ITaskSchedulerService scheduler)
    {
        _db        = db;
        _scheduler = scheduler;
    }

    /// <summary>Returns all registered background tasks with live status and plugin branding.</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var rows = await _db.BackgroundTasks
            .Include(t => t.Plugin)
            .OrderBy(t => t.DisplayName)
            .ToListAsync();

        var dtos = rows.Select(r => new BackgroundTaskDto(
            TaskId:           r.TaskId,
            DisplayName:      r.DisplayName,
            Description:      r.Description,
            CronExpression:   r.CronExpression,
            IsEnabled:        r.IsEnabled,
            IsRunning:        _scheduler.IsRunning(r.TaskId),
            LastRunAt:        r.LastRunAt,
            LastRunSucceeded: r.LastRunSucceeded,
            LastErrorMessage: r.LastErrorMessage,
            NextRunAt:        r.NextRunAt,
            PluginId:         r.PluginId,
            PluginName:       r.Plugin?.Name,
            PluginIconUrl:    r.Plugin?.IconUrl,
            BrandColorLight:  r.Plugin?.BrandColorLight,
            BrandColorDark:   r.Plugin?.BrandColorDark
        ));

        return Ok(new { success = true, data = dtos });
    }

    /// <summary>Updates a task's schedule and/or enabled state.</summary>
    [HttpPatch("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> Update(string id, [FromBody] UpdateBackgroundTaskRequest body)
    {
        var row = await _db.BackgroundTasks.FindAsync(id);
        if (row is null)
            return NotFound(new
            {
                success = false,
                error = new { code = "TASK_NOT_FOUND", message = $"No background task with ID '{id}' was found." }
            });

        if (body.CronExpression is not null)
        {
            if (!TryParseCron(body.CronExpression, out var parsed))
                return BadRequest(new
                {
                    success = false,
                    error = new
                    {
                        code = "INVALID_CRON",
                        message = $"The cron expression '{body.CronExpression}' is not valid. " +
                                  "A cron expression has five fields: minute, hour, day-of-month, month, day-of-week. " +
                                  "Example: 0 */4 * * * (every 4 hours)."
                    }
                });

            row.CronExpression = body.CronExpression;
            row.NextRunAt      = parsed!.GetNextOccurrence(DateTime.UtcNow, TimeZoneInfo.Utc);
        }

        if (body.IsEnabled.HasValue)
            row.IsEnabled = body.IsEnabled.Value;

        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Triggers a task immediately. Returns 409 if already running.</summary>
    [HttpPost("{id}/run")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> RunNow(string id)
    {
        var result = await _scheduler.TriggerNowAsync(id);

        return result switch
        {
            TriggerResult.Started        => Accepted(new { success = true, message = "Task started." }),
            TriggerResult.AlreadyRunning => Conflict(new
            {
                success = false,
                error = new
                {
                    code = "TASK_ALREADY_RUNNING",
                    message = "This task is already running. Wait for it to finish before running it again."
                }
            }),
            TriggerResult.NotFound => NotFound(new
            {
                success = false,
                error = new { code = "TASK_NOT_FOUND", message = $"No background task with ID '{id}' was found." }
            }),
            _ => StatusCode(500)
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool TryParseCron(string expression, out CronExpression? parsed)
    {
        try
        {
            parsed = CronExpression.Parse(expression);
            return true;
        }
        catch
        {
            parsed = null;
            return false;
        }
    }
}

public record BackgroundTaskDto(
    string    TaskId,
    string    DisplayName,
    string    Description,
    string    CronExpression,
    bool      IsEnabled,
    bool      IsRunning,
    DateTime? LastRunAt,
    bool?     LastRunSucceeded,
    string?   LastErrorMessage,
    DateTime? NextRunAt,
    // Plugin branding — null for system tasks
    string?   PluginId,
    string?   PluginName,
    string?   PluginIconUrl,
    string?   BrandColorLight,
    string?   BrandColorDark
);

public record UpdateBackgroundTaskRequest(
    string? CronExpression,
    bool? IsEnabled
);
