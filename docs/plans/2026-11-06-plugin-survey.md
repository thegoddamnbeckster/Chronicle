# Survey of every existing plugin against the checklist

2026-11-06. Method: a script read all 91 `W:\Scripts\Chronicle.Plugin.*` repos (manifest, project file, source, tests, git
state) and compared the 20 embedded copies in `Chronicle.API\plugins` and `plugins.json`; numbers below are counts from
that script, spot-checked by reading code where a claim matters. Static reading only: nothing was run, so "does not
redact" means "no redaction code found", not "a secret was seen leaking".

## 1. What the 91 repos are

| Group | Count | What it means |
|---|---|---|
| Real plugins (more than 150 lines of code) | 25 | The only ones that can be judged |
| Scaffolds | 66 | About 40 lines, every method `throw new NotImplementedException("TODO ...")`, one commit, no tests. All 66 have a manifest file and **35 of those are not valid JSON**. All 66 implement an **older** `SearchAsync(string)` shape, not the current `SearchAsync(MediaSearchContext)` |

`plugins.json` lists 22 plugins, none of them a scaffold.

## 2. What every real plugin already shares (so these need no new rule)

All target net9.0 with nullable enabled; all have a README and a `.gitignore`; 24 of 25 have an icon; 21 have both brand
colours; 20 have a Fix Match hint; all pass a `CancellationToken` and implement a health check; settings live in code
(`GetSettingsSchema`), never in the manifest; every repo is clean and pushed (no uncommitted or unpushed work).

## 3. Not covered by the checklist (each is now a row in section 9 of the checklist)

| # | Finding | Evidence | Why it matters |
|---|---|---|---|
| K1 | **Two sources of version.** The host shows the manifest's version; the `Version` property in code is a second, hand-typed one that has drifted in 18 of 24 real plugins (Hardcover: manifest 1.6.0, metadata class 1.4.0, import class 1.0.0, the two halves disagree) | survey | Logs and diagnostics print the wrong version; the checklist asks for "assembly version equals manifest" but nothing enforces it and 18 break it |
| K2 | **Contract compatibility.** A plugin built against a newer plugin contract fails to load on an older host (a missing constructor or method). Nothing tracks which host contract a plugin needs: the real plugins say `min_chronicle_version` 0.1.0, 0.6.0 or 0.7.0, 33 scaffolds say 1.0.0 (a version the host, at 0.9.0, has not reached), and the number does not move when the contract does | hit today: Steam 1.1.1 needs the 2026-11-06 host | A hot deploy can take a plugin down; nothing says "update the host first" |
| K3 | **Identity and catalogue consistency.** Hardcover's `plugin_id` is `hardcover`, every other is `chronicle.plugin.<name>`; Goodreads, Letterboxd and Kodi.NFO are real but not in `plugins.json`; five real plugins (Goodreads, Kodi.NFO, Letterboxd, TheTVDB, Trakt) have no embedded fallback copy; MoviesRemastered's newest tag is behind its manifest and Kodi.NFO's ahead; Letterboxd has no tag at all | survey | Install, update and "latest version" logic depends on these matching |
| K4 | **No minimum test floor.** 71 of 91 repos have no tests; of the 25 real, TheTVDB, TVMaze, Goodreads, Letterboxd and Themes.Default have none, and TVMaze and TheTVDB are built in. Only 6 have recorded live fixtures | survey | The checklist asks for fixtures and tests but names no baseline that every plugin must meet |
| K5 | **No automation, no licence.** 0 of 91 have a CI workflow; 88 have no LICENSE; releases are zipped by hand | survey | Nothing builds or tests a plugin when it is pushed; a plugin's terms of use are unstated |
| K6 | **Scaffolds look like plugins.** 66 placeholder repos carry the old interface, so a new plugin started by copying one starts broken, and nothing marks them as unbuilt | survey | Wasted starts; confusion about what exists |
| K7 | **Secrets in URLs and redaction.** API keys or tokens are put in the query string in TMDB (14 places), Steam (9, redacted), Simkl (5), OMDb (2), Fanart.tv (1), Last.fm (1); redaction code exists only in IGDB, Steam and TheAudioDB | survey (static) | C1 says redact, but there is no per-plugin evidence; the other plugins have not been shown to keep a key out of logs and error text |
| K8 | **Image fetching.** The browser loads every image **straight from the address in the metadata**, and the host never calls `GetImageAsync` (no caller exists), so image rules belong to the address, not to a plugin method. In up to 16 of 24 real plugins `GetImageAsync` has no host check (IMDb and Fanart.tv simply refuse, which is the safe answer; the rest fetch any address they are given); Hardcover's client carries its bearer token on that same client | read code, grep for callers | Harmless today only because nothing calls it; the checklist's M2 ("host allowlist") described the unused path |
| K9 | **Outgoing identity.** 20 of 91 mention a User-Agent; Steam (mine) sends none; MusicBrainz, among others, asks for a descriptive one | survey | Some services block or throttle an anonymous client |
| K10 | **HTTP hygiene.** 11 real plugins set no client timeout (the default is 100 s, the host gives a call 25 s and a search 4 s); 10 construct `new HttpClient` per use, 3 share a static one | survey | Slow calls tie up connections after the host has given up; per-use clients exhaust sockets |
| K11 | **Rate-limit handling is uneven.** 429 handled in 18 of 25, `Retry-After` in 17; MusicBrainz (which limits to one request a second) and Last.fm have no 429 handling | survey | One plugin hammering a service can get the owner's address blocked |
| K12 | **Silent failures.** Empty `catch {}` blocks: IMDb 11, TMDB 4, Simkl 2, IGDB 1 | survey | H3 covers an import failing visibly, but not swallowed errors elsewhere |
| K13 | **Docs and licences of third-party data.** 9 real plugins have no `docs/plugins/PLUGIN_*.md` page (MusicBrainz, TMDB, FanartTV, MoviesRemastered, AniList, FileScanner, Goodreads, Letterboxd, Kodi.NFO); nothing records each source's terms (attribution, caching limits, non-commercial) | survey | The owner cannot see what a source allows |

## 4. What the survey did NOT check

Anything at runtime: whether a plugin leaks a secret, how it behaves on a 429, whether its Metadata Assignment fields
match what it fills (P2), its alias coverage (Q9), its settings text (X2), or how it looks on screen. Those need the
verification pass per plugin, which this survey cannot replace.

## 5. Corrections to my own earlier statements

- The Steam search gap I reported last turn was mis-stated. I said "Civilization VI" would not find "Sid Meier's
  Civilization VI"; it does, because local search matches anywhere inside a name. The real gaps were symbols inside a
  title, numerals written the other way, "&" versus "and", and the fact that the host recorded alternate names only for
  authors, artists and shows, never for games (so IGDB's would not have helped either). All four are fixed.
- Checklist M2 described a plugin-side image allowlist; see K8.
