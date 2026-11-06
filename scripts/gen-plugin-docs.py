"""Writes docs/plugins/PLUGIN_<NAME>.md for built plugins that have no page, and rewrites the "Already Built" table of
docs/PLUGIN_CATALOGUE.md from the manifests. Run after a plugin is added or its manifest changes; then rerun audit-plugins.py."""
import json, os, re, sys

root = r"W:\Scripts"
host = os.path.join(root, "Chronicle")
docs = os.path.join(host, "docs")

# What each service asks of the owner and what its terms say the plugin must respect. Only facts the plugin or the
# service documents; anything not known here says "see the service's terms" rather than guessing.
INFO = {
    "AniList":          ("None. Public GraphQL API.", "Free API; see AniList's terms before any commercial use, and keep the request rate polite.", "https://anilist.co"),
    "Deezer":           ("None for public metadata.", "Public API; see Deezer's terms for artwork and previews.", "https://developers.deezer.com"),
    "FanEdit":          ("A free fanedit.org account (username and password).", "Reads the Fan Edit Database as the owner; do not redistribute its content.", "https://fanedit.org"),
    "FanartTV":         ("A Fanart.tv API key; an optional personal client key raises the limits.", "Artwork belongs to its uploaders; the project asks for attribution.", "https://fanart.tv"),
    "FileScanner":      ("None. Reads local folders only.", "Local files only; nothing is sent anywhere.", ""),
    "Hardcover":        ("A Hardcover API token (Hardcover.app, Settings, API).", "Reads the owner's own Hardcover account; see Hardcover's API terms.", "https://hardcover.app"),
    "IGDB":             ("A Twitch developer Client ID and Client Secret.", "Subject to the Twitch Developer Services Agreement; IGDB asks for attribution.", "https://api-docs.igdb.com"),
    "IMDb":             ("None. Uses IMDb's published non-commercial datasets.", "Personal, non-commercial use only; the datasets must not be redistributed.", "https://developer.imdb.com/non-commercial-datasets/"),
    "LastFM":           ("A Last.fm API key; scrobble import also needs the owner's username.", "Last.fm API terms; keep to its rate limit.", "https://www.last.fm/api"),
    "MoviesRemastered": ("None. Reads the public pages of moviesremastered.com.", "A scraped site: the plugin paces itself and identifies itself; be gentle.", "https://moviesremastered.com"),
    "MusicBrainz":      ("None. An optional MusicBrainz username and password are supported.", "Core data is CC0. One request per second and a descriptive User-Agent are required.", "https://musicbrainz.org/doc/MusicBrainz_API/Rate_Limiting"),
    "OMDb":             ("An OMDb API key (the free one allows 1,000 requests a day).", "OMDb's terms; the daily limit is honoured and reported.", "https://www.omdbapi.com"),
    "OpenLibrary":      ("None. An optional contact address goes into the User-Agent.", "Open data; Open Library asks heavy users to identify themselves.", "https://openlibrary.org/developers/api"),
    "Simkl":            ("A SIMKL client ID, then a sign-in from the plugin settings.", "SIMKL API terms; the plugin respects its rate limits.", "https://simkl.docs.apiary.io"),
    "Steam":            ("A Steam Web API key and the owner's Steam ID; the profile's game details must be public.", "Steam Web API terms; the store is paced at about 200 requests per 5 minutes.", "https://steamcommunity.com/dev"),
    "TMDB":             ("A TMDB API key.", "Free for non-commercial use; TMDB requires attribution and its logo where its data is shown.", "https://www.themoviedb.org/api-terms-of-use"),
    "TVMaze":           ("None. Public REST API.", "Data is CC BY-SA; the API allows 20 calls per 10 seconds.", "https://www.tvmaze.com/api"),
    "TheAudioDB":       ("A TheAudioDB API key (the free key 123 is the default; a premium key lifts the limits).", "The free key returns one record per search and is rate limited; see TheAudioDB's terms.", "https://www.theaudiodb.com/api_guide.php"),
    "TheTVDB":          ("A free TheTVDB API key from thetvdb.com/dashboard.", "See TheTVDB's terms of use.", "https://thetvdb.github.io/v4-api/"),
    "Themes.Default":   ("None. Four built-in colour themes.", "Ships with Chronicle.", ""),
    "Trakt":            ("A Trakt Client ID and Secret, then a sign-in from the plugin settings.", "Trakt API terms; its rate limits are honoured.", "https://trakt.docs.apiary.io"),
    "Wikipedia":        ("None. Public MediaWiki API.", "Text is CC BY-SA; attribute it. Chronicle ranks it last because articles vary in quality.", "https://www.mediawiki.org/wiki/API:Etiquette"),
}
SKIP = {"Goodreads", "Kodi.NFO", "Letterboxd"}   # not developed by decision (2026-11-06)


