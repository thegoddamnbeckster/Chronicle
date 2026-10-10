using Chronicle.API.Authentication;
using Chronicle.API.DTOs;
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Chronicle.Services.Scan;
using Chronicle.Services.Plugins;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.API.Controllers;

public sealed record PluginRefDto(string PluginId, string Name);

public sealed record MediaTypeAdminDto(
    int Id, string Name, string DisplayName, string? Description, int HierarchyLevels, string[] HierarchyLabels,
    string InteractionVerb, string ProgressUnit, bool IsBuiltIn, bool IsActive, bool SupportsCollections, bool IsTrackable,
    string? ScanStrategy, bool IsUserModified, int ItemCount, IReadOnlyList<PluginRefDto> Plugins, string? ScanHints = null,
    string? ProviderFamily = null, string? CastHeading = null);

public sealed record MediaTypeRequest(
    string? Name, string DisplayName, string? Description, int HierarchyLevels, string[]? HierarchyLabels,
    string InteractionVerb, string ProgressUnit, bool SupportsCollections, bool IsTrackable, string? ScanStrategy, bool IsActive = true,
    string? ScanHints = null, string? ProviderFamily = null, string? CastHeading = null);

/// <summary>
/// Settings -> Media Types. Lists every media type with how many items it holds and which installed plugins handle it,
/// and lets an administrator register a type of their own, edit one, or remove an empty one. Plugins that declare a type
/// still create and describe it automatically at start; a type edited here is marked and left as the administrator set it.
/// The ordinary <c>GET /api/v1/media/types</c> (for pickers) is unchanged.
/// </summary>
[ApiController]
[Route("api/v1/media-types")]
[Authorize(Policy = AuthPolicies.SessionAdmin)]
public class MediaTypesController : ControllerBase
{
    private readonly ChronicleDbContext _db;
    private readonly IPluginRegistry _registry;
    private readonly ILogger<MediaTypesController> _log;

    public MediaTypesController(ChronicleDbContext db, IPluginRegistry registry, ILogger<MediaTypesController> log)
    {
        _db = db;
        _registry = registry;
        _log = log;
    }

    private Dictionary<string, List<PluginRefDto>> PluginsByType()
    {
        var map = new Dictionary<string, List<PluginRefDto>>(StringComparer.OrdinalIgnoreCase);
        foreach (var lp in _registry.GetLoadedPlugins())
        {
            var names = lp.MetadataProviders.SelectMany(p => p.GetSupportedMediaTypes())
                .Concat(lp.FileScannerPlugins.SelectMany(p => p.GetSupportedMediaTypes()))
                .Select(s => s.MediaTypeName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var n in names)
            {
                if (!map.TryGetValue(n, out var list)) map[n] = list = [];
                list.Add(new PluginRefDto(lp.Manifest.PluginId, lp.Manifest.Name));
            }
        }
        return map;
    }

