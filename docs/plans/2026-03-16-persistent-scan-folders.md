# Persistent Scan Folders Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Save scan folder configurations to the database, display them on the File Scan page with inline CRUD, and run nightly automated imports at 3am using the file scanner plugin's confidence threshold setting.

**Architecture:** A `scan_folders` table stores each folder's path, media type, recursive flag, enabled state, and last scanned timestamp. The confidence threshold moves from a hardcoded default into the file scanner plugin's settings schema (visible in Plugins → Configure), exposed via a new `ConfidenceThreshold` property on `IFileScannerPlugin`. A `ScheduledScanService` runs nightly at 3am, loops enabled folders, previews grouped results, filters by threshold, and auto-imports. The Scan page gains a collapsible "Saved Folders" panel with inline CRUD that auto-collapses when scan results are shown.

**Tech Stack:** .NET 9 / EF Core 9 / ASP.NET Core, React 18 + TypeScript strict, SQLite

---

## Task 1: ScanFolder domain model

**Files:**
- Create: `src/Chronicle.Core/Models/ScanFolder.cs`
- Modify: `src/Chronicle.Data/ChronicleDbContext.cs`

**Step 1: Create the model**

```csharp
// src/Chronicle.Core/Models/ScanFolder.cs
namespace Chronicle.Core.Models;

public class ScanFolder
{
    public int Id { get; set; }
    public string Path { get; set; } = string.Empty;
    public int MediaTypeId { get; set; }
    public bool Recursive { get; set; } = true;
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastScannedAt { get; set; }

    public MediaType MediaType { get; set; } = null!;
}
```

**Step 2: Add DbSet and configure in ChronicleDbContext**

Add the DbSet alongside the existing ones (after `BackgroundTasks`):
```csharp
public DbSet<ScanFolder> ScanFolders => Set<ScanFolder>();
```

Add configuration in `OnModelCreating` (after the BackgroundTask block):
```csharp
modelBuilder.Entity<ScanFolder>(e =>
{
    e.ToTable("scan_folders");
    e.HasKey(f => f.Id);
    e.Property(f => f.Path).IsRequired().HasMaxLength(1000);
    e.HasOne(f => f.MediaType)
     .WithMany()
     .HasForeignKey(f => f.MediaTypeId)
     .OnDelete(DeleteBehavior.Restrict);
});
```

**Step 3: Generate the EF migration**

```bash
cd src/Chronicle.API
dotnet ef migrations add AddScanFolders --project ../Chronicle.Data --startup-project .
```

Expected: new migration file created in `src/Chronicle.Data/Migrations/` with timestamp prefix.

**Step 4: Apply migration to verify SQL is correct**

```bash
dotnet ef database update --project ../Chronicle.Data --startup-project .
```

Expected: `Done.` with no errors.

**Step 5: Commit**

```bash
git add src/Chronicle.Core/Models/ScanFolder.cs src/Chronicle.Data/ChronicleDbContext.cs src/Chronicle.Data/Migrations/
git commit -m "feat(scan): add ScanFolder model and migration"
```

---

## Task 2: Add `ConfidenceThreshold` to `IFileScannerPlugin`

**Files:**
- Modify: `src/Chronicle.Plugins/IFileScannerPlugin.cs`
- Modify: concrete `IFileScannerPlugin` implementation (search for `class.*IFileScannerPlugin` in the codebase to find it)

**Step 1: Add property to the interface**

In `src/Chronicle.Plugins/IFileScannerPlugin.cs`, add to the Capability declarations section:

```csharp
/// <summary>
/// Minimum confidence score (0–100) a grouped result must have to be auto-imported
/// by the scheduled scan task. Configured via the plugin settings schema.
/// </summary>
int ConfidenceThreshold { get; }
```

**Step 2: Find the concrete implementation**

Run: `grep -rn "IFileScannerPlugin" src/ --include="*.cs" -l`

Look for the class that implements (not defines) `IFileScannerPlugin`. It will be in a project like `Chronicle.Services` or `Chronicle.Plugins.FileScanner`.

**Step 3: Add `confidence_threshold` to `GetSettingsSchema()`**

In the concrete implementation, add to the `GetSettingsSchema()` return value:

```csharp
new SettingDefinition
{
    Key = "confidence_threshold",
    Label = "Confidence threshold",
    Description = "Minimum confidence score (0–100) a scan group must reach to be auto-imported by the scheduled scan. Groups below this score are shown in the manual scan UI but skipped by the background task.",
    Type = SettingType.Number,
    Required = false,
    DefaultValue = "80",
}
```

**Step 4: Store and expose the value via `Configure()` and the new property**

Add a backing field and wire up `Configure()` and the property:

```csharp
private int _confidenceThreshold = 80;

public int ConfidenceThreshold => _confidenceThreshold;

public void Configure(IReadOnlyDictionary<string, string> settings)
{
    // ... existing configuration code ...

    if (settings.TryGetValue("confidence_threshold", out var raw)
        && int.TryParse(raw, out var parsed)
        && parsed >= 0 && parsed <= 100)
    {
        _confidenceThreshold = parsed;
    }
}
```

**Step 5: Verify SettingType serialisation in the frontend**

Check `src/Chronicle.API/Program.cs` for `JsonStringEnumConverter` or `options.JsonSerializerOptions`. If enums are serialised as integers (default), check `src/Chronicle.Web/src/pages/plugins/PluginsPage.tsx` line with `def.type === 'int'`.

