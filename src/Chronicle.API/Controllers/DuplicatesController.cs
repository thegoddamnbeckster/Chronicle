using System.Text.Json;
using Chronicle.API.DTOs;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Chronicle.API.Controllers;

[ApiController]
[Route("api/v1/duplicates")]
[Authorize(Roles = "Admin")]
public class DuplicatesController(
    ChronicleDbContext db,
    DuplicateCandidateScanService scanner,
    ILogger<DuplicatesController> logger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetCandidates(
        [FromQuery] string? mediaType,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        page     = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var q = db.MediaItemDuplicateCandidates
            .Include(c => c.ItemA).ThenInclude(m => m!.MediaType)
            .Include(c => c.ItemA).ThenInclude(m => m!.ExternalIds)
            .Include(c => c.ItemB).ThenInclude(m => m!.MediaType)
            .Include(c => c.ItemB).ThenInclude(m => m!.ExternalIds)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(mediaType))
            q = q.Where(c => c.ItemA!.MediaType!.Name == mediaType);

        var total = await q.CountAsync(ct);
        var candidates = await q
            .OrderBy(c => c.DetectedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        var data = candidates.Select(c => new
        {
            candidateId = c.Id,
            itemA = new {
                c.ItemA!.Id, c.ItemA.Name, c.ItemA.PosterUrl, c.ItemA.HierarchyLevel,
                c.ItemA.Year, c.ItemA.Overview,
                mediaType   = c.ItemA.MediaType?.Name,
                externalIds = c.ItemA.ExternalIds.Select(e => new { e.Source, e.ExternalId }).ToList(),
                filePath    = ExtractFilePath(c.ItemA.MetadataJson),
            },
            itemB = new {
                c.ItemB!.Id, c.ItemB.Name, c.ItemB.PosterUrl, c.ItemB.HierarchyLevel,
                c.ItemB.Year, c.ItemB.Overview,
                mediaType   = c.ItemB.MediaType?.Name,
                externalIds = c.ItemB.ExternalIds.Select(e => new { e.Source, e.ExternalId }).ToList(),
                filePath    = ExtractFilePath(c.ItemB.MetadataJson),
            },
        }).ToList<object>();

        return Ok(ApiResponse<List<object>>.Ok(data,
            new PaginationInfo(page, pageSize, total)));
    }

    [HttpPost("dismiss")]
    public async Task<IActionResult> Dismiss(
        [FromBody] DismissDuplicateDto dto,
        CancellationToken ct)
    {
        var a = Math.Min(dto.ItemAId, dto.ItemBId);
        var b = Math.Max(dto.ItemAId, dto.ItemBId);

        var exists = await db.MediaItemDuplicateDismissals
            .AnyAsync(d => d.ItemAId == a && d.ItemBId == b, ct);
        if (!exists)
        {
            db.MediaItemDuplicateDismissals.Add(new MediaItemDuplicateDismissal
            {
                ItemAId     = a,
                ItemBId     = b,
                DismissedAt = DateTime.UtcNow,
            });
        }

        // Remove from candidates
        var candidate = await db.MediaItemDuplicateCandidates
            .FirstOrDefaultAsync(c => (c.ItemAId == a && c.ItemBId == b) ||
                                       (c.ItemAId == b && c.ItemBId == a), ct);
        if (candidate is not null) db.MediaItemDuplicateCandidates.Remove(candidate);

        await db.SaveChangesAsync(ct);
        return Ok(ApiResponse<object>.Ok(new { dismissed = true }));
    }

    [HttpPost("scan")]
    public IActionResult TriggerScan()
    {
        _ = Task.Run(async () =>
        {
            try   { await scanner.ExecuteAsync(CancellationToken.None); }
            catch (Exception ex) { logger.LogError(ex, "Duplicate candidate scan failed"); }
        });
        return Accepted(ApiResponse<object>.Ok(new { message = "Duplicate candidate scan started." }));
    }

    /// <summary>
    /// Returns the most specific file path available from the fileScanner metadata blob:
    /// first individual file path, then folder path, then null.
    /// </summary>
    private static string? ExtractFilePath(string? metadataJson)
    {
        if (metadataJson is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            if (!doc.RootElement.TryGetProperty("fileScanner", out var scanner))
                return null;

            // Prefer the first individual file path
            if (scanner.TryGetProperty("filePaths", out var fps)
                && fps.ValueKind == JsonValueKind.Array
                && fps.GetArrayLength() > 0)
            {
                var first = fps[0].GetString();
                if (!string.IsNullOrEmpty(first)) return first;
            }

            // Fall back to folder path (groups / parent-level items)
            if (scanner.TryGetProperty("folderPath", out var fp))
            {
                var folder = fp.GetString();
                if (!string.IsNullOrEmpty(folder)) return folder;
            }
        }
        catch { /* malformed JSON */ }
        return null;
    }
}
