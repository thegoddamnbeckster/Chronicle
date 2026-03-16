using Chronicle.Core.Models.Scan;

namespace Chronicle.Services.Scan
{
    public interface IScanGroupingService
    {
        ScanGroupResult Group(IEnumerable<string> filePaths, string scanRoot, int hierarchyLevels);
    }
}
