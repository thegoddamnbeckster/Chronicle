using Chronicle.Data;
using Chronicle.Services.Plugins;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicle.API;

/// <summary>
/// <c>Chronicle.API --accept-plugin-changes</c>: records the plugin files currently on disk as the trusted ones, then
/// exits without starting the web server. Plugins are normally checked against a hash taken at install or update, so a
/// DLL swapped by hand is refused; a developer who rebuilds and redeploys plugins (RunTestEnvironment.ps1) runs this
/// afterwards to say "yes, I changed those". Like the password recovery command it needs no credentials because only
/// someone who controls the machine can run it.
/// </summary>
public static class PluginCommand
{
    public const string Flag = "--accept-plugin-changes";

    public static bool IsRequested(string[] args) =>
        Array.Exists(args, a => string.Equals(a, Flag, StringComparison.OrdinalIgnoreCase));

    public static async Task<int> RunAsync(IServiceProvider services, TextWriter output)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChronicleDbContext>();
        var integrity = scope.ServiceProvider.GetRequiredService<IPluginIntegrity>();

        var changed = 0;
        foreach (var plugin in await db.Plugins.OrderBy(p => p.PluginId).ToListAsync())
        {
            if (!File.Exists(plugin.DllPath))
            {
                await output.WriteLineAsync($"  {plugin.PluginId}: DLL not found at {plugin.DllPath}, skipped");
                continue;
            }
            var before = plugin.FilesSha256;
            await integrity.AcceptCurrentFilesAsync(plugin);
            if (!string.Equals(before, plugin.FilesSha256, StringComparison.OrdinalIgnoreCase))
            {
                changed++;
                await output.WriteLineAsync($"  {plugin.PluginId}: accepted new files");
            }
        }
        await output.WriteLineAsync(changed == 0 ? "No plugin files had changed." : $"Accepted changes to {changed} plugin(s).");
        return 0;
    }
}
