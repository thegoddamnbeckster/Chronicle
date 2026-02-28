using System.Security.Claims;
using Chronicle.API.DTOs;
using Chronicle.Core.Exceptions;
using Chronicle.Core.Models;
using Chronicle.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chronicle.API.Controllers;

[ApiController]
[Route("api/v1/lists")]
[Authorize]
public class MediaListsController : ControllerBase
{
    private readonly IMediaListService _listService;

    public MediaListsController(IMediaListService listService)
    {
        _listService = listService;
    }

    // ── GET /api/v1/lists ─────────────────────────────────────────────────────

    /// <summary>Returns all lists owned by the authenticated user.</summary>
    [HttpGet]
    public async Task<IActionResult> GetLists()
    {
        var lists = await _listService.GetAllForUserAsync(GetUserId());
        var dtos  = lists.Select(ToSummaryDto).ToList();
        return Ok(ApiResponse<List<MediaListDto>>.Ok(dtos));
    }

    // ── GET /api/v1/lists/{id} ────────────────────────────────────────────────

    /// <summary>Returns a list with its items, ordered by position.</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetList(int id)
    {
        var list = await _listService.GetByIdAsync(GetUserId(), id);
        if (list is null)
            return NotFound(ApiResponse<MediaListDetailDto>.Fail("LIST_NOT_FOUND", "List not found."));
        return Ok(ApiResponse<MediaListDetailDto>.Ok(ToDetailDto(list)));
    }

    // ── POST /api/v1/lists ────────────────────────────────────────────────────

    /// <summary>Creates a new list.</summary>
    [HttpPost]
    public async Task<IActionResult> CreateList([FromBody] CreateListRequestDto request)
    {
        var list = await _listService.CreateAsync(
            GetUserId(),
            new CreateListRequest(request.Name, request.Description, request.IsOrdered));

        return CreatedAtAction(
            nameof(GetList),
            new { id = list.Id },
            ApiResponse<MediaListDto>.Ok(ToSummaryDto(list)));
    }

    // ── PUT /api/v1/lists/{id} ────────────────────────────────────────────────

    /// <summary>Updates list metadata (name, description, ordered flag).</summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> UpdateList(int id, [FromBody] UpdateListRequestDto request)
    {
        try
        {
            var list = await _listService.UpdateAsync(
                GetUserId(), id,
                new UpdateListRequest(request.Name, request.Description, request.IsOrdered));
            return Ok(ApiResponse<MediaListDto>.Ok(ToSummaryDto(list)));
        }
        catch (MediaListNotFoundException ex)
        {
            return NotFound(ApiResponse<MediaListDto>.Fail("LIST_NOT_FOUND", ex.Message));
        }
    }

    // ── DELETE /api/v1/lists/{id} ─────────────────────────────────────────────

    /// <summary>Deletes a list (and all its items).</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteList(int id)
    {
        try
        {
            await _listService.DeleteAsync(GetUserId(), id);
            return NoContent();
        }
        catch (MediaListNotFoundException ex)
        {
            return NotFound(ApiResponse<object>.Fail("LIST_NOT_FOUND", ex.Message));
        }
    }

    // ── POST /api/v1/lists/{id}/items ─────────────────────────────────────────

    /// <summary>Adds a media item to the list.</summary>
    [HttpPost("{id:int}/items")]
    public async Task<IActionResult> AddItem(int id, [FromBody] AddItemToListRequestDto request)
    {
        try
        {
            var item = await _listService.AddItemAsync(
                GetUserId(), id,
                new AddItemToListRequest(request.MediaItemId, request.Position, request.Notes));

            return Ok(ApiResponse<MediaListItemDto>.Ok(ToItemDto(item)));
        }
        catch (MediaListNotFoundException ex)
        {
            return NotFound(ApiResponse<object>.Fail("LIST_NOT_FOUND", ex.Message));
        }
        catch (DuplicateListItemException ex)
        {
            return Conflict(ApiResponse<object>.Fail("DUPLICATE_ITEM", ex.Message));
        }
        catch (Exception ex)
        {
            return BadRequest(ApiResponse<object>.Fail("ADD_ITEM_FAILED", ex.Message));
        }
    }

    // ── DELETE /api/v1/lists/{id}/items/{itemId} ──────────────────────────────

    /// <summary>Removes an item from the list.</summary>
    [HttpDelete("{id:int}/items/{itemId:int}")]
    public async Task<IActionResult> RemoveItem(int id, int itemId)
    {
        try
        {
            await _listService.RemoveItemAsync(GetUserId(), id, itemId);
            return NoContent();
        }
        catch (MediaListNotFoundException ex)
        {
            return NotFound(ApiResponse<object>.Fail("LIST_NOT_FOUND", ex.Message));
        }
        catch (MediaListItemNotFoundException ex)
        {
            return NotFound(ApiResponse<object>.Fail("ITEM_NOT_FOUND", ex.Message));
        }
    }

    // ── PUT /api/v1/lists/{id}/items/reorder ─────────────────────────────────

    /// <summary>Bulk-updates the position of items in an ordered list.</summary>
    [HttpPut("{id:int}/items/reorder")]
    public async Task<IActionResult> ReorderItems(int id, [FromBody] ReorderItemsRequestDto request)
    {
        try
        {
            var reorderItems = request.Items
                .Select(r => new ReorderItem(r.ItemId, r.Position));
            await _listService.ReorderAsync(GetUserId(), id, reorderItems);
            return Ok(ApiResponse<object>.Ok(new { }));
        }
        catch (MediaListNotFoundException ex)
        {
            return NotFound(ApiResponse<object>.Fail("LIST_NOT_FOUND", ex.Message));
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private int GetUserId() =>
        int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static MediaListDto ToSummaryDto(Chronicle.Core.Models.MediaList l) =>
        new(l.Id, l.UserId, l.Name, l.Description, l.IsOrdered,
            l.Items.Count, l.CreatedAt, l.UpdatedAt);

    private static MediaListDetailDto ToDetailDto(Chronicle.Core.Models.MediaList l) =>
        new(l.Id, l.UserId, l.Name, l.Description, l.IsOrdered,
            l.CreatedAt, l.UpdatedAt,
            l.Items
             .OrderBy(i => i.Position)
             .Select(ToItemDto)
             .ToList());

    private static MediaListItemDto ToItemDto(MediaListItem i) =>
        new(i.Id, i.Position, i.Notes, i.AddedAt, ToMediaDto(i.MediaItem!));

    private static MediaItemDto ToMediaDto(Chronicle.Core.Models.MediaItem m) =>
        new(m.Id, m.MediaTypeId, m.MediaType?.DisplayName ?? string.Empty,
            m.ParentId, m.Name, m.Year, m.Overview, m.PosterUrl,
            m.RuntimeMinutes, m.HierarchyLevel, m.Number, m.CreatedAt, m.UpdatedAt);
}
