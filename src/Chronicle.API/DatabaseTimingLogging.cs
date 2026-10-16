using System.Data.Common;
using Chronicle.Services;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Serilog;

namespace Chronicle.API;

/// <summary>
/// Says WHEN a slow or failed database command started, how long it ran against which timeouts, and which scheduled job it
/// belonged to. The log used to show only the failure ("database is locked") at the moment it gave up, with no way to tell
/// when the wait began or who was holding the lock.
/// </summary>
internal sealed class SlowCommandLogger : DbCommandInterceptor
{
    /// <summary>A command that takes this long was waiting for something, almost always another writer.</summary>
    internal static readonly TimeSpan SlowThreshold = TimeSpan.FromSeconds(2);

    private static readonly Serilog.ILogger Log = Serilog.Log.ForContext<SlowCommandLogger>();

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    { LogIfSlow(command, eventData); return result; }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
    { LogIfSlow(command, eventData); return new(result); }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    { LogIfSlow(command, eventData); return result; }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    { LogIfSlow(command, eventData); return new(result); }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    { LogIfSlow(command, eventData); return result; }

    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    { LogIfSlow(command, eventData); return new(result); }

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) => LogFailure(command, eventData);

    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    { LogFailure(command, eventData); return Task.CompletedTask; }

    private static void LogIfSlow(DbCommand command, CommandExecutedEventData eventData)
    {
        if (eventData.Duration < SlowThreshold) return;
        Log.Warning(
            "Slow database command: {Seconds:F1}s (started {Start:HH:mm:ss.fff} UTC, finished {End:HH:mm:ss.fff} UTC) in {Task}: {Sql}",
            eventData.Duration.TotalSeconds, eventData.StartTime.UtcDateTime, eventData.StartTime.UtcDateTime + eventData.Duration,
            Describe(TaskGate.CurrentTaskId), Summarise(command.CommandText));
    }

    private static void LogFailure(DbCommand command, CommandErrorEventData eventData)
    {
        Log.Error(
            "Database command failed after {Seconds:F1}s (started {Start:HH:mm:ss.fff} UTC, gave up {End:HH:mm:ss.fff} UTC; command timeout {Timeout}s, SQLite busy timeout {Busy} ms) in {Task}: {Message} -- {Sql}",
            eventData.Duration.TotalSeconds, eventData.StartTime.UtcDateTime, eventData.StartTime.UtcDateTime + eventData.Duration,
            command.CommandTimeout, SqliteBusyTimeoutInterceptor.BusyTimeoutMs, Describe(TaskGate.CurrentTaskId),
            eventData.Exception.GetBaseException().Message, Summarise(command.CommandText));
    }

    internal static string Describe(string? taskId) => taskId is null ? "no scheduled job (a request or a scan)" : $"job '{taskId}'";

    private static string Summarise(string sql)
    {
        var oneLine = string.Join(' ', sql.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return oneLine.Length <= 160 ? oneLine : oneLine[..160] + "...";
    }
}

/// <summary>
/// A transaction holds SQLite's one write lock from its first write until it ends. One held for seconds is what makes every
/// other writer wait, so say when it began, how long it lasted and which job held it.
/// </summary>
internal sealed class LongTransactionLogger : DbTransactionInterceptor
{
    internal static readonly TimeSpan LongThreshold = TimeSpan.FromSeconds(5);
    private static readonly Serilog.ILogger Log = Serilog.Log.ForContext<LongTransactionLogger>();

    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    { LogIfLong(eventData, "committed"); return Task.CompletedTask; }

    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData) => LogIfLong(eventData, "committed");

    public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    { LogIfLong(eventData, "rolled back"); return Task.CompletedTask; }

    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData) => LogIfLong(eventData, "rolled back");

    private static void LogIfLong(TransactionEndEventData eventData, string how)
    {
        if (eventData.Duration < LongThreshold) return;
        Log.Warning(
            "Long database transaction {How} after {Seconds:F1}s (opened {Start:HH:mm:ss.fff} UTC) in {Task} -- every other writer waited on it",
            how, eventData.Duration.TotalSeconds, eventData.StartTime.UtcDateTime, SlowCommandLogger.Describe(TaskGate.CurrentTaskId));
    }
}
