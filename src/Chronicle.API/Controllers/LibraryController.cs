using System.Security.Claims;
using System.Text.Json;
using Chronicle.API.DTOs;
using Chronicle.API.Helpers;
using Chronicle.Core.Exceptions;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.API.Controllers
{
    [ApiController]
    [Route("api/v1/library")]
    [Authorize]
    public class LibraryController : ControllerBase
    {
        private readonly ILibraryService _libraryService;
        private readonly ChronicleDbContext _context;
        private readonly IUserService _userService;
        private readonly IMovieCollectionService _movieCollectionService;

        // Matches auto-generated placeholder episode names like "S01E01" or "S01E339" --
        // scanners/sync fall back to these when no real per-episode title is known.
        private static readonly System.Text.RegularExpressions.Regex GenericEpisodeCodeRegex =
            new(@"^S\d{1,3}E\d{1,4}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase
                | System.Text.RegularExpressions.RegexOptions.Compiled);

        public LibraryController(ILibraryService libraryService, ChronicleDbContext context, IUserService userService,
            IMovieCollectionService movieCollectionService)
        {
            _libraryService = libraryService;
            _context = context;
            _userService = userService;
            _movieCollectionService = movieCollectionService;
        }

        [HttpPost]
        public async Task<IActionResult> Add([FromBody] AddToLibraryRequestDto request, CancellationToken ct)
        {
            var userId = GetUserId();
            if (!Enum.TryParse<LibraryStatus>(request.Status, out var status))
                return BadRequest(ApiResponse<LibraryEntryDto>.Fail("INVALID_STATUS", $"Unknown status '{request.Status}'."));

            var entry = await _libraryService.AddAsync(userId, new AddToLibraryRequest(request.MediaItemId, status));
            var fallbackPoster = await GetFallbackPosterIfNeededAsync(entry.MediaItem, ct);
            return Ok(ApiResponse<LibraryEntryDto>.Ok(ToDto(entry, fallbackPosterUrl: fallbackPoster)));
        }

        [HttpGet]
        public async Task<IActionResult> GetLibrary(
            [FromQuery] string? status,
            [FromQuery] int page = 1,
            [FromQuery] int perPage = 20,
            [FromQuery] bool rootOnly = false,
            [FromQuery] bool includeMoviesInCollections = false,
            CancellationToken ct = default)
        {
            var userId = GetUserId();
            LibraryStatus? parsedStatus = null;

            if (!string.IsNullOrEmpty(status))
            {
                if (!Enum.TryParse<LibraryStatus>(status, out var s))
                    return BadRequest(ApiResponse<List<LibraryEntryDto>>.Fail("INVALID_STATUS", $"Unknown status '{status}'."));
                parsedStatus = s;
            }

            var prefs = await _userService.GetPreferencesAsync(userId);
            var includeStubs = prefs.CreateCollectionStubs ?? true;

            var entries = await _libraryService.GetForUserAsync(userId, parsedStatus, page, perPage, rootOnly, includeMoviesInCollections, includeStubs, ct);

            // Batch-fetch descendant MetadataJson for all root items in two queries
            // (direct children + grandchildren) to avoid N+1 when computing physical-file flags.
            var rootIds = entries
                .Where(e => e.MediaItem != null)
                .Select(e => e.MediaItem!.Id)
                .ToList();

            Dictionary<int, List<string?>> directChildrenByRoot = new();
            Dictionary<int, List<string?>> grandchildrenByRoot = new();

            if (rootIds.Count > 0)
            {
                var directChildren = await _context.MediaItems
                    .Where(m => m.ParentId != null && rootIds.Contains(m.ParentId.Value))
                    .Select(m => new { m.Id, m.ParentId, m.MetadataJson })
                    .ToListAsync(ct);

                foreach (var c in directChildren)
                {
                    var pid = c.ParentId!.Value;
                    if (!directChildrenByRoot.TryGetValue(pid, out var list))
                        directChildrenByRoot[pid] = list = new List<string?>();
                    list.Add(c.MetadataJson);
                }

                var directChildIds = directChildren.Select(c => c.Id).ToList();
                if (directChildIds.Count > 0)
                {
                    // Map grandchildren back to the root item via the direct child's parent.
                    var directChildToRoot = directChildren.ToDictionary(c => c.Id, c => c.ParentId!.Value);

                    var grandchildren = await _context.MediaItems
                        .Where(m => m.ParentId != null && directChildIds.Contains(m.ParentId.Value))
                        .Select(m => new { m.ParentId, m.MetadataJson })
                        .ToListAsync(ct);

                    foreach (var gc in grandchildren)
                    {
                        if (!directChildToRoot.TryGetValue(gc.ParentId!.Value, out var rootId)) continue;
                        if (!grandchildrenByRoot.TryGetValue(rootId, out var list))
                            grandchildrenByRoot[rootId] = list = new List<string?>();
                        list.Add(gc.MetadataJson);
                    }
                }
            }

            // For an entry tracked above the leaf level (e.g. a TV season, not a specific
            // episode — the common case for "Watching" status), find the most recently
            // scrobbled descendant episode so the UI can show "Show › Season › Episode"
            // instead of just the season's bare name, which by itself tells the user nothing.
            //
            // HierarchyLevel > 0 excludes the root of the tree on purpose. The root is the
            // item's own identity (a Show, a Movie Collection, an Author) and its name is
            // never uninformative the way an intermediate "Season 07" is -- substituting it
            // away replaced "Vernor Vinge" with whichever book he last finished, while the
            // ancestor breadcrumb (computed from that substitute book's own parent chain)
            // still correctly said "Vernor Vinge" above it. Confirmed bug, 2026-08-25.
            var nonLeafRootIds = entries
                .Where(e => e.MediaItem != null &&
                            e.MediaItem.HierarchyLevel > 0 &&
                            e.MediaItem.HierarchyLevel < (e.MediaItem.MediaType?.HierarchyLevels ?? 1) - 1)
                .Select(e => e.MediaItem!.Id)
                .ToList();

            var displayIdByRoot = rootIds.ToDictionary(id => id, id => id);
            var displayNameByRoot = new Dictionary<int, string>();

            if (nonLeafRootIds.Count > 0)
            {
                var descendants = await DescendantHelper.BuildDescendantsBatchAsync(_context, nonLeafRootIds, ct);
                var allDescendantIds = descendants.DescendantIdsByRoot.Values.SelectMany(x => x).Distinct().ToList();

                if (allDescendantIds.Count > 0)
                {
                    // A descendant can belong to more than one tracked root at once — e.g. the
                    // user has both a show AND one of its own seasons marked "Watching", so an
                    // episode of that season is a descendant of both roots. Group (not a flat
                    // dictionary) so that's handled instead of throwing on the duplicate key.
                    var rootsByDescendant = descendants.DescendantIdsByRoot
                        .SelectMany(kv => kv.Value.Select(id => new { DescendantId = id, RootId = kv.Key }))
                        .ToLookup(x => x.DescendantId, x => x.RootId);

                    var recentEvents = await _context.InteractionEvents
                        .Where(ev => ev.UserId == userId && allDescendantIds.Contains(ev.MediaItemId))
                        .Select(ev => new { ev.MediaItemId, ev.Timestamp })
                        .ToListAsync(ct);

                    var bestTimestampByRoot = new Dictionary<int, DateTime>();
                    foreach (var ev in recentEvents)
                    {
                        if (!descendants.NameById.TryGetValue(ev.MediaItemId, out var name)) continue;

                        // Skip generic auto-numbered names ("S01E01", "S01E339") -- these carry
                        // no information a bare episode/season entry doesn't already have, and
                        // substituting one meaningless label for another (the tracked root's own
                        // name, e.g. "One Piece", is already meaningful) just replaces a clear
                        // show name with a confusing raw code. Only substitute when the
                        // descendant has a real title (e.g. "Children Of The Comet").
                        if (GenericEpisodeCodeRegex.IsMatch(name)) continue;

                        foreach (var rootId in rootsByDescendant[ev.MediaItemId])
                        {
                            if (bestTimestampByRoot.TryGetValue(rootId, out var best) && ev.Timestamp <= best)
                                continue;
                            bestTimestampByRoot[rootId] = ev.Timestamp;
                            displayIdByRoot[rootId] = ev.MediaItemId;
                            displayNameByRoot[rootId] = name;
                        }
                    }
                }
            }

            // Ancestor context (e.g. "Show" for a season-level entry, "Show, Season" for an
            // episode) — without this, entries like a season being tracked as "Watching" show
            // only its own bare name ("Season 07") with no indication of which show it's from.
            // Batched against the DISPLAY id (the substitute episode when one was found above,
            // otherwise the tracked item itself) so the breadcrumb matches whatever name is shown.
            var ancestorsByItem = await AncestorHelper.BuildAncestorsBatchAsync(
                _context, displayIdByRoot.Values, ct);

            // Collection containers with no poster of their own need a fallback -- same rule as
            // the collection detail page and Kodi's set-art fallback
            // (IMovieCollectionService.GetFallbackPosterAsync). Computed here, once per
            // posterless container on this page rather than inside ToDto, so entries that don't
            // need it (the overwhelming majority) cost nothing extra.
            var fallbackPosterByRoot = new Dictionary<int, string?>();
            foreach (var e in entries)
            {
                if (e.MediaItem is null || e.MediaItem.HierarchyLevel != 0) continue;
                if (!string.IsNullOrEmpty(e.MediaItem.PosterUrl)) continue;
                if (!IsMovieLikeTypeName(e.MediaItem.MediaType?.Name)) continue;
                if (!directChildrenByRoot.TryGetValue(e.MediaItem.Id, out var dc) || dc.Count == 0) continue;
                fallbackPosterByRoot[e.MediaItem.Id] =
                    await _movieCollectionService.GetFallbackPosterAsync(_context, e.MediaItem.Id, ct);
            }

            var dtos = entries.Select(e =>
            {
                List<string?>? dc = null;
                List<string?>? gc = null;
                List<AncestorDto>? ancestors = null;
                string? displayName = null;
                string? fallbackPosterUrl = null;
                if (e.MediaItem != null)
                {
                    directChildrenByRoot.TryGetValue(e.MediaItem.Id, out dc);
                    grandchildrenByRoot.TryGetValue(e.MediaItem.Id, out gc);
                    var displayId = displayIdByRoot.GetValueOrDefault(e.MediaItem.Id, e.MediaItem.Id);
                    ancestorsByItem.TryGetValue(displayId, out ancestors);
                    displayNameByRoot.TryGetValue(e.MediaItem.Id, out displayName);
                    fallbackPosterByRoot.TryGetValue(e.MediaItem.Id, out fallbackPosterUrl);
                }
                return ToDto(e, dc, gc, ancestors, displayName, fallbackPosterUrl);
            }).ToList();

            return Ok(ApiResponse<List<LibraryEntryDto>>.Ok(dtos, new PaginationInfo(page, perPage, null)));
        }

        [HttpPatch("{id:int}")]
        public async Task<IActionResult> Update(int id, [FromBody] UpdateLibraryRequestDto request, CancellationToken ct)
        {
            var userId = GetUserId();
            LibraryStatus? parsedStatus = null;

            if (!string.IsNullOrEmpty(request.Status))
            {
                if (!Enum.TryParse<LibraryStatus>(request.Status, out var s))
                    return BadRequest(ApiResponse<LibraryEntryDto>.Fail("INVALID_STATUS", $"Unknown status '{request.Status}'."));
                parsedStatus = s;
            }

            try
            {
                var entry = await _libraryService.UpdateAsync(userId, id, new UpdateLibraryRequest(parsedStatus, request.UserRating, request.Notes));
                var fallbackPoster = await GetFallbackPosterIfNeededAsync(entry.MediaItem, ct);
                return Ok(ApiResponse<LibraryEntryDto>.Ok(ToDto(entry, fallbackPosterUrl: fallbackPoster)));
            }
            catch (LibraryEntryNotFoundException ex)
            {
                return NotFound(ApiResponse<LibraryEntryDto>.Fail("ENTRY_NOT_FOUND", ex.Message));
            }
        }

        [HttpDelete("{id:int}")]
        public async Task<IActionResult> Remove(int id)
        {
            var userId = GetUserId();
            try
            {
                await _libraryService.RemoveAsync(userId, id);
                return NoContent();
            }
            catch (LibraryEntryNotFoundException ex)
            {
                return NotFound(ApiResponse<object>.Fail("ENTRY_NOT_FOUND", ex.Message));
            }
        }

        [HttpDelete("all")]
        public async Task<IActionResult> ClearAll(CancellationToken ct)
        {
            var userId = GetUserId();
            var removed = await _libraryService.ClearAllAsync(userId, ct);
            return Ok(ApiResponse<object>.Ok(new { removedItems = removed }));
        }

        [HttpPost("reset")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> NuclearReset(
            [FromBody] NuclearResetRequestDto request,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(request.ConfirmationToken))
                return BadRequest(ApiResponse<object>.Fail(
                    "MISSING_TOKEN", "Confirmation token is required."));

            try
            {
                var count = await _libraryService.NuclearResetAsync(request.ConfirmationToken, ct);
                return Ok(ApiResponse<object>.Ok(new { deleted = count }));
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ApiResponse<object>.Fail("INVALID_TOKEN", ex.Message));
            }
        }

        [HttpPost("clear-scanner-data")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> ClearScannerData(CancellationToken ct)
        {
            var count = await _libraryService.ClearScannerDataAsync(ct);
            return Ok(ApiResponse<object>.Ok(new { deleted = count }));
        }

        private int GetUserId() =>
            int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        /// <summary>
        /// Returns true when the given MetadataJson contains a fileScanner entry with at least
        /// one non-null file path.  Mirrors the same helper in MediaController.
        /// </summary>
        private static bool IsMovieLikeTypeName(string? name) =>
            name is not null &&
            (name.Equals("movies",       StringComparison.OrdinalIgnoreCase) ||
             name.Equals("fanedits",     StringComparison.OrdinalIgnoreCase) ||
             name.Equals("anime_movies", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// Single-item counterpart to GetLibrary's batched fallback-poster lookup, for the
        /// Add/Update actions that return one freshly-changed LibraryEntryDto rather than a
        /// page of them. Null unless <paramref name="item"/> is genuinely a posterless
        /// collection container -- same rule as everywhere else a collection's poster can be
        /// shown (IMovieCollectionService.GetFallbackPosterAsync).
        /// </summary>
        private async Task<string?> GetFallbackPosterIfNeededAsync(MediaItem? item, CancellationToken ct)
        {
            if (item is null || item.HierarchyLevel != 0) return null;
            if (!string.IsNullOrEmpty(item.PosterUrl)) return null;
            if (!IsMovieLikeTypeName(item.MediaType?.Name)) return null;
            var hasChildren = await _context.MediaItems.AnyAsync(m => m.ParentId == item.Id, ct);
            if (!hasChildren) return null;
            return await _movieCollectionService.GetFallbackPosterAsync(_context, item.Id, ct);
        }

        private static double? ExtractResolvedRating(string? metadataJson)
        {
            if (string.IsNullOrEmpty(metadataJson)) return null;
            try
            {
                using var doc = JsonDocument.Parse(metadataJson);
                if (doc.RootElement.TryGetProperty("_resolved", out var r) &&
                    r.TryGetProperty("rating", out var ratingEl) &&
                    ratingEl.ValueKind == JsonValueKind.Number)
                    return ratingEl.GetDouble();
            }
            catch { /* ignore */ }
            return null;
        }

        // Delegates to the single canonical reader (Chronicle.Services.Scan.FileIdentityJson) --
        // this used to be its own hand-rolled copy (noted as "mirrors the same helper in
        // MediaController", which had already drifted from a THIRD copy in ScraperController),
        // and it never checked scraperResolvedFile, so an item Kodi had confirmed a real file
        // for -- but that Chronicle's own scanner never touched -- showed as "Missing" here
        // regardless. It also carried a dead "fileScanner.filePath" (singular) fallback that no
        // writer anywhere in the codebase has ever produced -- FileIdentityJson never checked it
        // either, and dropping it here changes nothing reachable.
        private static bool HasFileScannerData(string? metadataJson) =>
            Chronicle.Services.Scan.FileIdentityJson.HasKnownFile(metadataJson);

        // displayName, when set, overrides the item's own Name with a more specific
        // recently-watched descendant episode's name (e.g. "Children Of The Comet" instead
        // of "Season 01" for a season tracked as Watching) — see the caller in GetLibrary.
        private static LibraryEntryDto ToDto(
            UserLibrary e,
            List<string?>? directChildrenMeta = null,
            List<string?>? grandchildrenMeta = null,
            List<AncestorDto>? ancestors = null,
            string? displayName = null,
            // Precomputed by the caller (IMovieCollectionService.GetFallbackPosterAsync) only
            // for entries that are actually posterless collection containers -- keeps ToDto
            // itself synchronous rather than threading a DbContext/service call through every
            // entry on the page. Same rule everywhere a collection's poster can be shown: the
            // Kodi scraper's set-art fallback and the collection detail page use the identical
            // shared method.
            string? fallbackPosterUrl = null)
        {
            MediaItemDto? mediaDto = null;
            if (e.MediaItem != null)
            {
                // Compute physical-file indicators using the same leaf-level logic as MediaController.
                bool hasOwnFile = HasFileScannerData(e.MediaItem.MetadataJson);
                bool childrenHaveFile;

                if (grandchildrenMeta?.Count > 0)
                {
                    childrenHaveFile = grandchildrenMeta.Any(HasFileScannerData);
                }
                else if (directChildrenMeta?.Count > 0)
                {
                    childrenHaveFile = directChildrenMeta.Any(HasFileScannerData);
                }
                else
                {
                    childrenHaveFile = false;
                }

                bool hasPhysicalFile = hasOwnFile || childrenHaveFile;
                // Per-user correction (2026-08-30): "MISSING" now means no physical file
                // anywhere in the item's subtree, not "some part of it is incomplete" -- see
                // MediaController's own ToDto for the full reasoning (same logic here).
                bool hasMetadataOnly = !hasPhysicalFile;

                mediaDto = new MediaItemDto(
                    e.MediaItem.Id, e.MediaItem.MediaTypeId,
                    e.MediaItem.MediaType?.DisplayName ?? string.Empty,
                    e.MediaItem.ParentId, displayName ?? e.MediaItem.Name, e.MediaItem.Year,
                    e.MediaItem.Overview, e.MediaItem.PosterUrl ?? fallbackPosterUrl, e.MediaItem.RuntimeMinutes,
                    e.MediaItem.HierarchyLevel, e.MediaItem.Number,
                    e.MediaItem.CreatedAt, e.MediaItem.UpdatedAt,
                    e.MediaItem.ExternalIds.Select(x => new ExternalIdDto(x.Source, x.ExternalId)).ToList(),
                    HasPhysicalFile: hasPhysicalFile,
                    HasMetadataOnly: hasMetadataOnly,
                    ResolvedMetadata: new ResolvedMetadataDto(
                        Title: null, Overview: null, Year: null,
                        PosterUrl: null, BackdropUrl: null, RuntimeMinutes: null,
                        Rating: ExtractResolvedRating(e.MediaItem.MetadataJson),
                        Genres: null, Cast: null, Crew: null, Tags: null),
                    IsCollectionContainer: e.MediaItem.HierarchyLevel == 0
                        && (e.MediaItem.MediaType?.SupportsCollections ?? false)
                        && directChildrenMeta?.Count > 0,
                    IsStub: e.MediaItem.IsStub,
                    Ancestors: ancestors is { Count: > 0 } ? ancestors : null);
            }

            var userRatingSource = e.UserRating.HasValue
                ? MediaController.ExtractUserRatingSource(e.MediaItem?.MetadataJson, e.UserRating.Value)
                : null;

            return new LibraryEntryDto(
                e.Id, e.UserId, mediaDto!, e.Status.ToString(),
                e.UserRating, userRatingSource, e.Notes, e.AddedAt, e.UpdatedAt,
                e.StartedAt, e.CompletedAt, e.ResumePositionPercent);
        }
    }
}
