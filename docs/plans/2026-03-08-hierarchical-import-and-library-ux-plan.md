# Hierarchical Import, Library UX & Plugin Settings — Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Fix TV/Music library grouping (Show→Season→Episode hierarchy), add plugin settings UI for TMDB API key, fix media detail page bugs, and improve library UX with collapsible sections.

**Architecture:** FileScanner plugin gains TagLib# embedded-tag reading and TV filename parsing. Chronicle's FileScanService builds parent/child MediaItem trees from the enriched ScannedFile data. Library API gains a rootOnly filter. Frontend gets collapsible sections (localStorage) and a plugin settings form.

**Tech Stack:** .NET 9 / C#, TagLib# NuGet, React 18 / TypeScript, localStorage

---

## Repos involved

- **Chronicle** (main): `W:\Scripts\Chronicle` — backend + frontend
- **Chronicle worktree** (active branch): `W:\Scripts\Chronicle\.claude\worktrees\frosty-allen`
- **FileScanner plugin**: `W:\Scripts\Chronicle.Plugin.FileScanner`

> All Chronicle backend/frontend edits go in the **worktree**. All FileScanner edits go in the FileScanner repo. Commit & push each repo separately.

---

## Task 1: Extend ScannedFile model

**Files:**
- Modify: `src/Chronicle.Plugins/ScannedFile.cs` (both main repo AND worktree — edit both)

**Step 1: Add new fields to `ScannedFile.cs`**

Open `W:\Scripts\Chronicle\.claude\worktrees\frosty-allen\src\Chronicle.Plugins\ScannedFile.cs` and append the following properties before the closing `}`:

```csharp
    // ── TV / Episode hierarchy ──────────────────────────────────────────────
    /// <summary>Show name parsed from filename before the SxxExx code.</summary>
    public string? ShowTitle { get; set; }
    /// <summary>Season number parsed from SxxExx / NxNN pattern.</summary>
    public int? SeasonNumber { get; set; }
    /// <summary>Episode number parsed from SxxExx / NxNN pattern.</summary>
    public int? EpisodeNumber { get; set; }
    /// <summary>Episode title — text after the SxxExx code, if present in filename.</summary>
    public string? EpisodeTitle { get; set; }

    // ── Music / Audio tags ──────────────────────────────────────────────────
    public string? AudioArtist { get; set; }
    public string? AudioAlbumArtist { get; set; }
    public string? AudioAlbum { get; set; }
    public int? AudioTrackNumber { get; set; }
    public int? AudioDiscNumber { get; set; }
    public int? AudioYear { get; set; }
    public string? AudioGenre { get; set; }

    // ── Container / embedded video tags ────────────────────────────────────
    public string? ContainerTitle { get; set; }
    public int? ContainerYear { get; set; }
    public string? ContainerDescription { get; set; }

    // ── Technical ───────────────────────────────────────────────────────────
    public int? DurationSeconds { get; set; }
    public long? FileSizeBytes { get; set; }
```

**Step 2: Apply the same edit to the main repo**

```
W:\Scripts\Chronicle\src\Chronicle.Plugins\ScannedFile.cs
```
Identical change — the FileScanner plugin references this path via its csproj.

**Step 3: Verify it compiles**

```bash
cd W:\Scripts\Chronicle\.claude\worktrees\frosty-allen
dotnet build src/Chronicle.Plugins/Chronicle.Plugins.csproj
```
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

**Step 4: Commit worktree**

```bash
cd W:\Scripts\Chronicle\.claude\worktrees\frosty-allen
git add src/Chronicle.Plugins/ScannedFile.cs
git commit -m "feat(plugins): extend ScannedFile with hierarchy, audio, container, and technical fields"
```

**Step 5: Commit main repo**

```bash
cd W:\Scripts\Chronicle
git add src/Chronicle.Plugins/ScannedFile.cs
git commit -m "feat(plugins): extend ScannedFile with hierarchy, audio, container, and technical fields"
git push origin main
```

---

## Task 2: FileScanner — Add TagLib# and EmbeddedTagReader

**Files:**
- Modify: `W:\Scripts\Chronicle.Plugin.FileScanner\Chronicle.Plugin.FileScanner.csproj`
- Create: `W:\Scripts\Chronicle.Plugin.FileScanner\EmbeddedTagReader.cs`

**Step 1: Add TagLib# dependency**

In `Chronicle.Plugin.FileScanner.csproj`, add inside `<ItemGroup>` with the existing ProjectReference:

```xml
<PackageReference Include="TagLibSharp" Version="2.3.0" />
```

**Step 2: Restore packages**

```bash
cd W:\Scripts\Chronicle.Plugin.FileScanner
dotnet restore
```
Expected: `Restore succeeded.`

**Step 3: Create `EmbeddedTagReader.cs`**

```csharp
using TagLib;

namespace Chronicle.Plugin.FileScanner;

/// <summary>
/// Reads embedded metadata tags from media files using TagLib#.
/// Supports MP3 (ID3), FLAC (Vorbis), OGG, M4A, MP4, MKV, AVI, WAV, WMA.
/// Never throws — returns empty struct on any read failure.
/// </summary>
internal static class EmbeddedTagReader
{
    public readonly struct EmbeddedTags
    {
        public string?  AudioArtist       { get; init; }
        public string?  AudioAlbumArtist  { get; init; }
        public string?  AudioAlbum        { get; init; }
        public int?     AudioTrackNumber  { get; init; }
        public int?     AudioDiscNumber   { get; init; }
        public int?     AudioYear         { get; init; }
        public string?  AudioGenre        { get; init; }
        public string?  ContainerTitle    { get; init; }
        public int?     ContainerYear     { get; init; }
        public string?  ContainerDesc     { get; init; }
        public int?     DurationSeconds   { get; init; }
        public long?    FileSizeBytes     { get; init; }
    }

    public static EmbeddedTags Read(string filePath)
    {
        try
        {
            using var file = TagLib.File.Create(filePath);
            var tag  = file.Tag;
            var prop = file.Properties;

            return new EmbeddedTags
            {
                AudioArtist      = NullIfEmpty(tag.FirstPerformer),
                AudioAlbumArtist = NullIfEmpty(tag.FirstAlbumArtist),
                AudioAlbum       = NullIfEmpty(tag.Album),
                AudioTrackNumber = tag.Track > 0 ? (int?)tag.Track : null,
                AudioDiscNumber  = tag.Disc  > 0 ? (int?)tag.Disc  : null,
                AudioYear        = tag.Year  > 0 ? (int?)tag.Year  : null,
                AudioGenre       = NullIfEmpty(tag.FirstGenre),
                ContainerTitle   = NullIfEmpty(tag.Title),
                ContainerYear    = tag.Year  > 0 ? (int?)tag.Year  : null,
                ContainerDesc    = NullIfEmpty(tag.Description),
                DurationSeconds  = prop is not null ? (int)prop.Duration.TotalSeconds : null,
                FileSizeBytes    = new FileInfo(filePath).Length,
            };
        }
        catch
        {
            // Unsupported format, corrupted file, access denied — ignore silently
            return default;
        }
    }

    private static string? NullIfEmpty(string? s) =>
        string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
```

**Step 4: Build to verify it compiles**

