# Chronicle.Plugin.ExifInfo — Design Document

**Plugin ID:** `chronicle.plugin.exifinfo`
**Version:** 1.0.0
**Media Types:** Photos (`photo`), Videos (`video`), Generic media (`media`)
**Auth:** None
**API:** Local EXIF extraction via `ExifTool` (bundled or user-installed)

---

## Purpose

This plugin extracts embedded EXIF/IPTC/XMP metadata from local image and
video files using the industry-standard [ExifTool](https://exiftool.org/)
utility. It enables Chronicle to read technical metadata (camera model, GPS,
date taken, resolution, codec, duration) directly from files without any
network call.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `photo` | 1 | EXIF/IPTC/XMP from image files |
| `video` | 1 | Technical metadata from video files |

---

## API Overview

ExifTool is invoked as a subprocess and its JSON output parsed:

```bash
exiftool -json -q -fast2 "{file_path}"
```

Key ExifTool tags extracted:

| Tag | Maps to |
|-----|---------|
| `Title` / `ObjectName` | `title` |
| `Description` / `Caption-Abstract` | `overview` |
| `DateTimeOriginal` / `CreateDate` | `year` |
| `ImageWidth` × `ImageHeight` | `metadata_json.resolution` |
| `Make`, `Model` | `metadata_json.camera` |
| `GPSLatitude`, `GPSLongitude` | `metadata_json.gps` |
| `Duration` | `runtime_minutes` |
| `VideoCodec`, `AudioCodec` | `metadata_json.codecs` |
| `Copyright` | `metadata_json.copyright` |
| `Keywords` / `Subject` | `genres` (tags) |

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `exiftool_path` | ExifTool Path | FilePath | No | Auto-detected if in PATH |
| `extract_gps` | Extract GPS Coordinates | Boolean | No | Default: true |
| `extract_camera` | Extract Camera Info | Boolean | No | Default: true |
| `file_extensions` | File Extensions | TextArea | No | Default: jpg jpeg png tiff mp4 mov |

---

## Fields Populated

```
title, overview, year, runtime, poster_url (first frame for video),
metadata_json: { resolution, camera_make, camera_model,
                 gps_lat, gps_lon, codecs, copyright, iso,
                 aperture, shutter_speed, focal_length }
```

---

## Rate Limits

- No network calls — all local
- ExifTool startup overhead: ~300 ms per process; use batch mode
  (`-json file1 file2 ...`) when processing multiple files

---

## Implementation Notes

- ExifTool must be installed on the host system; bundle a copy for
  Windows deployments in `plugins/exiftool/`
- Use `Process.StartAsync` with `exiftool -json -q "{path}"` and
  parse stdout as a JSON array (ExifTool always returns an array)
- For video files, extract a thumbnail using
  `exiftool -b -ThumbnailImage "{path}"` or fall back to ffmpeg
- GPS coordinates may be in degrees-minutes-seconds format;
  convert to decimal degrees in the plugin
- This plugin implements `IMetadataProvider` but its `SearchAsync`
  takes a file path as the query, not a title string

---

## Scaffold Location

```
Chronicle.Plugin.ExifInfo/
├── Chronicle.Plugin.ExifInfo.csproj
├── README.md (this document)
├── manifest.json
├── ExifInfoPlugin.cs
└── Models/
    └── ExifData.cs
```
