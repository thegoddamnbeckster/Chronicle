using Chronicle.API.DTOs;
using Chronicle.Core.Exceptions;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Chronicle.Services.Plugins;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.API.Controllers
{
    [ApiController]
    [Route("api/v1/media")]
    [Authorize]
    public class MediaController : ControllerBase
    {
        private readonly IMediaService _mediaService;
        private readonly IFileScanService _fileScanService;
        private readonly IMetadataEnrichmentService _enrichment;
        private readonly IMetadataContributionService _contributionService;
        private readonly ChronicleDbContext _context;
        private readonly IMergeService _mergeService;
        private readonly IMovieCollectionService _movieCollectionService;
        private readonly IPluginRegistry _pluginRegistry;
        private readonly Chronicle.Services.Scan.NfoDetailParser _nfoDetailParser;
        private readonly IMetadataResolutionService _resolutionService;
        private readonly OverrideResetProgressService _overrideResetProgress;

        public MediaController(IMediaService mediaService, IFileScanService fileScanService,
            IMetadataEnrichmentService enrichment, IMetadataContributionService contributionService,
            ChronicleDbContext context,
            IMergeService mergeService, IMovieCollectionService movieCollectionService,
            IPluginRegistry pluginRegistry, Chronicle.Services.Scan.NfoDetailParser nfoDetailParser,
            IMetadataResolutionService resolutionService, OverrideResetProgressService overrideResetProgress)
        {
            _mediaService            = mediaService;
            _fileScanService         = fileScanService;
            _enrichment              = enrichment;
            _contributionService     = contributionService;
            _context                 = context;
            _movieCollectionService  = movieCollectionService;
            _pluginRegistry          = pluginRegistry;
            _mergeService    = mergeService;
            _nfoDetailParser = nfoDetailParser;
            _resolutionService = resolutionService;
            _overrideResetProgress = overrideResetProgress;
        }

        [HttpGet("types")]
        public async Task<IActionResult> GetMediaTypes()
        {
            var types = await _context.MediaTypes
                .Where(t => t.IsActive)
                .OrderBy(t => t.DisplayName)
                .Select(t => new MediaTypeDto(t.Id, t.Name, t.DisplayName, t.HierarchyLevels))
                .ToListAsync();
            return Ok(ApiResponse<List<MediaTypeDto>>.Ok(types));
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] CreateMediaItemRequest request)
        {
            var item = await _mediaService.CreateAsync(new Chronicle.Services.CreateMediaRequest(
                request.MediaTypeId,
                request.ParentId,
                request.Name,
                request.Year,
                request.Overview,
                request.PosterUrl,
                request.RuntimeMinutes,
                request.HierarchyLevel,
                request.Number,
                request.IsCollection
            ));

            return CreatedAtAction(nameof(GetById), new { id = item.Id },
                ApiResponse<MediaItemDto>.Ok(ToDto(item)));
        }

        [HttpGet("{id:int}")]
        public async Task<IActionResult> GetById(int id, CancellationToken ct)
        {
            var item = await _mediaService.GetByIdAsync(id, ct);
            if (item == null)
                return NotFound(ApiResponse<MediaItemDto>.Fail("MEDIA_NOT_FOUND", $"Media item {id} not found."));

            var enrichmentRecords = await _enrichment.GetEnrichmentRecordsAsync(id, ct);
            var ancestors = await BuildAncestorsAsync(item.ParentId, ct);
            var enrichmentStatusDict = await GetEnrichmentStatusDictAsync(id, ct);

            // Fetch MetadataJson for all descendants (children + grandchildren) so we can compute
            // hasPhysicalFile / hasMetadataOnly for parent items (TV shows, artists, etc.) that
            // don't own files themselves. Chronicle's deepest hierarchy is 3 levels
            // (Show→Season→Episode or Artist→Album→Track), so two levels covers all cases.
            var (directChildrenMeta, grandchildrenMeta) = await GetDescendantMetaAsync(id, ct);

            return Ok(ApiResponse<MediaItemDto>.Ok(ToDto(item, enrichmentRecords, ancestors.Count > 0 ? ancestors : null, enrichmentStatusDict, directChildrenMeta, grandchildrenMeta)));
        }

        [HttpGet("search")]
        public async Task<IActionResult> Search(
            [FromQuery] string? query,
            [FromQuery] int? mediaTypeId,
            [FromQuery] int page = 1,
            [FromQuery] int perPage = 20,
            [FromQuery] bool allLevels = false)
        {
            var results = await _mediaService.SearchAsync(query ?? string.Empty, mediaTypeId, page, perPage, allLevels);
            var dtos = results.Select(m => ToDto(m)).ToList();
            return Ok(ApiResponse<List<MediaItemDto>>.Ok(dtos, new PaginationInfo(page, perPage, null)));
        }

        // ── GET /api/v1/media/{id}/local-poster ──────────────────────────────────

        /// <summary>
        /// Serves the local poster image found alongside the item's media file by the
        /// file scanner.  The path comes from the database (never from user input) and
        /// is validated to exist on disk before the bytes are sent.
        /// </summary>
        [HttpGet("{id:int}/local-poster")]
        [AllowAnonymous] // Poster images are not sensitive
        public async Task<IActionResult> GetLocalPoster(int id, CancellationToken ct)
        {
            var item = await _context.MediaItems.FindAsync([id], ct);
            if (item is null) return NotFound();

            var (fs, _) = ParseMetaJson(item.MetadataJson);
            var posterPath = fs?.LocalPosterPath;

            if (string.IsNullOrEmpty(posterPath)) return NotFound();
            if (!System.IO.File.Exists(posterPath)) return NotFound();

            // Restrict to known image extensions — the path comes from our own DB
            // but we still reject anything that isn't a recognised raster image.
            var ext = Path.GetExtension(posterPath).ToLowerInvariant();
            var contentType = ext switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".png"            => "image/png",
                ".webp"           => "image/webp",
                ".gif"            => "image/gif",
                _                 => null,
            };
            if (contentType is null) return NotFound();

            var bytes = await System.IO.File.ReadAllBytesAsync(posterPath, ct);
            return File(bytes, contentType);
        }

        // ── GET /api/v1/media/{id}/nfo ────────────────────────────────────────────

        /// <summary>
        /// Parses the rich display fields (plot, cast, genres, rating, etc.) from the
        /// .nfo sidecar found alongside the item's media file by the file scanner.
        /// The path comes from the database (never from user input) and is validated
        /// to exist on disk and end in .nfo before being parsed.
        /// </summary>
        [HttpGet("{id:int}/nfo")]
        public async Task<IActionResult> GetNfoDetail(int id, CancellationToken ct)
        {
            var item = await _context.MediaItems.FindAsync([id], ct);
            if (item is null) return NotFound();

            var (fs, _) = ParseMetaJson(item.MetadataJson);
            var nfoPath = fs?.NfoPath;

            if (string.IsNullOrEmpty(nfoPath)) return NotFound();
            if (!string.Equals(Path.GetExtension(nfoPath), ".nfo", StringComparison.OrdinalIgnoreCase))
                return NotFound();
            if (!System.IO.File.Exists(nfoPath)) return NotFound();

            var detail = _nfoDetailParser.Parse(nfoPath);
            if (detail is null) return NotFound();

            return Ok(ApiResponse<Chronicle.Services.Scan.NfoDetail>.Ok(detail));
        }

        [HttpGet("{id:int}/children")]
        public async Task<IActionResult> GetChildren(int id, CancellationToken ct)
        {
            var childrenSeq = await _mediaService.GetChildrenAsync(id, ct);
            var children = childrenSeq.ToList();
            if (children.Count == 0)
                return Ok(ApiResponse<List<MediaItemDto>>.Ok([]));

            // Batch-fetch enrichment statuses for all children in a single query.
            var childIds = children.Select(c => c.Id).ToList();
            var enrichmentRows = await _context.MediaEnrichments
                .Where(e => childIds.Contains(e.MediaItemId))
                .Select(e => new { e.MediaItemId, e.PluginId, Status = e.Status.ToString() })
                .ToListAsync(ct);

            var enrichmentByChild = enrichmentRows
                .GroupBy(e => e.MediaItemId)
                .ToDictionary(
                    g => g.Key,
                    g => g.ToDictionary(e => e.PluginId, e => e.Status));

            var dtos = children.Select(m =>
            {
                enrichmentByChild.TryGetValue(m.Id, out var statuses);
                return ToDto(m, enrichmentStatuses: statuses?.Count > 0 ? statuses : null);
            }).ToList();

            return Ok(ApiResponse<List<MediaItemDto>>.Ok(dtos));
        }

        [HttpPatch("{id:int}")]
        public async Task<IActionResult> Update(int id, [FromBody] UpdateMediaItemRequest request)
        {
            try
            {
                var item = await _mediaService.UpdateAsync(id, new Chronicle.Services.UpdateMediaRequest(
                    request.Name, request.Year, request.Overview, request.PosterUrl, request.RuntimeMinutes), HttpContext.RequestAborted);
                return Ok(ApiResponse<MediaItemDto>.Ok(ToDto(item)));
            }
            catch (MediaNotFoundException ex)
            {
                return NotFound(ApiResponse<MediaItemDto>.Fail("MEDIA_NOT_FOUND", ex.Message));
            }
        }

        [HttpPost("{id:int}/refresh")]
        public async Task<IActionResult> RefreshMetadata(int id, CancellationToken ct)
        {
            try
            {
                await _enrichment.EnrichItemAsync(id,
                    new EnrichmentOptions(EnrichmentMode.Force, Cascade: true), ct);
                var item = await _mediaService.GetByIdAsync(id, ct);
                if (item == null)
                    return NotFound(ApiResponse<MediaItemDto>.Fail("MEDIA_NOT_FOUND", $"Media item {id} not found."));
                var enrichmentRecords = await _enrichment.GetEnrichmentRecordsAsync(id, ct);
                var ancestors = await BuildAncestorsAsync(item.ParentId, ct);
                var enrichmentStatuses = await GetEnrichmentStatusDictAsync(id, ct);
                var (directChildrenMeta, grandchildrenMeta) = await GetDescendantMetaAsync(id, ct);
                return Ok(ApiResponse<MediaItemDto>.Ok(ToDto(item, enrichmentRecords, ancestors.Count > 0 ? ancestors : null, enrichmentStatuses, directChildrenMeta, grandchildrenMeta)));
            }
            catch (Exception ex)
            {
                return StatusCode(502, ApiResponse<MediaItemDto>.Fail("REFRESH_FAILED", ex.Message));
            }
        }

        /// <summary>
        /// Refreshes metadata for a single item from a specific plugin.
        /// If a body with <c>input</c> is supplied, performs a Fix Match (overrides external ID lookup).
        /// If no body / null input, re-fetches using the item's existing stored external ID.
        /// </summary>
        [HttpPost("{id:int}/refresh/{pluginId}")]
        public async Task<IActionResult> RefreshForPlugin(
            int id,
            string pluginId,
            [FromBody] PluginRefreshRequestDto? dto,
            CancellationToken ct)
        {
            try
            {
                var opts = new EnrichmentOptions(
                    EnrichmentMode.Force,
                    IdOverride: string.IsNullOrWhiteSpace(dto?.Input) ? null : dto.Input.Trim(),
                    Cascade: false);
                await _enrichment.EnrichItemAsync(id, pluginId, opts, ct);
                var item = await _mediaService.GetByIdAsync(id, ct);
                if (item == null)
                    return NotFound(ApiResponse<MediaItemDto>.Fail("MEDIA_NOT_FOUND", $"Media item {id} not found."));
                var enrichmentRecords = await _enrichment.GetEnrichmentRecordsAsync(id, ct);
                var ancestors = await BuildAncestorsAsync(item.ParentId, ct);
                var enrichmentStatuses = await GetEnrichmentStatusDictAsync(id, ct);
                var (refreshedDirectMeta, refreshedGrandchildMeta) = await GetDescendantMetaAsync(id, ct);
                return Ok(ApiResponse<MediaItemDto>.Ok(ToDto(item, enrichmentRecords, ancestors.Count > 0 ? ancestors : null, enrichmentStatuses, refreshedDirectMeta, refreshedGrandchildMeta)));
            }
            catch (MediaNotFoundException ex)
            {
                return NotFound(ApiResponse<MediaItemDto>.Fail("MEDIA_NOT_FOUND", ex.Message));
            }
            catch (Chronicle.Plugins.PluginAuthException ex)
            {
                return StatusCode(422, new
                {
                    success = false,
                    error = new { code = "PLUGIN_AUTH_FAILED", pluginId = ex.PluginId, message = ex.Message }
                });
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ApiResponse<MediaItemDto>.Fail("PLUGIN_NOT_FOUND", ex.Message));
            }
            catch (Exception ex)
            {
                return StatusCode(502, ApiResponse<MediaItemDto>.Fail("REFRESH_FAILED", ex.Message));
            }
        }

        /// <summary>
        /// Lets an authenticated external caller contribute metadata fields for an item —
        /// not tied to any specific integration, following Chronicle's lossless-ingestion
        /// principle. The contribution lands in its own metadata_json partition keyed by
        /// <paramref name="source"/>; every other source's data is left untouched.
        /// </summary>
        [HttpPost("{id:int}/metadata/{source}")]
        public async Task<IActionResult> ContributeMetadata(
            int id, string source, [FromBody] ContributeMetadataRequest request, CancellationToken ct)
        {
            var item = await _context.MediaItems
                .Include(m => m.MediaType)
                .FirstOrDefaultAsync(m => m.Id == id, ct);
            if (item is null)
                return NotFound(ApiResponse<object>.Fail("MEDIA_NOT_FOUND", $"Media item {id} not found."));

            var fileSnapshot = request.File is null ? null : new Chronicle.Services.Scan.FileIdentitySnapshot(
                request.File.SizeBytes, request.File.ModifiedUtc, request.File.BitrateKbps,
                request.File.SampleRateHz, request.File.DurationSeconds, request.File.FileType);

            var outcome = await _contributionService.ContributeAsync(
                item, _context, source, request.Metadata, fileSnapshot, ct);

            if (!outcome.Success)
                return BadRequest(ApiResponse<object>.Fail(outcome.ErrorCode!, outcome.ErrorMessage!));

            return Ok(ApiResponse<ContributeMetadataResponseDto>.Ok(new ContributeMetadataResponseDto(
                outcome.FingerprintChanged, outcome.TagMismatchDetected, outcome.RematchQueued)));
        }

        /// <summary>
        /// Removes a specific external ID (e.g. TMDB match) from a media item without
        /// deleting the item itself.  Also clears the corresponding metadata from MetadataJson.
        /// </summary>
        [HttpDelete("{id:int}/external-ids/{source}")]
        public async Task<IActionResult> ClearExternalId(int id, string source, CancellationToken ct)
        {
            var item = await _context.MediaItems
                .Include(m => m.ExternalIds)
                .Include(m => m.MediaType)
                .FirstOrDefaultAsync(m => m.Id == id, ct);

            if (item is null)
                return NotFound(ApiResponse<object>.Fail("MEDIA_NOT_FOUND", $"Media item {id} not found."));

            // Callers may pass either the short source name ("fanedit") or the full plugin ID
            // ("chronicle.plugin.fanedit"). Normalise to the short form for ExternalIds lookup,
            // and keep the full form for the MetadataJson key (which uses the full plugin ID).
            var shortSource = source.Contains('.') ? source[(source.LastIndexOf('.') + 1)..] : source;

            var toRemove = item.ExternalIds
                .Where(e => string.Equals(e.Source, shortSource, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (toRemove.Count == 0)
                return NoContent(); // already absent — idempotent

            _context.MediaExternalIds.RemoveRange(toRemove);

            // Strip the provider's block from MetadataJson so stale data doesn't linger.
            // MetadataJson is keyed by full plugin ID; also try the short source name for
            // items stored under the old flat format.
            if (!string.IsNullOrWhiteSpace(item.MetadataJson))
            {
                try
                {
                    var root = System.Text.Json.JsonSerializer.Deserialize<
                        System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>>(item.MetadataJson);
                    if (root is not null)
                    {
                        root.Remove(source.ToLowerInvariant());      // full plugin ID key
                        root.Remove(shortSource.ToLowerInvariant()); // short source key
                        item.MetadataJson = System.Text.Json.JsonSerializer.Serialize(root);
                    }
                }
                catch { /* malformed JSON — leave as-is */ }
            }

            // The external ID that just disappeared may be the only thing an "artwork-only"
            // provider (e.g. Fanart.tv) had to cross-reference — its own blob/enrichment row
            // never gets cleared by the block above since it's keyed under a DIFFERENT plugin
            // ID. Without this, stale poster/backdrop/disc/logo art from a since-corrected bad
            // match keeps displaying indefinitely. See ClearArtworkOnlyProviderDataAsync.
            await RemoveExternalIdsForUnsupportedTypeAsync(item, ct);
            await ClearArtworkOnlyProviderDataAsync(item, ct);

            ClearProviderMetadata(item);
            item.UpdatedAt = DateTime.UtcNow;
            await _resolutionService.ResolveAsync(item, _context, ct);
            await _context.SaveChangesAsync(ct);

            return NoContent();
        }

        /// <summary>
        /// Pins a manually-chosen value for one canonical field (e.g. "poster_url") on this
        /// item — it wins over the plugin-priority resolution walk in every future
        /// Refresh/Clear-Match/sync/merge, until explicitly cleared. Returns the fully
        /// re-resolved item so the caller can update its cache without a follow-up GET.
        /// </summary>
        [HttpPut("{id:int}/overrides/{field}")]
        public async Task<IActionResult> SetOverride(
            int id, string field, [FromBody] SetMediaOverrideRequest request, CancellationToken ct)
        {
            var item = await _context.MediaItems
                .Include(m => m.MediaType).Include(m => m.ExternalIds).Include(m => m.Aliases)
                .FirstOrDefaultAsync(m => m.Id == id, ct);
            if (item is null)
                return NotFound(ApiResponse<MediaItemDto>.Fail("MEDIA_NOT_FOUND", $"Media item {id} not found."));

            if (!_resolutionService.GetCanonicalFields().Contains(field))
                return BadRequest(ApiResponse<MediaItemDto>.Fail("INVALID_FIELD", $"'{field}' is not an assignable field."));

            try
            {
                await _resolutionService.SetOverrideAsync(
                    item, _context, field, request.Url, request.SourcePluginId, request.SourceType,
                    GetCurrentUserId(), ct);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ApiResponse<MediaItemDto>.Fail("INVALID_FIELD", ex.Message));
            }

            item.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);

            return Ok(ApiResponse<MediaItemDto>.Ok(ToDto(item)));
        }

        /// <summary>Clears one field's override on this item (idempotent). Returns the re-resolved item.</summary>
        [HttpDelete("{id:int}/overrides/{field}")]
        public async Task<IActionResult> ClearOverride(int id, string field, CancellationToken ct)
        {
            var item = await _context.MediaItems
                .Include(m => m.MediaType).Include(m => m.ExternalIds).Include(m => m.Aliases)
                .FirstOrDefaultAsync(m => m.Id == id, ct);
            if (item is null)
                return NotFound(ApiResponse<MediaItemDto>.Fail("MEDIA_NOT_FOUND", $"Media item {id} not found."));

            await _resolutionService.ClearOverrideAsync(item, _context, field, ct);
            item.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);

            return Ok(ApiResponse<MediaItemDto>.Ok(ToDto(item)));
        }

        /// <summary>Clears every override on this item. Returns the re-resolved item.</summary>
        [HttpDelete("{id:int}/overrides")]
        public async Task<IActionResult> ClearAllOverrides(int id, CancellationToken ct)
        {
            var item = await _context.MediaItems
                .Include(m => m.MediaType).Include(m => m.ExternalIds).Include(m => m.Aliases)
                .FirstOrDefaultAsync(m => m.Id == id, ct);
            if (item is null)
                return NotFound(ApiResponse<MediaItemDto>.Fail("MEDIA_NOT_FOUND", $"Media item {id} not found."));

            await _resolutionService.ClearItemOverridesAsync(item, _context, ct);
            item.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync(ct);

            return Ok(ApiResponse<MediaItemDto>.Ok(ToDto(item)));
        }

        /// <summary>
        /// Clears every image/field override for every item of the given media type. The
        /// library can be large (tens of thousands of items), so this runs as a background
        /// job — poll GET /media/overrides/reset-progress for status. 409 if one is already running.
        /// </summary>
        [HttpPost("overrides/reset-media-type/{mediaTypeId:int}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> ResetOverridesForMediaType(int mediaTypeId, CancellationToken ct)
        {
            if (_overrideResetProgress.GetSnapshot().IsRunning)
                return Conflict(ApiResponse<object>.Fail("OVERRIDE_RESET_RUNNING", "An override reset is already in progress."));

            var mediaType = await _context.MediaTypes.FindAsync([mediaTypeId], ct);
            if (mediaType is null)
                return NotFound(ApiResponse<object>.Fail("MEDIA_TYPE_NOT_FOUND", $"Media type {mediaTypeId} not found."));

            var typeName = mediaType.Name;
            _overrideResetProgress.Start($"media type '{mediaType.DisplayName}'");

            // Safe to run after this request completes — ClearOverridesForMediaTypeAsync
            // re-scopes its own DbContext internally per batch (same pattern already
            // established by ResolveAllForMediaTypeAsync, see SettingsController).
            _ = Task.Run(async () =>
            {
                try
                {
                    await _resolutionService.ClearOverridesForMediaTypeAsync(typeName,
                        (processed, cleared) => _overrideResetProgress.UpdateProgress(processed, cleared),
                        CancellationToken.None);
                    _overrideResetProgress.Complete();
                }
                catch (Exception ex)
                {
                    _overrideResetProgress.Fail(ex.Message);
                }
            }, CancellationToken.None);

            return Accepted(ApiResponse<object>.Ok(new { started = true }));
        }

        /// <summary>
        /// Clears every image/field override for one item and everything beneath it — a
        /// collection and its members, a show and its seasons/episodes. Runs as a background
        /// job — poll GET /media/overrides/reset-progress for status. 409 if one is already running.
        /// </summary>
        [HttpPost("{id:int}/overrides/reset-subtree")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> ResetOverridesForSubtree(int id, CancellationToken ct)
        {
            if (_overrideResetProgress.GetSnapshot().IsRunning)
                return Conflict(ApiResponse<object>.Fail("OVERRIDE_RESET_RUNNING", "An override reset is already in progress."));

            var item = await _context.MediaItems.FindAsync([id], ct);
            if (item is null)
                return NotFound(ApiResponse<object>.Fail("MEDIA_NOT_FOUND", $"Media item {id} not found."));

            _overrideResetProgress.Start($"\"{item.Name}\" and everything under it");

            // Same fire-and-forget shape as the media-type reset above: the service re-scopes
            // its own DbContext per batch, so it stays safe after this request has responded.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _resolutionService.ClearOverridesForSubtreeAsync(id,
                        (processed, cleared) => _overrideResetProgress.UpdateProgress(processed, cleared),
                        CancellationToken.None);
                    _overrideResetProgress.Complete();
                }
                catch (Exception ex)
                {
                    _overrideResetProgress.Fail(ex.Message);
                }
            }, CancellationToken.None);

            return Accepted(ApiResponse<object>.Ok(new { started = true }));
        }

        /// <summary>
        /// Clears every image/field override across the entire library. Requires the literal
        /// confirmation token "RESET" (same convention as LibraryController's NuclearReset).
        /// Runs as a background job — poll GET /media/overrides/reset-progress for status.
        /// </summary>
        [HttpPost("overrides/reset-all")]
        [Authorize(Roles = "Admin")]
        public IActionResult ResetAllOverrides([FromBody] NuclearResetRequestDto request)
        {
            if (request.ConfirmationToken != "RESET")
                return BadRequest(ApiResponse<object>.Fail("INVALID_TOKEN", "Confirmation token must be exactly 'RESET'."));

            if (_overrideResetProgress.GetSnapshot().IsRunning)
                return Conflict(ApiResponse<object>.Fail("OVERRIDE_RESET_RUNNING", "An override reset is already in progress."));

            _overrideResetProgress.Start("entire library");

            _ = Task.Run(async () =>
            {
                try
                {
                    await _resolutionService.ClearAllOverridesLibraryWideAsync(
                        (processed, cleared) => _overrideResetProgress.UpdateProgress(processed, cleared),
                        CancellationToken.None);
                    _overrideResetProgress.Complete();
                }
                catch (Exception ex)
                {
                    _overrideResetProgress.Fail(ex.Message);
                }
            }, CancellationToken.None);

            return Accepted(ApiResponse<object>.Ok(new { started = true }));
        }

        /// <summary>Polls the state of the current (or most recent) bulk override reset job.</summary>
        [HttpGet("overrides/reset-progress")]
        [AllowAnonymous]
        public IActionResult GetOverrideResetProgress()
        {
            var s = _overrideResetProgress.GetSnapshot();
            return Ok(ApiResponse<object>.Ok(new
            {
                isRunning = s.IsRunning,
                isComplete = s.IsComplete,
                scope = s.Scope,
                processed = s.Processed,
                cleared = s.Cleared,
                error = s.Error,
            }));
        }

        /// <summary>
        /// Suppresses auto-matching for a specific provider by storing a sentinel external ID.
        /// The metadata refresh service will skip this item for that provider permanently.
        /// Use ClearExternalId (DELETE) to un-suppress and allow auto-matching again.
        /// </summary>
        [HttpPost("{id:int}/suppress/{source}")]
        public async Task<IActionResult> SuppressMatch(int id, string source, CancellationToken ct)
        {
            var item = await _context.MediaItems
                .Include(m => m.ExternalIds)
                .Include(m => m.MediaType)
                .FirstOrDefaultAsync(m => m.Id == id, ct);

            if (item is null)
                return NotFound(ApiResponse<object>.Fail("MEDIA_NOT_FOUND", $"Media item {id} not found."));

            var shortSource = source.Contains('.') ? source[(source.LastIndexOf('.') + 1)..] : source;

            // Remove any existing ID for this source (real or previous suppress sentinel).
            var existing = item.ExternalIds
                .Where(e => string.Equals(e.Source, shortSource, StringComparison.OrdinalIgnoreCase))
                .ToList();
            _context.MediaExternalIds.RemoveRange(existing);

            // Also strip cached metadata for this provider.
            if (!string.IsNullOrWhiteSpace(item.MetadataJson))
            {
                try
                {
                    var root = System.Text.Json.JsonSerializer.Deserialize<
                        System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>>(item.MetadataJson);
                    if (root is not null)
                    {
                        root.Remove(source.ToLowerInvariant());
                        root.Remove(shortSource.ToLowerInvariant());
                        item.MetadataJson = System.Text.Json.JsonSerializer.Serialize(root);
                    }
                }
                catch { /* malformed JSON — leave as-is */ }
            }

            // Store the suppress sentinel using the short source name (matches ExternalIds convention).
            _context.MediaExternalIds.Add(new Chronicle.Core.Models.MediaExternalId
            {
                MediaItemId = item.Id,
                Source      = shortSource.ToLowerInvariant(),
                ExternalId  = "__suppress__"
            });

            // See ClearExternalId — same cross-referenced artwork-only-provider staleness risk.
            await RemoveExternalIdsForUnsupportedTypeAsync(item, ct);
            await ClearArtworkOnlyProviderDataAsync(item, ct);

            ClearProviderMetadata(item);
            item.UpdatedAt = DateTime.UtcNow;
            await _resolutionService.ResolveAsync(item, _context, ct);
            await _context.SaveChangesAsync(ct);

            return NoContent();
        }

        [HttpDelete("{id:int}")]
        public async Task<IActionResult> Delete(int id)
        {
            try
            {
                await _mediaService.DeleteAsync(id, HttpContext.RequestAborted);
                return NoContent();
            }
            catch (MediaNotFoundException ex)
            {
                return NotFound(ApiResponse<object>.Fail("MEDIA_NOT_FOUND", ex.Message));
            }
        }

        /// <summary>
        /// Changes the media type of an item and all its descendants, resetting all
        /// enrichment data, external IDs, and metadata JSON.
        /// Admin only. Must be called on the root item — child items return 400.
        /// </summary>
        [HttpPost("{id:int}/change-type")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> ChangeType(
            int id,
            [FromBody] ChangeMediaTypeRequest body,
            CancellationToken ct)
        {
            try
            {
                await _mediaService.ChangeTypeAsync(id, body.MediaTypeId, ct);
                var updated = await _mediaService.GetByIdAsync(id, ct);
                if (updated == null)
                    return NotFound(ApiResponse<MediaItemDto>.Fail("MEDIA_NOT_FOUND", $"Media item {id} not found."));
                return Ok(ApiResponse<MediaItemDto>.Ok(ToDto(updated)));
            }
            catch (MediaNotFoundException ex)
            {
                return NotFound(ApiResponse<MediaItemDto>.Fail("MEDIA_NOT_FOUND", ex.Message));
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("root"))
            {
                var item = await _context.MediaItems.FindAsync([id], ct);
                return BadRequest(new { success = false,
                    error = new { code = "CHANGE_TYPE_USE_ROOT", message = ex.Message, parentId = item?.ParentId } });
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("incompatible"))
            {
                return BadRequest(ApiResponse<MediaItemDto>.Fail("INCOMPATIBLE_TYPE", ex.Message));
            }
        }

        /// <summary>
        /// Removes a movie/fanedit/anime item from its collection container.
        /// Sets ParentId = null and HierarchyLevel = 0, resets enrichment to Pending,
        /// and deletes the container if it becomes empty. Admin only.
        /// </summary>
        [HttpPost("{id:int}/unparent")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> UnparentFromCollection(int id, CancellationToken ct)
        {
            try
            {
                await _movieCollectionService.UnparentFromCollectionAsync(_context, id, ct);
                var updated = await _mediaService.GetByIdAsync(id, ct);
                if (updated == null)
                    return NotFound(ApiResponse<MediaItemDto>.Fail("MEDIA_NOT_FOUND", $"Media item {id} not found."));
                return Ok(ApiResponse<MediaItemDto>.Ok(ToDto(updated)));
            }
            catch (MediaNotFoundException ex)
            {
                return NotFound(ApiResponse<MediaItemDto>.Fail("MEDIA_NOT_FOUND", ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ApiResponse<MediaItemDto>.Fail("UNPARENT_INVALID", ex.Message));
            }
        }

        /// <summary>
        /// Manually places a standalone item of any media type into an existing collection
        /// container of the same type. Admin only. The caller picks the target explicitly (via
        /// the collection's own page) rather than this being auto-detected from plugin metadata.
        /// </summary>
        [HttpPost("{id:int}/reparent")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Reparent(int id, [FromBody] ReparentRequest body, CancellationToken ct)
        {
            try
            {
                await _movieCollectionService.ReparentIntoCollectionAsync(_context, id, body.CollectionId, ct);
                var updated = await _mediaService.GetByIdAsync(id, ct);
                if (updated == null)
                    return NotFound(ApiResponse<MediaItemDto>.Fail("MEDIA_NOT_FOUND", $"Media item {id} not found."));
                return Ok(ApiResponse<MediaItemDto>.Ok(ToDto(updated)));
            }
            catch (MediaNotFoundException ex)
            {
                return NotFound(ApiResponse<MediaItemDto>.Fail("MEDIA_NOT_FOUND", ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ApiResponse<MediaItemDto>.Fail("REPARENT_INVALID", ex.Message));
            }
        }

        /// <summary>
        /// Every Level-0 item that already has at least one child — i.e. every real collection
        /// container, for the "Add Collection" management page. Scoped to flat media types
        /// (HierarchyLevels == 1) — movies, fanedits, anime_movies, and any other flat type the
        /// operator has configured. Types with a natural multi-level hierarchy (TV Show/Season/
        /// Episode, Music Artist/Album/Track, or "anime" itself) are excluded — their Level-0
        /// items having children is normal structure, not an ad-hoc collection grouping.
        /// </summary>
        [HttpGet("collections")]
        public async Task<IActionResult> GetCollections(CancellationToken ct)
        {
            var flatTypeIds = await _context.MediaTypes
                .Where(t => t.HierarchyLevels == 1)
                .Select(t => t.Id)
                .ToListAsync(ct);

            var collections = await _context.MediaItems
                .Where(m => m.HierarchyLevel == 0 && flatTypeIds.Contains(m.MediaTypeId)
                    && _context.MediaItems.Any(c => c.ParentId == m.Id))
                .OrderBy(m => m.Name)
                .Select(m => new CollectionSummaryDto(
                    m.Id, m.Name, m.PosterUrl,
                    _context.MediaItems.Count(c => c.ParentId == m.Id),
                    m.MediaTypeId))
                .ToListAsync(ct);

            return Ok(ApiResponse<List<CollectionSummaryDto>>.Ok(collections));
        }

        private async Task<List<AncestorDto>> BuildAncestorsAsync(int? parentId, CancellationToken ct)
        {
            var ancestors = new List<AncestorDto>();
            // Chronicle's deepest real hierarchy is 3 levels (Show→Season→Episode or
            // Artist→Album→Track); 10 is a generous ceiling. Also tracks visited IDs so a
            // corrupt ParentId chain (e.g. an item whose ParentId points back to itself, or to
            // one of its own ancestors) can never hang this request forever — it previously did
            // exactly that, spinning on the same row indefinitely with no request timeout.
            var visited = new HashSet<int>();
            while (parentId != null && ancestors.Count < 10 && visited.Add(parentId.Value))
            {
                var ancestor = await _context.MediaItems
                    .Where(m => m.Id == parentId)
                    .Select(m => new { m.Id, m.Name, m.ParentId })
                    .FirstOrDefaultAsync(ct);
                if (ancestor == null) break;
                ancestors.Insert(0, new AncestorDto(ancestor.Id, ancestor.Name));
                parentId = ancestor.ParentId;
            }
            return ancestors;
        }

        private async Task<Dictionary<string, string>?> GetEnrichmentStatusDictAsync(int mediaItemId, CancellationToken ct)
        {
            var rows = await _context.MediaEnrichments
                .Where(e => e.MediaItemId == mediaItemId)
                .Select(e => new { e.PluginId, Status = e.Status.ToString() })
                .ToListAsync(ct);
            return rows.Count > 0 ? rows.ToDictionary(e => e.PluginId, e => e.Status) : null;
        }

        private async Task<(List<string?> DirectChildren, List<string?> Grandchildren)>
            GetDescendantMetaAsync(int id, CancellationToken ct = default)
        {
            var directChildren = await _context.MediaItems
                .Where(m => m.ParentId == id)
                .Select(m => new { m.Id, m.MetadataJson })
                .ToListAsync(ct);

            if (!directChildren.Any())
                return (new List<string?>(), new List<string?>());

            var directChildIds = directChildren.Select(c => c.Id).ToList();

            var grandchildrenMeta = await _context.MediaItems
                .Where(m => m.ParentId != null && directChildIds.Contains(m.ParentId.Value))
                .Select(m => m.MetadataJson)
                .ToListAsync(ct);

            return (directChildren.Select(c => c.MetadataJson).ToList(), grandchildrenMeta);
        }

        private static MediaItemDto ToDto(
            Chronicle.Core.Models.MediaItem m,
            IReadOnlyList<EnrichmentRecord>? enrichmentRecords = null,
            List<AncestorDto>? ancestors = null,
            Dictionary<string, string>? enrichmentStatuses = null,
            List<string?>? directChildrenMeta = null,
            List<string?>? grandchildrenMeta = null)
        {
            var (fs, pluginMeta) = ParseMetaJson(m.MetadataJson);
            // Map enrichment records to RefreshLogDto for frontend compatibility
            var logDtos = enrichmentRecords?
                .Where(r => r.LastCompletedAt.HasValue || r.ErrorMessage is not null)
                .Select(r => new RefreshLogDto(
                    r.PluginId,
                    r.LastCompletedAt ?? DateTime.UtcNow,
                    r.Status == EnrichmentStatus.Completed,
                    r.ErrorMessage))
                .ToList();

            // Compute physical-file indicators.
            // Use only the deepest available level for "missing file" checks so that
            // intermediate nodes (seasons) do not falsely trigger the metadata-only flag.
            bool hasOwnFile = HasFileScannerData(m.MetadataJson);
            bool childrenHaveFile;

            if (grandchildrenMeta?.Count > 0)
            {
                // Grandchildren (episodes/tracks) are the real leaf level — use them exclusively.
                childrenHaveFile = grandchildrenMeta.Any(HasFileScannerData);
            }
            else if (directChildrenMeta?.Count > 0)
            {
                // No grandchildren — use direct children as the leaf level.
                childrenHaveFile = directChildrenMeta.Any(HasFileScannerData);
            }
            else
            {
                childrenHaveFile = false;
            }

            bool hasPhysicalFile = hasOwnFile || childrenHaveFile;
            // Per-user correction (2026-08-30): "why are so many tv shows showing as missing
            // when they aren't?" -- hasMetadataOnly used to also fire for a "mixed" state (some
            // leaves have a file, some don't -- e.g. one not-yet-aired episode out of a whole
            // season), which meant a show with 19/20 episodes present still showed the same
            // "Missing" badge as a show with literally nothing downloaded. Now true only when
            // there's no physical file anywhere in the item's own subtree.
            bool hasMetadataOnly = !hasPhysicalFile;

            ResolvedMetadataDto? resolvedMetadata = null;
            if (!string.IsNullOrEmpty(m.MetadataJson))
            {
                try
                {
                    using var resolvedDoc = System.Text.Json.JsonDocument.Parse(m.MetadataJson);
                    if (resolvedDoc.RootElement.TryGetProperty("_resolved", out var r) &&
                        r.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        resolvedMetadata = new ResolvedMetadataDto(
                            Title:          TryGetString(r, "title"),
                            Overview:       TryGetString(r, "overview"),
                            Year:           TryGetInt(r,    "year"),
                            PosterUrl:      TryGetString(r, "posterUrl"),
                            BackdropUrl:    TryGetString(r, "backdropUrl"),
                            RuntimeMinutes: TryGetInt(r,    "runtimeMinutes"),
                            Rating:         TryGetDouble(r, "rating"),
                            Genres:         TryGetStringList(r, "genres"),
                            Cast:           TryGetCastList(r, "cast"),
                            Crew:           TryGetCrewList(r, "crew"),
                            Tags:           TryGetStringList(r, "tags"),
                            Composer:       TryGetString(r, "composer"),
                            Label:          TryGetString(r, "label"),
                            Bpm:            TryGetDouble(r, "bpm"),
                            Mood:           TryGetString(r, "mood"),
                            Language:       TryGetString(r, "language"),
                            Isrc:           TryGetString(r, "isrc"),
                            LogoUrl:        TryGetString(r, "logoUrl"),
                            BannerUrl:      TryGetString(r, "bannerUrl"),
                            ClearartUrl:    TryGetString(r, "clearartUrl"),
                            DiscUrl:        TryGetString(r, "discUrl"),
                            CharacterArtUrl: TryGetString(r, "characterArtUrl"),
                            ThumbUrl:       TryGetString(r, "thumbUrl")
                        );
                    }
                }
                catch { /* malformed JSON — leave resolvedMetadata null */ }
            }

            Dictionary<string, MediaOverrideDto>? overrides = null;
            if (!string.IsNullOrEmpty(m.MetadataJson))
            {
                try
                {
                    using var overridesDoc = System.Text.Json.JsonDocument.Parse(m.MetadataJson);
                    if (overridesDoc.RootElement.TryGetProperty("_overrides", out var ov) &&
                        ov.ValueKind == System.Text.Json.JsonValueKind.Object)
                    {
                        foreach (var prop in ov.EnumerateObject())
                        {
                            if (prop.Value.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                            var url = TryGetString(prop.Value, "url");
                            if (string.IsNullOrEmpty(url)) continue;
                            overrides ??= new Dictionary<string, MediaOverrideDto>(StringComparer.OrdinalIgnoreCase);
                            overrides[prop.Name] = new MediaOverrideDto(
                                Url:            url,
                                SourcePluginId: TryGetString(prop.Value, "sourcePluginId"),
                                SourceType:     TryGetString(prop.Value, "sourceType"),
                                PinnedAt:       prop.Value.TryGetProperty("pinnedAt", out var pa) &&
                                                pa.ValueKind == System.Text.Json.JsonValueKind.String &&
                                                DateTime.TryParse(pa.GetString(), out var pinnedAt)
                                                    ? pinnedAt : default,
                                PinnedByUserId: TryGetInt(prop.Value, "pinnedByUserId")
                            );
                        }
                    }
                }
                catch { /* malformed JSON — leave overrides null */ }
            }

            // Exclude episode-title aliases (e.g. "Show S01E03 - Title") — these are never
            // real alternate names for a show; they appear when merge absorbs episode stubs.
            var aliases = m.Aliases.Count > 0
                ? m.Aliases
                    .Select(a => a.Alias)
                    .Where(a => !System.Text.RegularExpressions.Regex.IsMatch(a, @"S\d{1,2}E\d{1,2}", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    .ToList()
                : null;
            if (aliases?.Count == 0) aliases = null;
            var mergeHistory = m.MergesAsWinner.Count > 0
                ? m.MergesAsWinner.OrderByDescending(mr => mr.MergedAt)
                    .Select(mr => new MergeHistoryDto(mr.Id, mr.LoserOriginalId, mr.LoserName, mr.MergedAt, mr.MergedByUserId))
                    .ToList()
                : null;

            return new MediaItemDto(
                m.Id,
                m.MediaTypeId,
                m.MediaType?.DisplayName ?? string.Empty,
                m.ParentId,
                m.Name,
                m.Year,
                m.Overview,
                EffectivePosterUrl(m.Id, m.PosterUrl, fs?.LocalPosterPath),
                m.RuntimeMinutes,
                m.HierarchyLevel,
                m.Number,
                m.CreatedAt,
                m.UpdatedAt,
                m.ExternalIds.Select(e => new ExternalIdDto(e.Source, e.ExternalId)).ToList(),
                FileScannerMeta: fs,
                PluginMetadata: pluginMeta?.Count > 0 ? pluginMeta : null,
                RefreshLogs: logDtos,
                Ancestors: ancestors,
                EnrichmentStatuses: enrichmentStatuses,
                MediaTypeInternalName: m.MediaType?.Name,
                HasPhysicalFile: hasPhysicalFile,
                HasMetadataOnly: hasMetadataOnly,
                ResolvedMetadata: resolvedMetadata,
                Aliases: aliases,
                MergeHistory: mergeHistory,
                Overrides: overrides,
                BirthDate: m.BirthDate,
                DeathDate: m.DeathDate
            );
        }

        /// <summary>
        /// Returns true when <paramref name="metadataJson"/> contains a <c>fileScanner</c> entry
        /// with at least one non-null file path (filePaths array or filePath string).
        /// </summary>
        private static string? TryGetString(System.Text.Json.JsonElement el, string key) =>
            el.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                ? v.GetString() : null;

        private static int? TryGetInt(System.Text.Json.JsonElement el, string key) =>
            el.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number
                ? v.GetInt32() : null;

        private static bool IsMovieLikeTypeName(string? name) =>
            name is not null &&
            (name.Equals("movies",       StringComparison.OrdinalIgnoreCase) ||
             name.Equals("fanedits",     StringComparison.OrdinalIgnoreCase) ||
             name.Equals("anime_movies", StringComparison.OrdinalIgnoreCase));

        private static double? TryGetDouble(System.Text.Json.JsonElement el, string key) =>
            el.TryGetProperty(key, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number
                ? v.GetDouble() : null;

        private static List<string>? TryGetStringList(System.Text.Json.JsonElement el, string key)
        {
            if (!el.TryGetProperty(key, out var v) || v.ValueKind != System.Text.Json.JsonValueKind.Array)
                return null;
            var list = v.EnumerateArray()
                .Where(x => x.ValueKind == System.Text.Json.JsonValueKind.String)
                .Select(x => x.GetString()!)
                .ToList();
            return list.Count > 0 ? list : null;
        }

        /// <summary>Parses a "cast" array of {name, role} objects (see ScraperController's
        /// identical TryGetCastList -- kept as a separate copy since this controller already
        /// hand-rolls its own JsonElement helpers rather than sharing ScraperController's).</summary>
        private static List<Chronicle.API.DTOs.CastMemberDto>? TryGetCastList(System.Text.Json.JsonElement el, string key)
        {
            if (!el.TryGetProperty(key, out var v) || v.ValueKind != System.Text.Json.JsonValueKind.Array)
                return null;
            var list = new List<Chronicle.API.DTOs.CastMemberDto>();
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                var name = TryGetString(item, "name");
                if (string.IsNullOrEmpty(name)) continue;
                list.Add(new Chronicle.API.DTOs.CastMemberDto(name, TryGetString(item, "role")));
            }
            return list.Count > 0 ? list : null;
        }

        /// <summary>Parses a "crew" array of {name, job} objects (see ScraperController's
        /// identical TryGetCrewList).</summary>
        private static List<Chronicle.API.DTOs.CrewMemberDto>? TryGetCrewList(System.Text.Json.JsonElement el, string key)
        {
            if (!el.TryGetProperty(key, out var v) || v.ValueKind != System.Text.Json.JsonValueKind.Array)
                return null;
            var list = new List<Chronicle.API.DTOs.CrewMemberDto>();
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                var name = TryGetString(item, "name");
                if (string.IsNullOrEmpty(name)) continue;
                list.Add(new Chronicle.API.DTOs.CrewMemberDto(name, TryGetString(item, "job")));
            }
            return list.Count > 0 ? list : null;
        }

        // Delegates to the single canonical reader (Chronicle.Services.Scan.FileIdentityJson) --
        // this was the ORIGINAL of what LibraryController's own copy explicitly called itself a
        // mirror of, and it had the same gap: only ever checked fileScanner, never
        // scraperResolvedFile, so the media detail page for an item Kodi had confirmed a real
        // file for -- but Chronicle's own scanner never touched -- showed HasPhysicalFile=false
        // even while the very same page's scraperResolvedFile section proved a file existed.
        // Confirmed live 2026-08-20 on item 445734 ("Marked Men - Rule + Shaw").
        private static bool HasFileScannerData(string? metadataJson) =>
            Chronicle.Services.Scan.FileIdentityJson.HasKnownFile(metadataJson);

        /// <summary>
        /// Removes external-ID rows (and their MetadataJson blob key) whose owning plugin
        /// does not declare support for this item's CURRENT media type — e.g. a leftover
        /// "tmdb" ID on an item that is now "Fan Edits" (TMDB never declares "fanedits" as a
        /// supported type). These become orphaned after a type change or a corrected bad
        /// match: the owning plugin no longer runs against this item at all, so nothing in
        /// the normal enrichment cycle will ever touch or clear them — but they keep feeding
        /// OTHER providers' cross-reference lookups (see ClearArtworkOnlyProviderDataAsync)
        /// with stale IDs from the item's previous identity, indefinitely.
        /// </summary>
        private async Task RemoveExternalIdsForUnsupportedTypeAsync(Chronicle.Core.Models.MediaItem item, CancellationToken ct)
        {
            var mediaTypeName = item.MediaType?.Name;
            if (string.IsNullOrWhiteSpace(mediaTypeName) || item.ExternalIds.Count == 0) return;

            var installedProviders = _pluginRegistry.GetMetadataProviderEntries();
            var supportedSources = installedProviders
                .Where(e => e.Provider.GetSupportedMediaTypes()
                    .Any(s => string.Equals(s.MediaTypeName, mediaTypeName, StringComparison.OrdinalIgnoreCase)))
                .Select(e => Chronicle.Core.Helpers.PluginIdHelper.ToSource(e.PluginId))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Leave suppress sentinels alone — they're deliberate user configuration, not
            // provider-fetched data, and harmless even when the underlying plugin no longer
            // applies to this type.
            var orphaned = item.ExternalIds
                .Where(e => e.ExternalId != "__suppress__" && !supportedSources.Contains(e.Source))
                .ToList();
            if (orphaned.Count == 0) return;

            _context.MediaExternalIds.RemoveRange(orphaned);

            if (string.IsNullOrWhiteSpace(item.MetadataJson)) return;
            try
            {
                var root = System.Text.Json.JsonSerializer.Deserialize<
                    System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>>(item.MetadataJson);
                if (root is null) return;

                var changed = false;
                foreach (var e in orphaned)
                {
                    changed |= root.Remove(e.Source);
                    var fullId = installedProviders.FirstOrDefault(p =>
                        string.Equals(Chronicle.Core.Helpers.PluginIdHelper.ToSource(p.PluginId), e.Source,
                            StringComparison.OrdinalIgnoreCase)).PluginId;
                    if (fullId is not null)
                        changed |= root.Remove(fullId);
                }
                if (changed)
                    item.MetadataJson = System.Text.Json.JsonSerializer.Serialize(root);
            }
            catch { /* malformed JSON — leave as-is */ }
        }

        /// <summary>
        /// Deletes the enrichment row and MetadataJson blob for every installed metadata
        /// provider that is "artwork-only" for this item's media type/level — i.e. its
        /// declared <c>SupportedFields</c> supply image fields (poster/backdrop/logo/banner/
        /// disc/clearart/thumb) but no core identity field (title/overview). Fanart.tv is the
        /// only current example, detected structurally rather than by plugin ID so any future
        /// artwork-only plugin is covered automatically.
        ///
        /// Such providers (per <c>MediaSearchContext.KnownExternalIds</c>) have no independent
        /// text-search of their own — their art is entirely derived from cross-referencing
        /// OTHER providers' external IDs (e.g. Fanart.tv fetches via a TMDB/TVDB id). That means
        /// their correctness is coupled to every external ID on the item, not just their own —
        /// clearing or suppressing any one match can silently invalidate their art without ever
        /// touching their own enrichment row or blob. Without this cascade, a corrected match
        /// leaves the old provider's stale poster/backdrop/disc/logo on screen indefinitely.
        /// </summary>
        private async Task ClearArtworkOnlyProviderDataAsync(Chronicle.Core.Models.MediaItem item, CancellationToken ct)
        {
            var mediaTypeName = item.MediaType?.Name;
            if (string.IsNullOrWhiteSpace(mediaTypeName)) return;

            var artworkPluginIds = _pluginRegistry.GetMetadataProviderEntries()
                .Where(e =>
                {
                    var support = e.Provider.GetSupportedMediaTypes()
                        .FirstOrDefault(s => string.Equals(s.MediaTypeName, mediaTypeName, StringComparison.OrdinalIgnoreCase));
                    if (support is null) return false;
                    var fields = support.LevelFields is not null &&
                                 support.LevelFields.TryGetValue(item.HierarchyLevel, out var levelFields)
                        ? levelFields
                        : support.SupportedFields;
                    return fields.Count > 0
                        && !fields.Contains("title", StringComparer.OrdinalIgnoreCase)
                        && !fields.Contains("overview", StringComparer.OrdinalIgnoreCase);
                })
                .Select(e => e.PluginId)
                .ToList();

            if (artworkPluginIds.Count == 0) return;

            await _context.MediaEnrichments
                .Where(en => en.MediaItemId == item.Id && artworkPluginIds.Contains(en.PluginId))
                .ExecuteDeleteAsync(ct);

            // The provider's own external-ID row (e.g. fanarttv: "movie:8077") is now orphaned
            // too — it was fetched via the same stale cross-reference and nothing will re-seed
            // it (that only happens when the SOURCE plugin it cross-references re-matches this
            // item, which won't occur for a type it doesn't support). Leaving it behind is
            // inert but confusing; remove it for the same reason as the blob/enrichment row.
            var artworkSources = artworkPluginIds
                .Select(Chronicle.Core.Helpers.PluginIdHelper.ToSource)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var artworkExternalIds = item.ExternalIds
                .Where(e => artworkSources.Contains(e.Source))
                .ToList();
            if (artworkExternalIds.Count > 0)
                _context.MediaExternalIds.RemoveRange(artworkExternalIds);

            if (string.IsNullOrWhiteSpace(item.MetadataJson)) return;
            try
            {
                var root = System.Text.Json.JsonSerializer.Deserialize<
                    System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>>(item.MetadataJson);
                if (root is null) return;

                var changed = false;
                foreach (var pluginId in artworkPluginIds)
                    changed |= root.Remove(pluginId);

                if (changed)
                    item.MetadataJson = System.Text.Json.JsonSerializer.Serialize(root);
            }
            catch { /* malformed JSON — leave as-is */ }
        }

        /// <summary>
        /// Clears all provider-supplied metadata from <paramref name="item"/>, restoring any
        /// file-scanner poster if available.  Called when the user explicitly removes a match.
        /// </summary>
        private static void ClearProviderMetadata(Chronicle.Core.Models.MediaItem item)
        {
            var (fs, _) = ParseMetaJson(item.MetadataJson);

            // Restore poster from file scanner if it has one, otherwise wipe it.
            item.PosterUrl      = fs?.NfoPosterUrl ?? fs?.LocalPosterPath;
            item.Overview       = null;
            item.RuntimeMinutes = null;
            // Name and Year are intentionally left as-is — they were either set from the
            // file scanner originally or manually edited; reverting them would be unexpected.
        }

        // "fileScanner" is the only first-class key — it gets its own typed DTO field.
        // All plugin metadata (TMDB, MusicBrainz, etc.) flows through PluginMetadata
        // keyed by full plugin ID, so Chronicle never needs to know any plugin's data shape.
        // "_resolved"/"_overrides" are reserved, resolver-owned keys (see
        // MetadataResolutionService) — never plugin data, so both are excluded here too.
        private static readonly HashSet<string> _firstClassKeys =
            new(StringComparer.OrdinalIgnoreCase) { "fileScanner", "_resolved", "_overrides" };

        private static readonly System.Text.Json.JsonSerializerOptions _jsonOpts =
            new(System.Text.Json.JsonSerializerDefaults.Web);

        /// <summary>
        /// Returns a browser-accessible poster URL.  HTTP URLs pass through unchanged.
        /// Local file paths stored in PosterUrl (set by the file scanner) are redirected
        /// to the /local-poster API endpoint, which serves the bytes from disk.
        /// </summary>
        private static string? EffectivePosterUrl(int id, string? posterUrl, string? localPosterPath)
        {
            if (posterUrl?.StartsWith("http", StringComparison.OrdinalIgnoreCase) == true)
                return posterUrl;
            if (localPosterPath is not null)
                return $"/api/v1/media/{id}/local-poster";
            return null;
        }

        private static (FileScannerMetaDto? fs,
                        Dictionary<string, System.Text.Json.JsonElement>? pluginMeta)
            ParseMetaJson(string? json)
        {
            if (json is null) return (null, null);
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return (null, null);

                FileScannerMetaDto? fs = null;
                if (root.TryGetProperty("fileScanner", out var fsEl))
                    fs = System.Text.Json.JsonSerializer.Deserialize<FileScannerMetaDto>(fsEl.GetRawText(), _jsonOpts);

                // The hierarchical importer writes {"fileScanner":{"importedAt":...,"filePaths":[...]}}
                // which deserialises into a FileScannerMetaDto with all-null fields because the
                // property names don't match.  Extract filePaths[0] (or folderPath for parent
                // items) from the raw JSON so the File Scanner card appears on the media detail page.
                if (fs is not null && fs.FilePath is null && fs.LocalPosterPath is null && fs.NfoPosterUrl is null)
                    fs = TryExtractFilePathFromNewFormat(json) ?? fs;

                // Suppress a completely empty FileScannerMetaDto — but keep it when ImportedAt
                // is set (scanner-imported items that haven't re-recorded the path yet), or when
                // it carries technical/identity data only (e.g. a contribution with no file path,
                // such as a MusicBee push that reported size/bitrate/duration but no local path).
                var fsOut = (fs?.FilePath is not null || fs?.LocalPosterPath is not null ||
                             fs?.NfoPosterUrl is not null || fs?.ImportedAt is not null ||
                             fs?.Fingerprint is not null)
                    ? fs : null;

                // All non-fileScanner keys are plugin metadata — pass raw JsonElements so
                // the API remains agnostic about each plugin's internal data shape.
                Dictionary<string, System.Text.Json.JsonElement>? pluginMeta = null;
                foreach (var prop in root.EnumerateObject())
                {
                    if (_firstClassKeys.Contains(prop.Name)) continue;
                    pluginMeta ??= new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.OrdinalIgnoreCase);
                    // Normalize object keys to camelCase so TypeScript can read them directly
                    pluginMeta[prop.Name] = NormalizeToCamelCase(prop.Value);
                }

                return (fsOut, pluginMeta);
            }
            catch { return (null, null); }
        }

        /// <summary>
        /// Recursively rewrites all JSON object keys to camelCase (lowercases the first
        /// character) so that plugin metadata serialised with PascalCase conventions
        /// (the .NET default) can be read directly by TypeScript interfaces.
        /// Arrays and primitives are returned unchanged (cloned from the source document).
        /// </summary>
        private static System.Text.Json.JsonElement NormalizeToCamelCase(System.Text.Json.JsonElement element)
        {
            switch (element.ValueKind)
            {
                case System.Text.Json.JsonValueKind.Object:
                {
                    var ms = new System.IO.MemoryStream();
                    using var w = new System.Text.Json.Utf8JsonWriter(ms);
                    w.WriteStartObject();
                    foreach (var prop in element.EnumerateObject())
                    {
                        var key = prop.Name.Length > 0
                            ? char.ToLowerInvariant(prop.Name[0]) + prop.Name[1..]
                            : prop.Name;
                        w.WritePropertyName(key);
                        WriteNormalized(w, prop.Value);
                    }
                    w.WriteEndObject();
                    w.Flush();
                    ms.Position = 0;
                    using var doc = System.Text.Json.JsonDocument.Parse(ms);
                    return doc.RootElement.Clone();
                }
                default:
                    return element.Clone();
            }
        }

        private static void WriteNormalized(System.Text.Json.Utf8JsonWriter w, System.Text.Json.JsonElement element)
        {
            switch (element.ValueKind)
            {
                case System.Text.Json.JsonValueKind.Object:
                    w.WriteStartObject();
                    foreach (var prop in element.EnumerateObject())
                    {
                        var key = prop.Name.Length > 0
                            ? char.ToLowerInvariant(prop.Name[0]) + prop.Name[1..]
                            : prop.Name;
                        w.WritePropertyName(key);
                        WriteNormalized(w, prop.Value);
                    }
                    w.WriteEndObject();
                    break;
                case System.Text.Json.JsonValueKind.Array:
                    w.WriteStartArray();
                    foreach (var item in element.EnumerateArray())
                        WriteNormalized(w, item);
                    w.WriteEndArray();
                    break;
                default:
                    element.WriteTo(w);
                    break;
            }
        }

        /// <summary>
        /// Reads the raw MetadataJson and extracts a display path from the hierarchical
        /// group importer format.  For leaf items (seasons/episodes) this is the first
        /// entry in <c>filePaths</c>; for root show items (filePaths is empty) it falls
        /// back to the <c>folderPath</c> stored during import.
        /// </summary>
        private static FileScannerMetaDto? TryExtractFilePathFromNewFormat(string json)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("fileScanner", out var sect))
                {
                    // Pull importedAt timestamp (present on all hierarchical-importer items)
                    DateTime? importedAt = null;
                    if (sect.TryGetProperty("importedAt", out var iat) &&
                        iat.TryGetDateTime(out var dt))
                        importedAt = dt;

                    string? nfoPath = null;
                    if (sect.TryGetProperty("nfoPath", out var np))
                        nfoPath = np.GetString();

                    // Leaf items (episodes/tracks): first entry in filePaths array
                    if (sect.TryGetProperty("filePaths", out var arr) &&
                        arr.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var el in arr.EnumerateArray())
                        {
                            var path = el.GetString();
                            if (!string.IsNullOrEmpty(path))
                                return new FileScannerMetaDto(path, null, null, importedAt, NfoPath: nfoPath);
                        }
                    }

                    // Parent items (shows/artists/seasons): filePaths is empty; fall back to
                    // folderPath which is stored for level-0 and level-1 groups.
                    if (sect.TryGetProperty("folderPath", out var fp))
                    {
                        var folderPath = fp.GetString();
                        if (!string.IsNullOrEmpty(folderPath))
                            return new FileScannerMetaDto(folderPath, null, null, importedAt, NfoPath: nfoPath);
                    }

                    // fileScanner section exists but no path recorded yet (older import).
                    // Still return a non-null DTO so the File Scanner card is shown.
                    if (importedAt.HasValue || nfoPath is not null)
                        return new FileScannerMetaDto(null, null, null, importedAt, NfoPath: nfoPath);
                }
            }
            catch { /* ignore malformed JSON */ }
            return null;
        }

        private int? GetCurrentUserId()
        {
            var claim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            return int.TryParse(claim, out var id) ? id : null;
        }

        /// <summary>Merges two items. winnerId must be either id or targetId.</summary>
        [HttpPost("{id:int}/merge")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Merge(
            int id,
            [FromBody] MergeRequestDto dto,
            CancellationToken ct)
        {
            if (dto.WinnerId != id && dto.WinnerId != dto.TargetId)
                return BadRequest(ApiResponse<object>.Fail("INVALID_WINNER",
                    "winnerId must be either the source item id or targetId."));

            var loserId = dto.WinnerId == id ? dto.TargetId : id;
            try
            {
                await _mergeService.MergeAsync(dto.WinnerId, loserId, GetCurrentUserId(), ct);
                return Ok(ApiResponse<object>.Ok(new { merged = true }));
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ApiResponse<object>.Fail("MERGE_ERROR", ex.Message));
            }
        }

        /// <summary>Returns merge history for this item (as winner).</summary>
        [HttpGet("{id:int}/merges")]
        public async Task<IActionResult> GetMerges(int id, CancellationToken ct)
        {
            var merges = await _context.MediaItemMerges
                .Where(m => m.WinnerId == id)
                .OrderByDescending(m => m.MergedAt)
                .ToListAsync(ct);

            var dtos = merges.Select(m => new MergeHistoryDto(
                m.Id, m.LoserOriginalId, m.LoserName, m.MergedAt, m.MergedByUserId)).ToList();
            return Ok(ApiResponse<List<MergeHistoryDto>>.Ok(dtos));
        }

        /// <summary>Unmerges a specific merge, recreating the loser as a stub.</summary>
        [HttpDelete("{id:int}/merges/{mergeId:int}")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> Unmerge(int id, int mergeId, CancellationToken ct)
        {
            var merge = await _context.MediaItemMerges.FindAsync([mergeId], ct);
            if (merge is null || merge.WinnerId != id)
                return NotFound(ApiResponse<object>.Fail("MERGE_NOT_FOUND",
                    $"Merge record {mergeId} not found for item {id}."));

            try
            {
                await _mergeService.UnmergeAsync(mergeId, ct);
                return Ok(ApiResponse<object>.Ok(new { unmerged = true }));
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(ApiResponse<object>.Fail("UNMERGE_ERROR", ex.Message));
            }
        }

        /// <summary>Returns collection membership for a movie item.</summary>
        /// <remarks>
        /// Works both when id is the collection itself (Level 0) and when id is a movie
        /// within a collection (Level 1). In the latter case it resolves to the parent collection.
        /// Returns 404 if the item has no collection.
        /// </remarks>
        [HttpGet("{id:int}/collection")]
        public async Task<IActionResult> GetCollection(int id, CancellationToken ct)
        {
            var userId = GetCurrentUserId();
            if (userId is null)
                return Unauthorized();

            // Load the item
            var item = await _context.MediaItems
                .Include(m => m.MediaType)
                .Include(m => m.Parent)
                    .ThenInclude(p => p!.MediaType)
                .FirstOrDefaultAsync(m => m.Id == id, ct);

            if (item is null)
                return NotFound(ApiResponse<CollectionDto>.Fail("MEDIA_NOT_FOUND", "Media item not found."));

            // Resolve collection: if item IS a collection container (Level 0, flat media type, has
            // children) use it directly; if item is a member within a collection (Level 1, parent
            // is a flat media type) use its parent. A Level-0 item with no children is standalone
            // — return 404 (no collection).
            MediaItem? collectionItem = null;
            bool isFlatType = item.MediaType?.HierarchyLevels == 1;

            if (isFlatType && item.HierarchyLevel == 0)
            {
                // Only treat as a collection container if it actually has children.
                bool hasChildren = await _context.MediaItems.AnyAsync(m => m.ParentId == item.Id, ct);
                if (hasChildren)
                    collectionItem = item;
            }
            else if (item.HierarchyLevel == 1 && item.Parent is not null && item.Parent.MediaType?.HierarchyLevels == 1)
            {
                collectionItem = item.Parent;
            }

            if (collectionItem is null)
                return NotFound(ApiResponse<CollectionDto>.Fail("NO_COLLECTION", "Item does not belong to a collection."));

            // Sort by year ascending; items with no year sort to the end (unknown release date),
            // with name as a tiebreaker within the same year.
            var members = await _context.MediaItems
                .Where(m => m.ParentId == collectionItem.Id)
                .OrderBy(m => m.Year == null ? 1 : 0)
                .ThenBy(m => m.Year)
                .ThenBy(m => m.Name)
                .ToListAsync(ct);

            // Load library status for current user
            var memberIds = members.Select(m => m.Id).ToList();
            var libraryEntries = await _context.UserLibraries
                .Where(l => l.UserId == userId.Value && memberIds.Contains(l.MediaItemId))
                .ToDictionaryAsync(l => l.MediaItemId, ct);

            // The collection's own poster (set only by a Rebuild pulling the provider's
            // dedicated collection-level art) always wins when present. When it's empty, fall
            // back to the first member's own poster -- same rule, same order, the Kodi scraper's
            // set-art fallback uses (IMovieCollectionService.GetFallbackPosterAsync) -- so the
            // web page always has a poster to show wherever one exists in the collection at all,
            // regardless of whether that member is a file the user actually owns.
            var posterUrl = collectionItem.PosterUrl;
            if (string.IsNullOrEmpty(posterUrl))
                posterUrl = await _movieCollectionService.GetFallbackPosterAsync(_context, collectionItem.Id, ct);

            var dto = new CollectionDto
            {
                Id              = collectionItem.Id,
                Name            = collectionItem.Name,
                PosterUrl       = posterUrl,
                Overview        = collectionItem.Overview,
                SupportsRebuild = IsMovieLikeTypeName(collectionItem.MediaType?.Name),
                Movies          = members.Select(m => new CollectionMemberDto
                {
                    Id            = m.Id,
                    Name          = m.Name,
                    Year          = m.Year,
                    PosterUrl     = m.PosterUrl,
                    InLibrary        = libraryEntries.ContainsKey(m.Id),
                    LibraryStatus    = libraryEntries.TryGetValue(m.Id, out var le) ? le.Status.ToString() : null,
                    Rating           = ExtractRatingFromMetadata(m.MetadataJson),
                    UserRating       = le?.UserRating,
                    UserRatingSource = le?.UserRating.HasValue == true ? ExtractUserRatingSource(m.MetadataJson, le.UserRating.Value) : null,
                    IsStub           = m.IsStub,
                    HasFile          = Chronicle.Services.Scan.FileIdentityJson.HasKnownFile(m.MetadataJson),
                }).ToList(),
            };

            return Ok(ApiResponse<CollectionDto>.Ok(dto));
        }

        /// <summary>
        /// Rebuilds a single collection: re-parents any children that have moved to a different
        /// collection (based on their stored metadata) and creates stubs for missing members.
        /// Returns a summary of what changed plus the updated collection (or null if it was removed).
        /// </summary>
        [HttpPost("{id:int}/rebuild-collection")]
        public async Task<IActionResult> RebuildCollection(int id, CancellationToken ct)
        {
            // Snapshot before
            var beforeChildren = await _context.MediaItems
                .Where(m => m.ParentId == id)
                .Select(m => new { m.IsStub })
                .ToListAsync(ct);
            int stubsBefore = beforeChildren.Count(c => c.IsStub);
            int realBefore  = beforeChildren.Count(c => !c.IsStub);

            var providers = _pluginRegistry.GetMetadataProviderEntries()
                .Select(e => (e.PluginId, e.Provider))
                .ToList();

            await _movieCollectionService.RebuildSingleCollectionAsync(id, providers, ct);

            // Snapshot after
            var afterChildren = await _context.MediaItems
                .Where(m => m.ParentId == id)
                .Select(m => new { m.IsStub })
                .ToListAsync(ct);
            int stubsAfter = afterChildren.Count(c => c.IsStub);
            int realAfter  = afterChildren.Count(c => !c.IsStub);
            bool collectionRemoved = !await _context.MediaItems.AnyAsync(m => m.Id == id, ct);

            // Build a human-readable summary
            var lines = new List<string>();
            int stubsRemoved = stubsBefore - stubsAfter;
            int stubsAdded   = stubsAfter  - stubsBefore;
            int requeued     = realBefore  - realAfter; // movies that moved out due to wrong match

            if (collectionRemoved)
                lines.Add("Collection removed — all movies were re-queued for correct matching.");
            else if (stubsRemoved > 0 && requeued > 0)
                lines.Add($"Removed {stubsRemoved} wrong stub(s). {requeued} movie(s) cleared and queued for re-matching.");
            else if (stubsRemoved > 0)
                lines.Add($"Removed {stubsRemoved} stale stub(s).");
            else if (requeued > 0)
                lines.Add($"{requeued} movie(s) cleared and queued for re-matching.");
            else if (stubsAdded > 0)
                lines.Add($"Added {stubsAdded} missing stub(s).");
            else
                lines.Add("Collection is up to date — no changes needed.");

            var summary = string.Join(" ", lines);

            // Return updated collection (null if removed)
            CollectionDto? updatedCollection = null;
            if (!collectionRemoved)
            {
                var collResult = await GetCollection(id, ct) as OkObjectResult;
                updatedCollection = (collResult?.Value as ApiResponse<CollectionDto>)?.Data;
            }

            return Ok(ApiResponse<RebuildCollectionResultDto>.Ok(new RebuildCollectionResultDto
            {
                Summary    = summary,
                Collection = updatedCollection,
            }));
        }

        private static double? ExtractRatingFromMetadata(string? metadataJson)
        {
            if (string.IsNullOrEmpty(metadataJson)) return null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
                var root = doc.RootElement;
                // Prefer the assignment-resolved value if present
                if (root.TryGetProperty("_resolved", out var r) &&
                    r.TryGetProperty("rating", out var rr) &&
                    rr.ValueKind == System.Text.Json.JsonValueKind.Number)
                    return rr.GetDouble();
                // Fall back to first plugin blob with a rating
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Name.StartsWith('_')) continue;
                    if (prop.Value.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                    if (prop.Value.TryGetProperty("rating", out var ratingEl) &&
                        ratingEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                        return ratingEl.GetDouble();
                }
            }
            catch { /* ignore malformed json */ }
            return null;
        }

        // Returns the display name of the source that provided the user's personal rating.
        // Checks known plugin blobs for a userRating field matching the stored value.
        internal static string ExtractUserRatingSource(string? metadataJson, int userRating)
        {
            if (!string.IsNullOrEmpty(metadataJson))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(metadataJson);
                    var root = doc.RootElement;
                    foreach (var prop in root.EnumerateObject())
                    {
                        if (prop.Name.StartsWith('_')) continue;
                        if (prop.Value.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                        if (prop.Value.TryGetProperty("userRating", out var ur) &&
                            ur.ValueKind == System.Text.Json.JsonValueKind.Number &&
                            ur.GetInt32() == userRating)
                        {
                            var pluginName = prop.Name.Split('.').LastOrDefault() ?? prop.Name;
                            return char.ToUpper(pluginName[0]) + pluginName[1..];
                        }
                    }
                }
                catch { /* ignore */ }
            }
            return "Chronicle";
        }

        /// <summary>
        /// Server-side image proxy — fetches an external image URL and streams it back,
        /// bypassing browser CORS restrictions on third-party CDNs (Trakt, Fanart.tv, etc.).
        /// </summary>
        [HttpGet("poster-proxy")]
        [AllowAnonymous]
        public async Task<IActionResult> PosterProxy([FromQuery] string url, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != "https" && uri.Scheme != "http"))
                return BadRequest("Invalid URL.");

            using var http = new System.Net.Http.HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Chronicle/1.0");
            http.Timeout = TimeSpan.FromSeconds(10);
            try
            {
                var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode) return NotFound();
                var contentType = resp.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
                var stream = await resp.Content.ReadAsStreamAsync(ct);
                return File(stream, contentType);
            }
            catch
            {
                return NotFound();
            }
        }
    }
}