```bash
cd W:\Scripts\Chronicle.Plugin.FileScanner
dotnet build
```
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

**Step 5: Commit**

```bash
cd W:\Scripts\Chronicle.Plugin.FileScanner
git add Chronicle.Plugin.FileScanner.csproj EmbeddedTagReader.cs
git commit -m "feat(scanner): add TagLib# dependency and EmbeddedTagReader for embedded media tags"
```

---

## Task 3: FileScanner — Update FileNameParser for TV hierarchy + audio

**Files:**
- Modify: `W:\Scripts\Chronicle.Plugin.FileScanner\FileNameParser.cs`

**Step 1: Write unit test first**

Create `W:\Scripts\Chronicle.Plugin.FileScanner\FileNameParserTests.cs`:

```csharp
// Quick smoke tests — run with: dotnet script or paste into a top-level statements file
// These are manual verification tests; a proper xUnit project can be added later.
// For now, verify by running the build and checking output of a test program.
```

Since the plugin has no test project yet, do a quick manual verification in Step 4 instead. Add a test project as a follow-up backlog item.

**Step 2: Update `FileNameParser.cs`**

Replace the entire file content:

```csharp
using System.Text.RegularExpressions;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.FileScanner;

/// <summary>
/// Parses media file names into structured metadata including TV hierarchy fields.
/// All methods are static and allocation-minimal.
/// </summary>
internal static class FileNameParser
{
    // ── Supported extensions ──────────────────────────────────────────────────
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".mpg", ".mpeg"
    };

    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".ogg", ".m4a", ".aac", ".wma", ".opus", ".wav", ".ape"
    };

    // ── Movie filename patterns ───────────────────────────────────────────────
    private static readonly Regex TitleYearParens =
        new(@"^(.+?)\s*\((\d{4})\)", RegexOptions.Compiled);

    private static readonly Regex TitleYearSpaced =
        new(@"^(.+?)[\.\s](\d{4})(?:[\.\s]|$)", RegexOptions.Compiled);

    // ── TV episode patterns (capturing groups) ────────────────────────────────
    // Matches: S01E02, S1E2, s01e02  →  groups: 1=season, 2=episode
    private static readonly Regex SxxExx =
        new(@"^(.*?)[. _\-][Ss](\d{1,2})[Ee](\d{1,2})(?:[. _\-](.+?))?(?:\.\w+)?$",
            RegexOptions.Compiled);

    // Matches: 1x02, 01x02  →  groups: 1=season, 2=episode (less common)
    private static readonly Regex NxNN =
        new(@"^(.*?)[. _](\d{1,2})[xX](\d{2})(?:[. _](.+?))?(?:\.\w+)?$",
            RegexOptions.Compiled);

    // ── Public API ────────────────────────────────────────────────────────────

    public static bool IsVideoFile(string filePath) =>
        VideoExtensions.Contains(Path.GetExtension(filePath));

    public static bool IsAudioFile(string filePath) =>
        AudioExtensions.Contains(Path.GetExtension(filePath));

    /// <summary>Parses a TV episode filename into show/season/episode fields.</summary>
    public static ScannedFile ParseTv(string filePath)
    {
        var stem = Path.GetFileNameWithoutExtension(filePath);

        // Try SxxExx pattern first (most common: "Show Name S01E02 Episode Title")
        var m = SxxExx.Match(stem);
        if (m.Success)
        {
            var showTitle    = CleanTitle(m.Groups[1].Value);
            var seasonNum    = int.Parse(m.Groups[2].Value);
            var episodeNum   = int.Parse(m.Groups[3].Value);
            var episodeTitle = m.Groups[4].Success ? CleanTitle(m.Groups[4].Value) : null;

            return new ScannedFile
            {
                FilePath       = filePath,
                ParsedTitle    = episodeTitle ?? showTitle,
                ParsedYear     = null,
                ConfidenceScore = 90,
                MediaTypeHint  = "tv",
                ShowTitle      = showTitle,
                SeasonNumber   = seasonNum,
                EpisodeNumber  = episodeNum,
                EpisodeTitle   = episodeTitle,
            };
        }

        // Try NxNN pattern ("Show Name 1x02")
        m = NxNN.Match(stem);
        if (m.Success)
        {
            var showTitle  = CleanTitle(m.Groups[1].Value);
            var seasonNum  = int.Parse(m.Groups[2].Value);
            var episodeNum = int.Parse(m.Groups[3].Value);

            return new ScannedFile
            {
                FilePath       = filePath,
                ParsedTitle    = showTitle,
                ConfidenceScore = 75,
                MediaTypeHint  = "tv",
                ShowTitle      = showTitle,
                SeasonNumber   = seasonNum,
                EpisodeNumber  = episodeNum,
            };
        }

        // Fallback: no episode code found but directory says it's TV
        return new ScannedFile
        {
            FilePath       = filePath,
            ParsedTitle    = CleanTitle(stem),
            ConfidenceScore = 50,
            MediaTypeHint  = "tv",
            ShowTitle      = CleanTitle(stem),
        };
    }

    /// <summary>Parses a movie filename (existing behaviour, unchanged).</summary>
    public static ScannedFile Parse(string filePath)
    {
        var stem = Path.GetFileNameWithoutExtension(filePath);
        var mediaTypeHint = IsTvDirectory(filePath) ? "tv" : "movies";

        if (mediaTypeHint == "tv")
            return ParseTv(filePath);

        var m = TitleYearParens.Match(stem);
        if (m.Success)
            return new ScannedFile
            {
                FilePath        = filePath,
                ParsedTitle     = CleanTitle(m.Groups[1].Value),
                ParsedYear      = int.Parse(m.Groups[2].Value),
                ConfidenceScore = 85,
                MediaTypeHint   = "movies",
            };

        m = TitleYearSpaced.Match(stem);
        if (m.Success && IsReasonableYear(m.Groups[2].Value))
            return new ScannedFile
            {
                FilePath        = filePath,
                ParsedTitle     = CleanTitle(m.Groups[1].Value),
                ParsedYear      = int.Parse(m.Groups[2].Value),
                ConfidenceScore = 70,
                MediaTypeHint   = "movies",
            };

        return new ScannedFile
        {
            FilePath        = filePath,
            ParsedTitle     = CleanTitle(stem),
            ConfidenceScore = 50,
            MediaTypeHint   = "movies",
        };
    }

    /// <summary>Parses an audio filename (title from stem, no year pattern).</summary>
    public static ScannedFile ParseAudio(string filePath)
    {
        var stem = Path.GetFileNameWithoutExtension(filePath);
        return new ScannedFile
        {
            FilePath        = filePath,
            ParsedTitle     = CleanTitle(stem),
            ConfidenceScore = 50,
            MediaTypeHint   = "music",
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsTvDirectory(string filePath)
    {
        var stem = Path.GetFileNameWithoutExtension(filePath);
        // Check filename for SxxExx / NxNN before checking directory
        if (SxxExx.IsMatch(stem) || NxNN.IsMatch(stem)) return true;
        var dir = Path.GetDirectoryName(filePath) ?? string.Empty;
        return dir.Contains("Season", StringComparison.OrdinalIgnoreCase) ||
               dir.Contains("Series", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsReasonableYear(string value) =>
        int.TryParse(value, out var year) && year >= 1888 && year <= DateTime.UtcNow.Year + 2;

    private static string CleanTitle(string raw)
    {
        var cleaned = raw.Contains(' ')
            ? raw
            : raw.Replace('.', ' ').Replace('_', ' ');

        cleaned = Regex.Replace(cleaned,
            @"\s*(1080p|720p|4k|2160p|bluray|blu-ray|bdrip|webrip|web-dl|hdtv|dvdrip|xvid|x264|x265|hevc|aac|ac3|dts)\s*.*$",
            string.Empty, RegexOptions.IgnoreCase);

        return cleaned.Trim();
    }
}
```