- If serialised as integer: `Number` enum value is `2` (0=Text,1=Password,2=Number,...) — change frontend check to `def.type === 2`
- If serialised as string: change frontend check to `def.type === 'Number'`

Fix the PluginsPage.tsx check to match actual serialisation so the threshold input renders as `<input type="number">`.

**Step 6: Build to confirm no compile errors**

```bash
cd src/Chronicle.API
dotnet build
```

Expected: `Build succeeded. 0 Error(s)`

**Step 7: Commit**

```bash
git add src/Chronicle.Plugins/IFileScannerPlugin.cs
# plus the concrete implementation file(s)
git commit -m "feat(scan): add ConfidenceThreshold to IFileScannerPlugin settings schema"
```

---

## Task 3: Wire `ConfidenceThreshold` into `FileScanService`

**Files:**
- Modify: `src/Chronicle.Services/FileScanService.cs`

**Context:** `FileScanService.ScanAsync()` currently uses `request.ConfidenceThreshold` (default 80 from the DTO). We now want it to fall back to the loaded plugin's `ConfidenceThreshold` setting when the caller doesn't specify one. The `ConfidenceThreshold` default on `FileScanRequest` and `FileScanRequestDto` becomes irrelevant but can stay for backwards compatibility.

**Step 1: Write the failing unit test**

In `tests/Chronicle.Tests.Unit/`, find or create `FileScanServiceTests.cs`. Add:

```csharp
[Fact]
public async Task ScanAsync_UsesPluginThreshold_WhenRequestThresholdIsDefault()
{
    // Arrange: mock scanner plugin with ConfidenceThreshold = 90
    var mockScanner = new Mock<IFileScannerPlugin>();
    mockScanner.Setup(s => s.ConfidenceThreshold).Returns(90);
    mockScanner.Setup(s => s.GetSupportedMediaTypes()).Returns(
        [new MediaTypeSupport { MediaTypeName = "Movies" }]);
    mockScanner.Setup(s => s.ScanDirectoryAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
        .ReturnsAsync([
            new ScannedFile { FilePath = "/a.mkv", ParsedTitle = "Film A", ConfidenceScore = 95 },
            new ScannedFile { FilePath = "/b.mkv", ParsedTitle = "Film B", ConfidenceScore = 85 },
        ]);

    // ... set up in-memory DB, registry, service ...

    var request = new FileScanRequest("/movies", true, mediaTypeId: 1); // no explicit threshold

    // Act
    var result = await service.ScanAsync(request, userId: 1);

    // Assert: only Film A (score 95) passes threshold 90; Film B (score 85) is skipped
    Assert.Equal(1, result.Added);
    Assert.Equal(1, result.Skipped);
}
```

Run: `dotnet test tests/Chronicle.Tests.Unit/ --filter "FileScanServiceTests" -v`
Expected: FAIL (test infrastructure may need wiring up first)

**Step 2: Update `ScanAsync` to use plugin threshold**

In `FileScanService.ScanAsync()`, change the threshold variable:

```csharp
// Replace:
if (file.ConfidenceScore < request.ConfidenceThreshold)

// With:
var threshold = request.ConfidenceThreshold != 80
    ? request.ConfidenceThreshold      // caller explicitly set it
    : scanner.ConfidenceThreshold;     // fall back to plugin setting

// ... later in the loop:
if (file.ConfidenceScore < threshold)
```

**Step 3: Run tests**

```bash
dotnet test tests/Chronicle.Tests.Unit/ --filter "FileScanServiceTests" -v
```

Expected: PASS

**Step 4: Run full suite**

```bash
cd tests && dotnet test --verbosity normal
```

Expected: all previously passing tests still pass.

**Step 5: Commit**

```bash
git add src/Chronicle.Services/FileScanService.cs tests/
git commit -m "feat(scan): FileScanService reads confidence threshold from plugin settings"
```

---

## Task 4: `IScanFolderService` and `ScanFolderService`

**Files:**
- Create: `src/Chronicle.Services/IScanFolderService.cs`
- Create: `src/Chronicle.Services/ScanFolderService.cs`

**Step 1: Write failing tests for ScanFolderService**

In `tests/Chronicle.Tests.Unit/ScanFolderServiceTests.cs`:

```csharp
public class ScanFolderServiceTests
{
    // Use EF InMemory DB (see existing test patterns in the test project)

    [Fact]
    public async Task CreateAsync_ReturnsSavedFolder()
    {
        // Arrange
        var db = CreateInMemoryDb();
        var svc = new ScanFolderService(db);
        SeedMediaType(db, id: 1, name: "Movies");

        // Act
        var folder = await svc.CreateAsync(new CreateScanFolderRequest("/mnt/movies", 1, true));

        // Assert
        Assert.NotEqual(0, folder.Id);
        Assert.Equal("/mnt/movies", folder.Path);
        Assert.True(folder.IsEnabled);
    }

    [Fact]
    public async Task ValidatePathAsync_ReturnsFalse_WhenDirectoryDoesNotExist()
    {
        var db = CreateInMemoryDb();
        var svc = new ScanFolderService(db);
        var result = await svc.ValidatePathAsync("/nonexistent/path/xyz123");
        Assert.False(result.Valid);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task DeleteAsync_Throws_WhenFolderNotFound()
    {
        var db = CreateInMemoryDb();
        var svc = new ScanFolderService(db);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.DeleteAsync(999));
    }
}
```

