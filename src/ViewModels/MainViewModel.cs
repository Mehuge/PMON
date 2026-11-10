using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PMON.Data;
using PMON.Models;
using PMON.Monitoring;
using PMON.Recording;
using PMON.Services;

namespace PMON.ViewModels;

public sealed record IntervalOption(string Label, double Seconds);

/// <summary>A limit choice; a value of 0 means unlimited.</summary>
public sealed record LimitOption(string Label, double Value)
{
    public override string ToString() => Label;
}

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan TrimSlack = TimeSpan.FromMinutes(1);
    private const int ProcessTabIndex = 1;
    private static readonly TimeSpan MinimumRateWindow = TimeSpan.FromSeconds(30);

    public static readonly IReadOnlyList<int> TopCountOptions = [1, 3, 5, 10];

    public static readonly IReadOnlyList<LimitOption> RetentionOptions =
    [
        new("30 minutes", 0.5), new("1 hour", 1), new("2 hours", 2), new("6 hours", 6),
        new("12 hours", 12), new("24 hours", 24), new("Unlimited", 0),
    ];

    public static readonly IReadOnlyList<LimitOption> TraceSizeOptions =
    [
        new("50 MB", 50), new("100 MB", 100), new("250 MB", 250), new("500 MB", 500), new("1 GB", 1024), new("Unlimited", 0),
    ];

    public static readonly IReadOnlyList<LimitOption> KeepTraceOptions =
    [
        new("2 files", 2), new("5 files", 5), new("10 files", 10), new("20 files", 20), new("All", 0),
    ];

    public static readonly IReadOnlyList<IntervalOption> IntervalOptions =
    [
        new("0.5 s", 0.5), new("1 s", 1), new("2 s", 2), new("5 s", 5), new("10 s", 10), new("30 s", 30), new("1 min", 60),
    ];

    private readonly AppSettings _settings;
    private readonly MonitorService _monitor = new();
    private readonly SampleStore _liveStore = new();
    private readonly Dictionary<string, ProcessItemViewModel> _items = new();
    private ProcessItemViewModel? _untrackedItem;
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private RollingRecorder? _recorder;
    private SampleStore? _loadedStore;
    private bool _dirty = true;
    private bool _settingsDirty;
    private bool _restoringSelection;
    private string? _pendingSelection;

    [ObservableProperty]
    private IntervalOption _selectedInterval;

    [ObservableProperty]
    private LimitOption _selectedRetention;

    [ObservableProperty]
    private LimitOption _selectedTraceSize;

    [ObservableProperty]
    private LimitOption _selectedKeepTraces;

    [ObservableProperty]
    private SpanOption _selectedSpan;

    [ObservableProperty]
    private string _recordingFolder;

    [ObservableProperty]
    private bool _autoRecord;

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private string _recordingStatus = "Not recording";

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _sourceLabel = "Live";

    [ObservableProperty]
    private string _cpuText = "—";

    [ObservableProperty]
    private string _memoryText = "—";

    [ObservableProperty]
    private string _diskText = "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private ProcessItemViewModel? _selectedProcess;

    [ObservableProperty]
    private int _selectedTab;

    [ObservableProperty]
    private bool _captureTop;

    [ObservableProperty]
    private int _selectedTopCount;

    [ObservableProperty]
    private double _cpuValue;

    [ObservableProperty]
    private double _memoryValue;

    [ObservableProperty]
    private double _commitValue;

    [ObservableProperty]
    private double _diskValue;

    [ObservableProperty]
    private string _commitText = "—";

    [ObservableProperty]
    private string _recordingElapsed = "00:00:00";

    [ObservableProperty]
    private string _recordingRows = "0";

    [ObservableProperty]
    private string _recordingSize = "0 MB";

    [ObservableProperty]
    private string _freeSpaceText = string.Empty;

    public MainViewModel(AppSettings settings)
    {
        _settings = settings;
        Viewport = new Viewport(_liveStore);

        _selectedInterval = IntervalOptions.FirstOrDefault(o => o.Seconds == settings.IntervalSeconds) ?? IntervalOptions[1];
        _selectedRetention = PickLimit(RetentionOptions, settings.MemoryRetentionHours, 6);
        _selectedTraceSize = PickLimit(TraceSizeOptions, settings.TraceSizeMb, 250);
        _selectedKeepTraces = PickLimit(KeepTraceOptions, settings.KeepTraces, 0);
        _selectedSpan = Viewport.SpanOptions.FirstOrDefault(o => o.Span.TotalSeconds == settings.SpanSeconds) ?? Viewport.SpanOptions[4];
        _recordingFolder = settings.RecordingFolder;
        _autoRecord = settings.AutoRecord;
        _captureTop = settings.CaptureTopProcesses;
        _selectedTopCount = TopCountOptions.Contains(settings.TopCount) ? settings.TopCount : 3;
        _pendingSelection = settings.SelectedProcessName;
        Viewport.Span = _selectedSpan.Span;
        _freeSpaceText = DescribeFreeSpace(0);

        Consumers =
        [
            new("CPU", ValueKind.Percent, p => p.CpuPercent, OpenConsumer),
            new("Memory", ValueKind.Megabytes, p => p.WorkingSetMb, OpenConsumer),
            new("Disk", ValueKind.BytesPerSecond, p => p.IoReadBytesPerSec + p.IoWriteBytesPerSec, OpenConsumer),
        ];

        Rules = new RulesViewModel(settings.Rules);
        Rules.Changed += OnRulesChanged;

        OverviewCharts = BuildOverviewCharts();
        CpuCharts = BuildCpuCharts();
        MemoryCharts = BuildMemoryCharts();
        DiskCharts = BuildDiskCharts();
        ResourceCharts = BuildResourceCharts();
        DetailCharts = BuildDetailCharts();
        TopCharts = BuildTopCharts();

        Viewport.PropertyChanged += (_, e) =>
        {
            _dirty = true;
            if (e.PropertyName is nameof(Viewport.IsLive) or nameof(Viewport.FollowLive))
            {
                OnPropertyChanged(nameof(IsLive));
                OnPropertyChanged(nameof(CanGoLive));
            }

            if (e.PropertyName == nameof(Viewport.Span))
                SyncSelectedSpan();
        };

        _refreshTimer.Tick += (_, _) => OnRefreshTick();
        _monitor.SnapshotReady += OnSnapshotReady;
    }

    public Viewport Viewport { get; }
    public RulesViewModel Rules { get; }
    public ObservableCollection<ProcessGroupViewModel> Groups { get; } = [];

    public IReadOnlyList<ChartViewModel> OverviewCharts { get; }
    public IReadOnlyList<ChartViewModel> CpuCharts { get; }
    public IReadOnlyList<ChartViewModel> MemoryCharts { get; }
    public IReadOnlyList<ChartViewModel> DiskCharts { get; }
    public IReadOnlyList<ChartViewModel> ResourceCharts { get; }
    public IReadOnlyList<ChartViewModel> DetailCharts { get; }
    public IReadOnlyList<ChartViewModel> TopCharts { get; }

    public IReadOnlyList<ConsumerListViewModel> Consumers { get; }

    public bool HasSelection => SelectedProcess is not null;

    public bool IsLive => Viewport.IsLive;
    public bool CanGoLive => Viewport.IsLive && !Viewport.FollowLive;
    public bool HasRules => Rules.HasTracking;
    public bool HasProcesses => Groups.Count > 0;
    public string ProcessesLabel => $"Processes ({Groups.Count}) ▾";

    public string RulesSummary
    {
        get
        {
            var active = Rules.Items.Where(r => r.IsEnabled).ToList();
            if (active.Count == 0)
                return "No active rules";

            var tracked = active.Count(r => !r.IsExclude);
            var excluded = active.Count - tracked;
            var summary = tracked == 1 ? "1 pattern tracked" : $"{tracked} patterns tracked";
            return excluded == 0 ? summary : $"{summary}, {excluded} excluded";
        }
    }

    public void Start()
    {
        ApplyMonitorSettings();
        _monitor.Start();
        _refreshTimer.Start();

        if (AutoRecord)
            StartRecording();
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
        _monitor.SnapshotReady -= OnSnapshotReady;
        _monitor.Dispose();
        StopRecording();
        SaveSettings();
    }

    [RelayCommand]
    private void ToggleRecording()
    {
        if (IsRecording)
        {
            StopRecording();
            return;
        }

        StartRecording();
    }

    [RelayCommand]
    private void BackToLive()
    {
        _loadedStore = null;
        SourceLabel = "Live";
        Message = string.Empty;
        ShowStore(_liveStore, live: true);
    }

    [RelayCommand]
    private void GoLive() => Viewport.ResumeFollowing();

    [RelayCommand]
    private void ShowAllProcesses() => SetAllVisible(true);

    [RelayCommand]
    private void HideAllProcesses() => SetAllVisible(false);

    [RelayCommand]
    private void OpenRecordingFolder()
    {
        try
        {
            Directory.CreateDirectory(RecordingFolder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{RecordingFolder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            Message = $"Cannot open folder: {ex.Message}";
        }
    }

    public async Task LoadRecordingAsync(string path)
    {
        IsBusy = true;
        Message = string.Empty;
        try
        {
            var store = await Task.Run(() => RecordingLoader.Load(path));
            if (store.IsEmpty)
            {
                Message = "The recording contains no samples.";
                return;
            }

            _loadedStore = store;
            SourceLabel = Path.GetFileName(path);
            ShowStore(store, live: false);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Message = $"Could not open recording: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnSelectedProcessChanged(ProcessItemViewModel? oldValue, ProcessItemViewModel? newValue)
    {
        if (oldValue is not null)
            oldValue.IsSelected = false;

        if (newValue is not null)
            newValue.IsSelected = true;
    }

    partial void OnSelectedProcessChanged(ProcessItemViewModel? value)
    {
        _monitor.Pin(value is { IsUntracked: true } ? value.Pid : 0);

        if (!ReferenceEquals(value, _untrackedItem))
            _untrackedItem = null;

        Viewport.SelectedKey = value?.Key;
        if (value is null)
            return;

        _settings.SelectedProcessName = value.Name;
        _settingsDirty = true;
        _dirty = true;

        if (_restoringSelection)
            return;

        _pendingSelection = null;
        SelectedTab = ProcessTabIndex;
    }

    partial void OnSelectedIntervalChanged(IntervalOption value)
    {
        _settings.IntervalSeconds = value.Seconds;
        ApplyMonitorSettings();
        SaveSettings();
    }

    partial void OnSelectedSpanChanged(SpanOption value)
    {
        if (Viewport.Span != value.Span)
            Viewport.SetSpan(value.Span);

        _settings.SpanSeconds = value.Span.TotalSeconds;
    }

    partial void OnRecordingFolderChanged(string value)
    {
        _settings.RecordingFolder = value;
        if (!IsRecording)
            FreeSpaceText = DescribeFreeSpace(0);
    }

    partial void OnAutoRecordChanged(bool value)
    {
        _settings.AutoRecord = value;
        SaveSettings();
    }

    partial void OnCaptureTopChanged(bool value)
    {
        _settings.CaptureTopProcesses = value;
        ApplyMonitorSettings();
        SaveSettings();
    }

    partial void OnSelectedTopCountChanged(int value)
    {
        _settings.TopCount = value;
        ApplyMonitorSettings();
        SaveSettings();
    }

    private void SyncSelectedSpan()
    {
        var match = Viewport.SpanOptions.FirstOrDefault(o => o.Span == Viewport.Span);
        if (match is not null)
            SelectedSpan = match;
    }

    private void OnRulesChanged()
    {
        OnPropertyChanged(nameof(HasRules));
        OnPropertyChanged(nameof(RulesSummary));
        ApplyMonitorSettings();
        SaveSettings();
    }

    private void ApplyMonitorSettings() =>
        _monitor.Configure(Rules.ToRules(), TimeSpan.FromSeconds(SelectedInterval.Seconds), CaptureTop ? SelectedTopCount : 0);

    private void SaveSettings()
    {
        _settings.Rules = Rules.ToRules();
        _settings.Save();
    }

    private static LimitOption PickLimit(IReadOnlyList<LimitOption> options, double value, double fallback) =>
        options.FirstOrDefault(o => o.Value == value) ?? options.First(o => o.Value == fallback);

    partial void OnSelectedRetentionChanged(LimitOption value)
    {
        _settings.MemoryRetentionHours = value.Value;
        SaveSettings();
    }

    partial void OnSelectedTraceSizeChanged(LimitOption value)
    {
        _settings.TraceSizeMb = (int)value.Value;
        ApplyRecordingLimits();
        SaveSettings();
    }

    partial void OnSelectedKeepTracesChanged(LimitOption value)
    {
        _settings.KeepTraces = (int)value.Value;
        ApplyRecordingLimits();
        SaveSettings();
    }

    private long TraceSizeBytes => (long)(SelectedTraceSize.Value * 1048576);

    private void ApplyRecordingLimits() =>
        Volatile.Read(ref _recorder)?.SetLimits(TraceSizeBytes, (int)SelectedKeepTraces.Value);

    private void StartRecording()
    {
        try
        {
            var recorder = new RollingRecorder(RecordingFolder, TraceSizeBytes, (int)SelectedKeepTraces.Value);
            Volatile.Write(ref _recorder, recorder);
            IsRecording = true;
            UpdateRecordingStats(recorder);
            Message = string.Empty;
            SaveSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            Message = $"Cannot start recording: {ex.Message}";
        }
    }

    private void StopRecording()
    {
        var recorder = Interlocked.Exchange(ref _recorder, null);
        recorder?.Dispose();
        IsRecording = false;
        RecordingStatus = recorder is null
            ? "Not recording"
            : $"Saved {Path.GetFileName(recorder.Path)}";
        FreeSpaceText = DescribeFreeSpace(0);
    }

    private void OnSnapshotReady(Snapshot snapshot)
    {
        Volatile.Read(ref _recorder)?.Write(snapshot);
        Dispatcher.UIThread.Post(() => OnSnapshotOnUi(snapshot));
    }

    private void OnSnapshotOnUi(Snapshot snapshot)
    {
        _liveStore.Add(snapshot);
        TrimLiveStore(snapshot.Time);

        if (ReferenceEquals(Viewport.Store, _liveStore))
            Viewport.DataAppended();
    }

    private void TrimLiveStore(DateTime now)
    {
        if (SelectedRetention.Value <= 0)
            return;

        var keep = TimeSpan.FromHours(SelectedRetention.Value);
        if (_liveStore.Start >= now - keep - TrimSlack)
            return;

        _liveStore.TrimBefore(now - keep);
    }

    private void ShowStore(SampleStore store, bool live)
    {
        if (_untrackedItem is not null)
            SelectedProcess = null;

        _items.Clear();
        Groups.Clear();
        OnPropertyChanged(nameof(HasProcesses));
        OnPropertyChanged(nameof(ProcessesLabel));
        Viewport.Show(store, live);
    }

    private void SetAllVisible(bool visible)
    {
        foreach (var item in _items.Values)
            item.IsVisible = visible;
    }

    private void OnRefreshTick()
    {
        if (_settingsDirty)
        {
            _settingsDirty = false;
            SaveSettings();
        }

        RefreshRecording();

        if (!_dirty)
            return;

        _dirty = false;
        SyncProcessList();
        UpdateReadouts();
    }

    private void RefreshRecording()
    {
        if (_recorder is not { } recorder)
            return;

        if (recorder.Error is { } error)
        {
            StopRecording();
            Message = $"Recording stopped: {error.Message}";
            return;
        }

        UpdateRecordingStats(recorder);
    }

    private void UpdateRecordingStats(RollingRecorder recorder)
    {
        var elapsed = DateTime.Now - recorder.Started;
        var sizeMb = recorder.Size / 1048576.0;

        // Pruning bounds disk use, so a growth-rate projection would mislead.
        var bounded = SelectedKeepTraces.Value > 0 && SelectedTraceSize.Value > 0;
        var rate = elapsed < MinimumRateWindow || bounded ? 0 : sizeMb / elapsed.TotalSeconds;

        RecordingStatus = $"Recording to {Path.GetFileName(recorder.Path)}";
        RecordingElapsed = $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
        RecordingRows = recorder.Rows.ToString("N0");
        RecordingSize = ValueFormatter.Format(ValueKind.Megabytes, sizeMb);
        FreeSpaceText = DescribeFreeSpace(rate);
    }

    private string DescribeFreeSpace(double megabytesPerSecond)
    {
        var free = FreeSpaceMb();
        if (free is null)
            return string.Empty;

        var text = $"{ValueFormatter.Format(ValueKind.Megabytes, free.Value)} free";
        if (megabytesPerSecond <= 0)
            return text;

        return $"{text}  ·  room for ~{FormatDuration(TimeSpan.FromSeconds(free.Value / megabytesPerSecond))}";
    }

    private double? FreeSpaceMb()
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(RecordingFolder));
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace / 1048576.0;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string FormatDuration(TimeSpan span)
    {
        if (span.TotalDays >= 365)
            return "over a year";
        if (span.TotalDays >= 2)
            return $"{span.TotalDays:0} days";
        if (span.TotalHours >= 1)
            return $"{span.TotalHours:0.#} h";

        return $"{Math.Max(1, span.TotalMinutes):0} min";
    }

    private void SyncProcessList()
    {
        var store = Viewport.Store;
        var start = Viewport.ViewStart;
        var end = Viewport.ViewEnd;

        var wanted = store.Series
            .Where(s => s.Samples.Count > 0 && s.Samples[0].Time <= end && s.Samples[^1].Time >= start)
            .ToList();

        var changed = wanted.Count != _items.Count || wanted.Any(s => !_items.ContainsKey(s.Key));
        if (!changed)
            return;

        var wantedKeys = wanted.Select(s => s.Key).ToHashSet();
        foreach (var stale in _items.Values.Where(i => !wantedKeys.Contains(i.Key)).ToList())
        {
            _items.Remove(stale.Key);
            RemoveFromGroup(stale);
        }

        foreach (var series in wanted.Where(s => !_items.ContainsKey(s.Key)))
        {
            series.Visible = !_settings.HiddenProcesses.Contains(series.Name, StringComparer.OrdinalIgnoreCase);

            var item = new ProcessItemViewModel(series, Viewport, SelectProcess);
            item.PropertyChanged += OnItemChanged;
            _items[series.Key] = item;
            AddToGroup(item);
        }

        RestoreSelection();
        OnPropertyChanged(nameof(HasProcesses));
        OnPropertyChanged(nameof(ProcessesLabel));
    }

    private void SelectProcess(ProcessItemViewModel item) => SelectedProcess = item;

    private void OpenConsumer(ProcessSample sample)
    {
        var item = _items.GetValueOrDefault(sample.Key) ?? CreateUntrackedItem(sample.Key);
        if (item is null)
            return;

        Message = string.Empty;
        SelectedProcess = item;
        SelectedTab = ProcessTabIndex;
    }

    // Untracked processes get a transient item that is never added to the process list.
    private ProcessItemViewModel? CreateUntrackedItem(string key)
    {
        if (_untrackedItem?.Key == key)
            return _untrackedItem;

        var series = Viewport.Store.FindUntracked(key);
        if (series is null)
            return null;

        var item = new ProcessItemViewModel(series, Viewport, SelectProcess) { IsUntracked = true };
        item.Update(Viewport.CursorTime ?? Viewport.ViewEnd, Viewport.GapSeconds);
        _untrackedItem = item;
        return item;
    }

    private void AddToGroup(ProcessItemViewModel item)
    {
        var index = 0;
        while (index < Groups.Count)
        {
            var comparison = CompareGroup(Groups[index], item);
            if (comparison == 0)
            {
                Groups[index].Add(item);
                return;
            }

            if (comparison > 0)
                break;

            index++;
        }

        var aggregate = new ProcessItemViewModel(Viewport.Store.GroupSeries(item.Name, item.RootPid), Viewport, SelectProcess);
        var group = new ProcessGroupViewModel(item.Name, aggregate)
        {
            IsSplit = _settings.SplitProcesses.Contains(item.Name, StringComparer.OrdinalIgnoreCase),
        };
        group.PropertyChanged += OnGroupChanged;
        group.Add(item);
        Groups.Insert(index, group);
    }

    private static int CompareGroup(ProcessGroupViewModel group, ProcessItemViewModel item)
    {
        var byName = StringComparer.OrdinalIgnoreCase.Compare(group.Name, item.Name);
        return byName != 0 ? byName : group.RootPid.CompareTo(item.RootPid);
    }

    private void RemoveFromGroup(ProcessItemViewModel item)
    {
        var group = Groups.FirstOrDefault(g => g.Items.Contains(item));
        if (group is null)
            return;

        group.Remove(item);
        if (group.Items.Count > 0)
            return;

        group.PropertyChanged -= OnGroupChanged;
        Groups.Remove(group);
    }

    private void OnGroupChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProcessGroupViewModel.IsSplit) || sender is not ProcessGroupViewModel group)
            return;

        _settings.SplitProcesses.RemoveAll(n => n.Equals(group.Name, StringComparison.OrdinalIgnoreCase));
        if (group.IsSplit)
            _settings.SplitProcesses.Add(group.Name);

        _settingsDirty = true;
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ProcessItemViewModel.IsVisible) || sender is not ProcessItemViewModel changed)
            return;

        var allHidden = _items.Values
            .Where(i => i.Name.Equals(changed.Name, StringComparison.OrdinalIgnoreCase))
            .All(i => !i.IsVisible);

        _settings.HiddenProcesses.RemoveAll(n => n.Equals(changed.Name, StringComparison.OrdinalIgnoreCase));
        if (allHidden)
            _settings.HiddenProcesses.Add(changed.Name);

        _settingsDirty = true;
    }

    private void RestoreSelection()
    {
        if (_pendingSelection is null || SelectedProcess is not null)
            return;

        var match = Groups.FirstOrDefault(g => g.Name.Equals(_pendingSelection, StringComparison.OrdinalIgnoreCase))?.Items.FirstOrDefault();
        if (match is null)
            return;

        _restoringSelection = true;
        SelectedProcess = match;
        _restoringSelection = false;
        _pendingSelection = null;
    }

    private void UpdateConsumers(DateTime time, double gap)
    {
        var store = Viewport.Store;
        var latest = new Dictionary<string, ProcessSample>();

        foreach (var series in store.Series.Concat(store.HogSeries))
        {
            var index = TimeSearch.LastAtOrBefore(series.Samples, time);
            if (index < 0)
                continue;

            var sample = series.Samples[index];
            if ((time - sample.Time).TotalSeconds > gap)
                continue;

            if (!latest.TryGetValue(sample.Key, out var known) || sample.Time > known.Time)
                latest[sample.Key] = sample;
        }

        foreach (var list in Consumers)
            list.Show(latest.Values);
    }

    private void UpdateReadouts()
    {
        var time = Viewport.CursorTime ?? Viewport.ViewEnd;
        var gap = Viewport.GapSeconds;

        foreach (var group in Groups)
            group.Update(time, gap);

        _untrackedItem?.Update(time, gap);

        UpdateConsumers(time, gap);

        var system = Viewport.Store.System;
        var index = TimeSearch.LastAtOrBefore(system, time);
        if (index < 0 || (time - system[index].Time).TotalSeconds > gap)
        {
            CpuText = MemoryText = DiskText = CommitText = "—";
            CpuValue = MemoryValue = CommitValue = DiskValue = 0;
            return;
        }

        var s = system[index];
        CpuValue = s.CpuPercent;
        MemoryValue = s.MemoryPercent;
        CommitValue = s.CommitPercent;
        DiskValue = s.DiskBusyPercent;
        CommitText = $"{ValueFormatter.Format(ValueKind.Percent, s.CommitPercent)}  ·  {ValueFormatter.Format(ValueKind.Megabytes, s.CommitUsedMb)}";
        CpuText = ValueFormatter.Format(ValueKind.Percent, s.CpuPercent);
        MemoryText = $"{ValueFormatter.Format(ValueKind.Percent, s.MemoryPercent)}  ·  {ValueFormatter.Format(ValueKind.Megabytes, s.MemoryUsedMb)}";
        DiskText = $"{ValueFormatter.Format(ValueKind.Percent, s.DiskBusyPercent)}  ·  {ValueFormatter.Format(ValueKind.BytesPerSecond, s.DiskReadBytesPerSec + s.DiskWriteBytesPerSec)}";
    }

    private static readonly Color White = Color.Parse("#E6EDF3");
    private static readonly Color Teal = Color.Parse("#2DD4BF");
    private static readonly Color Violet = Color.Parse("#A78BFA");
    private static readonly Color Amber = Color.Parse("#FBBF24");
    private static readonly Color Blue = Color.Parse("#4C9AFF");
    private static readonly Color Coral = Color.Parse("#F97066");

    private ChartViewModel[] BuildOverviewCharts() =>
    [
        new("CPU usage", "System total and each monitored process, as % of all cores", ValueKind.Percent, Viewport)
        {
            FixedMax = 100,
            ProcessSelector = p => p.CpuPercent,
            SystemSeries = [new("System", White, s => s.CpuPercent)],
        },
        new("Memory", "Physical memory in use and commit charge (RAM + page file)", ValueKind.Percent, Viewport)
        {
            FixedMax = 100,
            SystemSeries = [new("Physical", Violet, s => s.MemoryPercent), new("Commit", Amber, s => s.CommitPercent)],
        },
        new("Disk busy", "Share of time the disks were servicing requests", ValueKind.Percent, Viewport)
        {
            FixedMax = 100,
            SystemSeries = [new("Disk busy", Blue, s => s.DiskBusyPercent)],
        },
        new("Disk throughput", "System-wide read and write bandwidth", ValueKind.BytesPerSecond, Viewport)
        {
            MinimumScale = 1024 * 100,
            SystemSeries = [new("Read", Teal, s => s.DiskReadBytesPerSec), new("Write", Coral, s => s.DiskWriteBytesPerSec)],
        },
    ];

    private ChartViewModel[] BuildCpuCharts() =>
    [
        new("Process CPU", "% of all cores, per process", ValueKind.Percent, Viewport) { MinimumScale = 5, ProcessSelector = p => p.CpuPercent },
        new("Threads", "Thread count per process", ValueKind.Count, Viewport) { MinimumScale = 10, ProcessSelector = p => p.Threads },
    ];

    private ChartViewModel[] BuildMemoryCharts() =>
    [
        new("Working set", "Physical memory currently resident", ValueKind.Megabytes, Viewport) { MinimumScale = 64, ProcessSelector = p => p.WorkingSetMb },
        new("Private bytes", "Memory committed exclusively to the process", ValueKind.Megabytes, Viewport) { MinimumScale = 64, ProcessSelector = p => p.PrivateMb },
        new("Page file usage", "Swap / commit charge attributed to the process", ValueKind.Megabytes, Viewport) { MinimumScale = 64, ProcessSelector = p => p.PagefileMb },
        new("Virtual size", "Reserved address space", ValueKind.Megabytes, Viewport) { MinimumScale = 256, ProcessSelector = p => p.VirtualMb },
        new("Page faults", "Page faults per second (high values indicate paging pressure)", ValueKind.PerSecond, Viewport) { MinimumScale = 10, ProcessSelector = p => p.PageFaultsPerSec },
    ];

    private ChartViewModel[] BuildDiskCharts() =>
    [
        new("Read throughput", "Bytes read per second, per process (all I/O incl. network)", ValueKind.BytesPerSecond, Viewport) { MinimumScale = 1024, ProcessSelector = p => p.IoReadBytesPerSec },
        new("Write throughput", "Bytes written per second, per process", ValueKind.BytesPerSecond, Viewport) { MinimumScale = 1024, ProcessSelector = p => p.IoWriteBytesPerSec },
        new("I/O operations", "Read + write operations per second", ValueKind.PerSecond, Viewport) { MinimumScale = 10, ProcessSelector = p => p.IoOpsPerSec },
    ];

    private ChartViewModel[] BuildResourceCharts() =>
    [
        new("Handles", "Open kernel handles", ValueKind.Count, Viewport) { MinimumScale = 100, ProcessSelector = p => p.Handles },
        new("GDI objects", "Graphics objects (leaks here freeze UI rendering)", ValueKind.Count, Viewport) { MinimumScale = 100, ProcessSelector = p => p.GdiObjects },
        new("USER objects", "Windows, menus, cursors and other USER resources", ValueKind.Count, Viewport) { MinimumScale = 100, ProcessSelector = p => p.UserObjects },
    ];

    private ChartViewModel[] BuildTopCharts() =>
    [
        new("Top CPU users", "Busiest processes, tracked or not, % of all cores", ValueKind.Percent, Viewport)
        {
            MinimumScale = 5,
            HogReason = HogReason.Cpu,
            ProcessSelector = p => p.CpuPercent,
        },
        new("Top memory users", "Largest working sets among all processes", ValueKind.Megabytes, Viewport)
        {
            MinimumScale = 64,
            HogReason = HogReason.Memory,
            ProcessSelector = p => p.WorkingSetMb,
        },
        new("Top disk users", "Read + write throughput of all processes", ValueKind.BytesPerSecond, Viewport)
        {
            MinimumScale = 1024,
            HogReason = HogReason.Disk,
            ProcessSelector = p => p.IoReadBytesPerSec + p.IoWriteBytesPerSec,
        },
    ];

    private ChartViewModel[] BuildDetailCharts() =>
    [
        new("CPU", "% of all cores used by the selected process", ValueKind.Percent, Viewport)
        {
            MinimumScale = 5,
            ProcessMetrics = [new("CPU", Teal, p => p.CpuPercent)],
        },
        new("Memory", "Resident, private and page-file memory", ValueKind.Megabytes, Viewport)
        {
            MinimumScale = 64,
            ProcessMetrics = [new("Working set", Blue, p => p.WorkingSetMb), new("Private", Violet, p => p.PrivateMb), new("Page file", Amber, p => p.PagefileMb)],
        },
        new("Page faults", "Page faults per second", ValueKind.PerSecond, Viewport)
        {
            MinimumScale = 10,
            ProcessMetrics = [new("Page faults", Coral, p => p.PageFaultsPerSec)],
        },
        new("Disk I/O", "Bytes read and written per second", ValueKind.BytesPerSecond, Viewport)
        {
            MinimumScale = 1024,
            ProcessMetrics = [new("Read", Teal, p => p.IoReadBytesPerSec), new("Write", Coral, p => p.IoWriteBytesPerSec)],
        },
        new("Threads", "Thread count", ValueKind.Count, Viewport)
        {
            MinimumScale = 10,
            ProcessMetrics = [new("Threads", Blue, p => p.Threads)],
        },
        new("Handles & GUI objects", "Kernel handles, GDI objects and USER objects", ValueKind.Count, Viewport)
        {
            MinimumScale = 100,
            ProcessMetrics = [new("Handles", Amber, p => p.Handles), new("GDI", Violet, p => p.GdiObjects), new("USER", Teal, p => p.UserObjects)],
        },
    ];
}