**Step 3: Build**

```bash
cd W:\Scripts\Chronicle.Plugin.FileScanner
dotnet build
```
Expected: `Build succeeded.`

**Step 4: Manual verification — create a temp test file**

```bash
cd W:\Scripts\Chronicle.Plugin.FileScanner
# Create a quick test program
cat > TestParse.cs << 'EOF'
using Chronicle.Plugin.FileScanner;
// TV parse test
var tv = FileNameParser.ParseTv(@"I:\TV\21st Century Renovation\Season 1\21st Century Renovation S01E02 The Reveal.mkv");
Console.WriteLine($"ShowTitle: {tv.ShowTitle}");       // 21st Century Renovation
Console.WriteLine($"Season: {tv.SeasonNumber}");       // 1
Console.WriteLine($"Episode: {tv.EpisodeNumber}");     // 2
Console.WriteLine($"EpisodeTitle: {tv.EpisodeTitle}"); // The Reveal
EOF
```

(This is a quick sanity check — delete `TestParse.cs` before committing.)

**Step 5: Commit**

```bash
cd W:\Scripts\Chronicle.Plugin.FileScanner
git add FileNameParser.cs
git commit -m "feat(parser): extract season/episode numbers, add ParseTv() and ParseAudio() methods"
```

---

## Task 4: FileScanner — Update FileScannerPlugin + version bump

**Files:**
- Modify: `W:\Scripts\Chronicle.Plugin.FileScanner\FileScannerPlugin.cs`
- Modify: `W:\Scripts\Chronicle.Plugin.FileScanner\manifest.json`

**Step 1: Update `FileScannerPlugin.cs`**

Replace the full file:

```csharp
using Chronicle.Plugins;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.FileScanner;

public sealed class FileScannerPlugin : IFileScannerPlugin
{
    public string PluginId    => "chronicle.plugin.filescanner";
    public string Name        => "File Scanner";
    public string Version     => "1.1.0";
    public string Author      => "Chronicle";
    public string Description => "Scans local directories for media files. Reads embedded tags (ID3, Vorbis, MP4, MKV) and parses TV episode structure from filenames.";

    public MediaTypeSupport[] GetSupportedMediaTypes() =>
    [
        new MediaTypeSupport { MediaTypeName = "movies", DefaultPriority = 1 },
        new MediaTypeSupport { MediaTypeName = "tv",     DefaultPriority = 1 },
        new MediaTypeSupport { MediaTypeName = "music",  DefaultPriority = 1 },
    ];

    public PluginSettingsSchema GetSettingsSchema() => new();

    public void Configure(IReadOnlyDictionary<string, string> settings) { }

    public Task<List<ScannedFile>> ScanDirectoryAsync(
        string path,
        bool recursive,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"Scan path does not exist: {path}");

        var searchOption = recursive
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;

        var results = new List<ScannedFile>();

        foreach (var file in Directory.EnumerateFiles(path, "*", searchOption))
        {
            ct.ThrowIfCancellationRequested();

            bool isVideo = FileNameParser.IsVideoFile(file);
            bool isAudio = FileNameParser.IsAudioFile(file);

            if (!isVideo && !isAudio)
                continue;

            // 1. Filename parse (TV-aware for video, audio-aware for audio)
            ScannedFile scanned;
            if (isAudio)
                scanned = FileNameParser.ParseAudio(file);
            else
                scanned = FileNameParser.Parse(file);   // handles TV detection internally

            // 2. NFO sidecar overrides (title, year, external ID)
            var nfo = NfoParser.TryParse(file);
            if (nfo is not null)
            {
                scanned.ParsedTitle         = nfo.ParsedTitle;
                scanned.ParsedYear          = nfo.ParsedYear ?? scanned.ParsedYear;
                scanned.SuggestedExternalId = nfo.SuggestedExternalId ?? scanned.SuggestedExternalId;
                scanned.NfoPosterUrl        = nfo.NfoPosterUrl ?? scanned.NfoPosterUrl;
                scanned.ConfidenceScore     = nfo.ConfidenceScore;
                scanned.MediaTypeHint       = nfo.MediaTypeHint;
            }

            // 3. Embedded tag reading (audio/video metadata)
            var tags = EmbeddedTagReader.Read(file);
            scanned.AudioArtist       = tags.AudioArtist;
            scanned.AudioAlbumArtist  = tags.AudioAlbumArtist;
            scanned.AudioAlbum        = tags.AudioAlbum;
            scanned.AudioTrackNumber  = tags.AudioTrackNumber;
            scanned.AudioDiscNumber   = tags.AudioDiscNumber;
            scanned.AudioYear         = tags.AudioYear;
            scanned.AudioGenre        = tags.AudioGenre;
            scanned.DurationSeconds   = tags.DurationSeconds;
            scanned.FileSizeBytes     = tags.FileSizeBytes;

            // Container title/year fill in ParsedTitle/ParsedYear gaps
            if (scanned.ContainerTitle is null)
                scanned.ContainerTitle = tags.ContainerTitle;
            if (scanned.ContainerYear is null)
                scanned.ContainerYear = tags.ContainerYear;
            scanned.ContainerDescription = tags.ContainerDesc;

            // 4. Local poster
            scanned.LocalPosterPath ??= LocalArtFinder.FindPoster(file);

            results.Add(scanned);
        }

        return Task.FromResult(results);
    }

    public Task<bool> HealthCheckAsync(CancellationToken ct = default) =>
        Task.FromResult(true);
}
```

**Step 2: Update `manifest.json`** — bump version to `"1.1.0"`

Open `W:\Scripts\Chronicle.Plugin.FileScanner\manifest.json` and change `"version"` field to `"1.1.0"`.

**Step 3: Build**

```bash
cd W:\Scripts\Chronicle.Plugin.FileScanner
dotnet build
```
Expected: `Build succeeded.`

**Step 4: Commit**

```bash
cd W:\Scripts\Chronicle.Plugin.FileScanner
git add FileScannerPlugin.cs manifest.json
git commit -m "feat(scanner): v1.1.0 — TV hierarchy parsing, audio file support, embedded tag reading"
```

---

## Task 5: FileScanner — Build release v1.1.0 and push

**Step 1: Publish release build**

```bash
cd W:\Scripts\Chronicle.Plugin.FileScanner
dotnet publish -c Release -o publish/
```

**Step 2: Create zip archive**

```powershell
cd W:\Scripts\Chronicle.Plugin.FileScanner
Compress-Archive -Path publish\* -DestinationPath Chronicle.Plugin.FileScanner.zip -Force
```

