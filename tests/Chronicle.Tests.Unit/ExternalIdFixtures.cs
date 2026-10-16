using System.Runtime.CompilerServices;
using Chronicle.Plugins.Models;
using Chronicle.Services;

namespace Chronicle.Tests.Unit;

/// <summary>
/// The external-id policies the shipped plugins declare (each plugin repo declares its own and tests it), loaded once for the
/// whole test run the way the application loads them when plugins load. A test that needs a bare installation loads its own
/// set with <see cref="ExternalIdPolicies.LoadForCurrentContext"/>.
/// </summary>
public static class ExternalIdFixtures
{
    public const string WikipediaPluginId = "chronicle.plugin.wikipedia";

    public static ExternalIdPolicy[] ShippedPlugins() =>
    [
        new() { Source = "tmdb", PluginId = "chronicle.plugin.tmdb", StoredPrefixRewrites = { ["person:"] = "tmdb:" },
                CrossRefFormats = { ["movie"] = "movie:{id}", ["default"] = "tv:{id}" }, CrossRefValueKinds = ["number"],
                FamilyIdPatterns = { ["movie"] = "^movie:" },
                PositionInIdPattern = @"season:(?<season>\d+)/episode:(?<episode>\d+)",
                ChildIds =
                [
                    new() { RootIdPattern = @"^tv:(?<root>\d+)$", Depth = 1, Template = "tv:{root}/season:{n}" },
                    new() { RootIdPattern = @"^tv:(?<root>\d+)$", Depth = 2, Template = "tv:{root}/season:{parentN}/episode:{n}" },
                ],
                IdShapes =
                [
                    new() { Pattern = @"^movie:\d+$", Depths = [0], ItemFamilies = ["movie"] },
                    new() { Pattern = @"^movie:\d+$", Depths = [1], ParentFamilies = ["movie"] },
                    new() { Pattern = @"^tv:\d+$", Depths = [0], ItemFamilies = ["tv"] },
                    new() { Pattern = @"^tv:\d+/season:\d+$", Depths = [1] },
                    new() { Pattern = @"^tv:\d+/season:\d+/episode:\d+$", Depths = [2] },
                ] },
        new() { Source = "musicbrainz", PluginId = "chronicle.plugin.musicbrainz",
                IdShapes =
                [
                    new() { Pattern = "^artist:", Depths = [0], ItemFamilies = ["music"] },
                    new() { Pattern = "^release-group:", Depths = [1] },
                    new() { Pattern = "^recording:", Depths = [2] },
                    new() { Pattern = "^release:", Depths = [] },
                ] },
        new() { Source = "imdb", PluginId = "chronicle.plugin.imdb", SharedRawIdPrefix = "imdb:", KeepSharedPrefixWhenIdStarts = ["nm"],
                CrossRefFormats = { ["default"] = "imdb:{id}" }, CrossRefValueKinds = ["string"] },
        new() { Source = "trakt", PluginId = "chronicle.plugin.trakt",
                CrossRefFormats = { ["movie"] = "trakt:movie:{id}", ["default"] = "trakt:show:{id}" }, CrossRefValueKinds = ["number"],
                FamilyIdPatterns = { ["movie"] = ":movie:" } },
        new() { Source = "simkl", PluginId = "chronicle.plugin.simkl", RefusedIdPattern = @"^simkl:\d+$",
                CrossRefFormats = { ["movie"] = "simkl:movie:{id}", ["anime"] = "simkl:anime:{id}", ["default"] = "simkl:tv:{id}" },
                CrossRefValueKinds = ["number"], FamilyIdPatterns = { ["movie"] = ":movie:" } },
        new() { Source = "hardcover", PluginId = "hardcover", SeriesIdMarker = "series:" },
        new() { Source = "fanarttv", PluginId = "chronicle.plugin.fanarttv", IgnoredForConflicts = true },
        new() { Source = "wikipedia", PluginId = WikipediaPluginId, NameDerived = true, DisambiguatedTitleField = "title",
                BirthYearTagPattern = @"^\s*(?<year>\d{4})\s+births\s*$", NotAnEntityTitlePattern = @"^\s*\d{4}\s+in\s+(film|television)\s*$" },
    ];

    [ModuleInitializer]
    public static void LoadForTheTestRun() => ExternalIdPolicies.Load(ShippedPlugins());
}
