using System.Reflection;
using Chronicle.Core.Models;

namespace Chronicle.Services.Plugins;

/// <summary>
/// The plugin catalog compiled into Chronicle, used only when the hosted list (see <see cref="PluginCatalogSource"/>) cannot be
/// reached and no earlier copy exists. It is the repository's own plugins.json embedded at build time, so there is exactly one
/// list to edit.
/// </summary>
public static class PluginCatalogSeeds
{
    public static IReadOnlyList<PluginCatalogSeed> Entries => _entries.Value;

    private static readonly Lazy<IReadOnlyList<PluginCatalogSeed>> _entries = new(Load);

    private static IReadOnlyList<PluginCatalogSeed> Load()
    {
        using var stream = typeof(PluginCatalogSeeds).Assembly.GetManifestResourceStream("plugins.json");
        if (stream is null) return [];
        using var reader = new StreamReader(stream);
        return PluginCatalogSource.Parse(reader.ReadToEnd(), out _) ?? [];
    }
}