**Step 3: Push to GitHub**

```bash
cd W:\Scripts\Chronicle.Plugin.FileScanner
git push origin main
```

**Step 4: Create GitHub release v1.1.0**

```bash
cd W:\Scripts\Chronicle.Plugin.FileScanner
gh release create v1.1.0 Chronicle.Plugin.FileScanner.zip \
  --title "v1.1.0 — TV hierarchy, audio support, embedded tags" \
  --notes "## What's new
- TV episode filenames now parsed into Show/Season/Episode hierarchy (S01E02, NxNN patterns)
- Audio file support: MP3, FLAC, OGG, M4A, AAC, WMA, OPUS, WAV, APE
- Embedded tag reading via TagLib#: ID3, Vorbis Comments, MP4 atoms, Matroska tags
- Technical fields: DurationSeconds, FileSizeBytes"
```

Expected: URL of the new release printed to console.

**Step 5: Update the plugin via Chronicle API**

The FileScanner plugin is already installed. To update it, call the update endpoint (or uninstall + reinstall via the UI). For now, use curl:

```bash
# Get plugin ID first
TOKEN="<your jwt>"
curl -s http://localhost:8080/api/v1/plugins \
  -H "Authorization: Bearer $TOKEN" | python -m json.tool

# Note the FileScanner plugin "id" field, then update:
curl -s -X POST http://localhost:8080/api/v1/plugins/2/update \
  -H "Authorization: Bearer $TOKEN"
```

If no update endpoint exists yet, uninstall and reinstall via the Plugins page in the UI.

---

## Task 6: FileScanService — FindOrCreateParentAsync helper

**Files:**
- Modify: `src/Chronicle.Services/FileScanService.cs`
- Test: `tests/Chronicle.Tests.Unit/Services/FileScanServiceHierarchyTests.cs`

**Step 1: Write the failing test**

Create `tests/Chronicle.Tests.Unit/Services/FileScanServiceHierarchyTests.cs`:

```csharp
using Chronicle.Core.Models;
using Chronicle.Data;
using Chronicle.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Chronicle.Tests.Unit.Services;

public class FileScanServiceHierarchyTests
{
    private static ChronicleDbContext MakeDb()
    {
        var opts = new DbContextOptionsBuilder<ChronicleDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new ChronicleDbContext(opts);
    }

    [Fact]
    public async Task GroupFilesForHierarchyImport_TvFiles_GroupsByShow()
    {
        // Arrange — three episodes of the same show, two different seasons
        var files = new[]
        {
            new Chronicle.Plugins.Models.ScannedFile
            {
                FilePath = "S01E01.mkv", ParsedTitle = "Ep1",
                ShowTitle = "My Show", SeasonNumber = 1, EpisodeNumber = 1
            },
            new Chronicle.Plugins.Models.ScannedFile
            {
                FilePath = "S01E02.mkv", ParsedTitle = "Ep2",
                ShowTitle = "My Show", SeasonNumber = 1, EpisodeNumber = 2
            },
            new Chronicle.Plugins.Models.ScannedFile
            {
                FilePath = "S02E01.mkv", ParsedTitle = "S2Ep1",
                ShowTitle = "My Show", SeasonNumber = 2, EpisodeNumber = 1
            },
        };

        // Act — group by show then season
        var groups = FileScanService.GroupByShowForTest(files);

        // Assert
        Assert.Single(groups);                                    // one show
        Assert.Equal("My Show", groups[0].ShowTitle);
        Assert.Equal(2, groups[0].Seasons.Count);                // two seasons
        Assert.Equal(2, groups[0].Seasons[1].Episodes.Count);    // S1 has 2 eps
        Assert.Single(groups[0].Seasons[2].Episodes);            // S2 has 1 ep
    }
}
```

**Step 2: Run to verify it fails**

```bash
cd W:\Scripts\Chronicle\.claude\worktrees\frosty-allen
dotnet test tests/Chronicle.Tests.Unit/ --filter "FileScanServiceHierarchyTests" -v
```
Expected: FAIL — `GroupByShowForTest` doesn't exist yet.

**Step 3: Add the helper + grouping logic to `FileScanService.cs`**

Add the following private records and methods to the `FileScanService` class (near the bottom, before the closing `}`):

```csharp
// ── Hierarchy grouping ────────────────────────────────────────────────────

internal record ShowGroup(string ShowTitle, Dictionary<int, SeasonGroup> Seasons);
internal record SeasonGroup(int SeasonNumber, List<Chronicle.Plugins.Models.ScannedFile> Episodes);

/// <summary>Exposed for unit testing only.</summary>
internal static List<ShowGroup> GroupByShowForTest(
    IEnumerable<Chronicle.Plugins.Models.ScannedFile> files) => GroupByShow(files);

private static List<ShowGroup> GroupByShow(
    IEnumerable<Chronicle.Plugins.Models.ScannedFile> files)
{
    var shows = new Dictionary<string, ShowGroup>(StringComparer.OrdinalIgnoreCase);

    foreach (var file in files)
    {
        var showTitle   = file.ShowTitle ?? file.ParsedTitle;
        var seasonNum   = file.SeasonNumber ?? 0; // 0 = "Specials"

        if (!shows.TryGetValue(showTitle, out var show))
        {
            show = new ShowGroup(showTitle, new Dictionary<int, SeasonGroup>());
            shows[showTitle] = show;
        }

        if (!show.Seasons.TryGetValue(seasonNum, out var season))
        {
            season = new SeasonGroup(seasonNum, new List<Chronicle.Plugins.Models.ScannedFile>());
            show.Seasons[seasonNum] = season;
        }

        season.Episodes.Add(file);
    }

    return shows.Values.ToList();
}

private async Task<MediaItem> FindOrCreateParentAsync(
    string name,
    int mediaTypeId,
    int? parentId,
    int hierarchyLevel,
    CancellationToken ct)
{
    // Case-insensitive match
    var existing = await _context.MediaItems
        .Where(m => m.MediaTypeId == mediaTypeId
                 && m.ParentId == parentId
                 && m.HierarchyLevel == hierarchyLevel
                 && m.Name.ToLower() == name.ToLower())
        .FirstOrDefaultAsync(ct);

    if (existing is not null)
        return existing;

    var item = new MediaItem
    {
        Name           = name,
        MediaTypeId    = mediaTypeId,
        ParentId       = parentId,
        HierarchyLevel = hierarchyLevel,
        CreatedAt      = DateTime.UtcNow,
        UpdatedAt      = DateTime.UtcNow,
    };

    _context.MediaItems.Add(item);
    await _context.SaveChangesAsync(ct);
    return item;
}
```

**Step 4: Run test**

```bash
dotnet test tests/Chronicle.Tests.Unit/ --filter "FileScanServiceHierarchyTests" -v
```
Expected: PASS

**Step 5: Build**

```bash
dotnet build src/Chronicle.Services/Chronicle.Services.csproj
```
Expected: `Build succeeded.`

**Step 6: Commit**

```bash
cd W:\Scripts\Chronicle\.claude\worktrees\frosty-allen
git add src/Chronicle.Services/FileScanService.cs \
        tests/Chronicle.Tests.Unit/Services/FileScanServiceHierarchyTests.cs
git commit -m "feat(scan): add show grouping helper and FindOrCreateParentAsync for hierarchy import"
```