Run: `dotnet test tests/Chronicle.Tests.Unit/ --filter "ScanFolderServiceTests" -v`
Expected: FAIL (types not defined yet)

**Step 2: Define the service interface and request/response models**

```csharp
// src/Chronicle.Services/IScanFolderService.cs
using Chronicle.Core.Models;

namespace Chronicle.Services;

public record CreateScanFolderRequest(string Path, int MediaTypeId, bool Recursive);
public record UpdateScanFolderRequest(string Path, int MediaTypeId, bool Recursive, bool IsEnabled);
public record PathValidationResult(bool Valid, string? Error);

public interface IScanFolderService
{
    Task<List<ScanFolder>> GetAllAsync(CancellationToken ct = default);
    Task<ScanFolder> CreateAsync(CreateScanFolderRequest request, CancellationToken ct = default);
    Task<ScanFolder> UpdateAsync(int id, UpdateScanFolderRequest request, CancellationToken ct = default);
    Task DeleteAsync(int id, CancellationToken ct = default);
    Task<PathValidationResult> ValidatePathAsync(string path, CancellationToken ct = default);
}
```

**Step 3: Implement `ScanFolderService`**

```csharp
// src/Chronicle.Services/ScanFolderService.cs
using Chronicle.Core.Models;
using Chronicle.Data;
using Microsoft.EntityFrameworkCore;

namespace Chronicle.Services;

public class ScanFolderService : IScanFolderService
{
    private readonly ChronicleDbContext _db;

    public ScanFolderService(ChronicleDbContext db) => _db = db;

    public Task<List<ScanFolder>> GetAllAsync(CancellationToken ct = default) =>
        _db.ScanFolders.Include(f => f.MediaType).OrderBy(f => f.Path).ToListAsync(ct);

    public async Task<ScanFolder> CreateAsync(CreateScanFolderRequest request, CancellationToken ct = default)
    {
        var folder = new ScanFolder
        {
            Path       = request.Path,
            MediaTypeId = request.MediaTypeId,
            Recursive  = request.Recursive,
            IsEnabled  = true,
            CreatedAt  = DateTime.UtcNow,
        };
        _db.ScanFolders.Add(folder);
        await _db.SaveChangesAsync(ct);
        await _db.Entry(folder).Reference(f => f.MediaType).LoadAsync(ct);
        return folder;
    }

    public async Task<ScanFolder> UpdateAsync(int id, UpdateScanFolderRequest request, CancellationToken ct = default)
    {
        var folder = await _db.ScanFolders.FindAsync([id], ct)
            ?? throw new InvalidOperationException($"Scan folder {id} not found.");
        folder.Path        = request.Path;
        folder.MediaTypeId = request.MediaTypeId;
        folder.Recursive   = request.Recursive;
        folder.IsEnabled   = request.IsEnabled;
        await _db.SaveChangesAsync(ct);
        await _db.Entry(folder).Reference(f => f.MediaType).LoadAsync(ct);
        return folder;
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var folder = await _db.ScanFolders.FindAsync([id], ct)
            ?? throw new InvalidOperationException($"Scan folder {id} not found.");
        _db.ScanFolders.Remove(folder);
        await _db.SaveChangesAsync(ct);
    }

    public Task<PathValidationResult> ValidatePathAsync(string path, CancellationToken ct = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return Task.FromResult(new PathValidationResult(false, "Path cannot be empty."));

            if (!Directory.Exists(path))
                return Task.FromResult(new PathValidationResult(false, $"Directory does not exist or is not accessible: {path}"));

            // Probe read access by listing top-level entries
            _ = Directory.EnumerateFileSystemEntries(path).Take(1).ToList();
            return Task.FromResult(new PathValidationResult(true, null));
        }
        catch (UnauthorizedAccessException)
        {
            return Task.FromResult(new PathValidationResult(false, $"Chronicle does not have permission to read: {path}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(new PathValidationResult(false, ex.Message));
        }
    }
}
```

**Step 4: Run the tests**

```bash
dotnet test tests/Chronicle.Tests.Unit/ --filter "ScanFolderServiceTests" -v
```

Expected: PASS

**Step 5: Commit**

```bash
git add src/Chronicle.Services/IScanFolderService.cs src/Chronicle.Services/ScanFolderService.cs tests/
git commit -m "feat(scan): ScanFolderService with CRUD and path validation"
```

---

## Task 5: ScanFolderController and DTOs

**Files:**
- Create: `src/Chronicle.API/DTOs/ScanFolderDTOs.cs`
- Create: `src/Chronicle.API/Controllers/ScanFolderController.cs`

**Step 1: Create DTOs**

```csharp
// src/Chronicle.API/DTOs/ScanFolderDTOs.cs
using System.ComponentModel.DataAnnotations;

namespace Chronicle.API.DTOs;

public record ScanFolderDto(
    int Id,
    string Path,
    int MediaTypeId,
    string MediaTypeName,
    bool Recursive,
    bool IsEnabled,
    DateTime CreatedAt,
    DateTime? LastScannedAt
);

public record CreateScanFolderDto(
    [Required] string Path,
    [Required] int MediaTypeId,
    bool Recursive = true
);

public record UpdateScanFolderDto(
    [Required] string Path,
    [Required] int MediaTypeId,
    bool Recursive = true,
    bool IsEnabled = true
);

public record ValidatePathDto(
    [Required] string Path
);

public record PathValidationResultDto(
    bool Valid,
    string? Error
);
```

