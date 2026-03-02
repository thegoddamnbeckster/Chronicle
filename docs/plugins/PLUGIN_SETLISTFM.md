# Chronicle.Plugin.Setlistfm — Design Document

**Plugin ID:** `chronicle.plugin.setlistfm`
**Version:** 1.0.0
**Media Types:** Music (`music`), Artists (`artist`), Live events (`live_event`)
**Auth:** API key (free at setlist.fm/settings/api)
**API:** setlist.fm REST API v1 — `https://api.setlist.fm/rest/1.0`

---

## Purpose

[Setlist.fm](https://www.setlist.fm/) is the world's largest database of
live concert setlists, crowd-sourced from fans at gigs worldwide. This plugin
enriches Chronicle artist entries with live performance data — setlists,
tour information, venue history — and provides a unique view into an artist's
live catalogue not available from studio-catalogue-focused databases.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `artist` | 7 | Artist live performance data |
| `live_event` | 1 | Individual concert setlists |
| `music` | 8 | Track performance frequency stats |

---

## API Overview

All requests require `x-api-key: {key}` and `Accept: application/json` headers.

| Endpoint | Description |
|----------|-------------|
| `GET /search/artists?artistName={name}` | Search artists |
| `GET /artist/{mbid}` | Artist by MusicBrainz ID |
| `GET /artist/{mbid}/setlists` | Artist's setlist history (paginated) |
| `GET /setlist/{setlist_id}` | Single setlist detail |
| `GET /search/setlists?artistMbid={mbid}&year={year}` | Filter setlists |
| `GET /search/venues?name={name}` | Venue search |

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `api_key` | Setlist.fm API Key | Password | Yes | setlist.fm/settings/api |
| `max_setlists` | Max Setlists to Fetch | Number | No | Default: 10 |
| `include_song_stats` | Calculate Song Frequency | Boolean | No | Default: true |

---

## Fields Populated

```
cast (artists), metadata_json: {
  most_played_live: [{ song, count }],
  recent_setlists: [{ date, venue, city, songs: [...] }],
  total_setlists, setlistfm_mbid, tour_names
}
```

---

## Rate Limits

- 2 req/sec; 1 req/sec recommended to be safe
- Paginate at 20 setlists per page; fetch only what is needed
- Cache setlist data for 7 days (fan-submitted, changes slowly)

---

## Implementation Notes

- Setlist.fm uses MusicBrainz artist IDs (MBIDs) as its primary identifiers
  — use the MusicBrainz plugin to resolve artist names to MBIDs before
  querying setlist.fm
- Setlist.fm setlist IDs are alphanumeric strings (e.g. `6bd6ca6e`)
- The setlist model has: `eventDate`, `venue { name, city { name, country } }`,
  `artist`, `sets { set [ { song: [ { name, with } ] } ] }`
- "Song frequency" can be calculated client-side by counting occurrences
  across fetched setlists — useful for showing "most performed live" tracks
- Store the setlist.fm URL in `metadata_json.setlistfm_url`

---

## Scaffold Location

```
Chronicle.Plugin.Setlistfm/
├── Chronicle.Plugin.Setlistfm.csproj
├── README.md
├── manifest.json
├── SetlistfmPlugin.cs
└── Models/
    ├── SetlistfmArtist.cs
    └── SetlistfmSetlist.cs
```
