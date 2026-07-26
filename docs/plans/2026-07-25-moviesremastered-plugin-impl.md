# Movies Remastered (MRDb) Plugin Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Build `Chronicle.Plugin.MoviesRemastered` — a second `IMetadataProvider` for the
existing `fanedits` media type, scraping moviesremastered.com (MRDb). Companion to
`Chronicle.Plugin.FanEdit`.

**Architecture:** Single new standalone repo, `W:\Scripts\Chronicle.Plugin.MoviesRemastered`,
same deployment shape as TMDB/MusicBrainz/FanEdit. **No changes to `W:\Scripts\Chronicle`
are needed** — see the design doc's "Why this doesn't touch Chronicle core" section. All
work here is confined to the new repo.

**Tech Stack:** .NET 9, HtmlAgilityPack, xUnit + FluentAssertions (tests)

**Reference implementation:** `W:\Scripts\Chronicle.Plugin.FanEdit` — read alongside this
plan. Interfaces confirmed by reading `Chronicle.Plugins/IMetadataProvider.cs` and
`Chronicle.Plugins/Models/{MediaMetadata,MediaSearchContext,ScoredCandidate,
MediaTypeSupport,PluginSettingsSchema,PluginManifest}.cs` directly — the signatures below
match what's actually there, not the earlier design doc's approximation.

**Key simplification vs. FanEdit:** MRDb has a real, working title-search endpoint
(`searchresults.php?searchtype=Title&searchterm=...`), so `SearchAsync` does **not** need
FanEdit's elaborate slug-guessing machinery (`BuildSlugCandidates`, `ToggleLeadingArticle`,
`_slugExpansions`). It's a single HTTP call plus scoring.

---

### Task 1: Scaffold the plugin project

**Files:**
- Create: `Chronicle.Plugin.MoviesRemastered.csproj`
- Create: `manifest.json`
- Create: `Models/MoviesRemasteredEntry.cs`, `Models/MoviesRemasteredSearchResult.cs` (stub namespaces)
- Create: `tests/Chronicle.Plugin.MoviesRemastered.Tests.csproj`

**Step 1 — `.csproj`** (mirrors `Chronicle.Plugin.FanEdit.csproj` exactly, minus nothing —
same dependency set is needed since we still scrape HTML):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <AssemblyName>Chronicle.Plugin.MoviesRemastered</AssemblyName>
    <RootNamespace>Chronicle.Plugin.MoviesRemastered</RootNamespace>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="Chronicle.Plugin.MoviesRemastered.Tests" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Chronicle\src\Chronicle.Plugins\Chronicle.Plugins.csproj"
                      Private="false" ExcludeAssets="runtime" />
    <ProjectReference Include="..\Chronicle\src\Chronicle.Core\Chronicle.Core.csproj"
                      Private="false" ExcludeAssets="runtime" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="HtmlAgilityPack" Version="1.11.*" />
  </ItemGroup>
  <ItemGroup>
    <Compile Remove="tests/**/*.cs" />
  </ItemGroup>
  <ItemGroup>
    <None Update="manifest.json">
      <CopyToOutputDirectory>Always</CopyToOutputDirectory>
    </None>
  </ItemGroup>
</Project>
```

**Step 2 — `manifest.json`** (real favicon confirmed via `<link rel="icon">` in the live
page source — no need for FanEdit's synthesized inline SVG icon):

```json
{
  "plugin_id":             "chronicle.plugin.moviesremastered",
  "name":                  "Movies Remastered (MRDb)",
  "version":               "1.0.0",
  "author":                "Chronicle Contributors",
  "description":           "Fetches fan edit metadata from the Movies Remastered Database (moviesremastered.com / MRDb), a community fanedit archive. No account required. Please use responsibly — a minimum 1-second delay between requests is enforced.",
  "min_chronicle_version": "0.1.0",
  "entry_type":            "Chronicle.Plugin.MoviesRemastered.MoviesRemasteredMetadataProvider",
  "iconUrl":               "https://www.moviesremastered.com/favicon.ico",
  "brandColorLight":       "#C0392B",
  "brandColorDark":        "#E74C3C",
  "fixMatchHint":          "Enter a moviesremastered.com URL (e.g. https://www.moviesremastered.com/movieinfo.php?id=12179) or a bare MRDb numeric ID",
  "background_tasks": [
    {
      "task_id":         "fetch-missing-metadata",
      "display_name":    "Fetch Missing Metadata",
      "description":     "Looks up MRDb metadata for fan edits that don't have it yet.",
      "default_cron":    null,
      "default_enabled": false,
      "schedulable":     false,
      "run_confirmation": {
        "title":   "Fetch MRDb metadata?",
        "message": "Movies Remastered is a small community site. This task makes one HTTP request per fan edit with a minimum 1-second delay between each — on a large library this will take a long time. Please run this sparingly, not more than a few times per week."
      }
    },
    {
      "task_id":         "resync-all-metadata",
      "display_name":    "Re-sync All Metadata",
      "description":     "Re-fetches MRDb metadata for all fan edits to pick up updated descriptions, ratings, and images.",
      "default_cron":    null,
      "default_enabled": false,
      "schedulable":     false,
      "run_confirmation": {
        "title":   "Re-sync all MRDb metadata?",
        "message": "This will re-fetch MRDb metadata for every fan edit in your library. Movies Remastered is a small community site — each request has a minimum 1-second delay. On a large library this will take a very long time. Please use this sparingly."
      }
    }
  ]
}
```

Brand colors are a proposal (site CSS is almost pure black/white; red is the only header
accent found) — revisit visually against `/users/images/logonew.png` in Task 8 before
shipping.

**Step 3 — test project** (`tests/Chronicle.Plugin.MoviesRemastered.Tests.csproj`, same as FanEdit's):

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net9.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\Chronicle.Plugin.MoviesRemastered.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.*" />
    <PackageReference Include="FluentAssertions" Version="6.*" />
  </ItemGroup>
</Project>
```

**Step 4:** `dotnet build` from the repo root — should succeed with no source files yet.

**Step 5 — commit:**
```bash
git init
git add Chronicle.Plugin.MoviesRemastered.csproj manifest.json tests/
git commit -m "feat: scaffold Chronicle.Plugin.MoviesRemastered project"
```

---

### Task 2: `MoviesRemasteredRateLimiter`

Identical to `FanEditRateLimiter` — copy verbatim, rename.

**Create `MoviesRemasteredRateLimiter.cs`:**
```csharp
namespace Chronicle.Plugin.MoviesRemastered;

/// <summary>
/// Serialises all outbound HTTP requests to moviesremastered.com with a minimum
/// inter-request delay. The 1,000 ms floor is hard-coded and cannot be reduced
/// via configuration. MRDb doesn't publish a stated rate limit like fanedit.org
/// does — this floor is a courtesy default to avoid hammering volunteer infrastructure.
/// </summary>
internal sealed class MoviesRemasteredRateLimiter
{
    private const int FloorMs = 1_000;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly System.Diagnostics.Stopwatch _last = System.Diagnostics.Stopwatch.StartNew();

    public int DelayMs { get; }

    public MoviesRemasteredRateLimiter(int delayMs = FloorMs)
    {
        DelayMs = Math.Max(delayMs, FloorMs);
    }

    public async Task ThrottleAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var elapsed = _last.ElapsedMilliseconds;
            if (elapsed < DelayMs)
                await Task.Delay((int)(DelayMs - elapsed), ct);
            _last.Restart();
        }
        finally
        {
            _gate.Release();
        }
    }
}
```

