using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Serilog;

namespace Chronicle.Services.Security
{
    public sealed record EmailMessage(string To, string Subject, string TextBody);

    /// <summary>Sends one plain-text message using the configured mail server. Failures throw <see cref="EmailSendException"/> with a message safe to show an administrator.</summary>
    public interface IEmailSender
    {
        Task SendAsync(EmailMessage message, SmtpCredentials credentials, CancellationToken ct = default);
    }

    public sealed class EmailSendException : Exception
    {
        public EmailSendException(string message, Exception? inner = null) : base(message, inner) { }
    }

    public sealed class MailKitEmailSender : IEmailSender
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
        private readonly ILogger _log = Log.ForContext<MailKitEmailSender>();

        public async Task SendAsync(EmailMessage message, SmtpCredentials credentials, CancellationToken ct = default)
        {
            var s = credentials.Settings;
            if (!s.IsConfigured) throw new EmailSendException("Outgoing email is not set up.");

            var mime = new MimeMessage();
            mime.From.Add(new MailboxAddress(s.FromName, s.FromAddress));
            mime.To.Add(MailboxAddress.Parse(message.To));
            mime.Subject = message.Subject;
            mime.Body = new TextPart("plain") { Text = message.TextBody };

            var options = s.Security switch
            {
                SmtpSecurity.Tls => SecureSocketOptions.SslOnConnect,
                SmtpSecurity.None => SecureSocketOptions.None,
                _ => SecureSocketOptions.StartTls,
            };

            using var client = new SmtpClient { Timeout = (int)Timeout.TotalMilliseconds };
            try
            {
                await client.ConnectAsync(s.Host, s.Port, options, ct);
                if (!string.IsNullOrEmpty(s.Username))
                    await client.AuthenticateAsync(s.Username, credentials.Password ?? "", ct);
                await client.SendAsync(mime, ct);
                await client.DisconnectAsync(true, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (AuthenticationException ex) { throw Fail("The mail server rejected the user name or password.", ex); }
            catch (SslHandshakeException ex) { throw Fail("The secure connection to the mail server could not be set up (check the Security setting and the server's certificate).", ex); }
            catch (SmtpCommandException ex) { throw Fail($"The mail server refused the message: {ex.StatusCode} {Shorten(ex.Message)}", ex); }
            catch (SmtpProtocolException ex) { throw Fail("The mail server did not respond as expected (check the port and the Security setting).", ex); }
            catch (System.Net.Sockets.SocketException ex) { throw Fail($"Could not reach the mail server at {s.Host}:{s.Port} ({Shorten(ex.Message)}).", ex); }
            catch (IOException ex) { throw Fail("The connection to the mail server was lost.", ex); }
            catch (FormatException ex) { throw Fail("An email address was not valid.", ex); }
        }

        private EmailSendException Fail(string message, Exception inner)
        {
            // The detail goes to the log; the caller gets the plain-language version.
            _log.Warning(inner, "Sending email failed: {Reason}", message);
            return new EmailSendException(message, inner);
        }

        private static string Shorten(string s) => s.Length <= 160 ? s : s[..160] + "...";
    }
}
