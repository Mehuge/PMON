using System.Text;
using PMON.Models;

namespace PMON.Recording;

/// <summary>Appends snapshots to a CSV file, flushing every sample so data survives a crash or hard reset.</summary>
public sealed class CsvRecorder : IDisposable
{
    private readonly object _gate = new();
    private readonly FileStream _stream;
    private readonly StreamWriter _writer;
    private long _rows;
    private long _size;
    private bool _disposed;

    public CsvRecorder(string path)
    {
        Path = path;
        Started = DateTime.Now;

        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(_stream, new UTF8Encoding(false));
        _writer.WriteLine(CsvFormat.Header);
        _writer.Flush();
        _size = _stream.Length;
    }

    public string Path { get; }
    public DateTime Started { get; }
    public long Rows => Interlocked.Read(ref _rows);
    public long Size => Interlocked.Read(ref _size);
    public Exception? Error { get; private set; }

    public void Write(Snapshot snapshot)
    {
        lock (_gate)
        {
            if (_disposed || Error is not null)
                return;

            try
            {
                _writer.WriteLine(CsvFormat.FormatSystem(snapshot.System));
                foreach (var process in snapshot.Processes)
                    _writer.WriteLine(CsvFormat.FormatProcess(process));

                foreach (var hog in snapshot.Hogs)
                    _writer.WriteLine(CsvFormat.FormatHog(hog));

                _writer.Flush();
                Interlocked.Exchange(ref _size, _stream.Length);
                Interlocked.Add(ref _rows, 1 + snapshot.Processes.Count + snapshot.Hogs.Count);
            }
            catch (IOException ex)
            {
                Error = ex;
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _writer.Dispose();
        }
    }
}