**Create `tests/MoviesRemasteredRateLimiterTests.cs`** (identical structure to FanEdit's):
```csharp
using FluentAssertions;
using System.Diagnostics;
using Xunit;

namespace Chronicle.Plugin.MoviesRemastered.Tests;

public class MoviesRemasteredRateLimiterTests
{
    [Fact]
    public async Task ThrottleAsync_EnforcesMinimumDelay()
    {
        var limiter = new MoviesRemasteredRateLimiter(delayMs: 200);
        var sw = Stopwatch.StartNew();

        await limiter.ThrottleAsync(CancellationToken.None);
        await limiter.ThrottleAsync(CancellationToken.None);

        sw.Elapsed.TotalMilliseconds.Should().BeGreaterThan(150);
    }

    [Fact]
    public void Constructor_ClampsDelayToFloor()
    {
        var limiter = new MoviesRemasteredRateLimiter(delayMs: 100);
        limiter.DelayMs.Should().Be(1000);
    }

    [Fact]
    public async Task ThrottleAsync_RespectsCancellation()
    {
        var limiter = new MoviesRemasteredRateLimiter(delayMs: 5000);
        await limiter.ThrottleAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource(50);
        var act = () => limiter.ThrottleAsync(cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
```

Run `dotnet test tests/ --filter MoviesRemasteredRateLimiterTests`, confirm pass, commit.

---

### Task 3: Models

**Create `Models/MoviesRemasteredEntry.cs`:**
```csharp
namespace Chronicle.Plugin.MoviesRemastered.Models;

internal sealed class MoviesRemasteredEntry
{
    public string  Title          { get; set; } = string.Empty;
    public string  Url            { get; set; } = string.Empty;
    public string? Overview       { get; set; }
    public int?    Year           { get; set; }
    public int?    RuntimeMinutes { get; set; }
    public string? PosterUrl      { get; set; }
    public List<string> Genres    { get; set; } = [];
    public double? Rating         { get; set; }
    public List<string> Tags      { get; set; } = [];

    // Faneditor
    public string? FaneditorUsername   { get; set; }
    public string? FaneditorProfileUrl { get; set; }

    // Classification
    public string? FanEditType { get; set; }
    public string? Franchise   { get; set; }

    // Source material
    public string? OriginalTitle       { get; set; }
    public string? OriginalReleaseDate { get; set; }
    public int?    OriginalRuntimeMinutes { get; set; }

    // Tech specs
    public MoviesRemasteredTechSpecs? TechSpecs { get; set; }

    // Cut/edit details
    public string? TimeCut   { get; set; }
    public string? TimeAdded { get; set; }
    public string? Intentions  { get; set; }
    public string? ChangeList  { get; set; }

    // Reception
    public int? Views        { get; set; }
    public int? ReviewCount  { get; set; }
    public int? FavoriteCount { get; set; }

    // Certificate / language
    public string? Certificate { get; set; }
    public string? Language    { get; set; }
    public List<string> Subtitles { get; set; } = [];

    // Publishing
    public string? MrdbId          { get; set; }
    public string? ReleaseDate     { get; set; }
}
```

**Create `Models/MoviesRemasteredTechSpecs.cs`:**
```csharp
namespace Chronicle.Plugin.MoviesRemastered.Models;

internal sealed class MoviesRemasteredTechSpecs
{
    public string? Source     { get; set; }
    public string? Resolution { get; set; }
    public string? SoundMix   { get; set; }
}
```

**Create `Models/MoviesRemasteredSearchResult.cs`:**
```csharp
namespace Chronicle.Plugin.MoviesRemastered.Models;

internal sealed class MoviesRemasteredSearchResult
{
    public string  Title           { get; set; } = string.Empty;
    public string  Url             { get; set; } = string.Empty;
    public string? ThumbnailUrl    { get; set; }
    public string? Synopsis        { get; set; }
    public string? OriginalTitle   { get; set; }
    public string? Faneditor       { get; set; }
    public string? Franchise       { get; set; }
    public string? FanEditType     { get; set; }
    public int?    Year            { get; set; }
    /// <summary>Already in minutes on the search-results page (unlike the detail page's "3h:38m:0s" format).</summary>
    public int?    RuntimeMinutes  { get; set; }
    public double? Rating          { get; set; }
}
```

Commit: `git add Models/ && git commit -m "feat: add MoviesRemastered model types"`.

---

### Task 4: `MoviesRemasteredScraper` — search-results parsing

MRDb search-results cards (confirmed real structure, `div.result-card`):

```html
<DIV class="result-card d-flex" ...>
  <DIV ...><A HREF=movieinfo.php?id=12179><IMG SRC=https://moviesremastered.com/images/12179-posterart.jpeg?cb=...></A>
    <DIV class=column ...><DIV ...><i class="fa-sharp fa-solid fa-star" ...></i> N/A <img .../></DIV></DIV>
  </DIV>
  <DIV ...>
    <B style='font-size:1.2em;'><A HREF=/movieinfo.php?id=12179>Snow: Part I</A></B><BR>
    <B>Original Title: </B><A HREF=/searchresults.php?...>Game of Thrones (TV Series)(2011)</A><BR>
    <B>Faneditor: </B><A HREF=/user/Spartan47>Spartan47</A><BR>
    <B>Franchise:</B> <span style='color:var(--text-dim)'>Game of Thrones</span><BR>
    <B>Fanedit Type:</B> <span style='color:var(--text-dim)'>TV-to-Movie</span><BR>
    <B>Fanedit Release Date: </B><span style='color:var(--text-dim)'>25th July 2026</span><BR>
    <B>Fanedit Runtime:</B> <span style='color:var(--text-dim)'>218</span><BR>
    <B>Synopsis:</B> <span style='color:var(--text-dim)'>As the Seven Kingdoms...</span><BR>
  </DIV>
</DIV>
```

Note the runtime here is already plain minutes (`218`), unlike the detail page's `3h:38m:0s`.

**Add to `MoviesRemasteredScraper.cs`** (new file):
```csharp
using HtmlAgilityPack;
using Chronicle.Plugin.MoviesRemastered.Models;
using System.Text;
using System.Text.RegularExpressions;

namespace Chronicle.Plugin.MoviesRemastered;

internal sealed class MoviesRemasteredScraper
{
    private static readonly Regex _idFromUrl = new(@"movieinfo\.php\?id=(\d+)", RegexOptions.IgnoreCase);
    private static readonly Regex _hmsRuntime = new(@"(\d+)h:(\d+)m:(\d+)s", RegexOptions.IgnoreCase);

    public List<MoviesRemasteredSearchResult> ParseSearchResults(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var results = new List<MoviesRemasteredSearchResult>();

        var cards = doc.DocumentNode.SelectNodes("//*[contains(@class,'result-card')]");
        if (cards is null) return results;

        foreach (var card in cards)
        {
            var titleAnchor = card.SelectSingleNode(".//b[contains(@style,'font-size:1.2em')]/a")
                            ?? card.SelectSingleNode(".//b/a[contains(@href,'movieinfo.php')]");
            if (titleAnchor is null) continue;

            var href = titleAnchor.GetAttributeValue("href", string.Empty);
            var idMatch = _idFromUrl.Match(href);
            if (!idMatch.Success) continue;

            var fields = ParseLabeledFields(card);

            var result = new MoviesRemasteredSearchResult
            {
                Title         = HtmlEntity.DeEntitize(titleAnchor.InnerText).Trim(),
                Url           = $"https://www.moviesremastered.com/movieinfo.php?id={idMatch.Groups[1].Value}",
                ThumbnailUrl  = card.SelectSingleNode(".//img[@src]")?.GetAttributeValue("src", null),
                OriginalTitle = fields.GetValueOrDefault("Original Title"),
                Faneditor     = fields.GetValueOrDefault("Faneditor"),
                Franchise     = fields.GetValueOrDefault("Franchise"),
                FanEditType   = fields.GetValueOrDefault("Fanedit Type"),
                Synopsis      = fields.GetValueOrDefault("Synopsis"),
            };

            if (fields.TryGetValue("Fanedit Release Date", out var relDate))
            {
                var ym = Regex.Match(relDate, @"\b(19|20)\d{2}\b");
                if (ym.Success) result.Year = int.Parse(ym.Value);
            }

            if (fields.TryGetValue("Fanedit Runtime", out var rt) && int.TryParse(rt.Trim(), out var minutes))
                result.RuntimeMinutes = minutes;

            var ratingNode = card.SelectSingleNode(".//i[contains(@class,'fa-star')]/parent::*");
            if (ratingNode is not null)
            {
                var ratingText = HtmlEntity.DeEntitize(ratingNode.InnerText).Trim();
                if (!ratingText.Contains("N/A", StringComparison.OrdinalIgnoreCase))
                {
                    var rm = Regex.Match(ratingText, @"[\d.]+");
                    if (rm.Success && double.TryParse(rm.Value,
                        System.Globalization.NumberStyles.Any,
                        System.Globalization.CultureInfo.InvariantCulture, out var rv))
                        result.Rating = rv;
                }
            }

            results.Add(result);
        }

        return results;
    }

    // ── Shared labeled-field walker ───────────────────────────────────────────
    // MRDb has no class hooks on field values (unlike FanEdit's jrFieldRow/jrFieldLabel/
    // jrFieldValue) — fields are a flat run of <B>Label:</B> value <BR> siblings inside one
    // container. Walk children, tracking the current label, buffering nodes until the next
    // <B>…:</B> or <HR>, then render the buffer to a string.
    internal static Dictionary<string, string> ParseLabeledFields(HtmlNode container)
    {
        var raw = new Dictionary<string, List<HtmlNode>>(StringComparer.OrdinalIgnoreCase);
        string? currentLabel = null;
        var buffer = new List<HtmlNode>();

        void Flush()
        {
            if (currentLabel is not null)
                raw[currentLabel] = buffer;
            buffer = new List<HtmlNode>();
        }

        foreach (var node in container.ChildNodes)
        {
            if (node.Name.Equals("b", StringComparison.OrdinalIgnoreCase))
            {
                var text = HtmlEntity.DeEntitize(node.InnerText).Trim();
                if (text.EndsWith(':'))
                {
                    Flush();
                    currentLabel = text.TrimEnd(':').Trim();
                    continue;
                }
            }
            if (node.Name.Equals("hr", StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                currentLabel = null;
                continue;
            }
            if (currentLabel is not null)
                buffer.Add(node);
        }
        Flush();

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (label, nodes) in raw)
        {
            var value = RenderFieldValue(nodes);
            if (!string.IsNullOrWhiteSpace(value))
                result[label] = value;
        }
        return result;
    }

    /// <summary>
    /// Multi-value fields (Genre, Subtitles) render as several &lt;a&gt;/&lt;span&gt;
    /// items separated by "•" in the source — join with " • ". Single-value fields
    /// are plain text or one anchor/span.
    /// </summary>
    private static string RenderFieldValue(List<HtmlNode> nodes)
    {
        var namedAnchors = nodes
            .Where(n => n.Name.Equals("a", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(HtmlEntity.DeEntitize(n.InnerText)))
            .ToList();
        if (namedAnchors.Count > 1)
            return string.Join(" • ", namedAnchors.Select(a => HtmlEntity.DeEntitize(a.InnerText).Trim()));

        var sb = new StringBuilder();
        foreach (var n in nodes)
        {
            if (n.Name is "br" or "img") continue;
            sb.Append(HtmlEntity.DeEntitize(n.InnerText));
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    internal static int? ParseHmsRuntimeToMinutes(string s)
    {
        var m = _hmsRuntime.Match(s);
        if (!m.Success) return null;
        return int.Parse(m.Groups[1].Value) * 60 + int.Parse(m.Groups[2].Value);
    }
}
```

**Create `tests/MoviesRemasteredScraperTests.cs`** (fixture trimmed from a real captured
`searchresults.php?searchtype=Title&searchterm=Snow` response):
```csharp
using FluentAssertions;
using Xunit;

namespace Chronicle.Plugin.MoviesRemastered.Tests;

public class MoviesRemasteredScraperSearchTests
{
    private static MoviesRemasteredScraper Scraper() => new();

    private const string SearchHtml = """
        <html><body>
        <DIV class="result-card d-flex">
          <DIV><A HREF=movieinfo.php?id=12179><IMG SRC=https://moviesremastered.com/images/12179-posterart.jpeg?cb=1785036872></A>
            <DIV class=column><DIV><i class="fa-sharp fa-solid fa-star"></i> N/A <img src=Staroutline.png></DIV></DIV>
          </DIV>
          <DIV>
            <B style='font-size:1.2em;'><A HREF=/movieinfo.php?id=12179>Snow: Part I</A></B><BR>
            <B>Original Title: </B><A HREF=/searchresults.php?searchtype=OriginalTitle&searchterm=x>Game of Thrones (TV Series)(2011)</A><BR>
            <B>Faneditor: </B><A HREF=/user/Spartan47>Spartan47</A><BR>
            <B>Franchise:</B> <span style='color:var(--text-dim)'>Game of Thrones</span><BR>
            <B>Fanedit Type:</B> <span style='color:var(--text-dim)'>TV-to-Movie</span><BR>
            <B>Fanedit Release Date: </B><span style='color:var(--text-dim)'>25th July 2026</span><BR>
            <B>Fanedit Runtime:</B> <span style='color:var(--text-dim)'>218</span><BR>
            <B>Synopsis:</B> <span style='color:var(--text-dim)'>As the Seven Kingdoms are consumed by political conflict.</span><BR>
          </DIV>
        </DIV>
        </body></html>
        """;

    [Fact]
    public void ParseSearchResults_ExtractsTitleAndId()
    {
        var results = Scraper().ParseSearchResults(SearchHtml);

        results.Should().HaveCount(1);
        results[0].Title.Should().Be("Snow: Part I");
        results[0].Url.Should().Be("https://www.moviesremastered.com/movieinfo.php?id=12179");
    }

    [Fact]
    public void ParseSearchResults_ExtractsLabeledFields()
    {
        var r = Scraper().ParseSearchResults(SearchHtml)[0];

        r.OriginalTitle.Should().Be("Game of Thrones (TV Series)(2011)");
        r.Faneditor.Should().Be("Spartan47");
        r.Franchise.Should().Be("Game of Thrones");
        r.FanEditType.Should().Be("TV-to-Movie");
        r.Year.Should().Be(2026);
        r.RuntimeMinutes.Should().Be(218);
        r.Synopsis.Should().StartWith("As the Seven Kingdoms");
    }

    [Fact]
    public void ParseSearchResults_RatingIsNull_WhenNA()
    {
        var r = Scraper().ParseSearchResults(SearchHtml)[0];
        r.Rating.Should().BeNull();
    }

    [Fact]
    public void ParseSearchResults_HandlesNoResults_Gracefully()
    {
        Scraper().ParseSearchResults("<html><body>No results</body></html>").Should().BeEmpty();
    }

    [Theory]
    [InlineData("3h:38m:0s", 218)]
    [InlineData("0h:45m:0s", 45)]
    [InlineData("9h:1m:0s", 541)]
    public void ParseHmsRuntimeToMinutes_ParsesCorrectly(string input, int expectedMinutes)
    {
        MoviesRemasteredScraper.ParseHmsRuntimeToMinutes(input).Should().Be(expectedMinutes);
    }
}
```

Run, verify pass, commit.

---

### Task 5: `MoviesRemasteredScraper` — detail-page parsing

Extends the same file with `ParseDetailPage`, reusing `ParseLabeledFields` from Task 4
against the detail page's field container.

**Append to `MoviesRemasteredScraper.cs`:**
```csharp
    // ── Detail page ───────────────────────────────────────────────────────────

    public MoviesRemasteredEntry ParseDetailPage(string html, string url)
    {
        var doc   = new HtmlDocument();
        doc.LoadHtml(html);
        var entry = new MoviesRemasteredEntry { Url = url };

        // 1. Title — JSON-LD "name" preferred (no site-name suffix), fall back to og:title
        entry.Title = ParseJsonLdField(doc, "name")
            ?? OgMeta(doc, "og:title")
            ?? PageTitle(doc)
            ?? string.Empty;
        entry.Title = Regex.Replace(entry.Title, @"\s*\|\s*MRDb Fanedits\s*$", "").Trim();

        // 2. Overview — prefer the full Synopsis section; JSON-LD/og:description as fallback
        entry.Overview = ParseLabeledSection(doc, "Synopsis")
            ?? ParseJsonLdField(doc, "description")
            ?? OgMeta(doc, "og:description");

        // 3. Poster — og:image is stable (no cache-bust query string)
        entry.PosterUrl = OgMeta(doc, "og:image") ?? ParseJsonLdField(doc, "image");

        // 4. Labeled key/value fields — the flat <B>Label:</B> value <BR> run
        var container = FindFieldContainer(doc);
        var fields = container is null
            ? new Dictionary<string, string>()
            : ParseLabeledFields(container);

        entry.FaneditorUsername = fields.GetValueOrDefault("Faneditor");
        var faneditorAnchor = container?.SelectSingleNode(".//b[contains(text(),'Faneditor')]/following-sibling::a[1]");
        entry.FaneditorProfileUrl = faneditorAnchor?.GetAttributeValue("href", null);

        entry.FanEditType         = fields.GetValueOrDefault("Fanedit Type");
        entry.ReleaseDate         = fields.GetValueOrDefault("Fanedit Release Date");
        entry.TimeCut             = fields.GetValueOrDefault("Time Cut");
        entry.TimeAdded           = fields.GetValueOrDefault("Time Added");
        entry.Franchise           = fields.GetValueOrDefault("Franchise");
        entry.OriginalTitle       = fields.GetValueOrDefault("Original Title");
        entry.OriginalReleaseDate = fields.GetValueOrDefault("Original Release Date");
        entry.Certificate         = fields.GetValueOrDefault("Certificate");
        entry.Language            = fields.GetValueOrDefault("Language");

        if (fields.TryGetValue("Fanedit Runtime", out var rt))
            entry.RuntimeMinutes = ParseHmsRuntimeToMinutes(rt);
        if (fields.TryGetValue("Original Runtime", out var ort))
            entry.OriginalRuntimeMinutes = ParseHmsRuntimeToMinutes(ort);

        if (fields.TryGetValue("Genre", out var genreVal))
            entry.Genres = SplitDotList(genreVal);
        if (fields.TryGetValue("Subtitles", out var subVal))
            entry.Subtitles = SplitDotList(subVal);

        if (fields.TryGetValue("Fanedit Release Date", out var rel))
        {
            var ym = Regex.Match(rel, @"\b(19|20)\d{2}\b");
            if (ym.Success) entry.Year = int.Parse(ym.Value);
        }

        var source     = fields.GetValueOrDefault("Source");
        var resolution = fields.GetValueOrDefault("Resolution");
        var soundMix   = fields.GetValueOrDefault("Sound Mix");
        if (source is not null || resolution is not null || soundMix is not null)
            entry.TechSpecs = new MoviesRemasteredTechSpecs
            {
                Source = source, Resolution = resolution, SoundMix = soundMix,
            };

        // 5. Free-text sections
        entry.Intentions = ParseLabeledSection(doc, "Intentions");
        entry.ChangeList = ParseLabeledSection(doc, "Change List");

        // 6. Stats block — rating / views / reviews / favorites
        ParseStats(doc, entry);

        // 7. Franchise tag list
        if (entry.Franchise is not null)
            entry.Tags = [entry.Franchise, .. entry.Tags];
        if (entry.FanEditType is not null && !entry.Tags.Contains(entry.FanEditType))
            entry.Tags.Add(entry.FanEditType);

        // 8. MRDb numeric ID from the URL
        var idM = _idFromUrl.Match(url);
        entry.MrdbId = idM.Success ? idM.Groups[1].Value : null;

        return entry;
    }

    /// <summary>
    /// The field container is the &lt;div&gt; whose direct children include the
    /// "Faneditor:" &lt;B&gt; label — locate it by that anchor rather than assuming a
    /// fixed class name, since the surrounding layout classes are inline-style-only.
    /// </summary>
    private static HtmlNode? FindFieldContainer(HtmlDocument doc)
    {
        var label = doc.DocumentNode.SelectSingleNode("//b[starts-with(normalize-space(text()),'Faneditor')]");
        return label?.ParentNode;
    }

    private static void ParseStats(HtmlDocument doc, MoviesRemasteredEntry entry)
    {
        var items = doc.DocumentNode.SelectNodes("//*[contains(@class,'stats-item')]");
        if (items is null) return;

        foreach (var item in items)
        {
            var label = item.SelectSingleNode(".//b")?.InnerText.Trim();
            if (label is null) continue;
            var text = HtmlEntity.DeEntitize(item.InnerText).Replace(label, "", StringComparison.OrdinalIgnoreCase).Trim();

            switch (label.ToLowerInvariant())
            {
                case "mrdb rating":
                    if (!text.Contains("No votes", StringComparison.OrdinalIgnoreCase))
                    {
                        var m = Regex.Match(text, @"[\d.]+");
                        if (m.Success && double.TryParse(m.Value,
                            System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out var r))
                            entry.Rating = r;
                    }
                    break;
                case "views":
                {
                    var m = Regex.Match(text, @"\d+");
                    if (m.Success) entry.Views = int.Parse(m.Value);
                    break;
                }
                case "reviews":
                {
                    var m = Regex.Match(text, @"\d+");
                    if (m.Success) entry.ReviewCount = int.Parse(m.Value);
                    break;
                }
                case "favorite":
                {
                    var m = Regex.Match(text, @"\d+");
                    if (m.Success) entry.FavoriteCount = int.Parse(m.Value);
                    break;
                }
            }
        }
    }

    /// <summary>Finds the &lt;h3&gt; matching <paramref name="label"/> (e.g. "Synopsis") and
    /// returns the text of its following siblings within the same parent div, up to the next &lt;hr&gt;.</summary>
    private static string? ParseLabeledSection(HtmlDocument doc, string label)
    {
        var h3 = doc.DocumentNode.SelectNodes("//h3")?.FirstOrDefault(n =>
            HtmlEntity.DeEntitize(n.InnerText).Trim().TrimEnd(':').Equals(label, StringComparison.OrdinalIgnoreCase));
        if (h3 is null) return null;

        var sb = new StringBuilder();
        var node = h3.NextSibling;
        while (node is not null)
        {
            sb.Append(HtmlEntity.DeEntitize(node.InnerText ?? string.Empty));
            node = node.NextSibling;
        }

        var text = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static string? ParseJsonLdField(HtmlDocument doc, string field)
    {
        var scripts = doc.DocumentNode.SelectNodes("//script[@type='application/ld+json']");
        if (scripts is null) return null;

        foreach (var script in scripts)
        {
            try
            {
                using var jd = System.Text.Json.JsonDocument.Parse(script.InnerText);
                if (jd.RootElement.TryGetProperty("@type", out var t) && t.GetString() == "Movie" &&
                    jd.RootElement.TryGetProperty(field, out var v))
                    return v.GetString();
            }
            catch (System.Text.Json.JsonException) { /* malformed/partial LD+JSON — skip */ }
        }
        return null;
    }

    private static string? OgMeta(HtmlDocument doc, string property)
        => doc.DocumentNode.SelectSingleNode($"//meta[@property='{property}']")
              ?.GetAttributeValue("content", null);

    private static string? PageTitle(HtmlDocument doc)
        => doc.DocumentNode.SelectSingleNode("//title")?.InnerText.Trim();

    private static List<string> SplitDotList(string s) =>
        s.Split('•', StringSplitOptions.RemoveEmptyEntries)
         .Select(x => x.Trim())
         .Where(x => x.Length > 0)
         .ToList();
```

Add `using System.Text;` at the top of the file if not already present from Task 4.

**Append to `tests/MoviesRemasteredScraperTests.cs`** (fixture trimmed from the real
`movieinfo.php?id=12179` response captured during design):
```csharp
public class MoviesRemasteredScraperDetailTests
{
    private static MoviesRemasteredScraper Scraper() => new();

    private const string DetailHtml = """
        <html><head>
        <title>Snow: Part I | MRDb Fanedits</title>
        <meta property="og:title" content="Snow: Part I | MRDb Fanedits">
        <meta property="og:description" content="As the Seven Kingdoms are consumed by political conflict and the struggle for the Iron Throne, Jon Snow leaves Winterfell.">
        <meta property="og:image" content="https://moviesremastered.com/images/12179-posterart.jpeg">
        <script type="application/ld+json">
        {"@context":"https://schema.org","@type":"Movie","name":"Snow: Part I","description":"As the Seven Kingdoms are consumed by political conflict.","image":"https://moviesremastered.com/images/12179-posterart.jpeg","url":"https://www.moviesremastered.com/movieinfo.php?id=12179"}
        </script>
        </head><body>
        <div class="stats-container">
          <div class="stats-item"><B>MRDb Rating</B><br><i class="fa-solid fa-star"></i> No votes</div>
          <div class="stats-item"><B>Views</B><br><IMG SRC="views icon.png">&nbsp95</div>
          <div class="stats-item"><B>Reviews</B><br><B id=reviewcount>0</B></div>
          <div class="stats-item"><B>Favorite</B><br><SPAN ID=favcnt>3</SPAN></div>
        </div>
        <div class=column>
          <B>Faneditor: </B><A HREF=Spartan47>Spartan47</A>&nbsp&nbsp<BR>
          <B>Fanedit Type: </B>TV-to-Movie<BR>
          <B>Fanedit Release Date: </B>25th July 2026<BR>
          <B>Fanedit Runtime: </B>3h:38m:0s<BR>
          <B>Time Cut: </B>5h:23m:0s<BR>
          <B>Time Added: </B>0h:0m:0s<BR>
          <B>Franchise: </B><A HREF=searchresults.php?searchtype=Franchise&franchise=Game+of+Thrones>Game of Thrones</A><BR>
          <B>Genre: </B><A HREF=x?genre=Adventure>Adventure</A> • <A HREF=x?genre=Drama>Drama</A><BR>
          <B>Original Title: </B><A HREF=x>Game of Thrones (TV Series)(2011)</A><BR>
          <B>Original Release Date: </B>3rd January 2011<BR>
          <B>Original Runtime: </B>9h:1m:0s<BR>
          <HR>
          <B>Certificate: </B>18<BR>
          <B>Source: </B>4K<BR>
          <B>Resolution: </B>4k<BR>
          <B>Sound Mix: </B>5.1. Channels<BR>
          <B>Language: </B>English<BR>
          <B>Subtitles: </B>English • Spanish<BR>
        </div>
        <DIV><H3 style="color:red;">Synopsis:</H3>As the Seven Kingdoms are consumed by political conflict and the struggle for the Iron Throne, Jon Snow leaves Winterfell.<BR><BR></DIV>
        <HR>
        <DIV><H3 style="color:red;">Intentions:</H3>To combine Jon and Bran's storylines from season 1 and 2.<BR><BR></DIV>
        <HR>
        <DIV><H3 style="color:red;">Change List:</H3>Combined Jon Snow's storyline with Bran Stark's journey.<BR></DIV>
        </body></html>
        """;

    [Fact]
    public void ParseDetailPage_ExtractsTitle_WithoutSiteSuffix()
    {
        var entry = Scraper().ParseDetailPage(DetailHtml, "https://www.moviesremastered.com/movieinfo.php?id=12179");
        entry.Title.Should().Be("Snow: Part I");
    }

    [Fact]
    public void ParseDetailPage_ExtractsPosterUrl_FromOgImage()
    {
        var entry = Scraper().ParseDetailPage(DetailHtml, "https://www.moviesremastered.com/movieinfo.php?id=12179");
        entry.PosterUrl.Should().Be("https://moviesremastered.com/images/12179-posterart.jpeg");
    }

    [Fact]
    public void ParseDetailPage_ExtractsSynopsisSection_FullText()
    {
        var entry = Scraper().ParseDetailPage(DetailHtml, "https://www.moviesremastered.com/movieinfo.php?id=12179");
        entry.Overview.Should().Contain("Jon Snow leaves Winterfell");
    }

    [Fact]
    public void ParseDetailPage_ExtractsIntentionsAndChangeList()
    {
        var entry = Scraper().ParseDetailPage(DetailHtml, "x");
        entry.Intentions.Should().Contain("Bran's storylines");
        entry.ChangeList.Should().Contain("Bran Stark's journey");
    }

    [Fact]
    public void ParseDetailPage_ExtractsLabeledFields()
    {
        var entry = Scraper().ParseDetailPage(DetailHtml, "x");

        entry.FaneditorUsername.Should().Be("Spartan47");
        entry.FanEditType.Should().Be("TV-to-Movie");
        entry.Franchise.Should().Be("Game of Thrones");
        entry.OriginalTitle.Should().Be("Game of Thrones (TV Series)(2011)");
        entry.Certificate.Should().Be("18");
        entry.Genres.Should().BeEquivalentTo(["Adventure", "Drama"]);
        entry.Subtitles.Should().BeEquivalentTo(["English", "Spanish"]);
    }

    [Fact]
    public void ParseDetailPage_ParsesHmsRuntimes()
    {
        var entry = Scraper().ParseDetailPage(DetailHtml, "x");
        entry.RuntimeMinutes.Should().Be(218);          // 3h:38m
        entry.OriginalRuntimeMinutes.Should().Be(541);  // 9h:1m
    }

    [Fact]
    public void ParseDetailPage_RatingNull_WhenNoVotes()
    {
        var entry = Scraper().ParseDetailPage(DetailHtml, "x");
        entry.Rating.Should().BeNull();
    }

    [Fact]
    public void ParseDetailPage_ExtractsStats()
    {
        var entry = Scraper().ParseDetailPage(DetailHtml, "x");
        entry.Views.Should().Be(95);
        entry.ReviewCount.Should().Be(0);
        entry.FavoriteCount.Should().Be(3);
    }

    [Fact]
    public void ParseDetailPage_ExtractsYear_FromReleaseDate()
    {
        var entry = Scraper().ParseDetailPage(DetailHtml, "x");
        entry.Year.Should().Be(2026);
    }

    [Fact]
    public void ParseDetailPage_HandlesMissingFields_Gracefully()
    {
        var entry = Scraper().ParseDetailPage("<html><body></body></html>", "x");
        entry.Should().NotBeNull();
        entry.Title.Should().BeEmpty();
        entry.Overview.Should().BeNull();
    }
}
```

Run the full test suite for this file, fix any XPath/whitespace mismatches against the
fixtures (the `<B>` sibling-walk is the one part of this plan most likely to need small
adjustments once run for real — HtmlAgilityPack's whitespace-text-node handling around
`&nbsp;` is the usual culprit), then commit.

---

### Task 6: `MoviesRemasteredMetadataProvider` — identity, capabilities, settings, `Configure`

**Create `MoviesRemasteredMetadataProvider.cs`:**
```csharp
using Chronicle.Plugin.MoviesRemastered.Models;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using System.Net;
using System.Text.Json;

namespace Chronicle.Plugin.MoviesRemastered;

/// <summary>
/// IMetadataProvider implementation for moviesremastered.com (MRDb).
/// Supports media type "fanedits" — the same type Chronicle.Plugin.FanEdit declares.
/// No authentication required; search and detail pages are public.
/// </summary>
public sealed class MoviesRemasteredMetadataProvider : IMetadataProvider
{
    private const string BaseUrl        = "https://www.moviesremastered.com";
    private const int    ScoreThreshold = 50;

    private MoviesRemasteredRateLimiter? _limiter;
    private MoviesRemasteredScraper?     _scraper;
    private HttpClient?                  _http;

    public string PluginId => "chronicle.plugin.moviesremastered";
    public string Name     => "Movies Remastered (MRDb)";
    public string Version  => "1.0.0";
    public string Author   => "Chronicle Contributors";

    public MediaTypeSupport[] GetSupportedMediaTypes() =>
    [
        new MediaTypeSupport
        {
            MediaTypeName   = "fanedits",
            // Same DisplayName as Chronicle.Plugin.FanEdit's declaration — an idempotent
            // upsert of identical data, so the "fanedits" type still exists even if only
            // one of the two plugins is installed.
            DisplayName     = "Fan Edits",
            HierarchyLevels = 1,
            // Lower priority than FanEdit's default of 10 — user can reorder via
            // Settings > Metadata Assignment regardless.
            DefaultPriority = 20,
            SupportedFields = ["title", "overview", "year", "poster_url",
                               "runtime_minutes", "genres", "rating", "tags"],
        },
    ];

    public PluginSettingsSchema GetSettingsSchema() => new()
    {
        Settings =
        [
            new SettingDefinition
            {
                Key          = "request_delay_ms",
                Label        = "Request Delay (ms)",
                Description  = "Minimum delay between requests. Floor: 1000 ms. Be kind to the server.",
                Type         = SettingType.Number,
                Required     = false,
                DefaultValue = "1000",
            },
            new SettingDefinition
            {
                Key          = "user_agent",
                Label        = "User-Agent String",
                Type         = SettingType.Text,
                Required     = false,
                DefaultValue = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36",
            },
        ]
    };

    public void Configure(IReadOnlyDictionary<string, string> settings)
    {
        var delayMs = settings.TryGetValue("request_delay_ms", out var d) && int.TryParse(d, out var di) ? di : 1000;
        var ua      = settings.GetValueOrDefault("user_agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36");

        _limiter = new MoviesRemasteredRateLimiter(delayMs);
        _scraper = new MoviesRemasteredScraper();
        _http    = new HttpClient();
        _http.DefaultRequestHeaders.Add("User-Agent", ua);
    }

    private void EnsureConfigured()
    {
        if (_limiter is null)
            throw new InvalidOperationException(
                "MoviesRemasteredMetadataProvider is not configured. Call Configure() first.");
    }

    // SearchAsync / GetByIdAsync / GetImageAsync / HealthCheckAsync — Tasks 7 and 8.
}
```

**Create `tests/MoviesRemasteredMetadataProviderTests.cs`** (identity/settings smoke tests):
```csharp
using FluentAssertions;
using Xunit;

namespace Chronicle.Plugin.MoviesRemastered.Tests;

public class MoviesRemasteredMetadataProviderIdentityTests
{
    [Fact]
    public void GetSupportedMediaTypes_ReturnsFaneditsOnly()
    {
        var provider = new MoviesRemasteredMetadataProvider();
        var types = provider.GetSupportedMediaTypes();

        types.Should().ContainSingle();
        types[0].MediaTypeName.Should().Be("fanedits");
    }

    [Fact]
    public void GetSettingsSchema_HasNoRequiredCredentials()
    {
        var provider = new MoviesRemasteredMetadataProvider();
        var schema = provider.GetSettingsSchema();

        schema.Settings.Should().NotContain(s => s.Required);
        schema.Settings.Should().NotContain(s => s.Key is "username" or "password");
    }

    [Fact]
    public void SearchAsync_ThrowsInvalidOperation_WhenNotConfigured()
    {
        var provider = new MoviesRemasteredMetadataProvider();
        var act = () => provider.SearchAsync(new Chronicle.Plugins.Models.MediaSearchContext("Test"));
        act.Should().ThrowAsync<InvalidOperationException>();
    }
}
```

Run, verify pass, commit.

---

### Task 7: `SearchAsync` + scoring

Much simpler than FanEdit's — MRDb has a real search endpoint, so no slug-guessing is
needed. Confirmed URL shape:
`GET /searchresults.php?searchtype=Title&genre=&franchise=&certificate=&award=&language=&fanedittype=&searchterm={query}`

**Append to `MoviesRemasteredMetadataProvider.cs`:**
```csharp
    private static readonly System.Text.RegularExpressions.Regex _trailingYear =
        new(@"\s*\(\d{4}\)\s*$");
    private static readonly System.Text.RegularExpressions.Regex _punctuation =
        new(@"[^a-z0-9\s]");

    public async Task<IReadOnlyList<ScoredCandidate>> SearchAsync(
        MediaSearchContext context, CancellationToken ct = default)
    {
        EnsureConfigured();

        var titles = new List<string> { context.Name };
        if (context.AltTitles is { Count: > 0 })
            titles.AddRange(context.AltTitles);
        titles = titles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var seen       = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<ScoredCandidate>();

        foreach (var title in titles)
        {
            if (candidates.Count >= 10) break;

            await _limiter!.ThrottleAsync(ct);
            var query = Uri.EscapeDataString(_trailingYear.Replace(title, ""));
            var url   = $"{BaseUrl}/searchresults.php?searchtype=Title&genre=&franchise=&certificate=&award=&language=&fanedittype=&searchterm={query}";
            var resp  = await _http!.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) continue;

            var html    = await resp.Content.ReadAsStringAsync(ct);
            var results = _scraper!.ParseSearchResults(html);

            foreach (var r in results)
            {
                if (!seen.Add(r.Url)) continue;
                var (score, reason) = ScoreSearchResult(context, r);
                if (score >= ScoreThreshold)
                    candidates.Add(new ScoredCandidate(
                        Metadata: new MediaMetadata
                        {
                            Title      = r.Title,
                            Overview   = r.Synopsis,
                            Year       = r.Year,
                            RuntimeMinutes = r.RuntimeMinutes,
                            Rating     = r.Rating,
                            ExternalId = UrlToExternalId(r.Url),
                        },
                        Score: score,
                        ScoreReason: reason));
            }
        }

        return candidates.OrderByDescending(c => c.Score).Take(10).ToList();
    }

    private static (int Score, string Reason) ScoreSearchResult(MediaSearchContext ctx, MoviesRemasteredSearchResult r)
    {
        var score  = 0;
        var reasons = new List<string>();

        var norm  = Normalise(r.Title);
        var query = Normalise(_trailingYear.Replace(ctx.Name, ""));

        if (norm == query) { score += 40; reasons.Add("exact title match"); }
        else if (LevenshteinRatio(norm, query) <= 0.2) { score += 20; reasons.Add("fuzzy title match"); }

        if (ctx.Year.HasValue && r.Year.HasValue)
        {
            var diff = Math.Abs(ctx.Year.Value - r.Year.Value);
            if (diff == 0)      { score += 20; reasons.Add("year exact match"); }
            else if (diff == 1) { score += 10; reasons.Add("year within 1"); }
            else                { score -= 10; reasons.Add("year mismatch"); }
        }

        if (ctx.ParentName is not null &&
            (r.OriginalTitle?.Contains(ctx.ParentName, StringComparison.OrdinalIgnoreCase) ?? false))
        {
            score += 10; reasons.Add("source title match");
        }

        return (score, string.Join("; ", reasons));
    }

    private static string Normalise(string s)
    {
        s = s.ToLowerInvariant();
        s = _punctuation.Replace(s, " ");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ");
        return s.Trim();
    }

    private static double LevenshteinRatio(string a, string b)
    {
        if (a == b) return 0;
        var maxLen = Math.Max(a.Length, b.Length);
        if (maxLen == 0) return 0;
        return (double)LevenshteinDistance(a, b) / maxLen;
    }

    private static int LevenshteinDistance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        for (var j = 1; j <= b.Length; j++)
        {
            var cost = a[i - 1] == b[j - 1] ? 0 : 1;
            d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
        }
        return d[a.Length, b.Length];
    }

    private static string UrlToExternalId(string url)
    {
        var m = System.Text.RegularExpressions.Regex.Match(url, @"movieinfo\.php\?id=(\d+)");
        return m.Success ? $"mrdb:{m.Groups[1].Value}" : url;
    }
```

**Note on testing `SearchAsync` itself:** unlike the pure-parsing methods above, this
method makes live HTTP calls — following FanEdit's precedent, don't unit-test the HTTP
plumbing directly; test `ScoreSearchResult` as an isolated static-ish method instead (it's
private, so either make it `internal` for test visibility via `InternalsVisibleTo`, already
configured in the `.csproj`, or test it indirectly through a small seam). Add:

```csharp
// tests/MoviesRemasteredMetadataProviderTests.cs (append)
public class MoviesRemasteredScoreSearchResultTests
{
    // If ScoreSearchResult is made internal (recommended — matches the InternalsVisibleTo
    // already set up for the assembly), test it directly:
    [Fact]
    public void ScoreSearchResult_ExactTitleAndYear_ScoresHigh()
    {
        var ctx = new Chronicle.Plugins.Models.MediaSearchContext("Snow: Part I", Year: 2026);
        var r = new Chronicle.Plugin.MoviesRemastered.Models.MoviesRemasteredSearchResult
        {
            Title = "Snow: Part I", Year = 2026, Url = "https://www.moviesremastered.com/movieinfo.php?id=12179",
        };

        // Access via reflection or make ScoreSearchResult internal — see note above.
    }
}
```

(Mark `ScoreSearchResult` `internal static` rather than `private static` so this test can
call it directly without reflection — update the method signature in the snippet above
accordingly before implementing.)

Run, verify pass, commit.

---

### Task 8: `GetByIdAsync`, `GetImageAsync`, `HealthCheckAsync`, `MapToMetadata`

**Append to `MoviesRemasteredMetadataProvider.cs`:**
```csharp
    public async Task<MediaMetadata> GetByIdAsync(string externalId, CancellationToken ct = default)
    {
        EnsureConfigured();

        var url = ResolveUrl(externalId);
        await _limiter!.ThrottleAsync(ct);
        var resp = await _http!.GetAsync(url, ct);

        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new KeyNotFoundException($"No MRDb entry found at {url}");

        resp.EnsureSuccessStatusCode();
        var html  = await resp.Content.ReadAsStringAsync(ct);
        var entry = _scraper!.ParseDetailPage(html, url);

        return MapToMetadata(entry, url);
    }

    public async Task<byte[]> GetImageAsync(string url, CancellationToken ct = default)
    {
        EnsureConfigured();
        await _limiter!.ThrottleAsync(ct);
        var resp = await _http!.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        EnsureConfigured();
        try
        {
            var resp = await _http!.GetAsync($"{BaseUrl}/hub.php", ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    private static string ResolveUrl(string externalId)
    {
        if (externalId.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            // SSRF guard — only fetch moviesremastered.com URLs, same precedent as FanEdit's
            // host allowlist check in ResolveUrl.
            if (!Uri.TryCreate(externalId, UriKind.Absolute, out var uri) ||
                (!uri.Host.Equals("www.moviesremastered.com", StringComparison.OrdinalIgnoreCase) &&
                 !uri.Host.Equals("moviesremastered.com", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"URL must be a moviesremastered.com address: '{externalId}'");
            return externalId;
        }
        if (externalId.StartsWith("mrdb:", StringComparison.OrdinalIgnoreCase))
        {
            var id = externalId["mrdb:".Length..];
            return $"{BaseUrl}/movieinfo.php?id={id}";
        }
        // Bare integer
        return $"{BaseUrl}/movieinfo.php?id={externalId}";
    }

    private static MediaMetadata MapToMetadata(MoviesRemasteredEntry entry, string url)
    {
        var extData = new Dictionary<string, object?>
        {
            ["originalTitle"]         = entry.OriginalTitle,
            ["originalReleaseDate"]   = entry.OriginalReleaseDate,
            ["originalRuntimeMinutes"] = entry.OriginalRuntimeMinutes,
            ["faneditorUsername"]     = entry.FaneditorUsername,
            ["faneditorProfileUrl"]   = entry.FaneditorProfileUrl,
            ["fanEditType"]           = entry.FanEditType,
            ["franchise"]             = entry.Franchise,
            ["techSpecs"]             = entry.TechSpecs is null ? null : new
            {
                source     = entry.TechSpecs.Source,
                resolution = entry.TechSpecs.Resolution,
                soundMix   = entry.TechSpecs.SoundMix,
            },
            ["certificate"]     = entry.Certificate,
            ["language"]        = entry.Language,
            ["subtitles"]       = entry.Subtitles,
            ["timeCut"]         = entry.TimeCut,
            ["timeAdded"]       = entry.TimeAdded,
            ["intentions"]      = entry.Intentions,
            ["changeList"]      = entry.ChangeList,
            ["views"]           = entry.Views,
            ["reviewCount"]     = entry.ReviewCount,
            ["favoriteCount"]   = entry.FavoriteCount,
            ["mrdbId"]          = entry.MrdbId,
            ["mrdbUrl"]         = url,
            ["releaseDate"]     = entry.ReleaseDate,
        };

        return new MediaMetadata
        {
            Title          = entry.Title,
            Overview       = entry.Overview,
            Year           = entry.Year,
            RuntimeMinutes = entry.RuntimeMinutes,
            PosterUrl      = entry.PosterUrl,
            Genres         = entry.Genres,
            Rating         = entry.Rating,
            Tags           = entry.Tags,
            ExternalId     = UrlToExternalId(url),
            ExtendedData   = JsonSerializer.SerializeToElement(extData),
        };
    }
```

**Append tests** for `ResolveUrl`'s SSRF guard (make `ResolveUrl` `internal static` like
`ScoreSearchResult` above, for direct testability):
```csharp
public class MoviesRemasteredResolveUrlTests
{
    [Fact]
    public void ResolveUrl_RejectsNonMrdbHost()
    {
        var act = () => MoviesRemasteredMetadataProvider.ResolveUrl("https://evil.example.com/x");
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("mrdb:12179", "https://www.moviesremastered.com/movieinfo.php?id=12179")]
    [InlineData("12179", "https://www.moviesremastered.com/movieinfo.php?id=12179")]
    public void ResolveUrl_HandlesAllInputFormats(string input, string expected)
    {
        MoviesRemasteredMetadataProvider.ResolveUrl(input).Should().Be(expected);
    }
}
```

