using PMON.Models;

namespace PMON.Recording;

/// <summary>Records to a series of CSV files: starts a new file at a size limit and deletes the oldest beyond a count.</summary>
public sealed class RollingRecorder : IDisposable
{
    private const string FilePattern = "pmon-????????-??????.csv";

    private readonly object _gate = new();
    private readonly string _folder;
    private CsvRecorder _current;
    private long _closedRows;
    private long _closedBytes;
    private long _maxBytes;
    private int _keepFiles;
    private Exception? _rollError;
    private bool _disposed;

    /// <param name="maxBytes">Size at which to start a new file; 0 never splits.</param>
    /// <param name="keepFiles">Files to keep, counting the current one; 0 keeps all.</param>
    public RollingRecorder(string folder, long maxBytes, int keepFiles)
    {
        _folder = folder;
        _maxBytes = maxBytes;
        _keepFiles = keepFiles;
        Started = DateTime.Now;
        _current = new CsvRecorder(NextPath());
        Prune();
    }

    public DateTime Started { get; }

    public string Path
    {
        get
        {
            lock (_gate)
                return _current.Path;
        }
    }

    /// <summary>Rows written across every file of this session.</summary>
    public long Rows
    {
        get
        {
            lock (_gate)
                return _closedRows + _current.Rows;
        }
    }

    /// <summary>Bytes written across every file of this session, including any since deleted.</summary>
    public long Size
    {
        get
        {
            lock (_gate)
                return _closedBytes + _current.Size;
        }
    }

    public Exception? Error
    {
        get
        {
            lock (_gate)
                return _rollError ?? _current.Error;
        }
    }

    public void SetLimits(long maxBytes, int keepFiles)
    {
        lock (_gate)
        {
            _maxBytes = maxBytes;
            _keepFiles = keepFiles;
        }
    }

    public void Write(Snapshot snapshot)
    {
        lock (_gate)
        {
            if (_disposed || _rollError is not null)
                return;

            _current.Write(snapshot);
            if (_current.Error is not null || _maxBytes <= 0 || _current.Size < _maxBytes)
                return;

            Roll();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _current.Dispose();
        }
    }

    private string NextPath() =>
        System.IO.Path.Combine(_folder, $"pmon-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

    private void Roll()
    {
        var path = NextPath();

        // The current file started within this second; names are per-second, so retry on the next write.
        if (File.Exists(path))
            return;

        try
        {
            var next = new CsvRecorder(path);
            _closedRows += _current.Rows;
            _closedBytes += _current.Size;
            _current.Dispose();
            _current = next;
            Prune();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _rollError = ex;
        }
    }

    /// <summary>Deletes the oldest PMON-named files; names embed the start time, so name order is age order.</summary>
    private void Prune()
    {
        if (_keepFiles <= 0)
            return;

        try
        {
            var files = Directory.EnumerateFiles(_folder, FilePattern).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var stale in files.Take(Math.Max(0, files.Count - _keepFiles)))
                Delete(stale);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The folder cannot be listed; pruning is retried on the next roll.
        }
    }

    private void Delete(string path)
    {
        if (path.Equals(_current.Path, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file open elsewhere cannot be deleted; the next roll retries.
        }
    }
}
