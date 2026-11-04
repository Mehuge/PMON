using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PMON.Models;
using PMON.Monitoring;

namespace PMON.ViewModels;

/// <summary>Edits the tracking rules and shows, against the processes running right now, what they match.</summary>
public sealed partial class RulesViewModel : ObservableObject
{
    private const int PreviewNames = 8;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private Dictionary<string, int> _running = new(StringComparer.OrdinalIgnoreCase);
    private ProcessMatcher? _draft;
    private string _draftPattern = string.Empty;
    private bool _refreshing;

    [ObservableProperty]
    private string _newPattern = string.Empty;

    [ObservableProperty]
    private string _error = string.Empty;

    [ObservableProperty]
    private string _preview = string.Empty;

    public RulesViewModel(IEnumerable<TrackingRule> rules)
    {
        foreach (var rule in rules)
            Attach(new RuleViewModel(rule));

        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    public event Action? Changed;

    public ObservableCollection<RuleViewModel> Items { get; } = [];
    public ObservableCollection<string> TrackedNow { get; } = [];

    /// <summary>Names of the processes running now, offered as completions while typing a pattern.</summary>
    public IReadOnlyList<string> Suggestions { get; private set; } = [];

    public bool HasItems => Items.Count > 0;
    public bool HasTrackedNow => TrackedNow.Count > 0;
    public bool HasTracking => Items.Any(r => r.IsEnabled && !r.IsExclude);

    public List<TrackingRule> ToRules() => Items.Select(r => r.ToRule()).ToList();

    public void Activate()
    {
        _timer.Start();
        _ = RefreshAsync();
    }

    public void Deactivate() => _timer.Stop();

    [RelayCommand]
    private void AddTrack() => Add(exclude: false);

    [RelayCommand]
    private void AddExclude() => Add(exclude: true);

    [RelayCommand]
    private void Remove(RuleViewModel rule)
    {
        rule.PropertyChanged -= OnRuleChanged;
        Items.Remove(rule);
        OnRulesChanged();
    }

    partial void OnNewPatternChanged(string value)
    {
        var pattern = value.Trim();
        _draftPattern = pattern;
        _draft = ProcessMatcher.ForPattern(pattern);
        Error = pattern.Length > 0 && _draft is null ? "Invalid regular expression" : string.Empty;
        UpdatePreview();
    }

    private void Add(bool exclude)
    {
        var pattern = NewPattern.Trim();
        if (_draft is null)
            return;

        NewPattern = string.Empty;
        if (Items.Any(r => r.IsExclude == exclude && r.Pattern.Equals(pattern, StringComparison.OrdinalIgnoreCase)))
            return;

        Attach(new RuleViewModel(new TrackingRule { Pattern = pattern, Exclude = exclude }));
        OnRulesChanged();
    }

    private void Attach(RuleViewModel rule)
    {
        rule.PropertyChanged += OnRuleChanged;
        Items.Add(rule);
    }

    private void OnRuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RuleViewModel.IsEnabled))
            OnRulesChanged();
    }

    private void OnRulesChanged()
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasTracking));
        Recompute();
        Changed?.Invoke();
    }

    private async Task RefreshAsync()
    {
        if (_refreshing)
            return;

        _refreshing = true;
        try
        {
            _running = await Task.Run(ReadRunning);
            UpdateSuggestions();
            Recompute();
        }
        finally
        {
            _refreshing = false;
        }
    }

    private static Dictionary<string, int> ReadRunning()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == 0)
                        continue;

                    var name = process.ProcessName;
                    counts[name] = counts.GetValueOrDefault(name) + 1;
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
                {
                    // Process exited while being read.
                }
            }
        }

        return counts;
    }

    private void UpdateSuggestions()
    {
        var names = _running.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (Suggestions.SequenceEqual(names))
            return;

        Suggestions = names;
        OnPropertyChanged(nameof(Suggestions));
    }

    private void Recompute()
    {
        foreach (var rule in Items)
            rule.MatchCount = CountMatches(rule.Matcher);

        UpdateTrackedNow();
        UpdatePreview();
    }

    private int CountMatches(ProcessMatcher matcher) => _running.Where(p => matcher.IsMatch(p.Key)).Sum(p => p.Value);

    private void UpdateTrackedNow()
    {
        var active = Items.Where(r => r.IsEnabled).ToList();
        var tracked = _running
            .Where(p => active.Any(r => !r.IsExclude && r.Matcher.IsMatch(p.Key)) && !active.Any(r => r.IsExclude && r.Matcher.IsMatch(p.Key)))
            .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(Describe)
            .ToList();

        if (TrackedNow.SequenceEqual(tracked))
            return;

        TrackedNow.Clear();
        foreach (var entry in tracked)
            TrackedNow.Add(entry);

        OnPropertyChanged(nameof(HasTrackedNow));
    }

    private void UpdatePreview()
    {
        if (_draft is null)
        {
            Preview = string.Empty;
            return;
        }

        var matches = _running
            .Where(p => _draft.IsMatch(p.Key))
            .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (matches.Count == 0)
        {
            Preview = DescribeNoMatch();
            return;
        }

        var total = matches.Sum(p => p.Value);
        var names = string.Join(", ", matches.Take(PreviewNames).Select(Describe));
        var more = matches.Count > PreviewNames ? $", +{matches.Count - PreviewNames} more" : string.Empty;
        Preview = $"Matches {total} running {(total == 1 ? "process" : "processes")}: {names}{more}";
    }

    private string DescribeNoMatch()
    {
        const string Fallback = "No running process matches. It will still apply to any that start later.";
        if (_draftPattern.IndexOfAny(['*', '?']) >= 0 || _draftPattern.StartsWith("re:", StringComparison.OrdinalIgnoreCase))
            return Fallback;

        var similar = _running.Keys
            .Where(n => n.Contains(_draftPattern, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (similar.Count == 0)
            return Fallback;

        var names = string.Join(", ", similar.Take(PreviewNames));
        return $"No process is named exactly \"{_draftPattern}\". Use *{_draftPattern}* to match {names}, or pick a name from the list.";
    }

    private static string Describe(KeyValuePair<string, int> process) =>
        process.Value == 1 ? process.Key : $"{process.Key} ×{process.Value}";
}
