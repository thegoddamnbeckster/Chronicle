using Chronicle.Core.Models;

namespace Chronicle.Services.Plugins
{
    /// <summary>"Plugins that handle this media type" for the catalog. A plugin matches a type when it names the type
    /// itself, or names the type's provider family ("tv" for anime). Names are compared without case and without a
    /// plural s, because catalogs say "movies" where providers say "movie".</summary>
    public static class PluginCatalogFilter
    {
        public static bool Handles(PluginCatalogEntry entry, string mediaTypeName, string? providerFamily)
        {
            if (entry.SupportedMediaTypes is not { Length: > 0 } supported) return true;   // unknown: do not hide it
            var wanted = new HashSet<string>(StringComparer.Ordinal) { Norm(mediaTypeName) };
            if (!string.IsNullOrWhiteSpace(providerFamily)) wanted.Add(Norm(providerFamily));
            return supported.Any(s => wanted.Contains(Norm(s)));
        }

        private static string Norm(string name)
        {
            var n = name.Trim().ToLowerInvariant();
            return n.Length > 3 && n.EndsWith('s') ? n[..^1] : n;
        }
    }
}