    private async Task<MediaTypeAdminDto> ToDtoAsync(MediaType t, Dictionary<string, List<PluginRefDto>> plugins, int? knownCount = null) =>
        new(t.Id, t.Name, t.DisplayName, t.Description, t.HierarchyLevels, MediaTypeRules.SplitLabels(t.HierarchyLabels),
            t.InteractionVerb, t.ProgressUnit, t.IsBuiltIn, t.IsActive, t.SupportsCollections, t.IsTrackable, t.ScanStrategy,
            t.IsUserModified, knownCount ?? await _db.MediaItems.CountAsync(i => i.MediaTypeId == t.Id),
            plugins.TryGetValue(t.Name, out var list) ? list : [], t.ScanHintsJson, t.ProviderFamily, t.CastHeading);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var plugins = PluginsByType();
        var counts = await _db.MediaItems.GroupBy(i => i.MediaTypeId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, ct);
        var types = await _db.MediaTypes.AsNoTracking().OrderBy(t => t.DisplayName).ToListAsync(ct);
        var dtos = new List<MediaTypeAdminDto>();
        foreach (var t in types) dtos.Add(await ToDtoAsync(t, plugins, counts.GetValueOrDefault(t.Id)));
        return Ok(ApiResponse<List<MediaTypeAdminDto>>.Ok(dtos));
    }

    private static MediaTypeInput ToInput(MediaTypeRequest r) => new(
        r.Name, r.DisplayName, r.Description, r.HierarchyLevels, r.HierarchyLabels ?? [],
        (r.InteractionVerb ?? "").Trim().ToLowerInvariant(), (r.ProgressUnit ?? "").Trim().ToLowerInvariant(),
        r.SupportsCollections, r.IsTrackable, string.IsNullOrWhiteSpace(r.ScanStrategy) ? null : r.ScanStrategy.Trim(), r.IsActive,
        string.IsNullOrWhiteSpace(r.ProviderFamily) ? null : r.ProviderFamily.Trim().ToLowerInvariant(),
        string.IsNullOrWhiteSpace(r.CastHeading) ? null : r.CastHeading.Trim());

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] MediaTypeRequest req, CancellationToken ct)
    {
        var input = ToInput(req);
        if (MediaTypeRules.Validate(input, creating: true) is { } problem)
            return BadRequest(ApiResponse<object>.Fail("INVALID_MEDIA_TYPE", problem));

        if (ScanHints.Validate(req.ScanHints) is { } hintsProblem)
            return BadRequest(ApiResponse<object>.Fail("INVALID_SCAN_HINTS", hintsProblem));

        var name = input.Name!.Trim();
        if (await _db.MediaTypes.AnyAsync(t => t.Name == name, ct))
            return Conflict(ApiResponse<object>.Fail("MEDIA_TYPE_EXISTS", $"A media type named '{name}' already exists."));

        var type = new MediaType
        {
            Name = name, DisplayName = input.DisplayName.Trim(), Description = input.Description?.Trim(),
            HierarchyLevels = input.HierarchyLevels, HierarchyLabels = MediaTypeRules.JoinLabels(input.HierarchyLabels),
            InteractionVerb = input.InteractionVerb, ProgressUnit = input.ProgressUnit,
            SupportsCollections = input.SupportsCollections, IsTrackable = input.IsTrackable, ScanStrategy = input.ScanStrategy,
            ScanHintsJson = string.IsNullOrWhiteSpace(req.ScanHints) ? null : req.ScanHints.Trim(),
            ProviderFamily = input.ProviderFamily, CastHeading = input.CastHeading,
            IsBuiltIn = false, IsActive = true,
            // Made by a person: no plugin's declaration may rewrite it later.
            IsUserModified = true, CreatedAt = DateTime.UtcNow,
        };
        _db.MediaTypes.Add(type);
        await _db.SaveChangesAsync(ct);
        await MediaTypeFamilies.RefreshAsync(_db, ct);
        _log.LogInformation("MEDIATYPE created '{Name}' by an administrator", name);
        return Ok(ApiResponse<MediaTypeAdminDto>.Ok(await ToDtoAsync(type, PluginsByType(), 0)));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] MediaTypeRequest req, CancellationToken ct)
    {
        var type = await _db.MediaTypes.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (type is null) return NotFound(ApiResponse<object>.Fail("NOT_FOUND", "No such media type."));

        var input = ToInput(req);
        if (MediaTypeRules.Validate(input, creating: false) is { } problem)
            return BadRequest(ApiResponse<object>.Fail("INVALID_MEDIA_TYPE", problem));

        if (ScanHints.Validate(req.ScanHints) is { } hintsProblem)
            return BadRequest(ApiResponse<object>.Fail("INVALID_SCAN_HINTS", hintsProblem));

        var items = await _db.MediaItems.CountAsync(i => i.MediaTypeId == id, ct);
        if (items > 0 && input.HierarchyLevels != type.HierarchyLevels)
            return Conflict(ApiResponse<object>.Fail("HAS_ITEMS",
                $"This type already holds {items} item(s), so the number of levels cannot change. Create a new type instead."));
        if (!input.IsActive && type.IsBuiltIn && type.IsActive && items > 0)
            return Conflict(ApiResponse<object>.Fail("BUILT_IN_IN_USE", "A built-in type that still holds items cannot be switched off."));

        type.DisplayName = input.DisplayName.Trim();
        type.Description = input.Description?.Trim();
        type.HierarchyLevels = input.HierarchyLevels;
        type.HierarchyLabels = MediaTypeRules.JoinLabels(input.HierarchyLabels);
        type.InteractionVerb = input.InteractionVerb;
        type.ProgressUnit = input.ProgressUnit;
        type.SupportsCollections = input.SupportsCollections;
        type.IsTrackable = input.IsTrackable;
        type.ScanStrategy = input.ScanStrategy;
        // Omitted = leave as is; an empty string clears the hints.
        if (req.ScanHints is not null)
            type.ScanHintsJson = string.IsNullOrWhiteSpace(req.ScanHints) ? null : req.ScanHints.Trim();
        // Omitted = leave as is; an empty string clears (same rule as the scan hints).
        if (req.ProviderFamily is not null) type.ProviderFamily = input.ProviderFamily;
        if (req.CastHeading is not null) type.CastHeading = input.CastHeading;
        type.IsActive = input.IsActive;
        type.IsUserModified = true;
        await _db.SaveChangesAsync(ct);
        await MediaTypeFamilies.RefreshAsync(_db, ct);
        _log.LogInformation("MEDIATYPE '{Name}' edited by an administrator", type.Name);
        return Ok(ApiResponse<MediaTypeAdminDto>.Ok(await ToDtoAsync(type, PluginsByType(), items)));
    }

    /// <summary>Hands the type back to its plugins: the next start applies what they declare again.</summary>
    [HttpPost("{id:int}/release-to-plugins")]
    public async Task<IActionResult> ReleaseToPlugins(int id, CancellationToken ct)
    {
        var type = await _db.MediaTypes.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (type is null) return NotFound(ApiResponse<object>.Fail("NOT_FOUND", "No such media type."));
        type.IsUserModified = false;
        await _db.SaveChangesAsync(ct);
        await MediaTypeFamilies.RefreshAsync(_db, ct);
        return Ok(ApiResponse<MediaTypeAdminDto>.Ok(await ToDtoAsync(type, PluginsByType())));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var type = await _db.MediaTypes.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (type is null) return NotFound(ApiResponse<object>.Fail("NOT_FOUND", "No such media type."));
        if (type.IsBuiltIn)
            return Conflict(ApiResponse<object>.Fail("BUILT_IN", "Built-in types cannot be deleted."));
        var items = await _db.MediaItems.CountAsync(i => i.MediaTypeId == id, ct);
        if (items > 0)
            return Conflict(ApiResponse<object>.Fail("HAS_ITEMS", $"This type still holds {items} item(s). Move or delete them first, or switch the type off instead."));
        if (PluginsByType().ContainsKey(type.Name))
            return Conflict(ApiResponse<object>.Fail("HANDLED_BY_PLUGIN", "An installed plugin handles this type and would recreate it at the next start. Uninstall the plugin first, or switch the type off instead."));

        _db.MediaTypes.Remove(type);
        await _db.SaveChangesAsync(ct);
        await MediaTypeFamilies.RefreshAsync(_db, ct);
        _log.LogInformation("MEDIATYPE '{Name}' deleted by an administrator", type.Name);
        return Ok(ApiResponse<object>.Ok(new { deleted = type.Name }));
    }
}