---

## Task 7: FileScanService — Hierarchical ImportDirectAsync

**Files:**
- Modify: `src/Chronicle.Services/FileScanService.cs` (update `ImportDirectAsync`)

**Step 1: Read the current `ImportDirectAsync` implementation**

Find the `ImportDirectAsync` method in `FileScanService.cs`. It currently creates one flat `MediaItem` per file. We need to detect whether the media type has `HierarchyLevels > 1` and branch accordingly.

**Step 2: Update `ImportDirectAsync`**

Replace the body of `ImportDirectAsync` with:

```csharp
public async Task<ImportApprovedSummary> ImportDirectAsync(
    DirectImportRequest request, CancellationToken ct = default)
{
    var mediaType = await _context.MediaTypes.FindAsync(
        new object[] { request.MediaTypeId }, ct)
        ?? throw new InvalidOperationException($"MediaType {request.MediaTypeId} not found");

    int imported = 0, skipped = 0;

    if (mediaType.HierarchyLevels >= 3)
    {
        // Three-tier import: root (show/artist) → mid (season/album) → leaf (episode/track)
        (imported, skipped) = await ImportHierarchicalAsync(
            request.Files, mediaType, request.UserId, ct);
    }
    else
    {
        // Flat import: one library item per file (movies)
        foreach (var file in request.Files)
        {
            try
            {
                await ImportSingleFileAsync(file, mediaType.Id, parentId: null,
                    hierarchyLevel: 0, request.UserId, addLibraryEntry: true, ct);
                imported++;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Skipping file {Path}", file.FilePath);
                skipped++;
            }
        }
    }

    return new ImportApprovedSummary(imported, skipped);
}

private async Task<(int imported, int skipped)> ImportHierarchicalAsync(
    IReadOnlyList<DirectImportFile> files,
    MediaType mediaType,
    int userId,
    CancellationToken ct)
{
    int imported = 0, skipped = 0;

    // Convert DirectImportFile to ScannedFile-like structure for grouping
    // (ShowTitle comes from ParsedTitle for TV files when no ShowTitle field is available
    //  on DirectImportFile — use the convention that ParsedTitle is show name for flat imports)
    // NOTE: After FileScanner v1.1.0, ShowTitle will be populated; until then we fall back.
    var showGroups = GroupByShow(files.Select(f => new Chronicle.Plugins.Models.ScannedFile
    {
        FilePath      = f.FilePath,
        ParsedTitle   = f.ParsedTitle,
        ParsedYear    = f.ParsedYear,
        ShowTitle     = f.ShowTitle,
        SeasonNumber  = f.SeasonNumber,
        EpisodeNumber = f.EpisodeNumber,
        EpisodeTitle  = f.EpisodeTitle,
    }));

    foreach (var show in showGroups)
    {
        try
        {
            // Level 0 — root item (show / artist)
            var rootItem = await FindOrCreateParentAsync(
                show.ShowTitle, mediaType.Id, parentId: null, hierarchyLevel: 0, ct);

            // Upsert library entry for root only
            await UpsertLibraryEntryAsync(userId, rootItem.Id, ct);

            foreach (var (seasonNum, season) in show.Seasons)
            {
                // Level 1 — mid item (season / album)
                var seasonName = seasonNum == 0 ? "Specials" : $"Season {seasonNum}";
                var midItem = await FindOrCreateParentAsync(
                    seasonName, mediaType.Id, rootItem.Id, hierarchyLevel: 1, ct);

                foreach (var ep in season.Episodes)
                {
                    try
                    {
                        // Find the original DirectImportFile to get all its fields
                        var file = files.First(f => f.FilePath == ep.FilePath);
                        var epName = ep.EpisodeTitle ?? ep.ParsedTitle;

                        await ImportSingleFileAsync(file with { ParsedTitle = epName },
                            mediaType.Id, midItem.Id, hierarchyLevel: 2,
                            userId, addLibraryEntry: false, ct);
                        imported++;
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning(ex, "Skipping episode {Path}", ep.FilePath);
                        skipped++;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Skipping show group {Show}", show.ShowTitle);
            skipped += show.Seasons.Values.Sum(s => s.Episodes.Count);
        }
    }

    return (imported, skipped);
}

private async Task ImportSingleFileAsync(
    DirectImportFile file,
    int mediaTypeId,
    int? parentId,
    int hierarchyLevel,
    int userId,
    bool addLibraryEntry,
    CancellationToken ct)
{
    var item = new MediaItem
    {
        Name           = file.ParsedTitle,
        MediaTypeId    = mediaTypeId,
        ParentId       = parentId,
        HierarchyLevel = hierarchyLevel,
        Year           = file.ParsedYear,
        Number         = file.EpisodeNumber ?? file.AudioTrackNumber,
        MetadataJson   = SerializeMetadata(scannerFilePath: file.FilePath),
        CreatedAt      = DateTime.UtcNow,
        UpdatedAt      = DateTime.UtcNow,
    };

    _context.MediaItems.Add(item);
    await _context.SaveChangesAsync(ct);

    if (file.SuggestedExternalId is not null)
    {
        var (provider, externalId) = ParseExternalId(file.SuggestedExternalId);
        if (provider is not null)
        {
            _context.MediaExternalIds.Add(new MediaExternalId
            {
                MediaItemId = item.Id,
                Provider    = provider,
                ExternalId  = externalId,
            });
            await _context.SaveChangesAsync(ct);
        }
    }

    if (addLibraryEntry)
        await UpsertLibraryEntryAsync(userId, item.Id, ct);
}
```

> **Note:** `DirectImportFile` needs `ShowTitle`, `SeasonNumber`, `EpisodeNumber`, `EpisodeTitle`, and `AudioTrackNumber` fields added to match what the FileScanner now provides. Update `FileScanModels.cs` and `FileScanDTOs.cs` to add these fields.

**Step 2b: Update `DirectImportFile` record in `FileScanModels.cs`**

```csharp
public record DirectImportFile(
    string FilePath,
    string ParsedTitle,
    int? ParsedYear,
    string? SuggestedExternalId,
    string MediaTypeHint,
    // New hierarchy fields:
    string? ShowTitle = null,
    int? SeasonNumber = null,
    int? EpisodeNumber = null,
    string? EpisodeTitle = null,
    int? AudioTrackNumber = null);
```

**Step 2c: Update `DirectImportFileDto` in `FileScanDTOs.cs`**

Add optional fields to the DTO record:

```csharp
public record DirectImportFileDto(
    [Required] string FilePath,
    [Required] string ParsedTitle,
    int? ParsedYear,
    string? SuggestedExternalId,
    string MediaTypeHint = "movie",
    string? ShowTitle = null,
    int? SeasonNumber = null,
    int? EpisodeNumber = null,
    string? EpisodeTitle = null,
    int? AudioTrackNumber = null);
```

**Step 2d: Update the mapping in `FileScanController.cs`**

In the `ImportDirect` action where `DirectImportFileDto` is mapped to `DirectImportFile`, add the new fields:

