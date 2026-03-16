namespace Chronicle.Core.Models;

public class BackgroundTask
{
    public string TaskId           { get; set; } = string.Empty;
    public string DisplayName      { get; set; } = string.Empty;
    public string Description      { get; set; } = string.Empty;
    public string CronExpression   { get; set; } = string.Empty;
    public bool   IsEnabled        { get; set; } = true;
    public DateTime? LastRunAt     { get; set; }
    public bool?  LastRunSucceeded { get; set; }
    public string? LastErrorMessage{ get; set; }
    public DateTime? NextRunAt     { get; set; }
}
