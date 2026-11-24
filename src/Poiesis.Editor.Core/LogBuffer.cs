namespace Poiesis.Editor;

public enum LogLevel { Info, Warning, Error }

public sealed record LogEntry(DateTime Time, LogLevel Level, string Source, string Text);

/// <summary>Journal de l'éditeur, alimenté depuis n'importe quel thread (build, agent IA) et lu par la console.</summary>
public sealed class LogBuffer
{
    readonly List<LogEntry> _entries = new();
    readonly object _lock = new();

    public int Capacity { get; init; } = 5000;

    public int Version { get; private set; }

    public void Add(LogLevel level, string source, string text)
    {
        lock (_lock)
        {
            _entries.Add(new LogEntry(DateTime.Now, level, source, text));
            if (_entries.Count > Capacity)
                _entries.RemoveRange(0, _entries.Count - Capacity);
            Version++;
        }
    }

    public void Info(string source, string text) => Add(LogLevel.Info, source, text);
    public void Warn(string source, string text) => Add(LogLevel.Warning, source, text);
    public void Error(string source, string text) => Add(LogLevel.Error, source, text);

    public LogEntry[] Snapshot()
    {
        lock (_lock)
            return _entries.ToArray();
    }

    public void Clear()
    {
        lock (_lock)
        {
            _entries.Clear();
            Version++;
        }
    }
}