```csharp
var file = new DirectImportFile(
    dto.FilePath, dto.ParsedTitle, dto.ParsedYear,
    dto.SuggestedExternalId, dto.MediaTypeHint,
    dto.ShowTitle, dto.SeasonNumber, dto.EpisodeNumber,
    dto.EpisodeTitle, dto.AudioTrackNumber);
```

**Step 3: Build**

```bash
cd W:\Scripts\Chronicle\.claude\worktrees\frosty-allen
dotnet build src/Chronicle.API/Chronicle.API.csproj
```
Expected: `Build succeeded.`

**Step 4: Commit**

```bash
git add src/Chronicle.Services/FileScanService.cs \
        src/Chronicle.Services/FileScanModels.cs \
        src/Chronicle.API/DTOs/FileScanDTOs.cs \
        src/Chronicle.API/Controllers/FileScanController.cs
git commit -m "feat(scan): hierarchical import for TV (Show→Season→Episode) and music (Artist→Album→Track)"
```

---

## Task 8: DELETE /api/v1/library/all endpoint

**Files:**
- Modify: `src/Chronicle.API/Controllers/LibraryController.cs`
- Modify: `src/Chronicle.Services/ILibraryService.cs`
- Modify: `src/Chronicle.Services/LibraryService.cs`

**Step 1: Add to `ILibraryService`**

```csharp
Task<int> ClearAllAsync(int userId, CancellationToken ct = default);
```

**Step 2: Implement in `LibraryService.cs`**

```csharp
public async Task<int> ClearAllAsync(int userId, CancellationToken ct = default)
{
    // Remove all library entries for this user
    var entries = await _context.UserLibraries
        .Where(e => e.UserId == userId)
        .ToListAsync(ct);

    // Find media items that are ONLY referenced by this user (no other user's library)
    var itemIds = entries.Select(e => e.MediaItemId).ToHashSet();
    var sharedIds = await _context.UserLibraries
        .Where(e => e.UserId != userId && itemIds.Contains(e.MediaItemId))
        .Select(e => e.MediaItemId)
        .ToHashSetAsync(ct);

    var exclusiveIds = itemIds.Except(sharedIds).ToHashSet();

    // Also gather all descendants of exclusive root items
    var allToDelete = new HashSet<int>(exclusiveIds);
    foreach (var rootId in exclusiveIds)
    {
        var children = await GetAllDescendantIdsAsync(rootId, ct);
        allToDelete.UnionWith(children);
    }

    _context.UserLibraries.RemoveRange(entries);
    var itemsToDelete = await _context.MediaItems
        .Where(m => allToDelete.Contains(m.Id))
        .ToListAsync(ct);
    _context.MediaItems.RemoveRange(itemsToDelete);

    await _context.SaveChangesAsync(ct);
    return entries.Count;
}

private async Task<List<int>> GetAllDescendantIdsAsync(int parentId, CancellationToken ct)
{
    var result = new List<int>();
    var queue = new Queue<int>();
    queue.Enqueue(parentId);

    while (queue.Count > 0)
    {
        var current = queue.Dequeue();
        var children = await _context.MediaItems
            .Where(m => m.ParentId == current)
            .Select(m => m.Id)
            .ToListAsync(ct);
        foreach (var child in children)
        {
            result.Add(child);
            queue.Enqueue(child);
        }
    }

    return result;
}
```

**Step 3: Add endpoint to `LibraryController.cs`**

```csharp
[HttpDelete("all")]
public async Task<IActionResult> ClearAll(CancellationToken ct)
{
    var userId = GetUserId();
    var removed = await _libraryService.ClearAllAsync(userId, ct);
    return Ok(ApiResponse<object>.Ok(new { removedItems = removed }));
}
```

**Step 4: Build**

```bash
dotnet build src/Chronicle.API/Chronicle.API.csproj
```

**Step 5: Test via curl**

```bash
TOKEN="<your jwt>"
curl -s -X DELETE http://localhost:8080/api/v1/library/all \
  -H "Authorization: Bearer $TOKEN" | python -m json.tool
```
Expected: `{"success":true,"data":{"removedItems":500}}`

**Step 6: Commit**

```bash
git add src/Chronicle.Services/ILibraryService.cs \
        src/Chronicle.Services/LibraryService.cs \
        src/Chronicle.API/Controllers/LibraryController.cs
git commit -m "feat(library): add DELETE /api/v1/library/all endpoint"
```

---

## Task 9: Library API — rootOnly filter

**Files:**
- Modify: `src/Chronicle.API/Controllers/LibraryController.cs`
- Modify: `src/Chronicle.Services/ILibraryService.cs`
- Modify: `src/Chronicle.Services/LibraryService.cs`

**Step 1: Add `rootOnly` param to `ILibraryService.GetLibraryAsync`**

Update the interface signature:

```csharp
Task<PagedResult<LibraryEntry>> GetLibraryAsync(
    int userId,
    string? status,
    int page,
    int perPage,
    bool rootOnly = false,       // ← new
    CancellationToken ct = default);
```

**Step 2: Implement in `LibraryService.cs`**

In `GetLibraryAsync`, after the base query and before pagination, add:

```csharp
if (rootOnly)
    query = query.Where(e => e.MediaItem.ParentId == null);
```

**Step 3: Update `LibraryController.GetLibrary`**

Add `[FromQuery] bool rootOnly = false` parameter and pass it through to the service.

**Step 4: Build**

```bash
dotnet build src/Chronicle.API/Chronicle.API.csproj
```

**Step 5: Commit**

```bash
git add src/Chronicle.Services/ILibraryService.cs \
        src/Chronicle.Services/LibraryService.cs \
        src/Chronicle.API/Controllers/LibraryController.cs
git commit -m "feat(library): add rootOnly query filter to GET /api/v1/library"
```

---

## Task 10: Media detail — Fix 502 → 409 for unconfigured provider

**Files:**
- Modify: `src/Chronicle.Services/FileScanService.cs`
- Modify: `src/Chronicle.API/Controllers/MediaController.cs`
- Modify: `src/Chronicle.Web/src/pages/media/MediaDetailPage.tsx`

**Step 1: Update `RefreshMetadataAsync` in `FileScanService.cs`**

At the top of `RefreshMetadataAsync`, before calling the provider:

```csharp
var provider = _registry.GetLoadedPlugins()
    .SelectMany(p => p.MetadataProviders)
    .FirstOrDefault();

if (provider is null)
    throw new NoProviderConfiguredException(
        "No metadata provider configured. Add an API key in Settings → Plugins.");
```

Add the exception class to `src/Chronicle.Services/Exceptions/` (create file `NoProviderConfiguredException.cs`):

```csharp
namespace Chronicle.Services.Exceptions;

public class NoProviderConfiguredException : InvalidOperationException
{
    public NoProviderConfiguredException(string message) : base(message) { }
}
```

**Step 2: Update `MediaController` catch block**

```csharp
catch (NoProviderConfiguredException ex)
{
    return StatusCode(409, ApiResponse<MediaItemDto>.Fail("NO_PROVIDER_CONFIGURED", ex.Message));
}
catch (Exception ex)
{
    return StatusCode(502, ApiResponse<MediaItemDto>.Fail("REFRESH_FAILED", ex.Message));
}
```

**Step 3: Update frontend — friendly error on 409**

In `MediaDetailPage.tsx`, find where `refreshMut.error` is displayed and add:

```tsx
{refreshMut.isError && (
  <div className={styles.refreshError}>
    {(refreshMut.error as any)?.response?.status === 409
      ? 'No metadata provider configured. Add an API key in Settings → Plugins.'
      : `Refresh failed: ${(refreshMut.error as any)?.message}`}
  </div>
)}
```

**Step 4: Build backend + frontend type-check**

```bash
dotnet build src/Chronicle.API/Chronicle.API.csproj
cd src/Chronicle.Web && npm run type-check
```

**Step 5: Commit**

```bash
git add src/Chronicle.Services/FileScanService.cs \
        src/Chronicle.Services/Exceptions/NoProviderConfiguredException.cs \
        src/Chronicle.API/Controllers/MediaController.cs \
        src/Chronicle.Web/src/pages/media/MediaDetailPage.tsx
git commit -m "fix(media): return 409 NO_PROVIDER_CONFIGURED instead of 502 when no metadata provider set up"
```

---

## Task 11: Media detail — Fix missing FileScanner metadata box

**Files:**
- Modify: `src/Chronicle.Web/src/pages/media/MediaDetailPage.tsx` (or a helper file it uses)

**Step 1: Find where `FileScannerMetaDto` is parsed**

Search for where `metadata_json` is parsed in `MediaDetailPage.tsx`. Look for code reading `filePath`, `localPosterPath`, or `nfoPosterUrl`.

**Step 2: Ensure both key paths are checked**

The `import-direct` endpoint writes: `metadata_json.fileScanner.filePath`
The old flow writes: `metadata_json.filePath` (legacy flat)

Update the parser to check both:

```typescript
// In the function that extracts file scanner meta from the raw metadata JSON:
function extractFileScannerMeta(metaJson: Record<string, unknown> | null) {
  if (!metaJson) return null;

  // New partitioned format (import-direct, scanner v1.1.0)
  const fs = metaJson.fileScanner as Record<string, unknown> | undefined;
  const filePath = (fs?.filePath ?? metaJson.filePath) as string | undefined;
  const localPosterPath = (fs?.localPosterPath ?? metaJson.localPosterPath) as string | undefined;
  const nfoPosterUrl = (fs?.nfoPosterUrl ?? metaJson.nfoPosterUrl) as string | undefined;

  if (!filePath && !localPosterPath && !nfoPosterUrl) return null;

  return { filePath, localPosterPath, nfoPosterUrl };
}
```

**Step 3: Type-check**

```bash
cd src/Chronicle.Web && npm run type-check
```

**Step 4: Commit**

```bash
git add src/Chronicle.Web/src/pages/media/MediaDetailPage.tsx
git commit -m "fix(media): show FileScanner metadata box for import-direct items (check both key paths)"
```

---

## Task 12: Media detail — Fix TMDB icon + bundled fallback

**Files:**
- Create: `src/Chronicle.Web/src/assets/tmdb-logo.svg`
- Modify: `src/Chronicle.Web/src/pages/media/MediaDetailPage.tsx`

**Step 1: Add TMDB logo SVG asset**

Download the TMDB logo SVG from their brand assets page, or create a minimal version:

```svg
<!-- src/Chronicle.Web/src/assets/tmdb-logo.svg -->
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 185.04 133.4">
  <text x="0" y="120" font-size="80" font-weight="bold" fill="#01B4E4">TMDB</text>
</svg>
```