**Step 2: Write integration tests**

In `tests/Chronicle.Tests.Integration/ScanFolderControllerTests.cs`:

```csharp
public class ScanFolderControllerTests : IClassFixture<ChronicleApiFactory>
{
    private readonly HttpClient _client;
    private readonly ChronicleApiFactory _factory;

    public ScanFolderControllerTests(ChronicleApiFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetAll_ReturnsEmptyList_Initially()
    {
        await AuthHelper.LoginAsAdmin(_client, _factory);
        var resp = await _client.GetAsync("/api/v1/scan-folders");
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<ApiResponseList<ScanFolderDto>>();
        Assert.NotNull(body?.Data);
        Assert.Empty(body.Data);
    }

    [Fact]
    public async Task Create_ThenGet_ReturnsSavedFolder()
    {
        await AuthHelper.LoginAsAdmin(_client, _factory);
        // Use a path that exists on the test runner (temp dir)
        var path = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var payload = new { path, mediaTypeId = 1, recursive = true };

        var createResp = await _client.PostAsJsonAsync("/api/v1/scan-folders", payload);
        createResp.EnsureSuccessStatusCode();

        var listResp = await _client.GetAsync("/api/v1/scan-folders");
        var body = await listResp.Content.ReadFromJsonAsync<ApiResponseList<ScanFolderDto>>();
        Assert.Contains(body!.Data, f => f.Path == path);
    }

    [Fact]
    public async Task ValidatePath_ReturnsValid_ForTempDir()
    {
        await AuthHelper.LoginAsAdmin(_client, _factory);
        var path = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var resp = await _client.PostAsJsonAsync("/api/v1/scan/validate-path", new { path });
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<ApiResponse<PathValidationResultDto>>();
        Assert.True(body?.Data?.Valid);
    }
}
```

Run: `dotnet test tests/Chronicle.Tests.Integration/ --filter "ScanFolderController" -v`
Expected: FAIL (controller not created yet)

**Step 3: Create the controller**

```csharp
// src/Chronicle.API/Controllers/ScanFolderController.cs
using Chronicle.API.DTOs;
using Chronicle.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Chronicle.API.Controllers;

[ApiController]
[Route("api/v1/scan-folders")]
[Authorize]
public class ScanFolderController : ControllerBase
{
    private readonly IScanFolderService _svc;

    public ScanFolderController(IScanFolderService svc) => _svc = svc;

    [HttpGet]
    public async Task<IActionResult> GetAll(CancellationToken ct)
    {
        var folders = await _svc.GetAllAsync(ct);
        return Ok(ApiResponse<List<ScanFolderDto>>.Ok(folders.Select(ToDto).ToList()));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateScanFolderDto dto, CancellationToken ct)
    {
        // Validate path exists before saving
        var validation = await _svc.ValidatePathAsync(dto.Path, ct);
        if (!validation.Valid)
            return BadRequest(ApiResponse<ScanFolderDto>.Fail("INVALID_PATH", validation.Error!));

        var folder = await _svc.CreateAsync(new(dto.Path, dto.MediaTypeId, dto.Recursive), ct);
        return Created($"/api/v1/scan-folders/{folder.Id}",
            ApiResponse<ScanFolderDto>.Ok(ToDto(folder)));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateScanFolderDto dto, CancellationToken ct)
    {
        var validation = await _svc.ValidatePathAsync(dto.Path, ct);
        if (!validation.Valid)
            return BadRequest(ApiResponse<ScanFolderDto>.Fail("INVALID_PATH", validation.Error!));

        try
        {
            var folder = await _svc.UpdateAsync(id, new(dto.Path, dto.MediaTypeId, dto.Recursive, dto.IsEnabled), ct);
            return Ok(ApiResponse<ScanFolderDto>.Ok(ToDto(folder)));
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ApiResponse<ScanFolderDto>.Fail("NOT_FOUND", ex.Message));
        }
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        try
        {
            await _svc.DeleteAsync(id, ct);
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ApiResponse<ScanFolderDto>.Fail("NOT_FOUND", ex.Message));
        }
    }

    private static ScanFolderDto ToDto(Chronicle.Core.Models.ScanFolder f) =>
        new(f.Id, f.Path, f.MediaTypeId, f.MediaType.DisplayName,
            f.Recursive, f.IsEnabled, f.CreatedAt, f.LastScannedAt);
}
```

Add `validate-path` to `FileScanController` (it belongs there logically):

```csharp
// In FileScanController.cs, add:
[HttpPost("validate-path")]
[AllowAnonymous]
public async Task<IActionResult> ValidatePath([FromBody] ValidatePathDto dto, CancellationToken ct)
{
    var result = await _scanFolderService.ValidatePathAsync(dto.Path, ct);
    return Ok(ApiResponse<PathValidationResultDto>.Ok(new(result.Valid, result.Error)));
}
```

This requires injecting `IScanFolderService` into `FileScanController` — add it to the constructor.

**Step 4: Register `ScanFolderService` in `Program.cs`**

Add alongside the other scoped services:
```csharp
builder.Services.AddScoped<IScanFolderService, ScanFolderService>();
```

