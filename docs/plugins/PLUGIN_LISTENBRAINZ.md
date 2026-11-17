# ListenBrainz plugin

**Version:** 1.0.0 (built 2026-11-12, not yet released)  ·  **Repository:** `Chronicle.Plugin.ListenBrainz`  ·  **Media types:** music

**What it needs from the owner:** Nothing for metadata. Your ListenBrainz **username** to import your history (your listens must be public, the default). A user token is optional and not needed for anything the plugin does today.

**The source's terms and limits:** ListenBrainz (MetaBrainz Foundation). documented limit one call per second per client (the live window measured 30; the service reports it in `X-RateLimit-*` headers and the plugin follows both). A `User-Agent` with name, version and contact URL is required and sent. The pages read for this record state no data licence or commercial-use terms: confirm them yourself before any commercial use. (https://listenbrainz.org)

**Design and run record:** [plans/2026-11-12-listenbrainz-plugin-design.md](../plans/2026-11-12-listenbrainz-plugin-design.md)

The plugin's own README follows; the repository is the source of truth.

---

# Chronicle.Plugin.ListenBrainz

[ListenBrainz](https://listenbrainz.org/) plugin for Chronicle.

- **Imports your listening history** (`GET /user/{name}/listens`, newest first, 1000 per page until a short page).
- **Adds ListenBrainz data to music items that already carry a MusicBrainz id:** listen and listener counts, tags and genres,
  credits, links, ISRCs, release years, cover art.

It works on its own: no key, no account, and no other plugin required.

Ids, each `listenbrainz:` + one kind + one uuid (never two ids in one): `artist`, `releasegroup`, `recording`, `track` (a MusicBrainz track,
not a recording) and `msid` (ListenBrainz's own id for a listen it could not map to MusicBrainz). See the README for what each means.

Not done on purpose: lookup by name (ListenBrainz's `/metadata/lookup` needs a token and is unmeasured), artist pictures
(ListenBrainz has none).

Note for whoever reads the older version of this page: its "50 requests a second" and "token required" were wrong. Measured on
the live API on 2026-11-12, listens, MusicBrainz-sourced metadata and popularity counts are public, and the documented limit is one call per second (30 per window measured).
