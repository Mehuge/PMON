using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PMON.Data;
using PMON.Models;

namespace PMON.ViewModels;

/// <summary>All instances (PIDs) of one process name, shown as a single row in the sidebar.</summary>
public sealed partial class ProcessGroupViewModel(string name) : ObservableObject
{
    [ObservableProperty]
    private string _metrics = "—";

    [ObservableProperty]
    private string _activity = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInstances))]
    [NotifyPropertyChangedFor(nameof(Chevron))]
    private bool _isExpanded;

    public string Name { get; } = name;
    public ObservableCollection<ProcessItemViewModel> Items { get; } = [];

    public bool HasMany => Items.Count > 1;
    public bool ShowInstances => IsExpanded && HasMany;
    public string Chevron => IsExpanded ? "▾" : "▸";
    public string Subtitle => Items.Count == 1 ? $"PID {Items[0].Pid}" : $"{Items.Count} instances";
    public IBrush? Brush => Items.Count > 0 ? Items[0].Brush : null;
    public bool IsSelected => Items.Any(i => i.IsSelected);

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
    }

    public void Remove(ProcessItemViewModel item)
    {
        item.PropertyChanged -= OnItemChanged;
        Items.Remove(item);
        NotifyMembership();
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

        var running = Items.Select(i => i.Sample).OfType<ProcessSample>().ToList();
        Activity = $"{running.Count} of {Items.Count} running";
        Metrics = running.Count == 0
            ? "—"
            : $"CPU {ValueFormatter.Format(ValueKind.Percent, running.Sum(s => s.CpuPercent))}   Mem {ValueFormatter.Format(ValueKind.Megabytes, running.Sum(s => s.WorkingSetMb))}   Threads {running.Sum(s => s.Threads)}";
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

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProcessItemViewModel.IsVisible))
            OnPropertyChanged(nameof(IsVisible));

        if (e.PropertyName == nameof(ProcessItemViewModel.IsSelected))
            OnPropertyChanged(nameof(IsSelected));
    }
}
