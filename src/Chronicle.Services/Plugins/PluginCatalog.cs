using Chronicle.Core.Models;

namespace Chronicle.Services.Plugins;

/// <summary>
/// Chronicle's static plugin catalog -- moved here (2026-09-04) from PluginsController so
/// the scheduled update-check task (Services layer) can read the same data the catalog/
/// install API endpoints (API layer) already use, without the API project depending on
/// itself or Services depending on API (the wrong direction). The install/download HTTP
/// logic itself stays in PluginsController -- only this static reference data moved.
/// </summary>
public static class PluginCatalog
{
    public static readonly PluginCatalogEntry[] Entries =
    [
        new PluginCatalogEntry(
            PluginId:    "chronicle.plugin.tmdb",
            Name:        "TMDB",
            Description: "Fetches movie and TV metadata from The Movie Database (TMDB). Requires a free TMDB API key.",
            Author:      "Chronicle Contributors",
            IconUrl:     "https://www.themoviedb.org/favicon.ico",
            GithubRepo:  "thegoddamnbeckster/Chronicle.Plugin.TMDB",
            AssetName:   "Chronicle.Plugin.TMDB.zip",
            DllName:     "Chronicle.Plugin.TMDB.dll",
            Tags:        ["movies", "tv", "metadata"],
            Sha256:      "",     // cleared — recalculate after each plugin release
            Version:     "1.0.0"
        ),
        new PluginCatalogEntry(
            PluginId:    "chronicle.plugin.musicbrainz",
            Name:        "MusicBrainz",
            Description: "Fetches comprehensive music metadata from MusicBrainz (artist, album, track) and cover art from the Cover Art Archive. No API key required.",
            Author:      "Chronicle Contributors",
            IconUrl:     "https://musicbrainz.org/favicon.ico",
            GithubRepo:  "thegoddamnbeckster/Chronicle.Plugin.MusicBrainz",
            // Confirmed live (2026-09-04, `gh release view`): the actual latest release is
            // v1.2.0, asset "chronicle.plugin.musicbrainz-v1.2.0.zip" (lowercase, versioned --
            // this entry's old AssetName/Sha256/Version were pinned to a stale v1.0.2 build
            // that the repo has since moved past, causing "Asset ... not found in the latest
            // release" for both fresh installs and the new update-from-catalog action).
            AssetName:   "chronicle.plugin.musicbrainz-v1.2.0.zip",
            DllName:     "Chronicle.Plugin.MusicBrainz.dll",
            Tags:        ["music", "audio", "metadata"],
            Sha256:      "afe7855d272565b41c6b9400518e35cfc0c2ab1771d576dc2ff5f35eea698fb2",
            Version:     "1.2.0"
        ),
        new PluginCatalogEntry(
            PluginId:    "chronicle.plugin.filescanner",
            Name:        "File Scanner",
            Description: "Scans local directories for media files. Parses NFO sidecars and filenames to extract title, year, and media type. Supports TV hierarchy (SxxExx), audio files (MP3/FLAC/OGG/etc.), and embedded tag reading via TagLib#.",
            Author:      "Chronicle",
            IconUrl:     null,
            GithubRepo:  "thegoddamnbeckster/Chronicle.Plugin.FileScanner",
            AssetName:   "Chronicle.Plugin.FileScanner.zip",
            DllName:     "Chronicle.Plugin.FileScanner.dll",
            Tags:        ["movies", "tv", "audio", "filescanner", "local"],
            Sha256:      "30f7996b2b3edd47f57084c1c774aa87d137fabdee50ffd3e0a185c2bef730e9",
            Version:     "1.2.0"
        ),
        new PluginCatalogEntry(
            PluginId:    "chronicle.plugin.wikipedia",
            Name:        "Wikipedia",
            Description: "Broad fallback summaries, full article sections, and images from Wikipedia for any media type — including People. No API key required.",
            Author:      "Chronicle Contributors",
            IconUrl:     "https://en.wikipedia.org/static/apple-touch/wikipedia.png",
            GithubRepo:  "thegoddamnbeckster/Chronicle.Plugin.Wikipedia",
            AssetName:   "Chronicle.Plugin.Wikipedia.zip",
            DllName:     "Chronicle.Plugin.Wikipedia.dll",
            Tags:        ["movies", "tv", "music", "books", "games", "people", "metadata"],
            // Confirmed live (2026-09-04, downloaded + hashed the real asset): repo has moved
            // to v1.0.7 since this entry was last synced at v1.0.0 -- asset name unchanged,
            // only Version/Sha256 were stale.
            Sha256:      "afbcd783a5d6cdcd5f093d738a967c97ae0211e880b31eef5811838105b73a4f",
            Version:     "1.0.7"
        ),
        // The five plugins below this comment (FanEdit, Simkl, FanartTV, Themes.Default,
        // and MoviesRemastered/Trakt/Hardcover/TheTVDB/TVMaze/Kodi.NFO further below) were
        // added/completed 2026-09-02 after discovering this array was badly stale: CLAUDE.md
        // lists 12 plugins as of v0.7.0 and this catalog only had 4. All six of
        // MoviesRemastered/Trakt/Hardcover/TheTVDB/TVMaze/Kodi.NFO now have a real GitHub
        // release with a packaged zip asset attached, tagged/pushed/created directly against
        // each repo (verified with `gh release view` + a local SHA-256 recompute of the
        // uploaded asset, not guessed). Notes on drift encountered along the way:
        // MoviesRemastered picked up an uncommitted field-name fix (certificate/releaseDate ->
        // certification/released) so it shipped as v1.0.1, not v1.0.0. Trakt's last prior tag
        // (v1.1.0) no longer compiled against current Chronicle.Plugins.Models
        // (CastMember/CrewMember refactor) so it shipped fresh as v1.2.0 from HEAD. Hardcover
        // had the same drift plus a csproj/manifest version mismatch (fixed to agree at 1.2.0
        // before building); its manifest.json's plugin_id is "hardcover", not
        // "chronicle.plugin.hardcover" -- that's the authoritative id per Chronicle's
        // plugin-loading convention. TheTVDB and TVMaze both needed the same CastMember/
        // CrewMember migration (uncommitted locally, now committed and pushed) before they'd
        // compile; TVMaze's repo additionally had leftover unpackaged v1.0.0/v1.0.1 tags from
        // an earlier session, so its real first packaged release is v1.0.2. Kodi.NFO depends on
        // ISidecarFormatPlugin (src/Chronicle.Plugins/ISidecarFormatPlugin.cs), which only
        // exists on this branch -- its catalog entry works once this PR merges to main, not
        // before.
        new PluginCatalogEntry(
            PluginId:    "chronicle.plugin.moviesremastered",
            Name:        "Movies Remastered (MRDb)",
            Description: "Fetches fan edit metadata from the Movies Remastered Database (moviesremastered.com / MRDb), a community fanedit archive. No account required. Please use responsibly — a minimum 1-second delay between requests is enforced.",
            Author:      "Chronicle Contributors",
            IconUrl:     "data:image/svg+xml;base64,PHN2ZyB4bWxucz0naHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmcnIHZpZXdCb3g9JzAgMCAyNCAyNCc+PHJlY3Qgd2lkdGg9JzI0JyBoZWlnaHQ9JzI0JyByeD0nMycgZmlsbD0nI0ZGMDAwMCcvPjxwYXRoIGQ9J001IDhoMnYySDV6bTEyIDBoMnYyaC0yek01IDE0aDJ2Mkg1em0xMiAwaDJ2MmgtMnonIGZpbGw9J3doaXRlJyBvcGFjaXR5PScwLjYnLz48cGF0aCBkPSdNOCA2aDh2MTJIOHonIGZpbGw9J3doaXRlJyBvcGFjaXR5PScwLjknLz48cGF0aCBkPSdNMTAgOWg0TTEwIDEyaDRNMTAgMTVoMycgc3Ryb2tlPScjRkYwMDAwJyBzdHJva2Utd2lkdGg9JzEuMicgc3Ryb2tlLWxpbmVjYXA9J3JvdW5kJy8+PC9zdmc+",
            GithubRepo:  "thegoddamnbeckster/Chronicle.Plugin.MoviesRemastered",
            AssetName:   "chronicle.plugin.moviesremastered-v1.0.1.zip",
            DllName:     "Chronicle.Plugin.MoviesRemastered.dll",
            Tags:        ["movies", "fanedits", "metadata"],
            Sha256:      "b0e36023fc2e4ae2ebb3ee6147052518a6432e46538497322f49953e9e2a6468",
            Version:     "1.0.1"
        ),
        new PluginCatalogEntry(
            PluginId:    "chronicle.plugin.trakt",
            Name:        "Trakt",
            Description: "Import watch history, ratings, watchlist, and in-progress playback position from Trakt.tv into Chronicle. Requires a Trakt API application (Settings → Your API Apps on trakt.tv) — as of 2026, creating one requires a paid Trakt VIP membership, so a free account cannot obtain a client_id at all.",
            Author:      "Chronicle Contributors",
            IconUrl:     "https://trakt.tv/favicon.ico",
            GithubRepo:  "thegoddamnbeckster/Chronicle.Plugin.Trakt",
            AssetName:   "chronicle.plugin.trakt-v1.2.0.zip",
            DllName:     "Chronicle.Plugin.Trakt.dll",
            Tags:        ["movies", "tv", "scrobbling", "sync"],
            Sha256:      "9c37b4198c66406a6438669adeeaaf3296011fefbcd04f30492819aaf7efa4a0",
            Version:     "1.2.0"
        ),
        new PluginCatalogEntry(
            PluginId:    "hardcover",
            Name:        "Hardcover",
            Description: "Book and audiobook metadata from Hardcover.app, plus reading history import.",
            Author:      "Chronicle Contributors",
            IconUrl:     "data:image/svg+xml;base64,PHN2ZyB4bWxucz0naHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmcnIHZpZXdCb3g9JzAgMCAyNCAyNCc+PHJlY3QgeD0nMycgeT0nMycgd2lkdGg9JzE4JyBoZWlnaHQ9JzE4JyByeD0nMicgZmlsbD0nIzdjM2FlZCcvPjxwYXRoIGZpbGw9J3doaXRlJyBkPSdNNyA3aDEwdjJIN3ptMCA0aDEwdjJIN3ptMCA0aDd2Mkg3eicvPjwvc3ZnPg==",
            GithubRepo:  "thegoddamnbeckster/Chronicle.Plugin.Hardcover",
            // Confirmed live (2026-09-04): repo moved to v1.2.1 with a differently-named
            // asset ("Chronicle.Plugin.Hardcover.zip", PascalCase/no version suffix) than
            // the v1.2.0 release this entry was pinned to ("chronicle.plugin.hardcover-
            // v1.2.0.zip", lowercase/versioned) -- the naming convention isn't even
            // consistent release-to-release within this one repo. Re-synced to what's
            // actually attached to the real latest release now.
            AssetName:   "Chronicle.Plugin.Hardcover.zip",
            DllName:     "Chronicle.Plugin.Hardcover.dll",
            Tags:        ["books", "audiobooks", "metadata", "sync"],
            Sha256:      "8c1ee3f43c677d265aa052910e5f3297cf8f6305d363a5542639aedfd3d49da9",
            Version:     "1.2.1"
        ),
        new PluginCatalogEntry(
            PluginId:    "chronicle.plugin.thetvdb",
            Name:        "TheTVDB",
            Description: "Metadata for TV series, seasons, and episodes from TheTVDB — the community standard used by Sonarr, Plex, Kodi, Trakt, and SIMKL.",
            Author:      "Chronicle Contributors",
            IconUrl:     "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAYAAACqaXHeAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAIuSURBVHhe7ZlhccMwDIUHYARGoARKYAiKoAzKYBRGYRhKoiwGRt69u7qnvcqJY7u1eqfvZyIl0bPkyPbbWxAEQRAEQRAENaSU9mkAInIWkYtx/YPfWaLXv4nBAhyN60d+pwUCNXwvbDecwQI0B1EQ78B2wxkpAJ7XmsaWX0rpne2eioic+IvYhimM5GIZFDLnh+2eTqMAVjCLZWCJllL6ZLun0yIAwOix31IZWOnPNlPoEODAfqUyKGTMN9tNoVUATF7sVyoDK/1FZMd2U2gVANSWAae/iPyyzTQ6BVgtg0L6n7TNVHoEAOzLZVBI/7ssmUavAJjM2F8HaKS/OU9Mo1cA/MvZP5dBIf0f3/puoVcAwP55lK1nT299Gesj2WaNUhlgtqdr81tfZoQA1mLLEsVF68uMEADwaFuwjwsGCvDFz9G4aX2ZgQLs+DkaN60vM0oAUCoDV60vM1gAswxctb7MYAHuGh/gqvUNgiAIgiDIWI2Q5rrzu9/qB+Drdg2QqQkE8Gqu1g+4FmFLIHpDY4ufy52gDAXyL9UxcvrwQ6/qlvwAb5byfTesBQL0Mjenc6XfORvwPTdUBnI73FBb3ot+5ONzNwisBQL0pmde22+cA/wuhx8twHWTxNdZgObRAgB3x2GaGgEa5wD8QW7ngu6OxDJrgQA9m2/5C1iZ446lQK7HW019gOH7EgKs0dQJArftcG0gmM1b/MAr9QF3dK4GL24nvyAIgiB4Tf4A7zKb1dPc3rkAAAAASUVORK5CYII=",
            GithubRepo:  "thegoddamnbeckster/Chronicle.Plugin.TheTVDB",
            AssetName:   "chronicle.plugin.thetvdb-v1.0.0.zip",
            DllName:     "Chronicle.Plugin.TheTVDB.dll",
            Tags:        ["tv", "metadata"],
            Sha256:      "d8f5dae46d518264f292e0f070b3aa2bd53bfc6f1a138d04fe587d317edf9aef",
            Version:     "1.0.0"
        ),
        new PluginCatalogEntry(
            PluginId:    "chronicle.plugin.tvmaze",
            Name:        "TVMaze",
            Description: "TV series, season, and episode metadata from TVMaze. No API key or account required.",
            Author:      "Chronicle Contributors",
            IconUrl:     "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAYAAACqaXHeAAAAAXNSR0IArs4c6QAAAARnQU1BAACxjwv8YQUAAAAJcEhZcwAADsMAAA7DAcdvqGQAAAKcSURBVHhe7ZpNkoIwEIU5HgfiONyFI7icpSzdsXSZqSiG7vcaDQiVzCSpelVOS376y2sQmObSNK5kNRgoTRUABkpTBYCB0lQBYGCLfvrR7W5D727Qfep4DlI36E5j737wmA1KCKDj/kNHc6CukH8UtDdKCuDS9u6ug+5qzLOoc9Om4z8rLYCm3VYGaP8Ix3zSVwBsYVLvd4kgvknqaPt7JQcQXwbH298rPQA6fmVnT7C/VwYA4srgDPt7ZQGAyoCu7efY3ysPANRndLdWfI+ADIfsVSYAuAzufbv63VH298oGAO1yKIOd40UqHwDUby4DACOdcYQyAsBW98nqGJwbDlBWAHC33Ti4SeWPV4fvlRcA6qvb0fb3ygwAl8HSjre/V3YAqAxe7QT7e+UHgPo/2xn298oQgFUG59jf6wQAf0sVAAZKUwWAgdJUAWCgNFUAGChNFQAGSlMlgIHSFAkAXkwY9+Z4B8ePrmEM89k+vgAx2mPuiOPMNbD2AaDbU7wFNibHd3s0hjWP0fIAAA8ojKc4OHl4tyf+NSb+IYcADABwnq3aDOA+zisRFg72H8cAQi9ML3g5nkvJknwx+hw3JYC+nz8vT3peC5z6xQlqYcH+c5/gGKsMtDh5vZ4EANolYbUbg7uKUpALW+z/co2wtHkyhH5ULlyS1CLdtQtA2FG/ePnZBGDv1nLVsJ8ZyqsKnytSAwiJDm6a89dxkSyd/bmhjdUl1XSIDXWP9gGgyx6/yHwuDI9baTJJCcxMXq8nEQDYpZfdEID4m20sx5gBRiWv15MMgJkcAKAEcVw1Rhfnlsc5I+IcsAIdtR+AtQsKAP54wTG95DHwJni1JQHwf1UBYKA0VQAYKE0VAAZKUwWAgdJUAWCgNBUP4BeYI+ijz8zs7AAAAABJRU5ErkJggg==",
            GithubRepo:  "thegoddamnbeckster/Chronicle.Plugin.TVMaze",
            AssetName:   "chronicle.plugin.tvmaze-v1.0.2.zip",
            DllName:     "Chronicle.Plugin.TVMaze.dll",
            Tags:        ["tv", "metadata"],
            Sha256:      "b8095c5a3db7de0af26c67942bcc7fadec982c0badca57f6797a19fd7324c9c3",
            Version:     "1.0.2"
        ),
        new PluginCatalogEntry(
            PluginId:    "chronicle.plugin.kodi.nfo",
            Name:        "Kodi NFO",
            Description: "Reads and writes Kodi's .nfo sidecar files — lossless local capture during a file scan, and building fresh NFOs from Chronicle's own resolved data on demand for Chronicle_Scraper's movie and TV addons.",
            Author:      "Chronicle Contributors",
            IconUrl:     "https://kodi.tv/favicon.ico",
            GithubRepo:  "thegoddamnbeckster/Chronicle.Plugin.Kodi.NFO",
            AssetName:   "chronicle.plugin.kodi.nfo-v1.0.0.zip",
            DllName:     "Chronicle.Plugin.Kodi.NFO.dll",
            Tags:        ["movies", "tv", "kodi", "nfo", "local"],
            Sha256:      "cc17f6b6f40a5a0828548d462c75638f3c16b225c64e3fe79d75d0785b285a00",
            Version:     "1.0.0"
        ),
        new PluginCatalogEntry(
            PluginId:    "chronicle.plugin.fanedit",
            Name:        "FanEdit",
            Description: "Fetches fanedit metadata from the Internet Fan Edit Database (fanedit.org). Requires a registered fanedit.org account. Please use responsibly — a minimum 1-second delay between requests is enforced.",
            Author:      "Chronicle Contributors",
            IconUrl:     "data:image/svg+xml;base64,PHN2ZyB4bWxucz0naHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmcnIHZpZXdCb3g9JzAgMCAyNCAyNCc+PHJlY3Qgd2lkdGg9JzI0JyBoZWlnaHQ9JzI0JyByeD0nMycgZmlsbD0nI2MyNDEwYycvPjxwYXRoIGQ9J001IDhoMnYySDV6bTEyIDBoMnYyaC0yek01IDE0aDJ2Mkg1em0xMiAwaDJ2MmgtMnonIGZpbGw9J3doaXRlJyBvcGFjaXR5PScwLjYnLz48cGF0aCBkPSdNOCA2aDh2MTJIOHonIGZpbGw9J3doaXRlJyBvcGFjaXR5PScwLjknLz48cGF0aCBkPSdNMTAgOWg0TTEwIDEyaDRNMTAgMTVoMycgc3Ryb2tlPScjYzI0MTBjJyBzdHJva2Utd2lkdGg9JzEuMicgc3Ryb2tlLWxpbmVjYXA9J3JvdW5kJy8+PC9zdmc+",
            GithubRepo:  "thegoddamnbeckster/Chronicle.Plugin.FanEdit",
            AssetName:   "chronicle.plugin.fanedit-v1.0.0.zip",
            DllName:     "Chronicle.Plugin.FanEdit.dll",
            Tags:        ["movies", "fanedits", "metadata"],
            Sha256:      "eb559c681d9f2fd5edddc8981fbea5a106bd4931f713d221aff703b039a44117",
            Version:     "1.0.0"
        ),
        new PluginCatalogEntry(
            PluginId:    "chronicle.plugin.simkl",
            Name:        "SIMKL",
            Description: "Metadata for Movies, TV, and Anime from SIMKL. Requires a free SIMKL API Client ID.",
            Author:      "Chronicle Contributors",
            IconUrl:     "https://simkl.com/favicon.ico",
            GithubRepo:  "thegoddamnbeckster/Chronicle.Plugin.Simkl",
            AssetName:   "chronicle.plugin.simkl-v1.1.0.zip",
            DllName:     "Chronicle.Plugin.Simkl.dll",
            Tags:        ["movies", "tv", "anime", "metadata"],
            Sha256:      "0be5da25f81a5a58cc42a0a0c6574a5070e5b1006448a8720046c3aab0535869",
            // The latest GitHub release (the version actually installable through this catalog)
            // is v1.1.0 -- the repo's own HEAD manifest.json has moved on to 1.4.0 since, but
            // that newer code has no attached release asset yet. Bump this once it does.
            Version:     "1.1.0"
        ),
        new PluginCatalogEntry(
            PluginId:    "chronicle.plugin.fanarttv",
            Name:        "Fanart.tv",
            Description: "Fetches high-quality artwork from Fanart.tv — posters, backgrounds, logos, disc art, clearart, and banners for movies, TV, and music. Requires a free Fanart.tv API key.",
            Author:      "Chronicle Contributors",
            IconUrl:     "https://fanart.tv/favicon.ico",
            GithubRepo:  "thegoddamnbeckster/Chronicle.Plugin.FanartTV",
            AssetName:   "chronicle.plugin.fanarttv-v1.0.0.zip",
            DllName:     "Chronicle.Plugin.FanartTV.dll",
            Tags:        ["movies", "tv", "music", "artwork", "metadata"],
            Sha256:      "449ebf8b1905d349dcc51bcb8e2708267d6e53e006b7001ed63d6e15d3fce532",
            Version:     "1.0.0"
        ),
        new PluginCatalogEntry(
            PluginId:    "chronicle.plugin.themes.default",
            Name:        "Default Themes",
            Description: "Provides the four built-in Chronicle themes: Light, Dark, Navy & Pink, and Dark Teal. Install additional theme plugins to expand the available theme list.",
            Author:      "Chronicle",
            IconUrl:     null,
            GithubRepo:  "thegoddamnbeckster/Chronicle.Plugin.Themes.Default",
            AssetName:   "chronicle.plugin.themes.default-v1.0.0.zip",
            DllName:     "Chronicle.Plugin.Themes.Default.dll",
            Tags:        ["themes", "ui"],
            Sha256:      "1bdf6ae1c4a109c946629baf7787aef8b3d9555127888d0182cb9b6b58cf7079",
            Version:     "1.0.0"
        ),
    ];
}
