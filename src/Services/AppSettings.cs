using System.Text.Json;
using PMON.Models;

namespace PMON.Services;

public sealed class AppSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PMON", "settings.json");

    public List<TrackingRule> Rules { get; set; } = [];

    /// <summary>Include-only patterns from before rules existed; moved into <see cref="Rules"/> on load.</summary>
    public List<string> Patterns { get; set; } = [];
    public double IntervalSeconds { get; set; } = 1;
    public double SpanSeconds { get; set; } = 300;
    public bool AutoRecord { get; set; }

    /// <summary>Also rank processes outside the tracked patterns and record the top consumers.</summary>
    public bool CaptureTopProcesses { get; set; } = true;

    public int TopCount { get; set; } = 3;

    /// <summary>Hours of live samples kept in memory; 0 keeps everything.</summary>
    public double MemoryRetentionHours { get; set; } = 6;

    /// <summary>Size in MB at which recording moves to a new file; 0 never splits.</summary>
    public int TraceSizeMb { get; set; } = 250;

    /// <summary>Number of recording files kept; older ones are deleted. 0 keeps all.</summary>
    public int KeepTraces { get; set; }

    /// <summary>Process names whose instances are all unticked; names are stored because PIDs change between runs.</summary>
    public List<string> HiddenProcesses { get; set; } = [];

    /// <summary>Process names whose instances are charted individually rather than as one combined line.</summary>
    public List<string> SplitProcesses { get; set; } = [];

    public string? SelectedProcessName { get; set; }
    public string RecordingFolder { get; set; } = DefaultRecordingFolder;

    public static string DefaultRecordingFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "PMON", "Recordings");

    public static AppSettings Load()
    {
        if (!File.Exists(FilePath))
            return new AppSettings();

        try
        {
            return MigratePatterns(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings());
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    private static AppSettings MigratePatterns(AppSettings settings)
    {
        if (settings.Patterns.Count == 0)
            return settings;

        settings.Rules.AddRange(settings.Patterns.Select(p => new TrackingRule { Pattern = p }));
        settings.Patterns = [];
        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings are a convenience; failing to persist them must not interrupt monitoring.
        }
    }
}
