namespace RestPeach.Services;

/// <summary>
/// Unexpected exceptions, appended to <c>crash.log</c> in the settings folder. A bug in one click handler is logged
/// and reported instead of closing the window (and losing the unsaved requests with it).
/// </summary>
public static class CrashLog
{
    private const long MaxBytes = 512 * 1024;

    public static string? Write(string folder, Exception e)
    {
        try
        {
            var path = Path.Combine(folder, "crash.log");
            if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) File.Delete(path);
            File.AppendAllText(path, $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {e}\n\n");
            return path;
        }
        catch { return null; }
    }
}
