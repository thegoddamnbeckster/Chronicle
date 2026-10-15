# Chronicle.Plugin.TheAudioDB

**Plugin ID:** `chronicle.plugin.theaudiodb`  **Version:** 1.0.0  **Media type:** `music` (Artist / Album / Track)
**Auth:** API key; the free key is `123` (the key `2` shown in older examples no longer works)  **API:** TheAudioDB v1, `https://www.theaudiodb.com/api/v1/json/{key}/`

Implemented. The design, the measurements behind it and the decisions are in [docs/plans/2026-10-14-theaudiodb-plugin-design.md](../plans/2026-10-14-theaudiodb-plugin-design.md); the user-facing description is the plugin's own README in its repository.

Summary: artist biographies and artwork (thumb, logo, banner, clear art, cut-out, fan art), album descriptions and artwork (CD art, 3D cases), scores, genres, moods and styles, and track details. Works with the free key (30 requests a minute; one result per search), needs no other plugin, enrich-only, priority 40. Add it to the Music lists in Metadata Assignment for its data to take priority.
