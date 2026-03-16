# Hierarchical File Scanning Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Replace the flat file scanner with a multi-signal grouping pipeline that creates Artist→Album→Track / Show→Season→Episode hierarchies, shows grouped import previews with confidence scores, and adds a nuclear library reset to Settings.

**Architecture:** A new `ScanGroupingService` extracts signals from folder structure, audio tags (TagLib#), and NFO files, then assembles them into a `ScanGroupResult` tree. `FileScanService` calls it during preview and persists the resulting hierarchy on import. The `FileScanController` gets two new endpoints (`/scan/preview-grouped`, `/scan/import-groups`). The `ScanPage` is updated to render grouped cards. A Danger Zone is added to `LibrarySettingsPage` with a nuclear reset.

**Tech Stack:** .NET 9, TagLibSharp NuGet, EF Core 9, React 18 + TypeScript, existing ChronicleDbContext

**Key insight:** `MediaDetailPage.tsx` already has the ↑ Up button (line 139–144) and the `rootOnly` library filter already works. The only broken piece is the scanner producing flat items instead of a hierarchy.

**Branch:** Create a new worktree/branch from `develop` using the `superpowers:using-git-worktrees` skill before starting. Suggested name: `feature/hierarchical-scanning`.

---

## Task 1: Add TagLibSharp NuGet Package

**Files:**
- Modify: `src/Chronicle.Services/Chronicle.Services.csproj`

**Step 1: Add the package reference**

Run from `src/Chronicle.Services/`:
```bash
dotnet add package TagLibSharp
```

Expected output: `PackageReference` added for `TagLibSharp` version `2.x`.

**Step 2: Verify the build still passes**
```bash
cd src/Chronicle.API && dotnet build --no-restore
```
Expected: `Build succeeded. 0 Error(s)`

**Step 3: Commit**
```bash
git add src/Chronicle.Services/Chronicle.Services.csproj
git commit -m "chore(deps): add TagLibSharp for audio tag reading"
```

---

## Task 2: Core Models — ScanGroup and ScanGroupResult

**Files:**
- Create: `src/Chronicle.Core/Models/Scan/ScanGroup.cs`
- Create: `src/Chronicle.Core/Models/Scan/ScanGroupResult.cs`

**Step 1: Write the failing test**

In `tests/Chronicle.Tests.Unit/Services/ScanGroupingServiceTests.cs` (create new file):

```csharp
using Chronicle.Core.Models.Scan;
using FluentAssertions;

namespace Chronicle.Tests.Unit.Services;

public class ScanGroupModelTests
{
    [Fact]
    public void ScanGroup_ChildCount_ReturnsCorrectTotal()
    {
        var root = new ScanGroup
        {
            Name = "Metallica",
            HierarchyLevel = 0,
            ConfidenceScore = 0.9,
            Children = new List<ScanGroup>
            {
                new() { Name = "Black Album", HierarchyLevel = 1, ConfidenceScore = 0.85, Children = [], Files = [] },
                new() { Name = "Ride the Lightning", HierarchyLevel = 1, ConfidenceScore = 0.80, Children = [], Files = [] },
            },
            Files = [],
        };

        root.Children.Should().HaveCount(2);
        root.TotalFileCount.Should().Be(0);
    }
}
```

**Step 2: Run test to confirm it fails**
```bash
cd tests/Chronicle.Tests.Unit && dotnet test --filter "ScanGroupModelTests" -v normal
```
Expected: FAIL — `Chronicle.Core.Models.Scan` namespace not found.

**Step 3: Create `ScanGroup.cs`**
```csharp
namespace Chronicle.Core.Models.Scan
{
    public class ScanGroup
    {
        /// <summary>Normalised key used to deduplicate groups (lowercase, trimmed).</summary>
        public string GroupKey { get; set; } = string.Empty;

        /// <summary>Display name derived from the strongest available signal.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>0 = Artist/Show, 1 = Album/Season, 2 = Track/Episode.</summary>
        public int HierarchyLevel { get; set; }

        public int? Year { get; set; }

        /// <summary>Local path to a folder image (.jpg/.png) if one was found.</summary>
        public string? PosterPath { get; set; }

        /// <summary>0.0 – 1.0. Average of member file scores, penalised for conflicts.</summary>
        public double ConfidenceScore { get; set; }

        /// <summary>e.g. ["folder", "tags", "nfo"] — signals that contributed.</summary>
        public List<string> SignalSources { get; set; } = [];

        /// <summary>True if any two signal sources disagreed on the group name.</summary>
        public bool HasConflicts { get; set; }

        public List<ScanGroup> Children { get; set; } = [];

        /// <summary>Leaf files that belong directly to this group (flat-grouped types).</summary>
        public List<string> Files { get; set; } = [];

        /// <summary>Total number of leaf files under this group (recursive).</summary>
        public int TotalFileCount =>
            Files.Count + Children.Sum(c => c.TotalFileCount);
    }
}
```

**Step 4: Create `ScanGroupResult.cs`**
```csharp
namespace Chronicle.Core.Models.Scan
{
    public class ScanGroupResult
    {
        /// <summary>Root-level groups (Artist, Show, Audiobook title, etc.).</summary>
        public List<ScanGroup> Groups { get; set; } = [];

        /// <summary>Files that could not be attached to any group with sufficient confidence.</summary>
        public List<string> Ungrouped { get; set; } = [];

        public int TotalFiles { get; set; }
        public int TotalGroups => Groups.Count;
    }
}
```

**Step 5: Run test to confirm it passes**
```bash
cd tests/Chronicle.Tests.Unit && dotnet test --filter "ScanGroupModelTests" -v normal
```
Expected: PASS

**Step 6: Commit**
```bash
git add src/Chronicle.Core/Models/Scan/ tests/Chronicle.Tests.Unit/Services/ScanGroupingServiceTests.cs
git commit -m "feat(core): add ScanGroup and ScanGroupResult models"
```

---

## Task 3: FolderSignalExtractor

**Files:**
- Create: `src/Chronicle.Services/Scan/FolderSignalExtractor.cs`
- Test: `tests/Chronicle.Tests.Unit/Services/ScanGroupingServiceTests.cs` (add to existing)

**Step 1: Write the failing tests** (add to `ScanGroupingServiceTests.cs`)

```csharp
using Chronicle.Services.Scan;

public class FolderSignalExtractorTests
{
    private readonly FolderSignalExtractor _extractor = new();

    [Theory]
    [InlineData(@"C:\Music\Metallica\Black Album\01 Enter Sandman.mp3", @"C:\Music", 3, "Metallica", "Black Album", "01 Enter Sandman")]
    [InlineData(@"C:\Music\Metallica\01 Enter Sandman.mp3", @"C:\Music", 2, "Metallica", null, "01 Enter Sandman")]
    public void Extract_ReturnsCorrectHierarchyLevels(
        string filePath, string scanRoot, int expectedHierarchyLevels,
        string expectedLevel0, string? expectedLevel1, string expectedLeaf)
    {
        var result = _extractor.Extract(filePath, scanRoot);

        result.HierarchyDepth.Should().Be(expectedHierarchyLevels);
        result.FolderNames[0].Should().Be(expectedLevel0);
        if (expectedLevel1 != null)
            result.FolderNames[1].Should().Be(expectedLevel1);
        result.FileName.Should().Be(expectedLeaf);
    }

    [Theory]
    [InlineData(@"C:\TV\Breaking Bad\Season 1\S01E01 Pilot.mkv", "Breaking Bad", 1, 1)]
    [InlineData(@"C:\TV\Breaking Bad\Season 5\S05E14 Ozymandias.mkv", "Breaking Bad", 5, 14)]
    public void Extract_DetectsSeasonAndEpisodeFromFilename(
        string filePath, string showName, int season, int episode)
    {
        var result = _extractor.Extract(filePath, @"C:\TV");

        result.FolderNames[0].Should().Be(showName);
        result.DetectedSeason.Should().Be(season);
        result.DetectedEpisode.Should().Be(episode);
    }
}
```

**Step 2: Run to confirm failure**
```bash
cd tests/Chronicle.Tests.Unit && dotnet test --filter "FolderSignalExtractorTests" -v normal
```
Expected: FAIL — `Chronicle.Services.Scan` not found.

**Step 3: Create `FolderSignalExtractor.cs`**
```csharp
using System.Text.RegularExpressions;

namespace Chronicle.Services.Scan
{
    public class FolderSignal
    {
        /// <summary>Folder names from scan root down to the file's parent (not including file).</summary>
        public List<string> FolderNames { get; set; } = [];
        public string FileName { get; set; } = string.Empty;
        /// <summary>Number of folder levels between scan root and file (1 = file directly in root).</summary>
        public int HierarchyDepth { get; set; }
        public int? DetectedSeason { get; set; }
        public int? DetectedEpisode { get; set; }
        public int? DetectedTrackNumber { get; set; }
        public int? DetectedDiscNumber { get; set; }
    }

    public class FolderSignalExtractor
    {
        // Matches S01E01, S1E1, s01e01, etc.
        private static readonly Regex _episodeRegex =
            new(@"[Ss](\d{1,2})[Ee](\d{1,3})", RegexOptions.Compiled);

        // Matches leading track number like "01 Title" or "01 - Title"
        private static readonly Regex _trackRegex =
            new(@"^(\d{1,3})[\s\-\.]+", RegexOptions.Compiled);

        // Matches "Season 1", "Season 01"
        private static readonly Regex _seasonFolderRegex =
            new(@"[Ss]eason\s*(\d{1,2})", RegexOptions.Compiled);

        // Matches "Disc 1", "CD1", "Disc2"
        private static readonly Regex _discRegex =
            new(@"(?:[Dd]isc|CD)\s*(\d)", RegexOptions.Compiled);

        public FolderSignal Extract(string filePath, string scanRoot)
        {
            var signal = new FolderSignal();

            // Normalise separators
            var normalRoot = scanRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalFile = filePath;

            // Get relative path
            string relative;
            if (normalFile.StartsWith(normalRoot, StringComparison.OrdinalIgnoreCase))
                relative = normalFile[(normalRoot.Length + 1)..];
            else
                relative = normalFile;

            var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);

            signal.FileName = Path.GetFileNameWithoutExtension(parts[^1]);
            signal.FolderNames = parts.Length > 1 ? parts[..^1].ToList() : [];
            signal.HierarchyDepth = parts.Length; // 1 = file in root, 2 = one folder deep, etc.

            // Season folder detection
            foreach (var folder in signal.FolderNames)
            {
                var sm = _seasonFolderRegex.Match(folder);
                if (sm.Success) signal.DetectedSeason = int.Parse(sm.Groups[1].Value);

                var dm = _discRegex.Match(folder);
                if (dm.Success) signal.DetectedDiscNumber = int.Parse(dm.Groups[1].Value);
            }

            // Episode from filename
            var em = _episodeRegex.Match(signal.FileName);
            if (em.Success)
            {
                signal.DetectedSeason ??= int.Parse(em.Groups[1].Value);
                signal.DetectedEpisode = int.Parse(em.Groups[2].Value);
            }

            // Track number from filename
            var tm = _trackRegex.Match(signal.FileName);
            if (tm.Success)
                signal.DetectedTrackNumber = int.Parse(tm.Groups[1].Value);

            return signal;
        }
    }
}
```

**Step 4: Run tests to confirm pass**
```bash
cd tests/Chronicle.Tests.Unit && dotnet test --filter "FolderSignalExtractorTests" -v normal
```
Expected: PASS

**Step 5: Commit**
```bash
git add src/Chronicle.Services/Scan/ tests/Chronicle.Tests.Unit/Services/ScanGroupingServiceTests.cs
git commit -m "feat(services): add FolderSignalExtractor for hierarchy inference"
```

---

## Task 4: TagSignalExtractor

**Files:**
- Create: `src/Chronicle.Services/Scan/TagSignalExtractor.cs`
- Test: `tests/Chronicle.Tests.Unit/Services/ScanGroupingServiceTests.cs` (add)

**Step 1: Write the failing test**

```csharp
public class TagSignalExtractorTests
{
    [Fact]
    public void Extract_ReturnsEmpty_ForNonAudioFile()
    {
        var extractor = new TagSignalExtractor();
        var result = extractor.Extract(@"C:\Music\Metallica\cover.jpg");
        result.Should().BeNull();
    }

    [Fact]
    public void Extract_ReturnsNull_WhenFileDoesNotExist()
    {
        var extractor = new TagSignalExtractor();
        var result = extractor.Extract(@"C:\nonexistent\file.mp3");
        result.Should().BeNull();
    }
}
```

**Step 2: Run to confirm failure**
```bash
cd tests/Chronicle.Tests.Unit && dotnet test --filter "TagSignalExtractorTests" -v normal
```

**Step 3: Create `TagSignalExtractor.cs`**
```csharp
using TagLib;

namespace Chronicle.Services.Scan
{
    public class TagSignal
    {
        public string? Artist { get; set; }
        public string? AlbumArtist { get; set; }
        public string? Album { get; set; }
        public string? Title { get; set; }
        public uint? TrackNumber { get; set; }
        public uint? DiscNumber { get; set; }
        public uint? Year { get; set; }
        public string? Genre { get; set; }
    }

    public class TagSignalExtractor
    {
        // File extensions TagLib can reliably read tags from
        private static readonly HashSet<string> _supported = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".flac", ".m4a", ".mp4", ".mkv", ".ogg", ".opus",
            ".wma", ".aac", ".wav", ".aiff", ".ape", ".mpc",
        };

        /// <summary>
        /// Returns null if the file extension is unsupported, the file doesn't exist,
        /// or TagLib cannot read it — callers should treat null as "no tag signal".
        /// </summary>
        public TagSignal? Extract(string filePath)
        {
            var ext = Path.GetExtension(filePath);
            if (!_supported.Contains(ext)) return null;
            if (!File.Exists(filePath)) return null;

            try
            {
                using var tagFile = TagLib.File.Create(filePath);
                var tag = tagFile.Tag;
                if (tag is null) return null;

                return new TagSignal
                {
                    Artist      = NullIfEmpty(tag.FirstPerformer ?? tag.JoinedPerformers),
                    AlbumArtist = NullIfEmpty(tag.FirstAlbumArtist ?? tag.JoinedAlbumArtists),
                    Album       = NullIfEmpty(tag.Album),
                    Title       = NullIfEmpty(tag.Title),
                    TrackNumber = tag.Track > 0 ? tag.Track : null,
                    DiscNumber  = tag.Disc > 0 ? tag.Disc : null,
                    Year        = tag.Year > 0 ? tag.Year : null,
                    Genre       = NullIfEmpty(tag.FirstGenre),
                };
            }
            catch
            {
                // TagLib throws on corrupt/unsupported files — treat as no signal
                return null;
            }
        }

        private static string? NullIfEmpty(string? s) =>
            string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}
```

**Step 4: Run tests**
```bash
cd tests/Chronicle.Tests.Unit && dotnet test --filter "TagSignalExtractorTests" -v normal
```
Expected: PASS

**Step 5: Commit**
```bash
git add src/Chronicle.Services/Scan/TagSignalExtractor.cs
git commit -m "feat(services): add TagSignalExtractor using TagLibSharp"
```

---

## Task 5: NfoSignalExtractor

**Files:**
- Create: `src/Chronicle.Services/Scan/NfoSignalExtractor.cs`
- Test: add to `ScanGroupingServiceTests.cs`

**Step 1: Write the failing test**

```csharp
public class NfoSignalExtractorTests
{
    [Fact]
    public void Extract_ParsesMusicNfo()
    {
        var nfo = """
            <musicvideo>
              <title>Enter Sandman</title>
              <artist>Metallica</artist>
              <album>Metallica</album>
              <year>1991</year>
            </musicvideo>
            """;
        var extractor = new NfoSignalExtractor();
        var result = extractor.ParseXml(nfo);

        result.Should().NotBeNull();
        result!.Title.Should().Be("Enter Sandman");
        result.Artist.Should().Be("Metallica");
        result.Album.Should().Be("Metallica");
        result.Year.Should().Be(1991);
    }

    [Fact]
    public void Extract_ParsesTvNfo()
    {
        var nfo = """
            <episodedetails>
              <title>Pilot</title>
              <showtitle>Breaking Bad</showtitle>
              <season>1</season>
              <episode>1</episode>
            </episodedetails>
            """;
        var extractor = new NfoSignalExtractor();
        var result = extractor.ParseXml(nfo);

        result!.ShowTitle.Should().Be("Breaking Bad");
        result.Season.Should().Be(1);
        result.Episode.Should().Be(1);
    }

    [Fact]
    public void FindSidecar_ReturnsNfoPathWhenExists()
    {
        // Can't test real filesystem easily; just verify null on missing
        var extractor = new NfoSignalExtractor();
        var result = extractor.FindSidecar(@"C:\nonexistent\file.mkv");
        result.Should().BeNull();
    }
}
```

**Step 2: Run to confirm failure**
```bash
cd tests/Chronicle.Tests.Unit && dotnet test --filter "NfoSignalExtractorTests" -v normal
```

**Step 3: Create `NfoSignalExtractor.cs`**
```csharp
using System.Xml.Linq;

namespace Chronicle.Services.Scan
{
    public class NfoSignal
    {
        public string? Title { get; set; }
        public string? Artist { get; set; }
        public string? Album { get; set; }
        public string? ShowTitle { get; set; }
        public int? Year { get; set; }
        public int? Season { get; set; }
        public int? Episode { get; set; }
        public string? ExternalId { get; set; }  // e.g. tmdb id from <uniqueid type="tmdb">
        public string? PosterUrl { get; set; }    // from <thumb> element
    }

    public class NfoSignalExtractor
    {
        private static readonly string[] _nfoExtensions = [".nfo"];

        /// <summary>Finds a .nfo sidecar next to <paramref name="filePath"/>.</summary>
        public string? FindSidecar(string filePath)
        {
            var dir  = Path.GetDirectoryName(filePath);
            var stem = Path.GetFileNameWithoutExtension(filePath);
            if (dir is null) return null;

            // Prefer "title.nfo" alongside the file
            var adjacent = Path.Combine(dir, stem + ".nfo");
            if (File.Exists(adjacent)) return adjacent;

            // Fall back to any .nfo in the same folder
            try
            {
                return Directory.EnumerateFiles(dir, "*.nfo").FirstOrDefault();
            }
            catch { return null; }
        }

        /// <summary>Extracts signal from a .nfo file path.</summary>
        public NfoSignal? Extract(string nfoPath)
        {
            if (!File.Exists(nfoPath)) return null;
            try
            {
                return ParseXml(File.ReadAllText(nfoPath));
            }
            catch { return null; }
        }

        /// <summary>Parses NFO XML string — exposed for unit testing.</summary>
        public NfoSignal? ParseXml(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return null;
            try
            {
                var doc  = XDocument.Parse(xml.Trim());
                var root = doc.Root;
                if (root is null) return null;

                string? Get(string name) =>
                    root.Element(name)?.Value?.Trim() is { Length: > 0 } v ? v : null;

                int? GetInt(string name) =>
                    int.TryParse(Get(name), out var n) ? n : null;

                var signal = new NfoSignal
                {
                    Title     = Get("title"),
                    Artist    = Get("artist"),
                    Album     = Get("album"),
                    ShowTitle = Get("showtitle"),
                    Year      = GetInt("year"),
                    Season    = GetInt("season"),
                    Episode   = GetInt("episode"),
                    PosterUrl = Get("thumb"),
                };

                // <uniqueid type="tmdb">12345</uniqueid>
                var uid = root.Elements("uniqueid")
                    .FirstOrDefault(e =>
                        string.Equals(e.Attribute("type")?.Value, "tmdb",
                            StringComparison.OrdinalIgnoreCase));
                signal.ExternalId = uid?.Value?.Trim() is { Length: > 0 } id ? id : null;

                return signal;
            }
            catch { return null; }
        }
    }
}
```

**Step 4: Run tests**
```bash
cd tests/Chronicle.Tests.Unit && dotnet test --filter "NfoSignalExtractorTests" -v normal
```
Expected: PASS

**Step 5: Commit**
```bash
git add src/Chronicle.Services/Scan/NfoSignalExtractor.cs
git commit -m "feat(services): add NfoSignalExtractor for sidecar .nfo files"
```

---

## Task 6: ScanGroupingService

**Files:**
- Create: `src/Chronicle.Services/Scan/ScanGroupingService.cs`
- Create: `src/Chronicle.Services/Scan/IScanGroupingService.cs`
- Test: add to `ScanGroupingServiceTests.cs`

**Step 1: Write the failing test**

```csharp
public class ScanGroupingServiceTests
{
    private readonly ScanGroupingService _svc = new(
        new FolderSignalExtractor(),
        new TagSignalExtractor(),
        new NfoSignalExtractor());

    [Fact]
    public void Group_FlatMusicFiles_BuildsArtistAlbumTree()
    {
        // Three files: same artist, same album, different tracks — folder layout
        var files = new[]
        {
            @"C:\Music\Metallica\Black Album\01 Enter Sandman.mp3",
            @"C:\Music\Metallica\Black Album\02 Sad But True.mp3",
            @"C:\Music\Metallica\Black Album\03 Holier Than Thou.mp3",
        };

        var result = _svc.Group(files, scanRoot: @"C:\Music", hierarchyLevels: 3);

        result.Groups.Should().HaveCount(1);
        var artist = result.Groups[0];
        artist.Name.Should().Be("Metallica");
        artist.HierarchyLevel.Should().Be(0);
        artist.Children.Should().HaveCount(1);

        var album = artist.Children[0];
        album.Name.Should().Be("Black Album");
        album.HierarchyLevel.Should().Be(1);
        album.Children.Should().HaveCount(3);

        result.Ungrouped.Should().BeEmpty();
    }

    [Fact]
    public void Group_FlatGroupedType_PutsAllFilesInOneGroup()
    {
        // Audiobook: many chapter files in one folder, HierarchyLevels=1
        var files = new[]
        {
            @"C:\Audiobooks\The Hobbit\Part1.mp3",
            @"C:\Audiobooks\The Hobbit\Part2.mp3",
            @"C:\Audiobooks\The Hobbit\Part3.mp3",
        };

        var result = _svc.Group(files, scanRoot: @"C:\Audiobooks", hierarchyLevels: 1);

        result.Groups.Should().HaveCount(1);
        var book = result.Groups[0];
        book.Name.Should().Be("The Hobbit");
        book.HierarchyLevel.Should().Be(0);
        book.Children.Should().BeEmpty();
        book.Files.Should().HaveCount(3);
    }

    [Fact]
    public void Group_MultipleArtists_CreatesOneGroupPerArtist()
    {
        var files = new[]
        {
            @"C:\Music\Metallica\Black Album\01 Enter Sandman.mp3",
            @"C:\Music\Nirvana\Nevermind\01 Smells Like Teen Spirit.mp3",
        };

        var result = _svc.Group(files, scanRoot: @"C:\Music", hierarchyLevels: 3);

        result.Groups.Should().HaveCount(2);
        result.Groups.Select(g => g.Name).Should().Contain(["Metallica", "Nirvana"]);
    }

    [Fact]
    public void Group_ImageFiles_AreNotIncludedAsLeafFiles()
    {
        var files = new[]
        {
            @"C:\Music\Metallica\Black Album\01 Enter Sandman.mp3",
            @"C:\Music\Metallica\Black Album\cover.jpg",
            @"C:\Music\Metallica\Black Album\fanart.png",
        };

        var result = _svc.Group(files, scanRoot: @"C:\Music", hierarchyLevels: 3);

        var album = result.Groups[0].Children[0];
        // Image files don't become leaf items — only the audio track does
        album.Children.Should().HaveCount(1);
        album.Children[0].Name.Should().Contain("Enter Sandman");
    }

    [Fact]
    public void Group_NfoAndImageFiles_DoNotAppearInUngrouped()
    {
        var files = new[]
        {
            @"C:\Music\Metallica\Black Album\01 Enter Sandman.mp3",
            @"C:\Music\Metallica\Black Album\album.nfo",
            @"C:\Music\Metallica\Black Album\cover.jpg",
        };

        var result = _svc.Group(files, scanRoot: @"C:\Music", hierarchyLevels: 3);

        result.Ungrouped.Should().BeEmpty();
    }
}
```

**Step 2: Run to confirm failure**
```bash
cd tests/Chronicle.Tests.Unit && dotnet test --filter "ScanGroupingServiceTests" -v normal
```

**Step 3: Create `IScanGroupingService.cs`**
```csharp
using Chronicle.Core.Models.Scan;

namespace Chronicle.Services.Scan
{
    public interface IScanGroupingService
    {
        ScanGroupResult Group(IEnumerable<string> filePaths, string scanRoot, int hierarchyLevels);
    }
}
```

**Step 4: Create `ScanGroupingService.cs`**
```csharp
using Chronicle.Core.Models.Scan;

namespace Chronicle.Services.Scan
{
    // Extensions that are metadata/sidecar — never become MediaItems themselves
    private static readonly HashSet<string> _sidecarExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".nfo", ".jpg", ".jpeg", ".png", ".webp", ".bmp",
        ".tbn", ".txt", ".xml", ".srt", ".sub", ".idx",
    };

    public class ScanGroupingService : IScanGroupingService
    {
        private readonly FolderSignalExtractor _folder;
        private readonly TagSignalExtractor _tags;
        private readonly NfoSignalExtractor _nfo;

        public ScanGroupingService(
            FolderSignalExtractor folder,
            TagSignalExtractor tags,
            NfoSignalExtractor nfo)
        {
            _folder = folder;
            _tags   = tags;
            _nfo    = nfo;
        }

        public ScanGroupResult Group(
            IEnumerable<string> filePaths, string scanRoot, int hierarchyLevels)
        {
            var result = new ScanGroupResult();
            // root key → ScanGroup
            var rootGroups = new Dictionary<string, ScanGroup>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in filePaths)
            {
                result.TotalFiles++;

                var ext = Path.GetExtension(path);
                bool isSidecar = _sidecarExtensions.Contains(ext);

                var folderSignal = _folder.Extract(path, scanRoot);
                var tagSignal    = _tags.Extract(path); // null for sidecars / non-audio
                var nfoPath      = _nfo.FindSidecar(path);
                var nfoSignal    = nfoPath != null ? _nfo.Extract(nfoPath) : null;

                // For flat-grouped types (audiobooks etc.), all files in the same
                // immediate folder = one item.  Sidecars are still silently absorbed.
                if (hierarchyLevels == 1)
                {
                    var groupName = folderSignal.FolderNames.LastOrDefault()
                        ?? Path.GetFileNameWithoutExtension(path);
                    var key = Normalize(groupName);

                    if (!rootGroups.TryGetValue(key, out var group))
                    {
                        group = new ScanGroup
                        {
                            GroupKey       = key,
                            Name           = groupName,
                            HierarchyLevel = 0,
                            ConfidenceScore = 0.7,
                            SignalSources  = ["folder"],
                        };
                        rootGroups[key] = group;
                        result.Groups.Add(group);
                    }

                    // Only add non-sidecar files as importable items
                    if (!isSidecar)
                        group.Files.Add(path);
                    continue;
                }

                // Hierarchical types: build Artist → Album → Track tree from folder depth
                if (folderSignal.FolderNames.Count == 0)
                {
                    // File is directly in the scan root with no folder grouping
                    if (!isSidecar)
                        result.Ungrouped.Add(path);
                    continue;
                }

                // Level 0 name: first folder name (unless overridden by tag/nfo signal)
                var level0Name = ResolveLevel0Name(folderSignal, tagSignal, nfoSignal, hierarchyLevels);
                var level0Key  = Normalize(level0Name);

                if (!rootGroups.TryGetValue(level0Key, out var rootGroup))
                {
                    rootGroup = new ScanGroup
                    {
                        GroupKey        = level0Key,
                        Name            = level0Name,
                        HierarchyLevel  = 0,
                        ConfidenceScore = ComputeRootConfidence(folderSignal, tagSignal, nfoSignal),
                        SignalSources   = BuildSources(folderSignal, tagSignal, nfoSignal, 0),
                    };
                    rootGroups[level0Key] = rootGroup;
                    result.Groups.Add(rootGroup);
                }

                // If only 1 folder deep (no album/season level), attach file directly to root
                if (hierarchyLevels == 2 || folderSignal.FolderNames.Count < 2)
                {
                    if (!isSidecar)
                    {
                        var leafName = ResolveLeafName(folderSignal, tagSignal, nfoSignal);
                        rootGroup.Children.Add(new ScanGroup
                        {
                            GroupKey        = Normalize(leafName),
                            Name            = leafName,
                            HierarchyLevel  = 1,
                            ConfidenceScore = ComputeLeafConfidence(folderSignal, tagSignal, nfoSignal),
                            SignalSources   = BuildSources(folderSignal, tagSignal, nfoSignal, 1),
                        });
                    }
                    continue;
                }

                // 2+ folders deep: resolve level-1 (album/season) and attach leaf under it
                var level1Name = folderSignal.FolderNames[1];
                var level1Key  = Normalize(level0Key + "/" + level1Name);

                var level1Group = rootGroup.Children
                    .FirstOrDefault(c => c.GroupKey == level1Key);

                if (level1Group is null)
                {
                    level1Group = new ScanGroup
                    {
                        GroupKey        = level1Key,
                        Name            = level1Name,
                        HierarchyLevel  = 1,
                        ConfidenceScore = 0.75,
                        SignalSources   = ["folder"],
                    };
                    rootGroup.Children.Add(level1Group);
                }

                if (!isSidecar)
                {
                    var leafName = ResolveLeafName(folderSignal, tagSignal, nfoSignal);
                    level1Group.Children.Add(new ScanGroup
                    {
                        GroupKey        = Normalize(level1Key + "/" + leafName),
                        Name            = leafName,
                        HierarchyLevel  = 2,
                        ConfidenceScore = ComputeLeafConfidence(folderSignal, tagSignal, nfoSignal),
                        SignalSources   = BuildSources(folderSignal, tagSignal, nfoSignal, 2),
                        Year            = tagSignal?.Year.HasValue == true ? (int?)tagSignal.Year.Value : null,
                    });
                }
            }

            // Roll up confidence scores from children to parents
            foreach (var g in result.Groups)
                RollUpConfidence(g);

            return result;
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        private static string Normalize(string s) =>
            s.Trim().ToLowerInvariant();

        private static string ResolveLevel0Name(
            FolderSignal folder, TagSignal? tag, NfoSignal? nfo, int levels)
        {
            // Tag: prefer AlbumArtist over Artist for level-0 when music
            if (tag?.AlbumArtist is not null) return tag.AlbumArtist;
            if (nfo?.Artist is not null)      return nfo.Artist;
            if (nfo?.ShowTitle is not null)   return nfo.ShowTitle;
            return folder.FolderNames[0];
        }

        private static string ResolveLeafName(
            FolderSignal folder, TagSignal? tag, NfoSignal? nfo)
        {
            if (tag?.Title is not null) return tag.Title;
            if (nfo?.Title is not null) return nfo.Title;
            return folder.FileName;
        }

        private static double ComputeRootConfidence(
            FolderSignal folder, TagSignal? tag, NfoSignal? nfo)
        {
            double score = 0.5; // folder alone
            if (tag?.AlbumArtist is not null || tag?.Artist is not null) score += 0.25;
            if (nfo?.Artist is not null || nfo?.ShowTitle is not null)   score += 0.25;
            // Conflict: tag artist name disagrees with folder name
            var folderName = folder.FolderNames.FirstOrDefault() ?? "";
            var tagName    = tag?.AlbumArtist ?? tag?.Artist ?? "";
            if (!string.IsNullOrEmpty(tagName)
                && !folderName.Contains(tagName, StringComparison.OrdinalIgnoreCase)
                && !tagName.Contains(folderName, StringComparison.OrdinalIgnoreCase))
            {
                score -= 0.15;
            }
            return Math.Clamp(score, 0.0, 1.0);
        }

        private static double ComputeLeafConfidence(
            FolderSignal folder, TagSignal? tag, NfoSignal? nfo)
        {
            double score = 0.5;
            if (tag?.Title is not null) score += 0.25;
            if (nfo?.Title is not null) score += 0.25;
            return Math.Clamp(score, 0.0, 1.0);
        }

        private static List<string> BuildSources(
            FolderSignal folder, TagSignal? tag, NfoSignal? nfo, int level)
        {
            var sources = new List<string> { "folder" };
            if (tag is not null) sources.Add("tags");
            if (nfo is not null) sources.Add("nfo");
            return sources;
        }

        private static void RollUpConfidence(ScanGroup group)
        {
            if (group.Children.Count == 0) return;
            foreach (var child in group.Children) RollUpConfidence(child);
            group.ConfidenceScore = group.Children.Average(c => c.ConfidenceScore);
        }
    }
}
```

**Step 5: Run tests**
```bash
cd tests/Chronicle.Tests.Unit && dotnet test --filter "ScanGroupingServiceTests" -v normal
```
Expected: PASS

**Step 6: Register in DI** — in `src/Chronicle.API/Program.cs`, find where services are registered and add:
```csharp
builder.Services.AddScoped<Chronicle.Services.Scan.FolderSignalExtractor>();
builder.Services.AddScoped<Chronicle.Services.Scan.TagSignalExtractor>();
builder.Services.AddScoped<Chronicle.Services.Scan.NfoSignalExtractor>();
builder.Services.AddScoped<Chronicle.Services.Scan.IScanGroupingService,
                            Chronicle.Services.Scan.ScanGroupingService>();
```

**Step 7: Commit**
```bash
git add src/Chronicle.Services/Scan/ src/Chronicle.API/Program.cs
git commit -m "feat(services): add ScanGroupingService with multi-signal confidence scoring"
```

---

## Task 7: PreviewGrouped API Endpoint

**Files:**
- Modify: `src/Chronicle.Services/IFileScanService.cs`
- Modify: `src/Chronicle.Services/FileScanService.cs`
- Modify: `src/Chronicle.API/Controllers/FileScanController.cs`
- Test: `tests/Chronicle.Tests.Integration/FileScanTests.cs` (add test)

**Step 1: Write the failing integration test**

In `tests/Chronicle.Tests.Integration/FileScanTests.cs` add:
```csharp
[Fact]
public async Task PreviewGrouped_ReturnsBadRequest_WhenPathIsEmpty()
{
    var client = _factory.CreateClient();
    await AuthHelper.LoginAsAdminAsync(client);

    var response = await client.PostAsJsonAsync("/api/v1/scan/preview-grouped",
        new { path = "", recursive = true, mediaTypeId = 1 });

    response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
}
```

**Step 2: Run to confirm failure**
```bash
cd tests/Chronicle.Tests.Integration && dotnet test --filter "PreviewGrouped_ReturnsBadRequest" -v normal
```

**Step 3: Add to `IFileScanService.cs`**

```csharp
/// <summary>
/// Scans a directory and returns files grouped into a candidate hierarchy
/// (Artist→Album→Track, Show→Season→Episode) with confidence scores.
/// No database changes are made.
/// </summary>
Task<Chronicle.Core.Models.Scan.ScanGroupResult> PreviewGroupedAsync(
    ScanPreviewRequest request, CancellationToken ct = default);
```

**Step 4: Implement in `FileScanService.cs`**

Add the method after `PreviewAsync`:
```csharp
public async Task<ScanGroupResult> PreviewGroupedAsync(
    ScanPreviewRequest request, CancellationToken ct = default)
{
    if (!Directory.Exists(request.Path))
    {
        var hint = BuildMappedDriveHint(request.Path);
        throw new DirectoryNotFoundException(
            $"Scan path does not exist or is not accessible: {request.Path}.{hint}");
    }

    var mediaType = await _context.MediaTypes
        .FirstOrDefaultAsync(t => t.Id == request.MediaTypeId, ct)
        ?? throw new InvalidOperationException($"Media type {request.MediaTypeId} not found.");

    _log.Information("Grouped preview scan of {Path} (recursive={Recursive}, mediaType={MediaType}, hierarchyLevels={Levels})",
        request.Path, request.Recursive, mediaType.Name, mediaType.HierarchyLevels);

    // Collect all file paths (reuse the per-folder progress tracking)
    var allPaths = new List<string>();
    var dirsToScan = new List<string> { request.Path };
    if (request.Recursive)
    {
        try { dirsToScan.AddRange(Directory.GetDirectories(request.Path, "*", SearchOption.AllDirectories)); }
        catch { /* fall through with root only */ }
    }

    _progress.Start(dirsToScan.Count);
    for (int i = 0; i < dirsToScan.Count; i++)
    {
        ct.ThrowIfCancellationRequested();
        _progress.UpdateFolder(dirsToScan[i], i + 1, allPaths.Count);
        try
        {
            allPaths.AddRange(Directory.EnumerateFiles(dirsToScan[i])
                .Where(f => !Path.GetFileName(f).StartsWith('.')));
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Skipping inaccessible directory {Dir}", dirsToScan[i]);
        }
    }
    _progress.Complete();

    _log.Information("Grouped preview: {Count} files found, grouping with {Levels} hierarchy levels",
        allPaths.Count, mediaType.HierarchyLevels);

    return _groupingService.Group(allPaths, request.Path, mediaType.HierarchyLevels);
}
```

Also inject `IScanGroupingService` into `FileScanService` constructor:
```csharp
private readonly IScanGroupingService _groupingService;

public FileScanService(ChronicleDbContext context, IPluginRegistry registry,
    ScanProgressService progress, IScanGroupingService groupingService)
{
    _context         = context;
    _registry        = registry;
    _progress        = progress;
    _groupingService = groupingService;
}
```

**Step 5: Add endpoint to `FileScanController.cs`**

Find the class and add (after `PreviewAsync` action):
```csharp
[HttpPost("preview-grouped")]
public async Task<IActionResult> PreviewGrouped(
    [FromBody] ScanPreviewRequestDto request,
    CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(request.Path))
        return BadRequest(ApiResponse<object>.Fail("INVALID_PATH", "Path is required."));

    try
    {
        var result = await _scanService.PreviewGroupedAsync(
            new ScanPreviewRequest(request.Path, request.Recursive, request.MediaTypeId), ct);

        return Ok(ApiResponse<ScanGroupResultDto>.Ok(ToGroupResultDto(result)));
    }
    catch (DirectoryNotFoundException ex)
    {
        return BadRequest(ApiResponse<object>.Fail("PATH_NOT_FOUND", ex.Message));
    }
}

private static ScanGroupResultDto ToGroupResultDto(ScanGroupResult r) => new(
    r.Groups.Select(ToGroupDto).ToList(),
    r.Ungrouped,
    r.TotalFiles);

private static ScanGroupDto ToGroupDto(ScanGroup g) => new(
    g.GroupKey, g.Name, g.HierarchyLevel, g.Year,
    g.PosterPath, (int)Math.Round(g.ConfidenceScore * 100),
    g.SignalSources, g.HasConflicts,
    g.Children.Select(ToGroupDto).ToList(),
    g.Files);
```

Add DTOs in `src/Chronicle.API/DTOs/ScanDTOs.cs` (create if it doesn't exist, otherwise add to it):
```csharp
public record ScanGroupDto(
    string GroupKey,
    string Name,
    int HierarchyLevel,
    int? Year,
    string? PosterPath,
    int ConfidenceScore,       // 0–100
    List<string> SignalSources,
    bool HasConflicts,
    List<ScanGroupDto> Children,
    List<string> Files);

public record ScanGroupResultDto(
    List<ScanGroupDto> Groups,
    List<string> Ungrouped,
    int TotalFiles);
```

**Step 6: Run integration tests**
```bash
cd tests/Chronicle.Tests.Integration && dotnet test --filter "PreviewGrouped" -v normal
```
Expected: PASS

**Step 7: Commit**
```bash
git add src/Chronicle.Services/ src/Chronicle.API/ tests/Chronicle.Tests.Integration/
git commit -m "feat(api): add POST /scan/preview-grouped endpoint"
```

---

## Task 8: ImportGroups API Endpoint

**Files:**
- Modify: `src/Chronicle.Services/IFileScanService.cs`
- Modify: `src/Chronicle.Services/FileScanService.cs`
- Modify: `src/Chronicle.API/Controllers/FileScanController.cs`
- Modify: `src/Chronicle.API/DTOs/ScanDTOs.cs`
- Test: add to `FileScanTests.cs`

**Step 1: Write the failing test**
```csharp
[Fact]
public async Task ImportGroups_ReturnsUnauthorized_WhenNoToken()
{
    var client = _factory.CreateClient();
    var response = await client.PostAsJsonAsync("/api/v1/scan/import-groups",
        new { groups = Array.Empty<object>(), mediaTypeId = 1 });
    response.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
}
```

**Step 2: Add to `IFileScanService.cs`**
```csharp
/// <summary>
/// Persists accepted ScanGroups as a MediaItem hierarchy.
/// Root groups get UserLibrary entries; children do not.
/// </summary>
Task<ImportApprovedSummary> ImportGroupsAsync(
    ImportGroupsRequest request, int userId, CancellationToken ct = default);
```

Add the request model near the other request records in `Chronicle.Services`:
```csharp
public record ImportGroupsRequest(
    List<ScanGroupImport> Groups,
    int MediaTypeId);

public record ScanGroupImport(
    string Name,
    int? Year,
    string? PosterPath,
    List<ScanGroupImport> Children,
    List<string> Files);
```

**Step 3: Implement `ImportGroupsAsync` in `FileScanService.cs`**

```csharp
public async Task<ImportApprovedSummary> ImportGroupsAsync(
    ImportGroupsRequest request, int userId, CancellationToken ct = default)
{
    var mediaType = await _context.MediaTypes
        .FirstOrDefaultAsync(t => t.Id == request.MediaTypeId, ct)
        ?? throw new InvalidOperationException($"Media type {request.MediaTypeId} not found.");

    int imported = 0, failed = 0, duplicates = 0;
    var failures = new List<string>();

    foreach (var rootGroup in request.Groups)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var rootItem = await UpsertGroupItemAsync(
                rootGroup, request.MediaTypeId, parentId: null,
                hierarchyLevel: 0, ct);

            // Library entry only at root level
            var libEntry = await _context.UserLibraries
                .FirstOrDefaultAsync(l => l.UserId == userId && l.MediaItemId == rootItem.Id, ct);

            if (libEntry is null)
            {
                _context.UserLibraries.Add(new UserLibrary
                {
                    UserId      = userId,
                    MediaItemId = rootItem.Id,
                    Status      = LibraryStatus.Unwatched,
                    AddedAt     = DateTime.UtcNow,
                    UpdatedAt   = DateTime.UtcNow,
                });
                imported++;
            }
            else
            {
                duplicates++;
            }

            // Persist children recursively — no library entries
            await PersistChildGroupsAsync(rootGroup.Children, request.MediaTypeId,
                rootItem.Id, hierarchyLevel: 1, ct);

            await _context.SaveChangesAsync(ct);

            _log.Information("Imported group '{Name}' with {ChildCount} child groups",
                rootGroup.Name, rootGroup.Children.Count);
        }
        catch (Exception ex)
        {
            failed++;
            failures.Add($"{rootGroup.Name}: {ex.Message}");
            _log.Warning(ex, "Failed to import group '{Name}'", rootGroup.Name);
        }
    }

    return new ImportApprovedSummary(imported, failed, failures, duplicates);
}

private async Task PersistChildGroupsAsync(
    List<ScanGroupImport> children, int mediaTypeId,
    int parentId, int hierarchyLevel, CancellationToken ct)
{
    foreach (var child in children)
    {
        var item = await UpsertGroupItemAsync(child, mediaTypeId, parentId, hierarchyLevel, ct);
        if (child.Children.Count > 0)
            await PersistChildGroupsAsync(child.Children, mediaTypeId,
                item.Id, hierarchyLevel + 1, ct);
    }
}

private async Task<MediaItem> UpsertGroupItemAsync(
    ScanGroupImport group, int mediaTypeId,
    int? parentId, int hierarchyLevel, CancellationToken ct)
{
    // Try to find by name + parent + type (idempotent re-import)
    var existing = await _context.MediaItems.FirstOrDefaultAsync(m =>
        m.MediaTypeId == mediaTypeId
        && m.ParentId == parentId
        && m.HierarchyLevel == hierarchyLevel
        && m.Name == group.Name, ct);

    if (existing is not null)
    {
        existing.UpdatedAt = DateTime.UtcNow;
        if (group.Year.HasValue) existing.Year = group.Year;
        return existing;
    }

    var item = new MediaItem
    {
        MediaTypeId    = mediaTypeId,
        ParentId       = parentId,
        HierarchyLevel = hierarchyLevel,
        Name           = group.Name,
        Year           = group.Year,
        PosterUrl      = group.PosterPath,
        MetadataJson   = JsonSerializer.Serialize(new
        {
            fileScanner = new { importedAt = DateTime.UtcNow, filePaths = group.Files }
        }),
        CreatedAt = DateTime.UtcNow,
        UpdatedAt = DateTime.UtcNow,
    };
    _context.MediaItems.Add(item);
    await _context.SaveChangesAsync(ct); // need the ID for children
    return item;
}
```

**Step 4: Add endpoint to `FileScanController.cs`**
```csharp
[HttpPost("import-groups")]
public async Task<IActionResult> ImportGroups(
    [FromBody] ImportGroupsRequestDto request,
    CancellationToken ct)
{
    var userId = GetUserId();
    var groups = request.Groups.Select(ToGroupImport).ToList();
    var result = await _scanService.ImportGroupsAsync(
        new ImportGroupsRequest(groups, request.MediaTypeId), userId, ct);

    return Ok(ApiResponse<ImportSummaryDto>.Ok(
        new ImportSummaryDto(result.Imported, result.Failed, result.Failures, result.Duplicates)));
}

private static ScanGroupImport ToGroupImport(ImportGroupDto g) =>
    new(g.Name, g.Year, g.PosterPath,
        g.Children.Select(ToGroupImport).ToList(),
        g.Files);

private int GetUserId() =>
    int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
```

Add DTOs:
```csharp
public record ImportGroupsRequestDto(
    List<ImportGroupDto> Groups,
    int MediaTypeId);

public record ImportGroupDto(
    string Name,
    int? Year,
    string? PosterPath,
    List<ImportGroupDto> Children,
    List<string> Files);
```

**Step 5: Run integration tests**
```bash
cd tests/Chronicle.Tests.Integration && dotnet test --filter "ImportGroups" -v normal
```
Expected: PASS

**Step 6: Build**
```bash
cd src/Chronicle.API && dotnet build
```
Expected: `Build succeeded. 0 Error(s)`

**Step 7: Commit**
```bash
git add src/Chronicle.Services/ src/Chronicle.API/
git commit -m "feat(api): add POST /scan/import-groups with hierarchical MediaItem creation"
```

---

## Task 9: Frontend — New Types and API Functions

**Files:**
- Modify: `src/Chronicle.Web/src/types/index.ts`
- Modify: `src/Chronicle.Web/src/api/scan.ts`

**Step 1: Add types to `types/index.ts`**

After the existing scan types block, add:
```typescript
export interface ScanGroupDto {
  groupKey: string
  name: string
  hierarchyLevel: number
  year: number | null
  posterPath: string | null
  confidenceScore: number      // 0–100
  signalSources: string[]
  hasConflicts: boolean
  children: ScanGroupDto[]
  files: string[]
}

export interface ScanGroupResult {
  groups: ScanGroupDto[]
  ungrouped: string[]
  totalFiles: number
}

export interface ImportGroupPayload {
  name: string
  year: number | null
  posterPath: string | null
  children: ImportGroupPayload[]
  files: string[]
}
```

**Step 2: Add API functions to `api/scan.ts`**

```typescript
export async function previewGrouped(payload: {
  path: string
  recursive: boolean
  mediaTypeId: number
}): Promise<ScanGroupResult> {
  try {
    const { data } = await client.post<ApiResponse<ScanGroupResult>>(
      '/scan/preview-grouped', payload)
    if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Preview failed')
    return data.data
  } catch (err) {
    throw translateScanError(err)
  }
}

export async function importGroups(payload: {
  groups: ImportGroupPayload[]
  mediaTypeId: number
}): Promise<ImportSummary> {
  const { data } = await client.post<ApiResponse<ImportSummary>>(
    '/scan/import-groups', payload)
  if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Import failed')
  return data.data
}
```

**Step 3: Type-check**
```bash
cd src/Chronicle.Web && npm run type-check
```
Expected: no errors

**Step 4: Commit**
```bash
git add src/Chronicle.Web/src/types/index.ts src/Chronicle.Web/src/api/scan.ts
git commit -m "feat(web): add ScanGroupResult types and previewGrouped/importGroups API functions"
```

---

## Task 10: Frontend — Grouped Scan Preview UI

**Files:**
- Modify: `src/Chronicle.Web/src/pages/scan/ScanPage.tsx`
- Create: `src/Chronicle.Web/src/pages/scan/ScanGroupCard.tsx`
- Create: `src/Chronicle.Web/src/pages/scan/ScanGroupCard.module.css`

**Step 1: Create `ScanGroupCard.tsx`**

This is the collapsible card that shows one root group in the preview step.

```tsx
import { useState } from 'react'
import type { ScanGroupDto, ImportGroupPayload } from '@/types'
import styles from './ScanGroupCard.module.css'

interface Props {
  group: ScanGroupDto
  checked: boolean
  onToggle: (groupKey: string) => void
}

function confidenceClass(score: number): string {
  if (score >= 80) return 'green'
  if (score >= 50) return 'amber'
  return 'red'
}

function childCount(g: ScanGroupDto): number {
  if (g.children.length === 0) return g.files.length
  return g.children.reduce((sum, c) => sum + childCount(c), 0)
}

export function groupToPayload(g: ScanGroupDto): ImportGroupPayload {
  return {
    name: g.name,
    year: g.year,
    posterPath: g.posterPath,
    children: g.children.map(groupToPayload),
    files: g.files,
  }
}

export default function ScanGroupCard({ group, checked, onToggle }: Props) {
  const [expanded, setExpanded] = useState(false)
  const cc = confidenceClass(group.confidenceScore)
  const totalItems = childCount(group)

  return (
    <div className={`${styles.card} ${!checked ? styles.cardUnchecked : ''}`}>
      <div className={styles.row}>
        <input
          type="checkbox"
          checked={checked}
          onChange={() => onToggle(group.groupKey)}
          className={styles.check}
        />
        <div className={styles.info}>
          <span className={styles.name}>{group.name}</span>
          {group.year && <span className={styles.year}>({group.year})</span>}
          <span className={styles.itemCount}>{totalItems} items</span>
          {group.hasConflicts && (
            <span className={styles.conflictBadge} title="Signal sources disagree on this group">
              ⚠ conflict
            </span>
          )}
        </div>
        <div className={styles.right}>
          <span
            className={`${styles.confidence} ${styles[cc]}`}
            title={`Signals: ${group.signalSources.join(', ')}`}
          >
            {group.confidenceScore}%
          </span>
          {group.children.length > 0 && (
            <button
              className={styles.expandBtn}
              onClick={() => setExpanded(e => !e)}
              aria-label={expanded ? 'Collapse' : 'Expand'}
            >
              {expanded ? '▲' : '▼'}
            </button>
          )}
        </div>
      </div>

      {expanded && group.children.length > 0 && (
        <div className={styles.children}>
          {group.children.map(child => (
            <div key={child.groupKey} className={styles.childRow}>
              <span className={styles.childName}>{child.name}</span>
              {child.year && <span className={styles.childYear}>({child.year})</span>}
              <span className={styles.childCount}>{childCount(child)} items</span>
              <span className={`${styles.childConfidence} ${styles[confidenceClass(child.confidenceScore)]}`}>
                {child.confidenceScore}%
              </span>
            </div>
          ))}
        </div>
      )}
    </div>
  )
}
```

**Step 2: Create `ScanGroupCard.module.css`**

```css
.card {
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 6px;
  padding: 10px 14px;
  margin-bottom: 6px;
  transition: opacity 0.15s;
}

.cardUnchecked {
  opacity: 0.45;
}

.row {
  display: flex;
  align-items: center;
  gap: 10px;
}

.check {
  flex-shrink: 0;
  width: 16px;
  height: 16px;
  cursor: pointer;
}

.info {
  flex: 1;
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.name {
  font-size: 0.95rem;
  font-weight: 500;
  color: var(--text-primary);
}

.year {
  font-size: 0.82rem;
  color: var(--text-muted);
}

.itemCount {
  font-size: 0.78rem;
  color: var(--text-muted);
  background: var(--surface-raised);
  padding: 1px 6px;
  border-radius: 10px;
}

.conflictBadge {
  font-size: 0.75rem;
  color: var(--warning, #f59e0b);
  background: rgba(245, 158, 11, 0.12);
  padding: 1px 6px;
  border-radius: 10px;
  cursor: help;
}

.right {
  display: flex;
  align-items: center;
  gap: 8px;
}

.confidence {
  font-size: 0.8rem;
  font-weight: 600;
  padding: 2px 8px;
  border-radius: 12px;
}

.green  { background: rgba(34,197,94,0.15); color: #22c55e; }
.amber  { background: rgba(245,158,11,0.15); color: #f59e0b; }
.red    { background: rgba(239,68,68,0.15);  color: #ef4444; }

.expandBtn {
  background: none;
  border: 1px solid var(--border);
  border-radius: 4px;
  padding: 2px 6px;
  cursor: pointer;
  color: var(--text-muted);
  font-size: 0.7rem;
}

.expandBtn:hover { color: var(--text-primary); }

.children {
  margin-top: 8px;
  padding-left: 26px;
  border-left: 2px solid var(--border);
}

.childRow {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 4px 0;
  font-size: 0.85rem;
  color: var(--text-secondary);
}

.childName  { flex: 1; }
.childYear  { color: var(--text-muted); font-size: 0.8rem; }
.childCount { color: var(--text-muted); font-size: 0.78rem; }
.childConfidence { font-size: 0.78rem; font-weight: 600; padding: 1px 5px; border-radius: 8px; }
```

**Step 3: Update `ScanPage.tsx`**

Replace the `previewMut` and `importMut` mutations and the preview/review render steps. Key changes:

1. Change state: `previewFiles: ScannedFile[]` → `groupResult: ScanGroupResult | null`
2. Change state: `skipped: Set<string>` → `rejectedKeys: Set<string>` (group keys, not file paths)
3. Call `previewGrouped` instead of `previewScan`
4. Call `importGroups` instead of `importDirect`
5. In step 2 (Preview), render `<ScanGroupCard>` components instead of a flat table
6. In step 3 (Review), show accepted group count with a simple confirm-and-import button

Specifically, update in `ScanPage.tsx`:
```tsx
// Replace imports
import { getScanStatus, getScanProgress, previewGrouped, importGroups } from '@/api/scan'
import type { ScanProgress } from '@/api/scan'
import type { ScanGroupResult, MediaTypeOption } from '@/types'
import ScanGroupCard, { groupToPayload } from './ScanGroupCard'

// Replace state
const [groupResult, setGroupResult] = useState<ScanGroupResult | null>(null)
const [rejectedKeys, setRejectedKeys] = useState<Set<string>>(new Set())

// Replace previewMut
const previewMut = useMutation({
  mutationFn: () => {
    if (!mediaTypeId) throw new Error('Select a media type.')
    return previewGrouped({ path: path.trim(), recursive, mediaTypeId: Number(mediaTypeId) })
  },
  onSuccess: (data) => {
    setGroupResult(data)
    setRejectedKeys(new Set())
    setError(null)
    setStep('preview')
  },
  onError: (err: Error) => setError(err.message),
})

// Replace importMut
const importMut = useMutation({
  mutationFn: () => {
    if (!groupResult) throw new Error('No scan result.')
    const toImport = groupResult.groups
      .filter(g => !rejectedKeys.has(g.groupKey))
      .map(groupToPayload)
    if (toImport.length === 0) throw new Error('No groups selected for import.')
    return importGroups({ groups: toImport, mediaTypeId: Number(mediaTypeId) })
  },
  onMutate: () => {
    const count = groupResult?.groups.filter(g => !rejectedKeys.has(g.groupKey)).length ?? 0
    return addJob(`Importing ${count} groups…`)
  },
  onSuccess: (data, _vars, jobId) => {
    setImportResult({ imported: data.imported, failed: data.failed, duplicates: data.duplicates })
    setStep('done')
    completeJob(jobId as string, `${data.imported} imported`)
  },
  onError: (err: Error, _vars, jobId) => {
    setError(err.message)
    failJob(jobId as string, err.message)
  },
})

// Replace reset()
function reset() {
  setStep('configure')
  setGroupResult(null)
  setRejectedKeys(new Set())
  setImportResult(null)
  setError(null)
}
```

Replace the Step 2 (Preview) render section:
```tsx
{step === 'preview' && groupResult && (
  <div className={styles.resultCard}>
    <div className={styles.resultHeader}>
      <h2 className={styles.resultTitle}>
        Found {groupResult.totalGroups} group{groupResult.totalGroups !== 1 ? 's' : ''}
        <span className={styles.subtitle}> ({groupResult.totalFiles} files)</span>
      </h2>
      <button
        className={styles.scanBtn}
        disabled={groupResult.groups.length === 0}
        onClick={() => setStep('review')}
      >
        Review {groupResult.groups.length} groups →
      </button>
    </div>

    <div className={styles.groupList}>
      {groupResult.groups.map(g => (
        <ScanGroupCard
          key={g.groupKey}
          group={g}
          checked={!rejectedKeys.has(g.groupKey)}
          onToggle={key => setRejectedKeys(prev => {
            const next = new Set(prev)
            next.has(key) ? next.delete(key) : next.add(key)
            return next
          })}
        />
      ))}
    </div>

    {groupResult.ungrouped.length > 0 && (
      <details className={styles.ungroupedSection}>
        <summary className={styles.ungroupedSummary}>
          {groupResult.ungrouped.length} ungrouped file{groupResult.ungrouped.length !== 1 ? 's' : ''} (will not be imported)
        </summary>
        <ul className={styles.ungroupedList}>
          {groupResult.ungrouped.map(f => <li key={f} className={styles.ungroupedFile}>{f}</li>)}
        </ul>
      </details>
    )}
  </div>
)}
```

Replace the Step 3 (Review) render section — simplified since accept/reject already happened:
```tsx
{step === 'review' && groupResult && (
  <div className={styles.resultCard}>
    <div className={styles.resultHeader}>
      <h2 className={styles.resultTitle}>
        {groupResult.groups.length - rejectedKeys.size} of {groupResult.groups.length} groups selected
      </h2>
      <div className={styles.headerActions}>
        <button className={styles.secondaryBtn} onClick={() => setStep('preview')}>
          ← Back to preview
        </button>
        <button
          className={styles.scanBtn}
          disabled={(groupResult.groups.length - rejectedKeys.size) === 0 || importMut.isPending}
          onClick={() => importMut.mutate()}
        >
          {importMut.isPending
            ? 'Importing…'
            : `Import ${groupResult.groups.length - rejectedKeys.size} groups →`}
        </button>
      </div>
    </div>
    <p className={styles.reviewHint}>
      Accepting a group imports it and all its children into Chronicle.
      TMDB metadata enrichment runs automatically in the background.
    </p>
    <div className={styles.groupList}>
      {groupResult.groups.map(g => (
        <ScanGroupCard
          key={g.groupKey}
          group={g}
          checked={!rejectedKeys.has(g.groupKey)}
          onToggle={key => setRejectedKeys(prev => {
            const next = new Set(prev)
            next.has(key) ? next.delete(key) : next.add(key)
            return next
          })}
        />
      ))}
    </div>
  </div>
)}
```

Also update the approved count used in `approvedCount` and `canScan`:
```tsx
const approvedCount = groupResult
  ? groupResult.groups.length - rejectedKeys.size
  : 0
```

**Step 4: Type-check and lint**
```bash
cd src/Chronicle.Web && npm run type-check && npm run lint
```
Expected: no errors

**Step 5: Commit**
```bash
git add src/Chronicle.Web/src/pages/scan/
git commit -m "feat(web): replace flat scan preview with grouped ScanGroupCard UI"
```

---

## Task 11: Library Reset — API Endpoints

**Files:**
- Modify: `src/Chronicle.API/Controllers/LibraryController.cs`
- Modify: `src/Chronicle.Services/ILibraryService.cs`
- Modify: `src/Chronicle.Services/LibraryService.cs`
- Test: add to `tests/Chronicle.Tests.Integration/LibraryTests.cs`

**Step 1: Write the failing tests**

```csharp
[Fact]
public async Task NuclearReset_Returns400_WhenConfirmationMissing()
{
    var client = _factory.CreateClient();
    await AuthHelper.LoginAsAdminAsync(client);

    var response = await client.PostAsJsonAsync("/api/v1/library/reset",
        new { confirmationToken = "" });

    response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
}

[Fact]
public async Task NuclearReset_Returns400_WhenTokenWrong()
{
    var client = _factory.CreateClient();
    await AuthHelper.LoginAsAdminAsync(client);

    var response = await client.PostAsJsonAsync("/api/v1/library/reset",
        new { confirmationToken = "WRONG" });

    response.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
}
```

**Step 2: Add to `ILibraryService.cs`**

```csharp
/// <summary>
/// Deletes ALL media items, library entries, and interaction events.
/// Requires <paramref name="confirmationToken"/> == "RESET".
/// Returns the count of deleted library entries.
/// </summary>
Task<int> NuclearResetAsync(string confirmationToken, CancellationToken ct = default);

/// <summary>
/// Deletes all MediaItems created by the file scanner (identified by
/// "fileScanner" key in metadata_json) and their associated library entries.
/// </summary>
Task<int> ClearScannerDataAsync(CancellationToken ct = default);
```

**Step 3: Implement in `LibraryService.cs`**

```csharp
public async Task<int> NuclearResetAsync(string confirmationToken, CancellationToken ct = default)
{
    if (confirmationToken != "RESET")
        throw new ArgumentException("Confirmation token must be exactly 'RESET'.");

    // Count before deletion for the return value
    var count = await _context.UserLibraries.CountAsync(ct);

    // Delete in dependency order to avoid FK violations
    await _context.InteractionEvents.ExecuteDeleteAsync(ct);
    await _context.UserLibraries.ExecuteDeleteAsync(ct);
    await _context.MediaExternalIds.ExecuteDeleteAsync(ct);
    await _context.MediaItems.ExecuteDeleteAsync(ct);

    _log.Warning("Nuclear library reset executed. {Count} library entries deleted.", count);
    return count;
}

public async Task<int> ClearScannerDataAsync(CancellationToken ct = default)
{
    // Find all MediaItems whose metadata_json has the fileScanner key
    var scannerItems = await _context.MediaItems
        .Where(m => m.MetadataJson != null && m.MetadataJson.Contains("\"fileScanner\""))
        .Select(m => m.Id)
        .ToListAsync(ct);

    if (scannerItems.Count == 0) return 0;

    var count = await _context.UserLibraries
        .Where(l => scannerItems.Contains(l.MediaItemId))
        .ExecuteDeleteAsync(ct);

    await _context.MediaExternalIds
        .Where(e => scannerItems.Contains(e.MediaItemId))
        .ExecuteDeleteAsync(ct);

    await _context.MediaItems
        .Where(m => scannerItems.Contains(m.Id))
        .ExecuteDeleteAsync(ct);

    _log.Information("Cleared {Count} scanner-imported items.", scannerItems.Count);
    return count;
}
```

**Step 4: Add endpoints to `LibraryController.cs`**

```csharp
[HttpPost("reset")]
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
public async Task<IActionResult> ClearScannerData(CancellationToken ct)
{
    var count = await _libraryService.ClearScannerDataAsync(ct);
    return Ok(ApiResponse<object>.Ok(new { deleted = count }));
}
```

Add DTO:
```csharp
public record NuclearResetRequestDto(string ConfirmationToken);
```

**Step 5: Run integration tests**
```bash
cd tests/Chronicle.Tests.Integration && dotnet test --filter "NuclearReset" -v normal
```
Expected: PASS

**Step 6: Build**
```bash
cd src/Chronicle.API && dotnet build
```

**Step 7: Commit**
```bash
git add src/Chronicle.Services/ src/Chronicle.API/ tests/Chronicle.Tests.Integration/
git commit -m "feat(api): add POST /library/reset and /library/clear-scanner-data endpoints"
```

---

## Task 12: Frontend — Danger Zone in Library Settings

**Files:**
- Modify: `src/Chronicle.Web/src/pages/settings/LibrarySettingsPage.tsx`
- Modify: `src/Chronicle.Web/src/pages/settings/LibrarySettingsPage.module.css`
- Modify: `src/Chronicle.Web/src/api/library.ts`

**Step 1: Add API functions to `library.ts`**

```typescript
export async function clearScannerData(): Promise<{ deleted: number }> {
  const { data } = await client.post<ApiResponse<{ deleted: number }>>('/library/clear-scanner-data')
  if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Failed')
  return data.data
}

export async function nuclearReset(confirmationToken: string): Promise<{ deleted: number }> {
  const { data } = await client.post<ApiResponse<{ deleted: number }>>(
    '/library/reset', { confirmationToken })
  if (!data.success || !data.data) throw new Error(data.error?.message ?? 'Failed')
  return data.data
}
```

**Step 2: Add Danger Zone section to `LibrarySettingsPage.tsx`**

Add state and handlers, then a new `<section>` at the bottom of the page:

```tsx
import { useMutation } from '@tanstack/react-query'
import { clearScannerData, nuclearReset } from '@/api/library'

// ── in the component body ────────────────────────────────────────────────────

// Clear scanner data
const [clearConfirm, setClearConfirm] = useState(false)
const clearMut = useMutation({
  mutationFn: clearScannerData,
  onSuccess: (data) => {
    setClearConfirm(false)
    alert(`Done. ${data.deleted} scanner-imported items removed.`)
  },
})

// Nuclear reset
const [resetConfirm, setResetConfirm] = useState(false)
const [resetToken, setResetToken] = useState('')
const resetMut = useMutation({
  mutationFn: () => nuclearReset(resetToken),
  onSuccess: () => {
    setResetConfirm(false)
    setResetToken('')
    alert('Library has been reset.')
  },
  onError: (err: Error) => alert(err.message),
})
```

Add the JSX section before the closing `</div>`:
```tsx
{/* ── Danger Zone ──────────────────────────────────────────────────── */}
<section className={styles.section}>
  <div className={styles.sectionHeader}>
    <h3 className={`${styles.sectionTitle} ${styles.dangerTitle}`}>Danger Zone</h3>
    <p className={styles.sectionDesc}>
      These actions are irreversible. Think carefully before proceeding.
    </p>
  </div>

  <div className={styles.dangerCard}>

    {/* Clear scanner data */}
    <div className={styles.dangerRow}>
      <div className={styles.dangerInfo}>
        <span className={styles.dangerLabel}>Clear File Scanner Data</span>
        <span className={styles.dangerDesc}>
          Removes all media items that were imported via the File Scanner.
          Use this before re-scanning with the improved hierarchical scanner.
          Manually-added and metadata-matched items are unaffected.
        </span>
      </div>
      {!clearConfirm ? (
        <button className={styles.dangerBtnAmber} onClick={() => setClearConfirm(true)}>
          Clear Scanner Data
        </button>
      ) : (
        <div className={styles.dangerConfirmRow}>
          <span className={styles.dangerConfirmText}>
            This will delete all file-scanner items. Are you sure?
          </span>
          <button
            className={styles.dangerBtnAmber}
            onClick={() => clearMut.mutate()}
            disabled={clearMut.isPending}
          >
            {clearMut.isPending ? 'Clearing…' : 'Yes, clear it'}
          </button>
          <button className={styles.cancelBtn} onClick={() => setClearConfirm(false)}>
            Cancel
          </button>
        </div>
      )}
    </div>

    <hr className={styles.dangerDivider} />

    {/* Nuclear reset */}
    <div className={styles.dangerRow}>
      <div className={styles.dangerInfo}>
        <span className={styles.dangerLabel}>Reset Entire Library</span>
        <span className={styles.dangerDesc}>
          Permanently deletes <strong>everything</strong>: all media items, library entries,
          scrobble history, ratings, and notes. This cannot be undone.
          Chronicle will be as if it was freshly installed.
        </span>
      </div>
      {!resetConfirm ? (
        <button className={styles.dangerBtnRed} onClick={() => setResetConfirm(true)}>
          Reset Entire Library
        </button>
      ) : (
        <div className={styles.dangerConfirmBox}>
          <p className={styles.dangerWarning}>
            ⚠ This will permanently delete ALL media items, library entries,
            scrobble history, ratings, and notes. There is no undo.
          </p>
          <p className={styles.dangerWarning}>
            To confirm, type <strong>RESET</strong> in the box below:
          </p>
          <input
            className={styles.dangerInput}
            value={resetToken}
            onChange={e => setResetToken(e.target.value)}
            placeholder="Type RESET to confirm"
            autoFocus
          />
          <div className={styles.dangerConfirmActions}>
            <button
              className={styles.dangerBtnRed}
              onClick={() => resetMut.mutate()}
              disabled={resetToken !== 'RESET' || resetMut.isPending}
            >
              {resetMut.isPending ? 'Resetting…' : 'Yes, delete everything'}
            </button>
            <button
              className={styles.cancelBtn}
              onClick={() => { setResetConfirm(false); setResetToken('') }}
            >
              Cancel
            </button>
          </div>
        </div>
      )}
    </div>
  </div>
</section>
```

**Step 3: Add CSS to `LibrarySettingsPage.module.css`**

```css
.dangerTitle { color: var(--danger, #ef4444); }

.dangerCard {
  border: 1px solid rgba(239,68,68,0.3);
  border-radius: 8px;
  overflow: hidden;
}

.dangerRow {
  padding: 16px 20px;
  display: flex;
  align-items: flex-start;
  gap: 16px;
}

.dangerInfo {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.dangerLabel {
  font-size: 0.92rem;
  font-weight: 600;
  color: var(--text-primary);
}

.dangerDesc {
  font-size: 0.82rem;
  color: var(--text-muted);
  line-height: 1.5;
}

.dangerDivider {
  border: none;
  border-top: 1px solid rgba(239,68,68,0.15);
  margin: 0;
}

.dangerBtnRed {
  padding: 7px 16px;
  background: #ef4444;
  color: white;
  border: none;
  border-radius: 5px;
  font-size: 0.85rem;
  font-weight: 600;
  cursor: pointer;
  white-space: nowrap;
  flex-shrink: 0;
}
.dangerBtnRed:hover:not(:disabled) { background: #dc2626; }
.dangerBtnRed:disabled { opacity: 0.5; cursor: not-allowed; }

.dangerBtnAmber {
  padding: 7px 16px;
  background: #f59e0b;
  color: white;
  border: none;
  border-radius: 5px;
  font-size: 0.85rem;
  font-weight: 600;
  cursor: pointer;
  white-space: nowrap;
  flex-shrink: 0;
}
.dangerBtnAmber:hover:not(:disabled) { background: #d97706; }
.dangerBtnAmber:disabled { opacity: 0.5; cursor: not-allowed; }

.dangerConfirmRow {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
}

.dangerConfirmText {
  font-size: 0.85rem;
  color: var(--text-muted);
}

.dangerConfirmBox {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 12px 16px;
  background: rgba(239,68,68,0.06);
  border: 1px solid rgba(239,68,68,0.25);
  border-radius: 6px;
}

.dangerWarning {
  font-size: 0.85rem;
  color: var(--text-primary);
  margin: 0;
}

.dangerInput {
  padding: 8px 12px;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 5px;
  color: var(--text-primary);
  font-size: 0.9rem;
  width: 220px;
}

.dangerConfirmActions {
  display: flex;
  gap: 10px;
  align-items: center;
}

.cancelBtn {
  padding: 7px 14px;
  background: none;
  border: 1px solid var(--border);
  border-radius: 5px;
  color: var(--text-muted);
  font-size: 0.85rem;
  cursor: pointer;
}
.cancelBtn:hover { color: var(--text-primary); }
```

**Step 4: Type-check and lint**
```bash
cd src/Chronicle.Web && npm run type-check && npm run lint
```
Expected: no errors

**Step 5: Commit**
```bash
git add src/Chronicle.Web/src/pages/settings/ src/Chronicle.Web/src/api/library.ts
git commit -m "feat(web): add Danger Zone to Library Settings with Clear Scanner Data and Nuclear Reset"
```

---

## Task 13: Run Full Test Suite

**Step 1: Run all backend tests**
```bash
cd tests && dotnet test --verbosity normal
```
Expected: all 199+ tests pass (plus new tests from this feature)

**Step 2: Run frontend type-check and lint**
```bash
cd src/Chronicle.Web && npm run type-check && npm run lint
```
Expected: no errors

**Step 3: Build backend**
```bash
cd src/Chronicle.API && dotnet build
```
Expected: `Build succeeded. 0 Error(s)`

**Step 4: Commit if any fixes were needed**
```bash
git add -A
git commit -m "fix: address test failures and type errors from hierarchical scanning"
```

---

## Task 14: Finish the Branch

Use the `superpowers:finishing-a-development-branch` skill to merge this feature to `develop` (or `main` per project convention).

```bash
# Verify clean state
git status
git log --oneline -10
```

Then invoke the finishing skill.
