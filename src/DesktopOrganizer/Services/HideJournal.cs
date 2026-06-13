using System.IO;
using DesktopOrganizer.Data;

namespace DesktopOrganizer.Services;

/// <summary>
/// Durable-журнал скрытых приложением файлов: полный путь → добавленные биты атрибутов.
/// Запись делается ДО изменения атрибутов файла и удаляется ПОСЛЕ его возврата на стол.
/// Источник правды для восстановления, НЕ зависящий от SQLite.
///
/// Гарантии:
/// - Каждая операция держит МЕЖПРОЦЕССНЫЙ mutex и читает файл заново с диска, затем пишет —
///   поэтому второй процесс (например, `--restore-hidden`, запускаемый до single-instance mutex)
///   не перезатрёт чужие изменения устаревшим снимком.
/// - Сохранение идёт через FileStream + Flush(true) (сброс буферов ОС на диск) и атомарную
///   замену .tmp → файл, поэтому crash/power-loss не оставит частично записанный журнал.
/// - Record/Remove возвращают успех; вызывающий меняет атрибуты только при подтверждённой записи.
///
/// Файл: %LOCALAPPDATA%\DesktopOrganizer\hide-journal.txt, строки "&lt;битыInt&gt;\t&lt;путь&gt;".
/// </summary>
public static class HideJournal
{
    private static string FilePath => Path.Combine(Db.AppDataDir, "hide-journal.txt");
    private static string MutexName => $"Local\\DesktopOrganizer_Journal_{Environment.UserName}";

    private static T WithLock<T>(Func<Dictionary<string, FileAttributes>, T> op)
    {
        using var mtx = new Mutex(false, MutexName);
        var acquired = false;
        try
        {
            try { acquired = mtx.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { acquired = true; } // прежний владелец упал — продолжаем
            var map = Load();
            return op(map);
        }
        finally
        {
            if (acquired) mtx.ReleaseMutex();
        }
    }

    private static Dictionary<string, FileAttributes> Load()
    {
        var map = new Dictionary<string, FileAttributes>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(FilePath))
            {
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    var tab = line.IndexOf('\t');
                    if (tab <= 0) continue;
                    if (int.TryParse(line[..tab], out var bits))
                        map[line[(tab + 1)..]] = (FileAttributes)bits;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("HideJournal.Load", ex);
        }
        return map;
    }

    /// <summary>Записывает журнал на диск с fsync и атомарной заменой. true — только при успехе.</summary>
    private static bool SaveDurable(Dictionary<string, FileAttributes> map)
    {
        try
        {
            Directory.CreateDirectory(Db.AppDataDir);
            var tmp = FilePath + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var sw = new StreamWriter(fs))
            {
                foreach (var kv in map) sw.Write($"{(int)kv.Value}\t{kv.Key}\n");
                sw.Flush();
                fs.Flush(flushToDisk: true); // буферы ОС → физический диск
            }
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("HideJournal.Save", ex);
            return false;
        }
    }

    /// <summary>
    /// Фиксирует запись. true — ТОЛЬКО если реально записано на диск.
    /// Вызывать ДО File.SetAttributes; при false атрибуты менять нельзя.
    /// </summary>
    public static bool Record(string path, FileAttributes added) => WithLock(map =>
    {
        map[path] = added;
        return SaveDurable(map);
    });

    /// <summary>Удаляет запись. true — если удаление зафиксировано на диске (или записи не было).</summary>
    public static bool Remove(string path) => WithLock(map =>
    {
        if (!map.Remove(path)) return true; // нечего удалять
        return SaveDurable(map);
    });

    public static bool TryGet(string path, out FileAttributes added)
    {
        // Чтение без блокировки безопасно: запись атомарна (.tmp → replace), частичного файла не будет.
        var map = Load();
        return map.TryGetValue(path, out added);
    }

    public static IReadOnlyList<KeyValuePair<string, FileAttributes>> GetAll() =>
        WithLock(map => (IReadOnlyList<KeyValuePair<string, FileAttributes>>)map.ToList());
}