**Step 5: Run integration tests**

```bash
dotnet test tests/Chronicle.Tests.Integration/ --filter "ScanFolderController" -v
```

Expected: PASS

**Step 6: Run full suite**

```bash
cd tests && dotnet test --verbosity normal
```

Expected: all previously passing tests still pass.

**Step 7: Commit**

```bash
git add src/Chronicle.API/DTOs/ScanFolderDTOs.cs src/Chronicle.API/Controllers/ src/Chronicle.API/Program.cs tests/
git commit -m "feat(scan): ScanFolderController with CRUD and path validation endpoints"
```

---

## Task 6: `ScheduledScanService`

**Files:**
- Create: `src/Chronicle.Services/ScheduledScanService.cs`
- Modify: `src/Chronicle.API/Program.cs`

**Step 1: Write a unit test**

In `tests/Chronicle.Tests.Unit/ScheduledScanServiceTests.cs`:

```csharp
[Fact]
public async Task ExecuteAsync_SkipsDisabledFolders()
{
    // Arrange: one disabled folder
    var db = CreateInMemoryDb();
    db.ScanFolders.Add(new ScanFolder { Path = "/x", MediaTypeId = 1, IsEnabled = false });
    await db.SaveChangesAsync();

    var mockScan = new Mock<IFileScanService>();
    var svc = new ScheduledScanService(CreateScopeFactory(db, mockScan.Object),
        Mock.Of<IPluginRegistry>());

    // Act
    await ((IScheduledTask)svc).ExecuteAsync(CancellationToken.None);

    // Assert: no scan was attempted
    mockScan.Verify(s => s.PreviewGroupedAsync(It.IsAny<ScanPreviewRequest>(), It.IsAny<CancellationToken>()), Times.Never);
}
```

Run: `dotnet test tests/Chronicle.Tests.Unit/ --filter "ScheduledScanServiceTests" -v`
Expected: FAIL

**Step 2: Implement `ScheduledScanService`**

```csharp
// src/Chronicle.Services/ScheduledScanService.cs
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Plugins;
using Chronicle.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Chronicle.Services;

public sealed class ScheduledScanService : IScheduledTask
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPluginRegistry _registry;
    private readonly ILogger _log = Log.ForContext<ScheduledScanService>();

    public ScheduledScanService(IServiceScopeFactory scopeFactory, IPluginRegistry registry)
    {
        _scopeFactory = scopeFactory;
        _registry = registry;
    }

    // ── IScheduledTask ──────────────────────────────────────────────────────
    public string TaskId      => "scheduled_scan";
    public string DisplayName => "Scheduled File Scan";
    public string Description => "Scans all enabled saved folders and auto-imports groups that meet the confidence threshold.";
    public string DefaultCron => "0 3 * * *"; // 3am daily

    async Task IScheduledTask.ExecuteAsync(CancellationToken ct) => await RunAsync(ct);

    // ── Core logic ──────────────────────────────────────────────────────────
    private async Task RunAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var scanService = scope.ServiceProvider.GetRequiredService<IFileScanService>();

        var folders = await db.ScanFolders
            .Include(f => f.MediaType)
            .Where(f => f.IsEnabled)
            .OrderBy(f => f.Path)
            .ToListAsync(ct);

        if (folders.Count == 0)
        {
            _log.Information("Scheduled scan: no enabled folders configured, skipping.");
            return;
        }

        _log.Information("Scheduled scan: processing {Count} folder(s).", folders.Count);

        foreach (var folder in folders)
        {
            ct.ThrowIfCancellationRequested();
            await ScanFolderAsync(db, scanService, folder, ct);
        }
    }

    private async Task ScanFolderAsync(
        ChronicleDbContext db,
        IFileScanService scanService,
        ScanFolder folder,
        CancellationToken ct)
    {
        _log.Information("Scheduled scan: scanning {Path} ({MediaType})", folder.Path, folder.MediaType.Name);

        // Resolve threshold from the active file scanner plugin, fall back to 80
        var threshold = GetConfidenceThreshold();

        try
        {
            var preview = await scanService.PreviewGroupedAsync(
                new ScanPreviewRequest(folder.Path, folder.Recursive, folder.MediaTypeId), ct);

            // Filter to groups meeting the threshold (root groups only; children come with them)
            var eligible = preview.Groups
                .Where(g => g.ConfidenceScore >= threshold)
                .ToList();

            if (eligible.Count == 0)
            {
                _log.Information("Scheduled scan: {Path} — no groups met threshold {Threshold}%.",
                    folder.Path, threshold);
            }
            else
            {
                _log.Information("Scheduled scan: {Path} — importing {Count}/{Total} groups (threshold {Threshold}%).",
                    folder.Path, eligible.Count, preview.Groups.Count, threshold);

                var importRequest = new ImportGroupsRequest(
                    eligible.Select(ToImport).ToList(),
                    folder.MediaTypeId);

                await scanService.ImportGroupsAsync(importRequest, ct);
            }

            folder.LastScannedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error(ex, "Scheduled scan: error scanning {Path}", folder.Path);
        }
    }

    private int GetConfidenceThreshold()
    {
        var scanner = _registry.GetFileScannerPlugins().FirstOrDefault();
        return scanner?.ConfidenceThreshold ?? 80;
    }

    private static ScanGroupImport ToImport(Core.Models.Scan.ScanGroup g) => new(
        g.Name,
        g.Year,
        g.PosterPath,
        g.Children.Select(ToImport).ToList(),
        g.Files,
        g.FolderPath,
        g.Number);
}
```

