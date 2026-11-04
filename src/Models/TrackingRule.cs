namespace PMON.Models;

/// <summary>A process name pattern that either adds matching processes to the tracked set or removes them from it.</summary>
public sealed class TrackingRule
{
    public string Pattern { get; set; } = string.Empty;
    public bool Exclude { get; set; }
    public bool Enabled { get; set; } = true;
}
