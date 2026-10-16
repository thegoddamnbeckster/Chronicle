using Chronicle.Services.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Chronicle.API;

/// <summary>
/// <c>Chronicle.API --unlock-registration</c>: reopens self-service sign-up and exits, without starting the web server.
/// It is the way back when registration was locked and the administrator's password is lost. Like
/// <see cref="RecoveryCommand"/> it needs no credentials, because it can only be run by someone who controls this machine
/// (it opens the same database file).
/// </summary>
public static class UnlockRegistrationCommand
{
    public const string Flag = "--unlock-registration";

    public static bool IsRequested(string[] args) =>
        args.Any(a => string.Equals(a, Flag, StringComparison.OrdinalIgnoreCase));

    public static async Task<int> RunAsync(IServiceProvider services, TextWriter output)
    {
        using var scope = services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IRegistrationPolicy>().SetLockedAsync(false);
        await output.WriteLineAsync("Registration is unlocked: anyone who can reach Chronicle can create an account again.");
        await output.WriteLineAsync("A new account is not an administrator. To recover the admin account use --reset-admin-password <username>.");
        return 0;
    }
}