**Note:** `PreviewGroupedAsync` returns `ScanGroupResult`. Check if `IFileScanService` exposes this method. If not, add it to the interface. `ImportGroupsAsync` signature should accept `ImportGroupsRequest` and a `CancellationToken` — check `IFileScanService` and add overload if needed.

**Step 3: Register in `Program.cs`**

Following the exact same pattern as `MetadataRefreshService`:

```csharp
builder.Services.AddSingleton<ScheduledScanService>();
builder.Services.AddSingleton<IScheduledTask>(
    sp => sp.GetRequiredService<ScheduledScanService>());
```

**Step 4: Run tests**

```bash
dotnet test tests/Chronicle.Tests.Unit/ --filter "ScheduledScanServiceTests" -v
```

Expected: PASS

**Step 5: Build the full solution**

```bash
cd src/Chronicle.API && dotnet build
```

Expected: `Build succeeded. 0 Error(s)`

**Step 6: Commit**

```bash
git add src/Chronicle.Services/ScheduledScanService.cs src/Chronicle.API/Program.cs
git commit -m "feat(scan): ScheduledScanService — nightly auto-import at 3am"
```

---

## Task 7: Frontend types and API client

**Files:**
- Modify: `src/Chronicle.Web/src/types/index.ts`
- Create: `src/Chronicle.Web/src/api/scanFolders.ts`

**Step 1: Add types to `types/index.ts`**

Add after the existing scan types:

```typescript
// ── Scan Folders ──────────────────────────────────────────────────────────
export interface ScanFolder {
  id: number
  path: string
  mediaTypeId: number
  mediaTypeName: string
  recursive: boolean
  isEnabled: boolean
  createdAt: string
  lastScannedAt: string | null
}

export interface CreateScanFolderPayload {
  path: string
  mediaTypeId: number
  recursive: boolean
}

export interface UpdateScanFolderPayload {
  path: string
  mediaTypeId: number
  recursive: boolean
  isEnabled: boolean
}

export interface PathValidationResult {
  valid: boolean
  error: string | null
}
```

**Step 2: Create `src/Chronicle.Web/src/api/scanFolders.ts`**

```typescript
import client from './client'
import type { ApiResponse, ScanFolder, CreateScanFolderPayload, UpdateScanFolderPayload, PathValidationResult } from '@/types'

export async function getScanFolders(): Promise<ScanFolder[]> {
  const { data } = await client.get<ApiResponse<ScanFolder[]>>('/scan-folders')
  return data.data ?? []
}

export async function createScanFolder(payload: CreateScanFolderPayload): Promise<ScanFolder> {
  const { data } = await client.post<ApiResponse<ScanFolder>>('/scan-folders', payload)
  if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Failed to create scan folder')
  return data.data
}

export async function updateScanFolder(id: number, payload: UpdateScanFolderPayload): Promise<ScanFolder> {
  const { data } = await client.put<ApiResponse<ScanFolder>>(`/scan-folders/${id}`, payload)
  if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Failed to update scan folder')
  return data.data
}

export async function deleteScanFolder(id: number): Promise<void> {
  await client.delete(`/scan-folders/${id}`)
}

export async function validatePath(path: string): Promise<PathValidationResult> {
  const { data } = await client.post<ApiResponse<PathValidationResult>>('/scan/validate-path', { path })
  return data.data ?? { valid: false, error: 'Validation failed' }
}
```

**Step 3: Type-check**

```bash
cd src/Chronicle.Web && npm run type-check
```

Expected: no errors.

**Step 4: Commit**

```bash
git add src/Chronicle.Web/src/types/index.ts src/Chronicle.Web/src/api/scanFolders.ts
git commit -m "feat(scan): frontend types and API client for scan folders"
```

---

## Task 8: Saved Folders panel on ScanPage

**Files:**
- Modify: `src/Chronicle.Web/src/pages/scan/ScanPage.tsx`
- Modify: `src/Chronicle.Web/src/pages/scan/ScanPage.module.css`

**Step 1: Plan the panel behaviour**

- Rendered above the Configure form
- `savedFoldersOpen` state: `true` by default
- When `step !== 'configure'` (results showing): panel auto-collapses (`savedFoldersOpen = false`)
- Panel header: "Saved Folders (N)" with a ▾/▸ toggle chevron
- Folder list: each row shows path, media type badge, last scanned date (or "Never"), enable toggle, Edit button, Delete button, "Scan Now" button
- "Scan Now" pre-fills path + mediaTypeId + recursive in the configure form and immediately kicks off `previewMut.mutate()`
- Add row: inline form below the list (path input + media type dropdown + recursive checkbox + Validate + Save + Cancel)
- Path validation: called on blur of path input or on Save; shows inline error if invalid
- Editing: clicking Edit replaces the row with an inline edit form (same fields as add)

**Step 2: Add state and queries to `ScanPage`**

Add imports:
```typescript
import { getScanFolders, createScanFolder, updateScanFolder, deleteScanFolder, validatePath } from '@/api/scanFolders'
import type { ScanFolder, CreateScanFolderPayload } from '@/types'
```

