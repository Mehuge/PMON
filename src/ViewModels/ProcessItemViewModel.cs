using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PMON.Data;
using PMON.Models;

namespace PMON.ViewModels;

public sealed partial class ProcessItemViewModel : ObservableObject
{
    private readonly ProcessSeries _series;
    private readonly Viewport _viewport;
    private readonly Action<ProcessItemViewModel> _select;

    [ObservableProperty]
    private string _metrics = "—";

    [ObservableProperty]
    private string _activity = string.Empty;

    [ObservableProperty]
    private string _details = string.Empty;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _cpuText = "—";

    [ObservableProperty]
    private string _memoryText = "—";

    [ObservableProperty]
    private string _state = string.Empty;

    public ProcessItemViewModel(ProcessSeries series, Viewport viewport, Action<ProcessItemViewModel> select)
    {
        _series = series;
        _viewport = viewport;
        _select = select;
        Brush = new SolidColorBrush(series.Color);
    }

    public string Key => _series.Key;
    public string Name => _series.Name;
    public int Pid => _series.Pid;
    public IBrush Brush { get; }

    /// <summary>The sample at the current cursor time, or null when the process was not running then.</summary>
    public ProcessSample? Sample { get; private set; }

    public bool IsVisible
    {
        get => _series.Visible;
        set
        {
            if (_series.Visible == value)
                return;

            _series.Visible = value;
            OnPropertyChanged();
            _viewport.Invalidate();
        }
    }

    [RelayCommand]
    public void Select() => _select(this);

    public void Update(DateTime time, double gapSeconds)
    {
        var index = TimeSearch.LastAtOrBefore(_series.Samples, time);
        if (index < 0 || (time - _series.Samples[index].Time).TotalSeconds > gapSeconds)
        {
            Sample = null;
            CpuText = MemoryText = "—";
            State = "Not running";
            Metrics = "—";
            Activity = "not running at this time";
            Details = "Not running at this time";
            return;
        }

        var s = _series.Samples[index];
        Sample = s;
        CpuText = ValueFormatter.Format(ValueKind.Percent, s.CpuPercent);
        MemoryText = ValueFormatter.Format(ValueKind.Megabytes, s.WorkingSetMb);
        State = DescribeState(s);
        Metrics = $"CPU {ValueFormatter.Format(ValueKind.Percent, s.CpuPercent)}   Mem {ValueFormatter.Format(ValueKind.Megabytes, s.WorkingSetMb)}   Threads {s.Threads}";
        Activity = DescribeActivity(s);
        Details = string.Join('\n',
            $"CPU {ValueFormatter.Format(ValueKind.Percent, s.CpuPercent)}   ·   Threads {s.Threads}   ·   Handles {s.Handles:N0}   ·   GDI {s.GdiObjects:N0}   ·   USER {s.UserObjects:N0}   ·   Priority {s.BasePriority}",
            $"Working set {ValueFormatter.Format(ValueKind.Megabytes, s.WorkingSetMb)} (peak {ValueFormatter.Format(ValueKind.Megabytes, s.PeakWorkingSetMb)})   ·   Private {ValueFormatter.Format(ValueKind.Megabytes, s.PrivateMb)}   ·   Page file {ValueFormatter.Format(ValueKind.Megabytes, s.PagefileMb)}   ·   Virtual {ValueFormatter.Format(ValueKind.Megabytes, s.VirtualMb)}",
            $"Disk read {ValueFormatter.Format(ValueKind.BytesPerSecond, s.IoReadBytesPerSec)}   ·   write {ValueFormatter.Format(ValueKind.BytesPerSecond, s.IoWriteBytesPerSec)}   ·   {ValueFormatter.Format(ValueKind.PerSecond, s.IoOpsPerSec)} ops   ·   Page faults {ValueFormatter.Format(ValueKind.PerSecond, s.PageFaultsPerSec)}",
            s.WindowTitle.Length > 0 ? $"{DescribeActivity(s)}   ·   “{s.WindowTitle}”" : DescribeActivity(s));
    }

    private static string DescribeState(ProcessSample s)
    {
        if (!s.Responding)
            return "Not responding";
        if (s.Window == WindowState.None)
            return "No window";

        return s.Foreground ? "Foreground" : s.Window == WindowState.Minimized ? "Minimised" : "Background";
    }

    private static string DescribeActivity(ProcessSample s)
    {
        if (!s.Responding)
            return "Not responding";
        if (s.Window == WindowState.None)
            return "No window";

        var state = s.Foreground ? "Foreground" : s.Window == WindowState.Minimized ? "Minimised" : "Background";
        return s.Window == WindowState.Minimized
            ? state
            : $"{state}  ·  {s.Window}  ·  {s.WindowWidth}×{s.WindowHeight} at {s.WindowX},{s.WindowY}";
    }
}
