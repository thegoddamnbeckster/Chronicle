using System.Net;
using System.Net.Sockets;
using System.Text;
using Chronicle.Services.Security;
using FluentAssertions;

namespace Chronicle.Tests.Unit.Security
{
    /// <summary>The real MailKit sender against a tiny in-process SMTP server (no TLS, no real network).</summary>
    public sealed class MailKitEmailSenderTests : IDisposable
    {
        /// <summary>Just enough SMTP to receive one message, or to refuse in the ways a real server does.</summary>
        private sealed class FakeSmtpServer : IDisposable
        {
            private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
            private readonly CancellationTokenSource _stop = new();
            public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
            public bool AdvertiseAuth { get; set; }
            public string? RejectRecipientWith { get; set; }
            public string? Username { get; private set; }
            public string? MailFrom { get; private set; }
            public List<string> Recipients { get; } = [];
            public string? Data { get; private set; }
            public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public FakeSmtpServer() { _listener.Start(); _ = Task.Run(AcceptAsync); }

            private async Task AcceptAsync()
            {
                try
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    await using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII);
                    await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                    await writer.WriteLineAsync("220 fake.test ESMTP ready");
                    string? line;
                    while ((line = await reader.ReadLineAsync(_stop.Token)) is not null)
                    {
                        var cmd = line.ToUpperInvariant();
                        if (cmd.StartsWith("EHLO") || cmd.StartsWith("HELO"))
                        {
                            await writer.WriteLineAsync(AdvertiseAuth ? "250-fake.test" : "250 fake.test");
                            if (AdvertiseAuth) await writer.WriteLineAsync("250 AUTH PLAIN LOGIN");
                        }
                        else if (cmd.StartsWith("AUTH"))
                        {
                            Username = line;
                            await writer.WriteLineAsync("535 5.7.8 Authentication credentials invalid");
                        }
                        else if (cmd.StartsWith("MAIL FROM")) { MailFrom = line; await writer.WriteLineAsync("250 OK"); }
                        else if (cmd.StartsWith("RCPT TO"))
                        {
                            if (RejectRecipientWith is not null) { await writer.WriteLineAsync(RejectRecipientWith); continue; }
                            Recipients.Add(line); await writer.WriteLineAsync("250 OK");
                        }
                        else if (cmd == "DATA")
                        {
                            await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                            var sb = new StringBuilder();
                            while ((line = await reader.ReadLineAsync(_stop.Token)) is not null && line != ".") sb.AppendLine(line);
                            Data = sb.ToString();
                            await writer.WriteLineAsync("250 OK queued");
                        }
                        else if (cmd == "QUIT") { await writer.WriteLineAsync("221 bye"); break; }
                        else await writer.WriteLineAsync("250 OK");
                    }
                }
                catch { /* the test finished or the client left */ }
                finally { Done.TrySetResult(); }
            }

            public void Dispose() { _stop.Cancel(); _listener.Stop(); }
        }

        private readonly FakeSmtpServer _server = new();
        private readonly MailKitEmailSender _sender = new();

        public void Dispose() => _server.Dispose();

        private SmtpCredentials Credentials(string? user = null, string? password = null, string security = SmtpSecurity.None, int? port = null) =>
            new(new EmailSettings("127.0.0.1", port ?? _server.Port, security, user, "chronicle@example.com", "Chronicle Server", "https://c.example.com", password is not null), password);

        [Fact]
        public async Task DeliversTheMessage_ToTheRightPeople_WithTheRightContent()
        {
            await _sender.SendAsync(new EmailMessage("alice@example.com", "Reset your Chronicle password", "Open https://c.example.com/reset-password#token=ABC"), Credentials());
            await _server.Done.Task.WaitAsync(TimeSpan.FromSeconds(10));

            _server.MailFrom.Should().Contain("chronicle@example.com");
            _server.Recipients.Should().ContainSingle().Which.Should().Contain("alice@example.com");
            _server.Data.Should().Contain("Subject: Reset your Chronicle password")
                .And.Contain("From: Chronicle Server <chronicle@example.com>")
                .And.Contain("To: alice@example.com")
                .And.Contain("reset-password#token=ABC");
        }

        [Fact]
        public async Task ABadRecipientAddress_IsReportedPlainly()
        {
            var act = () => _sender.SendAsync(new EmailMessage("not an address", "s", "b"), Credentials());

            (await act.Should().ThrowAsync<Exception>()).Which.Should().Match<Exception>(e => e is EmailSendException || e is FormatException);
        }

        [Fact]
        public async Task AServerThatRefusesTheRecipient_GivesAnExplainedFailure()
        {
            _server.RejectRecipientWith = "550 5.1.1 mailbox unavailable";

            var act = () => _sender.SendAsync(new EmailMessage("ghost@example.com", "s", "b"), Credentials());

            (await act.Should().ThrowAsync<EmailSendException>()).Which.Message.Should().Contain("refused");
        }

        [Fact]
        public async Task AServerThatIsNotListening_SaysSo_AndNamesWhereItTried()
        {
            var closedPort = FreePort();

            var act = () => _sender.SendAsync(new EmailMessage("a@example.com", "s", "b"), Credentials(port: closedPort));

            (await act.Should().ThrowAsync<EmailSendException>()).Which.Message.Should().Contain($"127.0.0.1:{closedPort}");
        }

        [Fact]
        public async Task WrongCredentials_AreReportedAsSuch_NotAsAGenericFailure()
        {
            _server.AdvertiseAuth = true;

            var act = () => _sender.SendAsync(new EmailMessage("a@example.com", "s", "b"), Credentials(user: "me", password: "wrong"));

            var ex = (await act.Should().ThrowAsync<EmailSendException>()).Which;
            ex.Message.Should().Contain("user name or password");
            ex.Message.Should().NotContain("wrong", "the password is never echoed");
        }

        [Fact]
        public async Task NotConfigured_IsRefusedBeforeAnyConnection()
        {
            var act = () => _sender.SendAsync(new EmailMessage("a@example.com", "s", "b"), new SmtpCredentials(EmailSettings.Empty, null));

            (await act.Should().ThrowAsync<EmailSendException>()).Which.Message.Should().Contain("not set up");
        }

        [Fact]
        public async Task ASecureConnectionToAServerThatDoesNotSpeakTls_FailsWithAHelpfulHint()
        {
            var act = () => _sender.SendAsync(new EmailMessage("a@example.com", "s", "b"), Credentials(security: SmtpSecurity.Tls));

            (await act.Should().ThrowAsync<EmailSendException>()).Which.Message.Should().ContainAny("secure connection", "Security setting", "did not respond", "connection");
        }

        [Fact]
        public async Task Cancellation_IsNotDisguisedAsAMailFailure()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            var act = () => _sender.SendAsync(new EmailMessage("a@example.com", "s", "b"), Credentials(), cts.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
        }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var port = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }
    }
}