Add state (inside `ScanPage`, after existing state declarations):
```typescript
// ── Saved folders ─────────────────────────────────────────────────────────
const [savedFoldersOpen, setSavedFoldersOpen] = useState(true)
const [editingFolderId, setEditingFolderId] = useState<number | null>(null)
const [showAddForm, setShowAddForm] = useState(false)
const [addPath, setAddPath] = useState('')
const [addMediaTypeId, setAddMediaTypeId] = useState<number | ''>('')
const [addRecursive, setAddRecursive] = useState(true)
const [addPathError, setAddPathError] = useState<string | null>(null)
const [addSaving, setAddSaving] = useState(false)
```

Add query:
```typescript
const { data: savedFolders = [], refetch: refetchFolders } = useQuery({
  queryKey: ['scan-folders'],
  queryFn: getScanFolders,
})
```

**Step 3: Auto-collapse panel when results are shown**

Add a `useEffect` that watches `step`:
```typescript
useEffect(() => {
  if (step !== 'configure') setSavedFoldersOpen(false)
}, [step])
```

**Step 4: Add handler functions**

```typescript
async function handleScanNow(folder: ScanFolder) {
  setPath(folder.path)
  setMediaTypeId(folder.mediaTypeId)
  setRecursive(folder.recursive)
  // Give React one render tick to update the controlled inputs, then scan
  setTimeout(() => previewMut.mutate(), 0)
}

async function handleDeleteFolder(id: number) {
  if (!confirm('Remove this saved folder? This does not delete any imported media.')) return
  await deleteScanFolder(id)
  void refetchFolders()
}

async function handleAddFolder() {
  setAddPathError(null)
  setAddSaving(true)
  try {
    const validation = await validatePath(addPath.trim())
    if (!validation.valid) {
      setAddPathError(validation.error ?? 'Invalid path')
      return
    }
    if (!addMediaTypeId) { setAddPathError('Select a media type.'); return }
    await createScanFolder({ path: addPath.trim(), mediaTypeId: Number(addMediaTypeId), recursive: addRecursive })
    setShowAddForm(false)
    setAddPath('')
    setAddMediaTypeId('')
    setAddRecursive(true)
    void refetchFolders()
  } finally {
    setAddSaving(false)
  }
}
```

**Step 5: Add the Saved Folders panel JSX**

Insert between the step bar and the error message (before `{error && ...}`):

```tsx
{/* ── Saved Folders panel ──────────────────────────────────────────── */}
<div className={styles.savedFoldersPanel}>
  <button
    className={styles.savedFoldersToggle}
    onClick={() => setSavedFoldersOpen(v => !v)}
    aria-expanded={savedFoldersOpen}
  >
    <span className={styles.savedFoldersTitle}>
      Saved Folders {savedFolders.length > 0 && `(${savedFolders.length})`}
    </span>
    <span className={styles.chevron}>{savedFoldersOpen ? '▾' : '▸'}</span>
  </button>

  {savedFoldersOpen && (
    <div className={styles.savedFoldersList}>
      {savedFolders.length === 0 && !showAddForm && (
        <p className={styles.savedFoldersEmpty}>No saved folders yet.</p>
      )}

      {savedFolders.map(folder => (
        editingFolderId === folder.id
          ? <SavedFolderEditRow
              key={folder.id}
              folder={folder}
              mediaTypes={supportedTypes}
              onSave={async (payload) => {
                await updateScanFolder(folder.id, { ...payload, isEnabled: folder.isEnabled })
                setEditingFolderId(null)
                void refetchFolders()
              }}
              onCancel={() => setEditingFolderId(null)}
            />
          : <div key={folder.id} className={styles.savedFolderRow}>
              <div className={styles.savedFolderInfo}>
                <span className={styles.savedFolderPath} title={folder.path}>{folder.path}</span>
                <span className={styles.savedFolderMeta}>
                  <span className={styles.mediaTypeBadge}>{folder.mediaTypeName}</span>
                  {folder.recursive && <span className={styles.recursiveBadge}>recursive</span>}
                  <span className={styles.lastScanned}>
                    {folder.lastScannedAt
                      ? `Last scanned ${new Date(folder.lastScannedAt).toLocaleDateString()}`
                      : 'Never scanned'}
                  </span>
                </span>
              </div>
              <div className={styles.savedFolderActions}>
                <button
                  className={styles.scanNowBtn}
                  onClick={() => handleScanNow(folder)}
                  disabled={previewMut.isPending}
                  title="Pre-fill form and scan now"
                >
                  Scan Now
                </button>
                <button className={styles.editFolderBtn} onClick={() => setEditingFolderId(folder.id)}>
                  Edit
                </button>
                <button className={styles.deleteFolderBtn} onClick={() => handleDeleteFolder(folder.id)}>
                  Remove
                </button>
              </div>
            </div>
      ))}

      {showAddForm ? (
        <div className={styles.addFolderForm}>
          <PathInput
            className={styles.addFolderPath}
            placeholder="C:\Movies or /mnt/media/movies"
            value={addPath}
            onChange={setAddPath}
          />
          <select
            className={styles.addFolderType}
            value={addMediaTypeId}
            onChange={e => setAddMediaTypeId(e.target.value === '' ? '' : Number(e.target.value))}
          >
            <option value="">— type —</option>
            {supportedTypes.map(t => <option key={t.id} value={t.id}>{t.displayName}</option>)}
          </select>
          <label className={styles.addFolderRecursive}>
            <input type="checkbox" checked={addRecursive} onChange={e => setAddRecursive(e.target.checked)} />
            Recursive
          </label>
          <button className={styles.saveFolderBtn} onClick={handleAddFolder} disabled={addSaving || !addPath.trim()}>
            {addSaving ? 'Saving…' : 'Save'}
          </button>
          <button className={styles.cancelFolderBtn} onClick={() => { setShowAddForm(false); setAddPath(''); setAddPathError(null) }}>
            Cancel
          </button>
          {addPathError && <p className={styles.addFolderError}>{addPathError}</p>}
        </div>
      ) : (
        <button className={styles.addFolderBtn} onClick={() => setShowAddForm(true)}>
          + Add Folder
        </button>
      )}
    </div>
  )}
</div>
```

