using System.Globalization;

namespace EnrolHQ.Sync;

/// <summary>
/// Shows progress on the console and keeps a log file. Neither ever contains
/// record data or tokens: only table names, counts, timings and field paths.
/// </summary>
internal sealed class Reporter : IDisposable
{
    private readonly StreamWriter? _log;
    private readonly bool _inPlace;
    private bool _lineOpen;

    public Reporter(string? logDirectory, DateTimeOffset startedAt)
    {
        _inPlace = !Console.IsOutputRedirected;
        if (logDirectory is not null)
        {
            Directory.CreateDirectory(logDirectory);
            LogPath = Path.Combine(logDirectory, $"sync-{startedAt.ToLocalTime():yyyyMMdd-HHmmss}.log");
            _log = new StreamWriter(LogPath, append: false) { AutoFlush = true };
        }
    }

    public string? LogPath { get; }

    public void Line(string message)
    {
        CloseLine();
        Console.WriteLine(message);
        Log(message);
    }

    /// <summary>A line that the next progress update overwrites, so a long download stays on one line.</summary>
    public void Progress(string message)
    {
        if (_inPlace)
        {
            var width = Math.Max(Console.WindowWidth - 1, 20);
            var text = message.Length > width ? message[..width] : message.PadRight(width);
            Console.Write("\r" + text);
            _lineOpen = true;
        }
        else
        {
            Console.WriteLine(message);
        }

        Log(message);
    }

    public void Dispose()
    {
        CloseLine();
        _log?.Dispose();
    }

    public static string Duration(TimeSpan time) =>
        time.TotalHours >= 1 ? $"{(int)time.TotalHours}h {time.Minutes}m"
        : time.TotalMinutes >= 1 ? $"{(int)time.TotalMinutes}m {time.Seconds}s"
        : $"{time.Seconds}s";

    public static string Number(long value) => value.ToString("N0", CultureInfo.InvariantCulture);

    private void CloseLine()
    {
        if (_lineOpen)
        {
            Console.WriteLine();
            _lineOpen = false;
        }
    }

    private void Log(string message) =>
        _log?.WriteLine($"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}  {message}");
}