(Use the actual TMDB logo SVG from https://www.themoviedb.org/about/logos-attribution)

**Step 2: Update the TMDB section header in `MediaDetailPage.tsx`**

Find where the TMDB plugin icon is rendered. Add an `onError` fallback:

```tsx
import tmdbLogoFallback from '../../assets/tmdb-logo.svg';

// In the TMDB metadata section header:
<img
  src={tmdbPluginIconUrl ?? tmdbLogoFallback}
  onError={(e) => { (e.target as HTMLImageElement).src = tmdbLogoFallback; }}
  alt="TMDB"
  className={styles.providerIcon}
/>
```

**Step 3: Type-check**

```bash
cd src/Chronicle.Web && npm run type-check
```

**Step 4: Commit**

```bash
git add src/Chronicle.Web/src/assets/tmdb-logo.svg \
        src/Chronicle.Web/src/pages/media/MediaDetailPage.tsx
git commit -m "fix(media): add TMDB logo fallback SVG when plugin icon URL unavailable"
```

---

## Task 13: Plugin Settings UI

**Files:**
- Find the existing Plugins page (search for `PluginCard` or plugin list component)
  Likely: `src/Chronicle.Web/src/pages/plugins/PluginsPage.tsx` or similar
- Modify that file + its CSS module
- Modify: `src/Chronicle.Web/src/api/plugins.ts` (add getSettingsSchema + putSettings)

**Step 1: Add API functions in `plugins.ts`**

```typescript
export interface SettingDefinition {
  key: string;
  label: string;
  type: 'string' | 'secret' | 'bool' | 'int';
  required: boolean;
  description?: string;
  defaultValue?: string;
}

export interface PluginSettingsSchema {
  settings: SettingDefinition[];
}

export async function getPluginSettingsSchema(id: number): Promise<PluginSettingsSchema> {
  const res = await api.get(`/plugins/${id}/settings-schema`);
  return res.data.data;
}

export async function putPluginSettings(
  id: number,
  settings: Record<string, string>
): Promise<void> {
  await api.put(`/plugins/${id}/settings`, { settings });
}
```

**Step 2: Add `PluginSettingsPanel` component inline in the Plugins page**

Find the existing plugin card rendering and add a "Configure" button + collapsible settings panel. The panel:

1. Shows a "Configure" button only when `settingsSchema.settings.length > 0`
2. On click: fetches schema, toggles the panel open
3. Renders each `SettingDefinition` as an input:
   - `type === 'secret'` → `<input type="password">` with show/hide button
   - `type === 'bool'` → `<input type="checkbox">`
   - others → `<input type="text">`
4. TMDB special case: if `pluginId === 'tmdb'`, show helper text after `api_key` field:
   `Get a free API key at themoviedb.org/settings/api`
5. "Save" button calls `putPluginSettings`, shows success toast on 200, error message on failure
6. After save: re-trigger the plugin health check badge

Key React pattern:

```tsx
const [configOpen, setConfigOpen] = useState(false);
const [formValues, setFormValues] = useState<Record<string, string>>({});
const [saving, setSaving] = useState(false);
const [saveError, setSaveError] = useState<string | null>(null);

const { data: schema } = useQuery(
  ['plugin-schema', plugin.id],
  () => getPluginSettingsSchema(plugin.id),
  { enabled: configOpen }
);

const handleSave = async () => {
  setSaving(true);
  setSaveError(null);
  try {
    await putPluginSettings(plugin.id, formValues);
    setConfigOpen(false);
    queryClient.invalidateQueries(['plugins']); // refresh health badge
  } catch (e) {
    setSaveError('Failed to save settings.');
  } finally {
    setSaving(false);
  }
};
```

**Step 3: Type-check**

```bash
cd src/Chronicle.Web && npm run type-check
```

**Step 4: Commit**

```bash
git add src/Chronicle.Web/src/api/plugins.ts \
        src/Chronicle.Web/src/pages/plugins/  # all modified files
git commit -m "feat(plugins): add inline settings panel with dynamic form for plugin configuration"
```

---

## Task 14: Library Frontend — rootOnly + collapsible sections

**Files:**
- Modify: `src/Chronicle.Web/src/pages/library/LibraryPage.tsx`
- Modify: `src/Chronicle.Web/src/pages/library/LibraryPage.module.css`

**Step 1: Add `rootOnly=true` to the API call**

Find `getLibrary(undefined, 1, 500)` and update:

```typescript
getLibrary(undefined, 1, 500, true)  // rootOnly = true
```

Update `getLibrary` signature in `src/Chronicle.Web/src/api/library.ts` to accept `rootOnly`:

```typescript
export async function getLibrary(
  status?: string,
  page = 1,
  perPage = 20,
  rootOnly = false
): Promise<PagedResult<LibraryEntry>> {
  const res = await api.get('/library', {
    params: { status, page, perPage, rootOnly }
  });
  return res.data;
}
```

**Step 2: Add collapsible state with localStorage persistence**

At the top of `LibraryPage`:

```typescript
const [collapsedSections, setCollapsedSections] = useState<Record<string, boolean>>(() => {
  try {
    const stored = localStorage.getItem('chronicle.library.collapsed');
    return stored ? JSON.parse(stored) : {};
  } catch {
    return {};
  }
});

const toggleSection = (mediaTypeName: string) => {
  setCollapsedSections(prev => {
    const next = { ...prev, [mediaTypeName]: !prev[mediaTypeName] };
    localStorage.setItem('chronicle.library.collapsed', JSON.stringify(next));
    return next;
  });
};
```

**Step 3: Update section header render**

For each media-type section header, replace the existing heading with:

```tsx
<div className={styles.sectionHeader} onClick={() => toggleSection(group.mediaTypeName)}>
  <span className={styles.sectionTitle}>{group.mediaTypeName}</span>
  <span className={styles.sectionCount}>{group.items.length}</span>
  <span className={`${styles.chevron} ${collapsedSections[group.mediaTypeName] ? styles.collapsed : ''}`}>
    ▾
  </span>
</div>
{!collapsedSections[group.mediaTypeName] && (
  <div className={styles.sectionGrid}>
    {/* existing card render */}
  </div>
)}
```

**Step 4: Add CSS for chevron toggle**

```css
.sectionHeader {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  cursor: pointer;
  user-select: none;
  padding: 0.5rem 0;
}

.chevron {
  transition: transform 0.2s ease;
  display: inline-block;
}

.chevron.collapsed {
  transform: rotate(-90deg);
}
```

**Step 5: Type-check**

```bash
cd src/Chronicle.Web && npm run type-check
```

**Step 6: Commit**

```bash
git add src/Chronicle.Web/src/pages/library/LibraryPage.tsx \
        src/Chronicle.Web/src/pages/library/LibraryPage.module.css \
        src/Chronicle.Web/src/api/library.ts
git commit -m "feat(library): only show root-level items; add collapsible sections with localStorage persistence"
```

---

## Task 15: BACKLOG.md additions + push everything

**Files:**
- Modify: `W:\Scripts\Chronicle\BACKLOG.md`

**Step 1: Add two items to Planned section**

Open `W:\Scripts\Chronicle\BACKLOG.md` and add under a `## Planned` heading:

```markdown
### Movie Collections
Group movies into named collections (e.g. "Alien Collection"). TMDB returns
`belongs_to_collection` on each movie response. Collections use `media_groups`
table. Each member movie links to the collection. Collections show their own
art, synopsis, and member list.
Touches: Chronicle.Plugin.TMDB, MetadataRefreshService, library UI.

### Dynamic Library Loading
Replace the single `getLibrary(undefined, 1, 500)` call with paginated/virtual
scroll. Count badges appear immediately; cards fill in progressively as the user
scrolls. Prevents page freeze on large libraries (1000+ items).
```

**Step 2: Commit and push main repo**

```bash
cd W:\Scripts\Chronicle
git add BACKLOG.md
git commit -m "docs(backlog): add Movie Collections and Dynamic Library Loading to Planned"
git push origin main
```

**Step 3: Push worktree branch**

```bash
cd W:\Scripts\Chronicle\.claude\worktrees\frosty-allen
git push origin claude/frosty-allen
```

---

## Task 16: Re-scan and verify end-to-end

**Step 1: Restart the API**

The API picks up the new FileScanner plugin automatically on startup once the plugin zip is updated.

```bash
cd W:\Scripts\Chronicle\.claude\worktrees\frosty-allen\src\Chronicle.API
dotnet run
```

**Step 2: Register user + get JWT**

```powershell
$r = Invoke-RestMethod -Method Post -Uri http://localhost:8080/api/v1/auth/register `
  -ContentType "application/json" `
  -Body '{"username":"mbeck","password":"password","email":"mbeck@example.com"}'
$TOKEN = $r.data.token
```

**Step 3: Configure TMDB API key via the new settings UI**

1. Navigate to `http://localhost:58707` (or wherever the frontend dev server runs)
2. Go to Settings → Plugins
3. Click "Configure" on the TMDB plugin card
4. Enter your TMDB API key
5. Click Save
6. Verify the health badge turns green

**Step 4: Re-scan TV files**

Use the File Scan page:
1. Enter your TV directory path
2. Select media type: TV Shows
3. Click Scan → Review → Import

Verify in the library:
- Library shows **shows**, not individual episodes
- Each show card can be clicked → detail page shows seasons → episodes

**Step 5: Smoke test rootOnly filter**

```powershell
Invoke-RestMethod -Uri "http://localhost:8080/api/v1/library?rootOnly=true" `
  -Headers @{ Authorization = "Bearer $TOKEN" }
```
Verify all returned items have no `parentId`.

**Step 6: Test clear library**

```powershell
Invoke-RestMethod -Method Delete -Uri "http://localhost:8080/api/v1/library/all" `
  -Headers @{ Authorization = "Bearer $TOKEN" }
```
Verify count returned matches library size; library empty afterwards.

---

## Summary of commits by repo

**Chronicle.Plugin.FileScanner:**
- `feat(scanner): add TagLib# dependency and EmbeddedTagReader`
- `feat(parser): extract season/episode numbers, ParseTv() and ParseAudio()`
- `feat(scanner): v1.1.0 — TV hierarchy, audio support, embedded tags`
- GitHub release: `v1.1.0`

**Chronicle (main + worktree):**
- `feat(plugins): extend ScannedFile with hierarchy, audio, container, technical fields`
- `feat(scan): add show grouping helper and FindOrCreateParentAsync`
- `feat(scan): hierarchical import for TV and music`
- `feat(library): DELETE /api/v1/library/all endpoint`
- `feat(library): add rootOnly query filter`
- `fix(media): 409 NO_PROVIDER_CONFIGURED instead of 502`
- `fix(media): FileScanner metadata box key path fix`
- `fix(media): TMDB logo fallback SVG`
- `feat(plugins): inline settings panel for plugin configuration`
- `feat(library): root-only items + collapsible sections with localStorage`
- `docs(backlog): Movie Collections and Dynamic Library Loading`
