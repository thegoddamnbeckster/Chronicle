# OMDb plugin: design

Status: **implemented and released as v1.0.0 (2026-10-13).** Repo: `Chronicle.Plugin.OMDb`. Plugin id `chronicle.plugin.omdb`, source name `omdb`.

## 1. Purpose and measured value

A lowest-priority source for movies, shows and episodes, looked up **by IMDb id**. It needs a free key (omdbapi.com/apikey.aspx, 1,000 requests a day; content CC BY-NC 4.0, personal use with attribution).

Measured on this library (2026-10-13), the gaps OMDb could fill:

* Movies: nothing. The 672 movies with no rating or runtime are all TMDB collection containers, not films.
* TV episodes: 2,349 have no overview (1,427 hold an IMDb id), 3,480 no picture (2,389 with an id), 5,335 no rating (2,139 with an id).
* A sample of 25 real gap episodes per gap, looked up by their IMDb id: found 68 to 84%, but a plot for only 3 of 21 and 8 of 17 (the gaps are daily and reality shows IMDb has no plot for), a picture for 7 of 21 and 8 of 17, and **no rating at all** (IMDb has none either). Expected yield: about 200 overviews and about 900 episode pictures. Control episodes that already had data: 23 of 24 had a plot, 24 of 24 a picture.
* Extended-data extras (Rotten Tomatoes, Metacritic, awards, box office, age rating) for about 3,400 movies and shows with IMDb ids.

A modest win; TheTVDB remains the real answer for episode text.

## 2. What OMDb gives us (verified against the live service)

`GET https://www.omdbapi.com/?apikey=KEY&i=tt...&plot=full` returns one title: Title, Year, Rated, Released, Runtime, Genre, Director, Writer, Actors, Plot, Language, Country, Awards, Poster, Ratings (IMDb, Rotten Tomatoes, Metacritic), Metascore, imdbRating, imdbVotes, imdbID, Type (movie, series, episode), BoxOffice, totalSeasons (series), Season/Episode/seriesID (episode). Every field is text, "N/A" where there is nothing. `t=`/`y=`/`type=` finds the best single match by title; `s=` lists ten. There is no season record, no person record, and the poster API is patrons-only.

Findings that shape the design:

1. **Errors arrive as HTTP 200** ("Incorrect IMDb ID.", "Movie not found!") or 401 ("Invalid API key!", "Request limit reached!"). The body is always read.
2. **"Error getting data."** is returned for some titles IMDb has; repeating the request gets the same answer (checked), so it is a normal not-found, not a retry.
3. **IMDb names unnamed episodes "Episode #9.54".** Offering that as a title would overwrite the library's own.
4. The `t=` search is fuzzy and can return an unrelated title.
5. A new key must be activated from the emailed link; until then it answers "Invalid API key!".

## 3. Decisions

* **Exact lookups by IMDb id only, by default.** The free quota is small and the library's other plugins already supply ids. Searching titles without an id is a setting (off). Episodes are never searched by name.
* **The record's type must fit the item** (movie / series / episode); a show's id on an episode is refused.
* **Seasons are declared with no fields** (`LevelFields[1] = []`), so the host drops those rows instead of queueing them; **episodes do not offer "title"**.
* **Priority 40**, below TMDB (10), IMDb and Last.fm (20).
* **The daily limit is a 429**, thrown not waited out: the item stays Pending and the next run continues. A first pass takes days.
* **The key** is a password setting, sent only as OMDb requires, never logged or placed in an error message (tested). A rejected key is a `PluginAuthException` that names the fix.
* **Rotten Tomatoes and Metacritic** go to extended data: Chronicle has one rating field and it holds IMDb's.
* ExternalId is the bare IMDb id under source `omdb`; the host's `imdb:` seeds are read too.

## 4. Tests

182 unit tests on recorded real responses (movie, series, episode, a sparse episode, every error), including: every wrong-kind id refused, no request for seasons, collections and other media types, the request budget (never more than two), the key never in a message or a query shown to the tests, the daily limit as 429, and the manifest against the code. Live tests run with `OMDB_LIVE=1 OMDB_KEY=...`.
