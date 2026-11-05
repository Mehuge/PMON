using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PMON.ViewModels;

/// <summary>All instances (PIDs) of one process name, shown as a single row in the sidebar.</summary>
public sealed partial class ProcessGroupViewModel : ObservableObject
{
    [ObservableProperty]
    private string _metrics = "—";

    [ObservableProperty]
    private string _activity = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInstances))]
    [NotifyPropertyChangedFor(nameof(Chevron))]
    private bool _isExpanded;

    /// <summary>Chart every instance on its own line instead of one combined line.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    private bool _isSplit;

    public ProcessGroupViewModel(string name, ProcessItemViewModel aggregate)
    {
        Name = name;
        Aggregate = aggregate;
        aggregate.PropertyChanged += OnItemChanged;
    }

    public string Name { get; }

    /// <summary>The combined view of every instance; only offered once there is more than one.</summary>
    public ProcessItemViewModel Aggregate { get; }
    public ObservableCollection<ProcessItemViewModel> Items { get; } = [];

    public bool HasMany => Items.Count > 1;
    public bool ShowInstances => IsExpanded && HasMany;
    public string Chevron => IsExpanded ? "▾" : "▸";
    public string Subtitle => Items.Count == 1 ? $"PID {Items[0].Pid}" : $"{Items.Count} instances · {(IsSplit ? "separate" : "combined")}";
    public IBrush? Brush => HasMany ? Aggregate.Brush : Items.Count > 0 ? Items[0].Brush : null;
    public bool IsSelected => Aggregate.IsSelected || Items.Any(i => i.IsSelected);

    /// <summary>True when every instance is drawn, false when none is, null when mixed.</summary>
    public bool? IsVisible
    {
        get
        {
            if (Items.All(i => i.IsVisible))
                return true;

            return Items.All(i => !i.IsVisible) ? false : null;
        }
        set
        {
            foreach (var item in Items)
                item.IsVisible = value == true;
        }
    }

    public void Add(ProcessItemViewModel item)
    {
        var index = 0;
        while (index < Items.Count && Items[index].Pid < item.Pid)
            index++;

        Items.Insert(index, item);
        item.PropertyChanged += OnItemChanged;
        NotifyMembership();
        ApplyMode();
    }

    public void Remove(ProcessItemViewModel item)
    {
        item.PropertyChanged -= OnItemChanged;
        Items.Remove(item);
        NotifyMembership();
        ApplyMode();
    }

    public void Update(DateTime time, double gapSeconds)
    {
        foreach (var item in Items)
            item.Update(time, gapSeconds);

        if (Items.Count == 1)
        {
            Metrics = Items[0].Metrics;
            Activity = Items[0].Activity;
            return;
        }

        Aggregate.Update(time, gapSeconds);
        Metrics = Aggregate.Metrics;
        Activity = Aggregate.Sample is { } sample ? $"{sample.Instances} of {Items.Count} running" : $"0 of {Items.Count} running";
    }

    [RelayCommand]
    private void Activate()
    {
        if (Items.Count == 1)
        {
            Items[0].Select();
            return;
        }

        IsExpanded = !IsExpanded;
    }

    private void NotifyMembership()
    {
        OnPropertyChanged(nameof(HasMany));
        OnPropertyChanged(nameof(ShowInstances));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(Brush));
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(IsSelected));
    }

    partial void OnIsSplitChanged(bool value) => ApplyMode();

    /// <summary>Decides whether charts draw the combined line or one line per instance.</summary>
    private void ApplyMode()
    {
        var combined = HasMany && !IsSplit;
        foreach (var item in Items)
            item.Collapsed = combined;

        Aggregate.IsVisible = combined && Items.Any(i => i.IsVisible);
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProcessItemViewModel.IsVisible) && sender != Aggregate)
        {
            OnPropertyChanged(nameof(IsVisible));
            ApplyMode();
        }

        if (e.PropertyName == nameof(ProcessItemViewModel.IsSelected))
            OnPropertyChanged(nameof(IsSelected));
    }
}
