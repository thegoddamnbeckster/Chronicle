# Chronicle.Plugin.Arr: design, terms and run record

**Plugin ID:** `chronicle.plugin.arr`
**Repository:** `thegoddamnbeckster/Chronicle.Plugin.Arr`
**Interface:** `IMediaLocator` (new in Chronicle 0.20.75, extended for music and books in 0.20.76; see "Contract compatibility")
**Applications:** Radarr, Sonarr, Lidarr, Readarr (each optional)
**Auth:** an address and an API key per application, both entered by the owner

## Purpose

Chronicle identifies a file by its name, and names collide: "The Beast (2026)" was shown as the unrelated "Heart of the Beast
(2026)" (2026-11-17). Radarr already knows exactly which film each file is, because it downloaded and named it. This plugin lets
Chronicle ask Radarr (and the other three applications) first, and use the id they report instead of guessing.

It is **reference data only and read-only**. Chronicle never writes to these applications and the plugin contains no request that
could: every call is a `GET`. It never needs to be kept in sync; it is asked when Chronicle needs an answer, and an absent,
broken or slow application changes nothing.

## What is asked, and what comes back

| Application | Asked | Call | Gives | Used when |
|---|---|---|---|---|
| Radarr | the file Kodi is scraping, else title and year | `GET /api/v3/movie` | TMDB (`movie:N`) and IMDb ids | Kodi movie scraping |
| Sonarr | title and year | `GET /api/v3/series` | TMDB (`tv:N`) and IMDb ids | Kodi TV scraping |
| Lidarr, artist | artist name | `GET /api/v1/artist` | MusicBrainz id (`artist:{mbid}`) | Chronicle enriches a music artist |
| Lidarr, album | album title within its artist | `GET /api/v1/album` | MusicBrainz release-group id (`release-group:{mbid}`) | Chronicle enriches a music album |
| Readarr, author | author name | `GET /api/v1/author` | Goodreads author id | Chronicle enriches an author |
| Readarr, book | book title within its author | `GET /api/v1/book` | the monitored edition's Goodreads id and ISBN-13 (`goodreads`, `isbn13`) | Chronicle enriches a book |

Readarr's own project is retired; it is still the best tool for the job and is supported as it is. A Readarr book's `foreignBookId`
is a Goodreads *work* id, which Open Library does not index, so the edition's `foreignEditionId` (the Goodreads *book* id) is used.
Tracks and chapters are never asked about: Chronicle derives them from their album or book.

`GET /api/v{3,1}/system/status` is the one call the health check makes. Each application's whole list is read once and kept for
five minutes, so a scan of thousands of files costs a handful of requests.

### How a match is made (the rule is equality, never similarity)

A wrong answer overrides Chronicle's own matching, while a miss costs nothing, so the rule errs towards a miss.

1. **Radarr by file:** the exact file name (any folder, either path separator) Radarr holds. Two films holding the same file name:
   the year decides, else a miss.
2. **By title:** the title, original title or an alternate title equals the searched one once case and punctuation are ignored and
   a trailing "(year)" is dropped, and the year agrees when both sides have one. Two equal titles with no year to separate them
   is a miss. "Beast" is not "The Beast", and "The Beast" is not "Heart of the Beast".

### What Chronicle does with an answer for music and books (`MetadataEnrichmentService`)

Before a provider is searched by name for an item, Chronicle asks the locators whose `SupportedMediaTypes` include the item's type
(`LocateItemAsync`, given the item's name, its parent's name, its level and its type). If the answer names an id under the same source
as the provider being run (`musicbrainz`), the provider is asked for that id directly. Every id reported is kept on the item as a hint
(`media_external_ids`), except a source the item already has and an id another item already holds, so Open Library, which reads a
book's `goodreads` id, finds it. No answer, a locator that is slow (4 seconds) or failing, or an id the provider cannot fetch all fall
through to the name search, exactly as before. Album and book matching requires the artist or author's name as well: a title alone
is never matched.

### What Chronicle does with an answer for Kodi (`ScraperController`)

0. The locator is asked even when a recorded file name already matched an item: a recorded file name can itself be wrong (live
   2026-11-18: `11 Flowers (2012).mkv` was recorded against "11/11/11"), so a recorded file name that points at an item holding a
   different id than the one reported is not used.
1. If an item already holds the reported id, it is used, even if its name differs.
2. Otherwise it matches by name using the reported title and year. A name match that carries a **different** id for the same
   source is a look-alike and is refused. A name match with no id of its own is still used (no duplicate is made).
3. Otherwise a new item is created with the reported ids already attached, so enrichment fetches exactly that film or show.

A locator is given 4 seconds per request. One that throws, times out or answers nothing is logged and skipped. The plugin's read
of an application's list is not tied to that deadline: a first read of a large library that takes longer finishes in the background
and is cached, so the next lookup answers (otherwise every lookup would be cancelled before the list arrived and the plugin would
never work).

## Settings

Eight settings: `{radarr,sonarr,lidarr,readarr}_url` (Url) and `{...}_api_key` (Password), all optional, default blank. An
application is used only when both its address and key are present and valid. Values are trimmed; an address must be `http` or
`https` with no user name or password in it; a key containing a space or line break is refused (it goes into a request header).
No redirects are followed. The settings text tells the owner where to find each value and what not to enter.

A key an application rejects (HTTP 401 or 403) throws one `PluginAuthException`, is **latched** (nothing more is sent to that
application) and the latch is cleared when the settings are saved. A 429 is retried twice honouring `Retry-After` (each wait capped
at 4 seconds), then thrown as `HttpRequestException` (TooManyRequests); a list already read is used instead when there is one.

## Terms and limits of the sources

