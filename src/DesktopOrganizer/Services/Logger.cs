using System.IO;
using DesktopOrganizer.Data;

namespace DesktopOrganizer.Services;

/// <summary>Простой файловый лог в %LOCALAPPDATA%\DesktopOrganizer\Logs (раздел 11 ТЗ).</summary>
public static class Logger
{
    private static readonly object _lock = new();
    public static string LogDir => Path.Combine(Db.AppDataDir, "Logs");

    public static void Log(string message)
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(LogDir);
                var file = Path.Combine(LogDir, $"app-{DateTime.Now:yyyyMMdd}.log");
                File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Лог не должен ронять приложение.
        }
    }

    public static void Error(string context, Exception ex) =>
        Log($"ERROR [{context}] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
}
