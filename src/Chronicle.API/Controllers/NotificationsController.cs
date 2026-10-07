using System.Security.Claims;
using Chronicle.API.DTOs;
using Chronicle.Core.Models;
using Chronicle.Services.Live;
using Chronicle.Services.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chronicle.API.Controllers;

/// <summary>The bell: a person's own notifications. Every route is scoped to the caller; another person's id is a 404.</summary>
[ApiController]
[Route("api/v1/notifications")]
[Authorize]
public class NotificationsController : ControllerBase
{
    private readonly INotificationService _notifications;

    public NotificationsController(INotificationService notifications) => _notifications = notifications;

    private int CurrentUserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int limit = 30, [FromQuery] bool unreadOnly = false, CancellationToken ct = default) =>
        Ok(ApiResponse<NotificationPage>.Ok(await _notifications.ListAsync(CurrentUserId, limit, unreadOnly, ct)));

    /// <summary>The kinds a person can switch off, for the preferences screen.</summary>
    [HttpGet("kinds")]
    public IActionResult Kinds() => Ok(ApiResponse<IReadOnlyList<NotificationKinds.Info>>.Ok(NotificationKinds.All));

    [HttpPost("{id:int}/read")]
    public async Task<IActionResult> MarkRead(int id, CancellationToken ct) =>
        await _notifications.MarkReadAsync(CurrentUserId, id, ct)
            ? Ok(ApiResponse<object>.Ok(new { read = id }))
            : NotFound(ApiResponse<object>.Fail("NOT_FOUND", "No such notification."));

    [HttpPost("read-all")]
    public async Task<IActionResult> MarkAllRead(CancellationToken ct) =>
        Ok(ApiResponse<object>.Ok(new { marked = await _notifications.MarkAllReadAsync(CurrentUserId, ct) }));

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct) =>
        await _notifications.DeleteAsync(CurrentUserId, id, ct)
            ? Ok(ApiResponse<object>.Ok(new { deleted = id }))
            : NotFound(ApiResponse<object>.Fail("NOT_FOUND", "No such notification."));

    /// <summary>Clears everything already read.</summary>
    [HttpDelete("read")]
    public async Task<IActionResult> DeleteRead(CancellationToken ct) =>
        Ok(ApiResponse<object>.Ok(new { deleted = await _notifications.DeleteReadAsync(CurrentUserId, ct) }));
}

/// <summary>
/// The one small poll the web UI makes in the background: which library items changed since I last asked, and how many
/// unread notifications do I have. Cheap enough to call every few seconds; background calls do not keep a session alive.
/// </summary>
[ApiController]
[Route("api/v1/library/changes")]
[Authorize]
public class LibraryChangesController : ControllerBase
{
    private readonly LibraryChangeFeed _feed;
    private readonly INotificationService _notifications;

    public LibraryChangesController(LibraryChangeFeed feed, INotificationService notifications)
    {
        _feed = feed;
        _notifications = notifications;
    }

    public sealed record ChangesResponse(Guid Epoch, long Revision, bool Reset, IReadOnlyList<ChangedItem> Changes, int UnreadNotifications);

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] long? since, [FromQuery] Guid? epoch, CancellationToken ct)
    {
        var userId = int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var set = _feed.GetSince(since, epoch, userId);
        var unread = await _notifications.UnreadCountAsync(userId, ct);
        return Ok(ApiResponse<ChangesResponse>.Ok(new ChangesResponse(set.Epoch, set.Revision, set.Reset, set.Changes, unread)));
    }
}