Run full test suite, verify pass, commit.

---

### Task 9: Finalize `manifest.json` branding + `README.md`

**Step 1:** Open `https://www.moviesremastered.com/users/images/logonew.png` (the actual
site logo referenced in the page nav) and pick a light/dark accent pair that reads well
against it — replace the Task 1 placeholder `brandColorLight`/`brandColorDark` if the real
logo suggests something better than the red-family guess.

**Step 2 — `README.md`:**
```markdown
# Chronicle.Plugin.MoviesRemastered

Metadata provider for [Movies Remastered](https://www.moviesremastered.com/) (MRDb), a
community fan-edit database. Supports the `fanedits` media type — the same type
Chronicle.Plugin.FanEdit uses. Install both to get two independent metadata sources; use
Settings → Metadata Assignment in Chronicle to set per-field priority between them.

## Settings

| Setting | Required | Default | Notes |
|---|---|---|---|
| Request Delay (ms) | No | 1000 | Floor of 1000ms enforced in code |
| User-Agent String | No | Chrome UA | Override HTTP User-Agent |

No account or credentials needed — MRDb search and detail pages are public.

## Fix Match

Enter a moviesremastered.com URL (`https://www.moviesremastered.com/movieinfo.php?id=12179`)
or a bare MRDb numeric ID.
```

**Step 3 — commit:**
```bash
git add manifest.json README.md
git commit -m "docs: finalize MRDb plugin branding and README"
```

---

### Task 10: Full build and test pass

```bash
cd W:/Scripts/Chronicle.Plugin.MoviesRemastered
dotnet build
dotnet test tests/ --verbosity normal
```

Expected: clean build, all tests green. No changes to `W:\Scripts\Chronicle` are needed or
expected — confirm `git status` there is clean before considering this plan complete.
