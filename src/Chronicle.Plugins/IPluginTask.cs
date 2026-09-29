namespace Chronicle.Plugins;

/// <summary>
/// Implement this interface in your plugin assembly to provide a custom
/// background task. Chronicle discovers implementations automatically when
/// the plugin loads (<c>PluginRegistry.LoadPluginCoreAsync</c> scans the assembly
/// for it the same way it already does for <see cref="IMetadataProvider"/>) and
/// wires them to the task declared in manifest.json via matching <see cref="TaskId"/>.
///
/// Only needed for custom task IDs. The well-known IDs
/// "fetch-missing-metadata", "resync-all-metadata", "import-all", and
/// "delta-sync" are handled internally by Chronicle — no implementation required.
/// </summary>
public interface IPluginTask
{
    /// <summary>
    /// Reserved <see cref="Configure"/> settings key. Chronicle injects the plugin's own
    /// persistent data directory under this key — a folder next to the plugin's DLL
    /// (created if missing) that survives plugin reloads and DLL redeploys, since nothing
    /// else in the plugin loader touches it. The same value is also injected into the
    /// settings dictionary passed to every <see cref="IMetadataProvider"/>/<see cref="IImportProvider"/>/
    /// etc. instance loaded from the same plugin assembly, so a custom task and a sibling
    /// provider can share one on-disk cache by each reading this key from their own
    /// <c>Configure</c> call — there is no other way for a plugin to get a stable path of
    /// its own, since <c>Assembly.Location</c> is empty for an assembly Chronicle loads via
    /// <c>LoadFromStream</c> (see PluginRegistry.LoadPluginCoreAsync).
    /// </summary>
    const string DataDirectorySettingsKey = "__data_dir";

    /// <summary>
    /// Must exactly match the <c>task_id</c> value declared in manifest.json
    /// (without the plugin-ID prefix Chronicle adds internally).
    /// </summary>
    string TaskId { get; }

    /// <summary>Called once after instantiation, mirroring <see cref="IMetadataProvider.Configure"/>.</summary>
    void Configure(IReadOnlyDictionary<string, string> settings);

    /// <summary>Perform the task's work. Called on the declared schedule or via "Run Now".</summary>
    Task RunAsync(CancellationToken ct);
}
