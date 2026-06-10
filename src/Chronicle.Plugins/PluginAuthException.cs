namespace Chronicle.Plugins;

/// <summary>
/// Thrown by a plugin when it cannot authenticate with its upstream service
/// (e.g. wrong credentials, expired session that could not be refreshed).
/// Chronicle treats this as a terminal enrichment failure so that the user
/// is prompted to fix the plugin's credentials rather than retrying silently.
/// </summary>
public class PluginAuthException : Exception
{
    /// <summary>The Chronicle plugin ID (e.g. "chronicle.plugin.tmdb").</summary>
    public string PluginId { get; }

    public PluginAuthException(string pluginId, string message)
        : base(message)
    {
        PluginId = pluginId;
    }

    public PluginAuthException(string pluginId, string message, Exception inner)
        : base(message, inner)
    {
        PluginId = pluginId;
    }
}