These are applications the owner runs on their own network, not third-party services. Radarr, Sonarr, Lidarr and Readarr are
open source (GPL-3.0) and have no usage terms for reading your own instance. The plugin reads only; it adds no load beyond one
list read per application per five minutes. A private address is allowed here on purpose (it is an address the owner typed), unlike
an address a metadata provider supplied, which Chronicle refuses to connect to. Nothing is cached to disk.

## Contract compatibility (K2)

Built against the `IMediaLocator` contract as extended in Chronicle **0.20.76** (the interface itself arrived in 0.20.75; 0.20.76 added
`SupportedMediaTypes`, `LocateItemAsync` and the `MediaTypeName`, `ParentName` and `HierarchyLevel` fields of the query, all optional
members with defaults); the manifest says `min_chronicle_version` 0.20.76. A host older than 0.20.75 has no `IMediaLocator` and will
fail to load the plugin's assembly (the type is missing), and one older than 0.20.76 loads it but never asks about music or books; this is stated
rather than tested, because no release of the host without the interface can be loaded here. The plugin installs and works for a
user who has installed nothing else.

## Checklist run record (`docs/PLUGIN_NEW_TYPE_CHECKLIST.md`)

Status words as the checklist defines them. This plugin supplies no metadata, images, people or credits and adds no media type,
so the sections for those are N/A with that reason. **Nothing below is "Verified in app" yet:** the plugin is built, tested and
released, and has not yet been shown to the real Radarr or Sonarr.

| Rows | Status | Where / evidence |
|---|---|---|
| S1 (endpoints called for real, fixtures recorded) | **Owner action** | The response shapes in `LocatorTests.cs` are written from the applications' documented v3 and v1 APIs, not recorded. Once the owner enters a key, record `GET /api/v3/movie` (trimmed) as a fixture and re-run. Until then the shapes are unproven against a real instance. |
| S2 to S5 | Designed | Not an unofficial source; each application's own API; no account risk beyond the owner's own key, which is only ever read. |
| D (every field inventoried), E (queue), Q (typed searches), P1 to P10, P12 to P15, M (images), T (media types) | N/A | No metadata, images, people, credits, search, queue or media type: the plugin only answers "which id is this?". |
| P11 (Plugins page: card, text, Test) | **Not verified in app** | Needs the catalog entry on `main`, a restart, and a signed-in pane. |
| B1, B2, B4 | Built | `manifest.json`: the Servarr icon (`https://wiki.servarr.com/favicon.ico`, which answers over HTTPS, checked 2026-11-18) and the Servarr orange: `brandColorDark` #d66b1d is the logo's own orange (3.6:1 or better on every shipped dark card); `brandColorLight` #c2570c is a darker shade of the same hue, because the exact orange is only 2.9:1 on the light cards (3.7:1). Contrast computed against `Themes.Default`. The icon on light and dark cards at 16 to 20 px is not yet seen on screen. |
| B5 | N/A | The plugin takes no part in Fix Match; the manifest says so. |
| B6 | N/A | No background tasks. |
| X1 to X3, X10 | Built | `ArrSettings.cs`; `EverySettingIsReadByFrom_HasHelpText_AndEverySecretIsAPassword`. |
| X4, X7 | Built | `ArrSettings.From` validates; `Configure_IsIdempotent`, latch cleared by `AWrongKey_HealthThrowsAuthExceptionNamingTheApp_AndSavingSettingsClearsTheLatch`. |
| X6, X8 | **Not verified in app** | Round trip of a saved password and the Test button need the running app. |
| C1, C4 | Built | No part of a key reaches an exception: `TheKeyNeverAppearsInAnExceptionOrAMessage_WhateverGoesWrong`. A rejected key costs one call: `AWrongKey_IsAMiss_NotAnException_AndIsLatched_SoNothingMoreIsSent`. |
| C2, C7 | Built | Each setting says where to find the value, an example and what not to enter; the rejected-key message names the application and what to do. To be read aloud before release (G2). |
| K1 | Built | `Version` read from the assembly; the manifest is compared to it in `ManifestAgreesWithThePlugin`. |
| K2 | Designed | See "Contract compatibility". |
| K3 | Built | `plugins.json` entry, `RunTestEnvironment.ps1` entry, manifest 1.0.0, project 1.0.0 and tag `v1.0.0` agree (checked by `scripts/audit-plugins.py`); the embedded fallback is the repository's `plugins.json`. |
| K4 | Built | 63 tests (plus 7 in Chronicle that drive the real enrichment path with a stand-in locator): matching, each application, albums and books, settings schema, redaction, rate limit, latch, cache, user-agent, manifest. |
| K5 | Built | `.github/workflows/build.yml` (build and test on push) ran green on GitHub, and `release.yml` built and published `chronicle.plugin.arr-v1.0.0.zip` (DLL, deps.json, manifest.json) from the tag, 2026-11-18; LICENSE (MIT); terms above. |
| K7 to K11 | Built | See the tests named above; no empty `catch`; one shared `HttpClient`, 20 s timeout. |
| K12 | Built | This page and the catalogue line. |
| K13 | N/A | Reads one list per application in well under 25 seconds. |
| K14 | Built | `docs/plugins/PLUGIN_COMPLIANCE.md` regenerated 2026-11-18 after the tag: no static `XX` for any plugin. |
| PK | Built | `plugins.json`, `RunTestEnvironment.ps1` and `Deploy-Plugin.ps1` entries; the hosted `plugins.json` on `main` carries it (Chronicle v0.20.75); GitHub release `v1.0.0` with the zip. |
| G3 (verification pass) | **Not done** | Owner enters each application's address and key; then the Plugins page, Test button and a real scrape of a Radarr-managed file are checked on screen. |
