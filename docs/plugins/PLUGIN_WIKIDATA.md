# Chronicle.Plugin.Wikidata — Design Document

**Plugin ID:** `chronicle.plugin.wikidata`
**Version:** 1.0.0
**Media Types:** Music (`music`), Albums (`album`), Artists (`artist`)
**Auth:** None (fully public SPARQL endpoint)
**API:** Wikidata SPARQL — `https://query.wikidata.org/sparql`

---

## Purpose

[Wikidata](https://www.wikidata.org/) is the free, open knowledge base
maintained by the Wikimedia Foundation. It contains structured data about
virtually every notable musical artist, album, and track — including
authoritative identifiers that cross-reference ISNI, ISRC, MusicBrainz,
Discogs, Spotify, and dozens of other services. This plugin uses the Wikidata
SPARQL endpoint to query music entities and harvest cross-reference IDs.

---

## Supported Media Types

| Media Type | Priority | Notes |
|-----------|---------|-------|
| `music` | 6 | Track and release cross-reference IDs |
| `album` | 5 | Album metadata and external IDs |
| `artist` | 5 | Artist biography and external IDs |

---

## API Overview

All requests to: `GET https://query.wikidata.org/sparql?query={sparql}&format=json`

**Example — Artist lookup by name:**
```sparql
SELECT ?item ?itemLabel ?mbid ?discogsId ?spotifyId ?description WHERE {
  ?item wdt:P31 wd:Q5;         # instance of: human
        wdt:P106 wd:Q177220;   # occupation: singer
        rdfs:label "{name}"@en .
  OPTIONAL { ?item wdt:P434 ?mbid. }        # MusicBrainz artist ID
  OPTIONAL { ?item wdt:P1953 ?discogsId. }  # Discogs artist ID
  OPTIONAL { ?item wdt:P1902 ?spotifyId. }  # Spotify artist ID
  SERVICE wikibase:label { bd:serviceParam wikibase:language "en". }
}
LIMIT 5
```

**Example — Album lookup:**
```sparql
SELECT ?item ?itemLabel ?mbAlbumId ?year WHERE {
  ?item wdt:P31 wd:Q482994;    # instance of: album
        rdfs:label "{title}"@en .
  OPTIONAL { ?item wdt:P435 ?mbAlbumId. }   # MusicBrainz release group ID
  OPTIONAL { ?item wdt:P577 ?year. }         # publication date
  SERVICE wikibase:label { bd:serviceParam wikibase:language "en". }
}
LIMIT 5
```

---

## Settings Schema

| Key | Label | Type | Required | Notes |
|-----|-------|------|----------|-------|
| `language` | Label Language | Dropdown | No | Default: `en` |
| `include_cross_refs` | Harvest External IDs | Boolean | No | Default: true |
| `timeout_seconds` | SPARQL Timeout | Number | No | Default: 30 |

---

## Fields Populated

```
title, overview (description), year, genres, cast (artists), poster_url,
wikidata_qid,
metadata_json: { musicbrainz_id, discogs_id, spotify_id, isni,
                 viaf_id, isrc, wikidata_url, wikipedia_url }
```

---

## Rate Limits

- Wikidata SPARQL: 60 req/min (soft limit), 300 req/5 min (hard limit)
- Requests taking > 60 s are killed — keep SPARQL queries simple
- Add `User-Agent` header: `Chronicle/{version} (contact@example.com)`
- Cache SPARQL responses for 7 days

---

## Implementation Notes

- Wikidata's primary value for music is as a **cross-reference hub**: use it
  to look up a known MusicBrainz ID and get back Discogs, Spotify, ISNI, etc.
- Key Wikidata music properties (P-numbers):
  - `P434` MusicBrainz artist ID, `P435` release group ID, `P436` release ID
  - `P1953` Discogs artist ID, `P1954` Discogs release ID
  - `P1902` Spotify artist ID, `P2205` Spotify album ID
  - `P213` ISNI, `P214` VIAF, `P1284` Musicbrainz label ID
- QID (e.g. `Q5656`) is the Wikidata entity ID — store as `wikidata_qid`
  in `media_external_ids` with source `wikidata`
- Use the Wikidata REST API (`/w/rest.php/wikibase/v0/entities/items/{QID}`)
  for direct item lookups once the QID is known — faster than SPARQL

---

## Scaffold Location

```
Chronicle.Plugin.Wikidata/
├── Chronicle.Plugin.Wikidata.csproj
├── README.md
├── manifest.json
├── WikidataPlugin.cs
└── Models/
    └── WikidataMusicEntity.cs
```