def rd(p):
    try:
        return open(p, encoding="utf8", errors="ignore").read()
    except Exception:
        return ""


rows = []
for n, (auth, terms, link) in sorted(INFO.items()):
    repo = os.path.join(root, f"Chronicle.Plugin.{n}")
    mf = json.loads(rd(os.path.join(repo, "manifest.json")) or "{}")
    types = ", ".join(mf.get("supported_media_types") or []) or "n/a"
    kind = "IImportProvider + IMetadataProvider" if "Import" in "".join(os.listdir(repo)) else "IMetadataProvider"
    rows.append((n, mf, types, kind, auth, terms, link))

    page = os.path.join(docs, "plugins", f"PLUGIN_{n.upper().replace('.', '')}.md")
    existing = os.path.exists(page) or any(
        n.lower().replace(".", "") in f.lower().replace("_", "") for f in os.listdir(os.path.join(docs, "plugins"))
        if f.lower().startswith("plugin_") and not re.search(r"(v\d|_v\d)", f.lower()))
    if existing and n not in ("Steam",):
        continue
    readme = rd(os.path.join(repo, "README.md")).strip()
    body = [f"# {mf.get('name', n)} plugin", "",
            f"**Version:** {mf.get('version', '?')}  ·  **Repository:** `Chronicle.Plugin.{n}`  ·  **Media types:** {types}", "",
            f"**What it needs from the owner:** {auth}", "",
            f"**The source's terms and limits:** {terms}" + (f" ({link})" if link else ""), "",
            "The plugin's own README follows; the repository is the source of truth.", "", "---", "", readme, ""]
    open(page, "w", encoding="utf8", newline="").write("\n".join(body))
    print("wrote", page)

# catalogue table
cat_path = os.path.join(docs, "PLUGIN_CATALOGUE.md")
cat = rd(cat_path)
table = ["| Plugin | Repository | Version | Interfaces | Media types | Needs | Source terms |", "|---|---|---|---|---|---|---|"]
for n, mf, types, kind, auth, terms, link in rows:
    table.append(f"| **{mf.get('name', n)}** | [Chronicle.Plugin.{n}](https://github.com/thegoddamnbeckster/Chronicle.Plugin.{n}) | {mf.get('version', '?')} | `{kind}` | {types} | {auth} | {terms} |")
new_block = ("## ✅ Already Built & Deployed\n\nThese plugins are implemented. Their status is audited by `scripts/audit-plugins.py` "
             "(`docs/plugins/PLUGIN_COMPLIANCE.md`). Goodreads, Kodi.NFO and Letterboxd have code but are **not developed** by decision "
             "(2026-11-06); every other repo named below the table is an unbuilt scaffold.\n\n" + "\n".join(table) + "\n\n---\n")
m = re.search(r"## ✅ Already Built & Deployed.*?\n---\n", cat, re.S)
if m:
    cat = cat[:m.start()] + new_block + cat[m.end():]
    cat = re.sub(r"\*\*Last Updated:\*\* [0-9-]+", "**Last Updated:** 2026-11-06", cat)
    open(cat_path, "w", encoding="utf8", newline="").write(cat)
    print("catalogue updated")
else:
    print("catalogue block not found", file=sys.stderr)
