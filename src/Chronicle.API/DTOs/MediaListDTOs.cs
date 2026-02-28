using System.ComponentModel.DataAnnotations;

namespace Chronicle.API.DTOs;

// ── Request DTOs ──────────────────────────────────────────────────────────────

public record CreateListRequestDto(
    [Required, MaxLength(200)] string Name,
    string? Description,
    bool IsOrdered = true
);

public record UpdateListRequestDto(
    [MaxLength(200)] string? Name,
    string? Description,
    bool? IsOrdered
);

public record AddItemToListRequestDto(
    [Required] int MediaItemId,
    int Position = 0,
    string? Notes = null
);

public record ReorderItemDto(
    [Required] int ItemId,
    [Required] int Position
);

public record ReorderItemsRequestDto(
    [Required] List<ReorderItemDto> Items
);

// ── Response DTOs ─────────────────────────────────────────────────────────────

/// <summary>Summary DTO — used in list-of-lists responses (no items included).</summary>
public record MediaListDto(
    int Id,
    int UserId,
    string Name,
    string? Description,
    bool IsOrdered,
    int ItemCount,
    DateTime CreatedAt,
    DateTime UpdatedAt
);

/// <summary>Detail DTO — used in single-list responses (items included).</summary>
public record MediaListDetailDto(
    int Id,
    int UserId,
    string Name,
    string? Description,
    bool IsOrdered,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    List<MediaListItemDto> Items
);

public record MediaListItemDto(
    int Id,
    int Position,
    string? Notes,
    DateTime AddedAt,
    MediaItemDto MediaItem
);
