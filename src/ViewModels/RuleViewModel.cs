using CommunityToolkit.Mvvm.ComponentModel;
using PMON.Models;
using PMON.Monitoring;

namespace PMON.ViewModels;

public sealed partial class RuleViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchText))]
    private bool _isEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MatchText))]
    private int _matchCount;

    public RuleViewModel(TrackingRule rule)
    {
        Pattern = rule.Pattern;
        IsExclude = rule.Exclude;
        _isEnabled = rule.Enabled;
        Matcher = ProcessMatcher.ForPattern(rule.Pattern) ?? ProcessMatcher.None;
    }

    public string Pattern { get; }
    public bool IsExclude { get; }
    public ProcessMatcher Matcher { get; }
    public string Kind => IsExclude ? "EXCLUDE" : "TRACK";
    public string MatchText => IsEnabled ? $"{MatchCount} running" : "off";

    public TrackingRule ToRule() => new() { Pattern = Pattern, Exclude = IsExclude, Enabled = IsEnabled };
}
