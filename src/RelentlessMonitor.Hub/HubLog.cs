namespace RelentlessMonitor.Hub;

/// <summary>Small append-only file log (plus optional console), rolled to hub.1.log at 5 MB.</summary>
internal sealed class HubLog
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private readonly object _lock = new();
    private readonly string? _path;
    private readonly bool _console;

    public HubLog(string? path, bool console)
    {
        _path = path;
        _console = console;
        if (_path is not null) Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    public string? Path_ => _path;

    public void Info(string msg) => Write("INFO", msg);
    public void Warn(string msg) => Write("WARN", msg);
    public void Error(string msg) => Write("ERROR", msg);

    private void Write(string level, string msg)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level,-5} {msg}";
        lock (_lock)
        {
            if (_console) Console.WriteLine(line);
            if (_path is null) return;
            try
            {
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > MaxBytes)
                    File.Move(_path, Path.ChangeExtension(_path, ".1.log"), overwrite: true);
                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch (IOException) { /* logging must never take the hub down */ }
            catch (UnauthorizedAccessException) { }
        }
    }
}