**Step 6: Implement `SavedFolderEditRow` as a local component**

Add below `ScanPage` in the same file:

```tsx
interface SavedFolderEditRowProps {
  folder: ScanFolder
  mediaTypes: MediaTypeOption[]
  onSave: (payload: { path: string; mediaTypeId: number; recursive: boolean }) => Promise<void>
  onCancel: () => void
}

function SavedFolderEditRow({ folder, mediaTypes, onSave, onCancel }: SavedFolderEditRowProps) {
  const [path, setPath] = useState(folder.path)
  const [mediaTypeId, setMediaTypeId] = useState<number>(folder.mediaTypeId)
  const [recursive, setRecursive] = useState(folder.recursive)
  const [error, setError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)

  async function handleSave() {
    setError(null)
    setSaving(true)
    try {
      const validation = await validatePath(path.trim())
      if (!validation.valid) { setError(validation.error ?? 'Invalid path'); return }
      await onSave({ path: path.trim(), mediaTypeId, recursive })
    } catch (e) {
      setError(e instanceof Error ? e.message : 'Save failed')
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className={styles.addFolderForm}>
      <PathInput className={styles.addFolderPath} value={path} onChange={setPath} />
      <select
        className={styles.addFolderType}
        value={mediaTypeId}
        onChange={e => setMediaTypeId(Number(e.target.value))}
      >
        {mediaTypes.map(t => <option key={t.id} value={t.id}>{t.displayName}</option>)}
      </select>
      <label className={styles.addFolderRecursive}>
        <input type="checkbox" checked={recursive} onChange={e => setRecursive(e.target.checked)} />
        Recursive
      </label>
      <button className={styles.saveFolderBtn} onClick={handleSave} disabled={saving}>
        {saving ? 'Saving…' : 'Save'}
      </button>
      <button className={styles.cancelFolderBtn} onClick={onCancel}>Cancel</button>
      {error && <p className={styles.addFolderError}>{error}</p>}
    </div>
  )
}
```

**Step 7: Add CSS to `ScanPage.module.css`**

Add styles for:
- `.savedFoldersPanel` — card-style container matching `.formCard`
- `.savedFoldersToggle` — full-width button with flex layout, no border
- `.savedFoldersTitle` — semibold text
- `.chevron` — right-aligned
- `.savedFoldersList` — padding inside panel
- `.savedFoldersEmpty` — muted hint text
- `.savedFolderRow` — flex row, space-between, items-center, border-bottom
- `.savedFolderInfo` — flex-column, gap 4px
- `.savedFolderPath` — truncate with ellipsis, max-width
- `.savedFolderMeta` — flex row, gap 8px, small muted text
- `.mediaTypeBadge` — teal pill badge
- `.recursiveBadge` — grey pill badge
- `.lastScanned` — muted italic text
- `.savedFolderActions` — flex row, gap 8px
- `.scanNowBtn` — teal outline button, small
- `.editFolderBtn` — grey outline button, small
- `.deleteFolderBtn` — red-tinted outline button, small
- `.addFolderForm` — flex row, gap 8px, align-center, wrap
- `.addFolderPath` — flex 1, min-width 200px
- `.addFolderType` — width 140px
- `.addFolderRecursive` — flex row, gap 4px, align-center
- `.saveFolderBtn` — teal filled button, small
- `.cancelFolderBtn` — grey button, small
- `.addFolderError` — red error text, full-width
- `.addFolderBtn` — dashed border button, full-width, teal text

Match the existing visual style in the file (dark teal theme, same font sizes and border radii as other elements).

**Step 8: Type-check and lint**

```bash
cd src/Chronicle.Web && npm run type-check && npm run lint
```

Expected: no errors.

**Step 9: Commit**

```bash
git add src/Chronicle.Web/src/pages/scan/
git commit -m "feat(scan): Saved Folders panel on Scan page with inline CRUD"
```

---

## Task 9: Final integration and clean-up

**Step 1: Run full test suite**

```bash
cd tests && dotnet test --verbosity normal
```

Expected: all tests pass.

**Step 2: Commit the 3-step scan fix from earlier in this session**

If not yet committed, stage and commit the ScanPage step collapse:
```bash
git add src/Chronicle.Web/src/pages/scan/ScanPage.tsx
git commit -m "fix(scan): collapse Preview+Review into single Review step"
```

**Step 3: Push and create release**

```bash
git push
```

**Step 4: Update README and MEMORY.md**

Update `README.md` "What's Built" section to mention persistent scan folders and nightly scheduled scan. Update `docs/plans/MEMORY.md` backlog to mark persistent scan folders as complete.
