namespace Chronicle.Services
{
    public interface IFileScanService
    {
        Task<FileScanSummary> ScanAsync(FileScanRequest request, int userId, CancellationToken ct = default);
        Task<(bool Available, string[] SupportedMediaTypeNames)> GetStatusAsync();
    }
}
