using Chronicle.Services.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicle.API;

/// <summary>
/// <c>Chronicle.API --reset-admin-password &lt;username&gt;</c>: prints a one-time password-reset code and exits, without
/// starting the web server. It is the way back in when the only administrator has forgotten their password and outgoing
/// email is not set up. It needs no credentials because it can only be run by someone who already controls the machine
/// (it opens the same database file); in Docker, run it with <c>docker compose exec api dotnet Chronicle.API.dll --reset-admin-password NAME</c>.
/// </summary>
public static class RecoveryCommand
{
    public const string Flag = "--reset-admin-password";

    /// <summary>The username following the flag, or null when the flag is absent. Empty string when the name is missing.</summary>
    public static string? ParseUsername(string[] args)
    {
        var i = Array.FindIndex(args, a => string.Equals(a, Flag, StringComparison.OrdinalIgnoreCase));
        if (i < 0) return null;
        return i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1] : string.Empty;
    }

    /// <returns>0 on success, 1 when the code could not be issued.</returns>
    public static async Task<int> RunAsync(IServiceProvider services, string username, TextWriter output)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            await output.WriteLineAsync($"Usage: Chronicle.API {Flag} <username>");
            return 1;
        }

        using var scope = services.CreateScope();
        var reset = scope.ServiceProvider.GetRequiredService<IPasswordResetService>();
        var email = await scope.ServiceProvider.GetRequiredService<IEmailSettingsStore>().GetAsync();
        try
        {
            var issued = await reset.IssueForUsernameAsync(username, PasswordResetService.DeliveryConsole);
            var url = reset.BuildResetUrl(email.PublicUrl ?? "https://<your-chronicle-address>", issued.Token);
            await output.WriteLineAsync();
            await output.WriteLineAsync($"Password reset code for '{issued.Username}' (valid until {issued.ExpiresAtUtc:u}, works once):");
            await output.WriteLineAsync();
            await output.WriteLineAsync($"    {issued.Token}");
            await output.WriteLineAsync();
            await output.WriteLineAsync("Open Chronicle's sign-in page, choose \"Forgot password?\", then \"I have a reset code\", and enter it.");
            await output.WriteLineAsync($"Or open: {url}");
            await output.WriteLineAsync();
            return 0;
        }
        catch (InvalidOperationException ex)
        {
            await output.WriteLineAsync(ex.Message);
            return 1;
        }
    }
}
