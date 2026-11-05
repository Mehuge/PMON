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

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan LiveRetention = TimeSpan.FromHours(6);
    private const int ProcessTabIndex = 1;
    private const double TopListLineHeight = 20;
    private static readonly TimeSpan MinimumRateWindow = TimeSpan.FromSeconds(30);

    public static readonly IReadOnlyList<int> TopCountOptions = [1, 3, 5, 10];

    public static readonly IReadOnlyList<IntervalOption> IntervalOptions =
    [
        new("0.5 s", 0.5), new("1 s", 1), new("2 s", 2), new("5 s", 5), new("10 s", 10), new("30 s", 30), new("1 min", 60),
    ];

    private readonly AppSettings _settings;
    private readonly MonitorService _monitor = new();
    private readonly SampleStore _liveStore = new();
    private readonly Dictionary<string, ProcessItemViewModel> _items = new();
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private CsvRecorder? _recorder;
    private SampleStore? _loadedStore;
    private bool _dirty = true;
    private bool _settingsDirty;
    private bool _restoringSelection;
    private string? _pendingSelection;

    [ObservableProperty]
    private IntervalOption _selectedInterval;

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
    [NotifyPropertyChangedFor(nameof(TopListHeight))]
    private int _selectedTopCount;

    [ObservableProperty]
    private string _topCpuText = string.Empty;

    [ObservableProperty]
    private string _topMemoryText = string.Empty;

    [ObservableProperty]
    private string _topDiskText = string.Empty;

    public MainViewModel(AppSettings settings)
    {
        _settings = settings;
        Viewport = new Viewport(_liveStore);

        _selectedInterval = IntervalOptions.FirstOrDefault(o => o.Seconds == settings.IntervalSeconds) ?? IntervalOptions[1];
        _selectedSpan = Viewport.SpanOptions.FirstOrDefault(o => o.Span.TotalSeconds == settings.SpanSeconds) ?? Viewport.SpanOptions[4];
        _recordingFolder = settings.RecordingFolder;
        _autoRecord = settings.AutoRecord;
        _captureTop = settings.CaptureTopProcesses;
        _selectedTopCount = TopCountOptions.Contains(settings.TopCount) ? settings.TopCount : 3;
        _pendingSelection = settings.SelectedProcessName;
        Viewport.Span = _selectedSpan.Span;

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

    /// <summary>Every instance, ordered by name then PID, for the detail-tab picker.</summary>
    public ObservableCollection<ProcessItemViewModel> Choices { get; } = [];

    public IReadOnlyList<ChartViewModel> OverviewCharts { get; }
    public IReadOnlyList<ChartViewModel> CpuCharts { get; }
    public IReadOnlyList<ChartViewModel> MemoryCharts { get; }
    public IReadOnlyList<ChartViewModel> DiskCharts { get; }
    public IReadOnlyList<ChartViewModel> ResourceCharts { get; }
    public IReadOnlyList<ChartViewModel> DetailCharts { get; }
    public IReadOnlyList<ChartViewModel> TopCharts { get; }

    public bool HasSelection => SelectedProcess is not null;

    /// <summary>Room for the longest possible top list, so the panel does not resize as the cursor moves.</summary>
    public double TopListHeight => SelectedTopCount * TopListLineHeight;

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
            StopRecording();
        else
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

    private void StartRecording()
    {
        try
        {
            var path = Path.Combine(RecordingFolder, $"pmon-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            Volatile.Write(ref _recorder, new CsvRecorder(path));
            IsRecording = true;
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
    }

    private void OnSnapshotReady(Snapshot snapshot)
    {
        Volatile.Read(ref _recorder)?.Write(snapshot);
        Dispatcher.UIThread.Post(() => OnSnapshotOnUi(snapshot));
    }

    private void OnSnapshotOnUi(Snapshot snapshot)
    {
        _liveStore.Add(snapshot);
        if (_liveStore.Start < snapshot.Time - LiveRetention - TimeSpan.FromMinutes(1))
            _liveStore.TrimBefore(snapshot.Time - LiveRetention);

        if (ReferenceEquals(Viewport.Store, _liveStore))
            Viewport.DataAppended();
    }

    private void ShowStore(SampleStore store, bool live)
    {
        _items.Clear();
        Groups.Clear();
        Choices.Clear();
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

        if (_recorder is { } recorder)
        {
            if (recorder.Error is { } error)
            {
                StopRecording();
                Message = $"Recording stopped: {error.Message}";
            }
            else
            {
                var size = File.Exists(recorder.Path) ? new FileInfo(recorder.Path).Length : 0;
                RecordingStatus = DescribeRecording(DateTime.Now - recorder.Started, recorder.Rows, size / 1048576.0);
            }
        }

        if (!_dirty)
            return;

        _dirty = false;
        SyncProcessList();
        UpdateReadouts();
    }

    private static string DescribeRecording(TimeSpan elapsed, long rows, double sizeMb)
    {
        var status = $"{elapsed:hh\\:mm\\:ss}  ·  {rows:N0} rows  ·  {ValueFormatter.Format(ValueKind.Megabytes, sizeMb)}";
        if (elapsed < MinimumRateWindow)
            return status;

        return $"{status}  ·  ~{ValueFormatter.Format(ValueKind.Megabytes, sizeMb / elapsed.TotalHours)}/h";
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

    private static int CompareItems(ProcessItemViewModel a, ProcessItemViewModel b)
    {
        var byName = StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name);
        return byName != 0 ? byName : a.Pid.CompareTo(b.Pid);
    }

    private void AddToGroup(ProcessItemViewModel item)
    {
        InsertChoice(item);

        var index = 0;
        while (index < Groups.Count)
        {
            var comparison = StringComparer.OrdinalIgnoreCase.Compare(Groups[index].Name, item.Name);
            if (comparison == 0)
            {
                Groups[index].Add(item);
                SyncAggregateChoice(Groups[index]);
                return;
            }

            if (comparison > 0)
                break;

            index++;
        }

        var aggregate = new ProcessItemViewModel(Viewport.Store.GroupSeries(item.Name), Viewport, SelectProcess);
        var group = new ProcessGroupViewModel(item.Name, aggregate)
        {
            IsSplit = _settings.SplitProcesses.Contains(item.Name, StringComparer.OrdinalIgnoreCase),
        };
        group.PropertyChanged += OnGroupChanged;
        group.Add(item);
        Groups.Insert(index, group);
    }

    private void InsertChoice(ProcessItemViewModel item)
    {
        var choice = 0;
        while (choice < Choices.Count && CompareItems(Choices[choice], item) < 0)
            choice++;

        Choices.Insert(choice, item);
    }

    /// <summary>Offers the combined view in the detail picker only while the group has several instances.</summary>
    private void SyncAggregateChoice(ProcessGroupViewModel group)
    {
        var listed = Choices.Contains(group.Aggregate);
        if (group.HasMany == listed)
            return;

        if (group.HasMany)
            InsertChoice(group.Aggregate);
        else
            Choices.Remove(group.Aggregate);
    }

    private void RemoveFromGroup(ProcessItemViewModel item)
    {
        Choices.Remove(item);

        var group = Groups.FirstOrDefault(g => g.Items.Contains(item));
        if (group is null)
            return;

        group.Remove(item);
        SyncAggregateChoice(group);
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

    private string DescribeTop(string reason, DateTime time, double gap, ValueKind kind, Func<ProcessSample, double> metric)
    {
        var latest = DateTime.MinValue;
        var entries = new List<ProcessSample>();

        foreach (var series in Viewport.Store.HogSeries.Where(s => s.Reason == reason))
        {
            var index = TimeSearch.LastAtOrBefore(series.Samples, time);
            if (index < 0 || (time - series.Samples[index].Time).TotalSeconds > gap)
                continue;

            var sample = series.Samples[index];
            if (sample.Time > latest)
            {
                latest = sample.Time;
                entries.Clear();
            }

            if (sample.Time == latest)
                entries.Add(sample);
        }

        return entries.Count == 0
            ? string.Empty
            : string.Join('\n', entries.OrderByDescending(metric).Select(s => $"{s.Name} ({s.Pid})   {ValueFormatter.Format(kind, metric(s))}"));
    }

    private void UpdateReadouts()
    {
        var time = Viewport.CursorTime ?? Viewport.ViewEnd;
        var gap = Viewport.GapSeconds;

        foreach (var group in Groups)
            group.Update(time, gap);

        TopCpuText = DescribeTop(HogReason.Cpu, time, gap, ValueKind.Percent, p => p.CpuPercent);
        TopMemoryText = DescribeTop(HogReason.Memory, time, gap, ValueKind.Megabytes, p => p.WorkingSetMb);
        TopDiskText = DescribeTop(HogReason.Disk, time, gap, ValueKind.BytesPerSecond, p => p.IoReadBytesPerSec + p.IoWriteBytesPerSec);

        var system = Viewport.Store.System;
        var index = TimeSearch.LastAtOrBefore(system, time);
        if (index < 0 || (time - system[index].Time).TotalSeconds > gap)
        {
            CpuText = MemoryText = DiskText = "—";
            return;
        }

        var s = system[index];
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
        new("Top CPU users", "Busiest processes that are not tracked, % of all cores", ValueKind.Percent, Viewport)
        {
            MinimumScale = 5,
            HogReason = HogReason.Cpu,
            ProcessSelector = p => p.CpuPercent,
        },
        new("Top memory users", "Largest working sets among processes that are not tracked", ValueKind.Megabytes, Viewport)
        {
            MinimumScale = 64,
            HogReason = HogReason.Memory,
            ProcessSelector = p => p.WorkingSetMb,
        },
        new("Top disk users", "Read + write throughput of processes that are not tracked", ValueKind.BytesPerSecond, Viewport)
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
