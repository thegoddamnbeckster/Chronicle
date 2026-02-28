using System.Reflection;
using System.Runtime.Loader;

namespace Chronicle.Services.Plugins;

/// <summary>
/// Isolated <see cref="AssemblyLoadContext"/> for a single plugin assembly.
/// Using a separate context per plugin prevents type-identity conflicts when
/// multiple plugins reference the same dependency at different versions, and
/// allows the plugin to be unloaded (collectible = true).
/// </summary>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;

    /// <param name="pluginAssemblyPath">Absolute path to the plugin's main DLL.</param>
    public PluginLoadContext(string pluginAssemblyPath)
        : base(name: Path.GetFileNameWithoutExtension(pluginAssemblyPath), isCollectible: true)
    {
        _resolver = new AssemblyDependencyResolver(pluginAssemblyPath);
    }

    /// <inheritdoc />
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        // First try to resolve from the plugin's own directory
        var resolved = _resolver.ResolveAssemblyToPath(assemblyName);
        if (resolved != null)
            return LoadFromAssemblyPath(resolved);

        // Fall through to the default context — this handles shared framework
        // assemblies (Microsoft.*, System.*) and Chronicle.Plugins.dll itself.
        return null;
    }

    /// <inheritdoc />
    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var resolved = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (resolved != null)
            return LoadUnmanagedDllFromPath(resolved);
        return IntPtr.Zero;
    }
}
