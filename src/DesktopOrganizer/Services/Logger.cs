using System.Globalization;
using System.IO;
using DesktopOrganizer.Data;

namespace DesktopOrganizer.Services;

/// <summary>Простой файловый лог в %LOCALAPPDATA%\DesktopOrganizer\Logs (раздел 11 ТЗ).</summary>
public static class Logger
{
    private const int RetentionDays = 30;

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
                PurgeOldLogs();
            }
        }
        catch
        {
            // Лог не должен ронять приложение.
        }
    }

    public static void Error(string context, Exception ex) =>
        Log($"ERROR [{context}] {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    /// <summary>
    /// Удаляет дневные логи старше <see cref="RetentionDays"/> дней. Имя файла — app-{yyyyMMdd}.log;
    /// дата парсится из имени, чтобы не зависеть от даты модификации (файл могли трогать архиваторы).
    /// Срабатывает при каждой записи (под lock) — отдельный таймер/поток не нужен. Любая ошибка
    /// удаления отдельного файла молча пропускается: ротация не должна мешать логированию.
    /// </summary>
    private static void PurgeOldLogs()
    {
        string[] files;
        try { files = Directory.GetFiles(LogDir, "app-*.log"); }
        catch { return; }

        var cutoff = DateTime.Now.AddDays(-RetentionDays);
        foreach (var f in files)
        {
            // Имя вида app-20240115.log → вырезаем 8 цифр после "app-".
            var name = Path.GetFileNameWithoutExtension(f);
            var datePart = name.StartsWith("app-", StringComparison.Ordinal) ? name[4..] : "";
            if (datePart.Length == 8 &&
                DateTime.TryParseExact(datePart, "yyyyMMdd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date) &&
                date < cutoff)
            {
                try { File.Delete(f); }
                catch { /* не критично */ }
            }
        }
    }
}
