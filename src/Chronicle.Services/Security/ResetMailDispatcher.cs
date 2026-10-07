using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Chronicle.Services.Security
{
    /// <summary>
    /// Sends reset emails after the HTTP request has already been answered. Doing it inline would make the response
    /// slower when an address exists than when it does not, which leaks which accounts exist. A send that fails is
    /// logged (with the reason) for the administrator; the person who asked is never told either way.
    /// </summary>
    public sealed class ResetMailDispatcher
    {
        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger _log = Log.ForContext<ResetMailDispatcher>();
        private readonly ConcurrentDictionary<Task, byte> _inFlight = new();

        public ResetMailDispatcher(IServiceScopeFactory scopes) => _scopes = scopes;

        public void Dispatch(PendingResetMail mail)
        {
            var task = Task.Run(() => SendAsync(mail));
            _inFlight[task] = 0;
            _ = task.ContinueWith(t => _inFlight.TryRemove(t, out _), TaskScheduler.Default);
        }

        /// <summary>Completes when every email handed over so far has been attempted. For tests and orderly shutdown.</summary>
        public Task WhenIdleAsync() => Task.WhenAll(_inFlight.Keys);

        private async Task SendAsync(PendingResetMail mail)
        {
            try
            {
                using var scope = _scopes.CreateScope();
                var credentials = await scope.ServiceProvider.GetRequiredService<IEmailSettingsStore>().GetCredentialsAsync();
                await scope.ServiceProvider.GetRequiredService<IEmailSender>().SendAsync(mail.Message, credentials);
                _log.Information("Password reset email sent for user id {UserId}", mail.UserId);
            }
            catch (EmailSendException ex)
            {
                _log.Warning("Password reset email for user id {UserId} could NOT be sent: {Reason}", mail.UserId, ex.Message);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Password reset email for user id {UserId} failed unexpectedly", mail.UserId);
            }
        }
    }
}
