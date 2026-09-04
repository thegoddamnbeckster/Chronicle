using Chronicle.Core.Models;

namespace Chronicle.Services.Plugins;

/// <summary>List of GitHub repos that make up Chronicle's plugin catalog. See PluginCatalogSeed's own doc for why this is deliberately the only static part left.</summary>
public static class PluginCatalogSeeds
{
    public static readonly PluginCatalogSeed[] Entries =
    [
        new("chronicle.plugin.tmdb",              "thegoddamnbeckster/Chronicle.Plugin.TMDB",              ["movies", "tv", "metadata"]),
        new("chronicle.plugin.musicbrainz",        "thegoddamnbeckster/Chronicle.Plugin.MusicBrainz",        ["music", "audio", "metadata"]),
        new("chronicle.plugin.filescanner",        "thegoddamnbeckster/Chronicle.Plugin.FileScanner",        ["movies", "tv", "audio", "filescanner", "local"]),
        new("chronicle.plugin.wikipedia",          "thegoddamnbeckster/Chronicle.Plugin.Wikipedia",          ["movies", "tv", "music", "books", "games", "people", "metadata"]),
        new("chronicle.plugin.moviesremastered",   "thegoddamnbeckster/Chronicle.Plugin.MoviesRemastered",   ["movies", "fanedits", "metadata"]),
        new("chronicle.plugin.trakt",              "thegoddamnbeckster/Chronicle.Plugin.Trakt",              ["movies", "tv", "scrobbling", "sync"]),
        new("hardcover",                           "thegoddamnbeckster/Chronicle.Plugin.Hardcover",          ["books", "audiobooks", "metadata", "sync"]),
        new("chronicle.plugin.thetvdb",            "thegoddamnbeckster/Chronicle.Plugin.TheTVDB",            ["tv", "metadata"]),
        new("chronicle.plugin.tvmaze",             "thegoddamnbeckster/Chronicle.Plugin.TVMaze",             ["tv", "metadata"]),
        new("chronicle.plugin.kodi.nfo",           "thegoddamnbeckster/Chronicle.Plugin.Kodi.NFO",           ["movies", "tv", "kodi", "nfo", "local"]),
        new("chronicle.plugin.fanedit",            "thegoddamnbeckster/Chronicle.Plugin.FanEdit",            ["movies", "fanedits", "metadata"]),
        new("chronicle.plugin.simkl",              "thegoddamnbeckster/Chronicle.Plugin.Simkl",              ["movies", "tv", "anime", "metadata"]),
        new("chronicle.plugin.fanarttv",           "thegoddamnbeckster/Chronicle.Plugin.FanartTV",           ["movies", "tv", "music", "artwork", "metadata"]),
        new("chronicle.plugin.themes.default",     "thegoddamnbeckster/Chronicle.Plugin.Themes.Default",     ["themes", "ui"]),
    ];
}
