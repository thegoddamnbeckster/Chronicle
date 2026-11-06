# File Scanner plugin

**Version:** 1.3.1  ·  **Repository:** `Chronicle.Plugin.FileScanner`  ·  **Media types:** n/a

**What it needs from the owner:** None. Reads local folders only.

**The source's terms and limits:** Local files only; nothing is sent anywhere.

The plugin's own README follows; the repository is the source of truth.

---

# Chronicle.Plugin.FileScanner

[![Latest Release](https://img.shields.io/github/v/release/thegoddamnbeckster/Chronicle.Plugin.FileScanner?label=Chronicle.Plugin.FileScanner&color=4f72c4)](https://github.com/thegoddamnbeckster/Chronicle.Plugin.FileScanner/releases/latest)

File Scanner plugin for [Chronicle](https://github.com/thegoddamnbeckster/Chronicle).

Scans local directories for media files, extracts metadata from filenames and embedded tags, detects local poster art, and returns structured results for Chronicle to process.

---

## Supported Media Types

| Media Type | Detection Method |
|------------|-----------------|
| Movies     | Filename patterns |
| TV Shows   | `S01E01` / `1x01` episode codes, `Season N` / `Series N` directory names |

---

## Supported File Extensions

| Extension | Format |
|-----------|--------|
| `.mkv`    | Matroska |
| `.mp4`    | MPEG-4 |
| `.avi`    | AVI |
| `.m4v`    | iTunes Video |
| `.mov`    | QuickTime |
| `.wmv`    | Windows Media |
| `.mpg` / `.mpeg` | MPEG |

---

## How Scanning Works

For each video file found, the scanner follows this priority chain:

```
1. Filename heuristics  ──▶  ScannedFile (confidence 50–85)
        │
        ▼
2. Embedded tags (audio files)
        │
        ▼
3. Attach local poster art if found alongside the file
```

---

## Confidence Scores

Chronicle uses confidence scores to decide whether to auto-import a file or surface it for user review.

| Source | Score | Notes |
|--------|-------|-------|
| Filename `Title (Year).ext` | **85** | Standard Radarr/Sonarr naming |
| Filename `Title.Year.Quality.ext` | **70** | Dotted/spaced release names |
| Filename (no year found) | **50** | Title-only fallback — needs review |

The Chronicle scan threshold (default: 70) controls which files are auto-processed versus held for review.

---

## Filename Parsing

### Patterns (tried in order)

**Pattern 1 — `Title (Year)` format** (confidence 85)
```
Fight Club (1999).mkv         → "Fight Club", 1999
The Dark Knight (2008).mkv    → "The Dark Knight", 2008
Inception (2010) [1080p].mkv  → "Inception", 2010
```

**Pattern 2 — dotted / spaced with year** (confidence 70)
```
Fight.Club.1999.1080p.BluRay.mkv  → "Fight Club", 1999
The Dark Knight 2008 BluRay.mkv   → "The Dark Knight", 2008
```

**Fallback — title only** (confidence 50)
```
Fight.Club.mkv    → "Fight Club", no year
some_movie.mkv    → "some movie", no year
```

### Title Cleaning

The parser strips common release tags from titles:
- Quality: `1080p`, `720p`, `4k`, `2160p`
- Source: `BluRay`, `Blu-Ray`, `BDRip`, `WEBRip`, `WEB-DL`, `HDTV`, `DVDRip`
- Codec: `x264`, `x265`, `HEVC`, `XviD`
- Audio: `AAC`, `AC3`, `DTS`

Dots and underscores are replaced with spaces only when no spaces are already present (preserves titles like `Mr. Robot`).

---

## TV Detection

A file is classified as TV (`tv` media type hint) if any of the following are true:

- Filename contains an episode code: `S01E01`, `s01e01`, `1x01`
- Parent directory name contains `Season` or `Series`

Otherwise it defaults to `movies`.

---

## Local Poster Art

The scanner searches the media file's directory for local images using these filenames (in order):

```
poster.jpg   poster.png
folder.jpg   folder.png
cover.jpg    cover.png
fanart.jpg   fanart.png
thumb.jpg    thumb.png
```

The first match is attached as `LocalPosterPath` on the `ScannedFile` result. Chronicle will display this image in scan results and optionally use it as the media item's poster.

---

## Configuration

This plugin has no required settings. All configuration (confidence threshold, scan path, recursive flag) is managed by Chronicle's scan request, not the plugin.

---

## Installation

**Automatic (recommended):** The File Scanner plugin ships with Chronicle and is automatically installed. No manual installation is required.

**Manual:**
1. Download `Chronicle.Plugin.FileScanner.zip` from [Releases](https://github.com/thegoddamnbeckster/Chronicle.Plugin.FileScanner/releases)
2. Extract to `plugins/filescanner/` inside your Chronicle content root
3. In Chronicle: **Settings → Plugins → Install** → enter path to `Chronicle.Plugin.FileScanner.dll`

---

## Development

### Prerequisites

- .NET 9.0 SDK
- Chronicle repository cloned to `../Chronicle` (for the `Chronicle.Plugins` interface library)

### Build

```powershell
dotnet build
```

### Publish (release output)

```powershell
dotnet publish -c Release -o ./publish
```

### Project Reference

This plugin references `Chronicle.Plugins` via a local path reference during development:

```xml
<ProjectReference Include="..\Chronicle\src\Chronicle.Plugins\Chronicle.Plugins.csproj"
                  Private="false"
                  ExcludeAssets="runtime" />
```

`Private="false"` ensures `Chronicle.Plugins.dll` is **not** copied to the output — the Chronicle host provides it at runtime.

---

## Repository Structure

```
Chronicle.Plugin.FileScanner/
├── Chronicle.Plugin.FileScanner.csproj
├── FileScannerPlugin.cs    # IFileScannerPlugin implementation — entry point
├── FileNameParser.cs       # Regex-based filename → title/year/confidence
├── LocalArtFinder.cs       # Poster/folder image discovery
└── manifest.json           # Plugin identity and entry type
```
