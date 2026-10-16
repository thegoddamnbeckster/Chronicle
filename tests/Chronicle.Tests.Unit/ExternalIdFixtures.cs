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
                CrossRefFormats = { ["movie"] = "movie:{id}", ["default"] = "tv:{id}" }, CrossRefValueKinds = ["number"] },
        new() { Source = "imdb", PluginId = "chronicle.plugin.imdb", SharedRawIdPrefix = "imdb:", KeepSharedPrefixWhenIdStarts = ["nm"],
                CrossRefFormats = { ["default"] = "imdb:{id}" }, CrossRefValueKinds = ["string"] },
        new() { Source = "trakt", PluginId = "chronicle.plugin.trakt",
                CrossRefFormats = { ["movie"] = "trakt:movie:{id}", ["default"] = "trakt:show:{id}" }, CrossRefValueKinds = ["number"] },
        new() { Source = "simkl", PluginId = "chronicle.plugin.simkl", RefusedIdPattern = @"^simkl:\d+$",
                CrossRefFormats = { ["movie"] = "simkl:movie:{id}", ["anime"] = "simkl:anime:{id}", ["default"] = "simkl:tv:{id}" },
                CrossRefValueKinds = ["number"] },
        new() { Source = "fanarttv", PluginId = "chronicle.plugin.fanarttv", IgnoredForConflicts = true },
        new() { Source = "wikipedia", PluginId = WikipediaPluginId, NameDerived = true },
    ];

    [ModuleInitializer]
    public static void LoadForTheTestRun() => ExternalIdPolicies.Load(ShippedPlugins());
}
