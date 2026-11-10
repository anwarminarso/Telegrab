namespace Telegrab.Services;

/// <summary>
/// Pencatat exception tak tertangani ke <c>{AppData}/crash.log</c>. Crash WinUI hanya muncul di
/// Event Viewer sebagai 0xc000027b tanpa stack trace; log ini menyimpan exception aslinya agar
/// penyebab dapat ditelusuri. Best-effort: kegagalan menulis log diabaikan.
/// </summary>
public static class CrashLog
{
    private static readonly object Gate = new();

    public static string LogPath => Path.Combine(FileSystem.AppDataDirectory, "crash.log");

    public static void Write(string source, object? error, string? message = null)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(FileSystem.AppDataDirectory);
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {source}{Environment.NewLine}" +
                    (string.IsNullOrEmpty(message) ? string.Empty : message + Environment.NewLine) +
                    $"{error}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch
        {
            // abaikan: jangan sampai pencatat crash memicu crash baru
        }
    }
}
