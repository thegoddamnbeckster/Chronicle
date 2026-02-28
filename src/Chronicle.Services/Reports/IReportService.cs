using Chronicle.Plugins;

namespace Chronicle.Services.Reports;

public interface IReportService
{
    /// <summary>Returns all available reports (built-in + loaded plugin reports).</summary>
    IReadOnlyList<ReportDefinition> GetAllReports();

    /// <summary>
    /// Runs the report identified by <paramref name="reportId"/> for the given user.
    /// </summary>
    /// <param name="reportId">ID from <see cref="GetAllReports"/>.</param>
    /// <param name="parameters">Optional key-value parameters (e.g. "days", "limit").</param>
    /// <param name="userId">Chronicle user whose data to analyse.</param>
    Task<ReportResult> RunReportAsync(
        string reportId,
        IReadOnlyDictionary<string, string> parameters,
        int userId,
        CancellationToken ct = default);
}
