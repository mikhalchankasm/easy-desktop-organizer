using System.IO;
using DesktopOrganizer.Data;

namespace DesktopOrganizer.Services;

/// <summary>
/// Durable-журнал скрытых приложением файлов: полный путь → добавленные биты атрибутов.
/// Запись делается ДО изменения атрибутов файла и удаляется ПОСЛЕ его возврата на стол.
/// Источник правды для восстановления, НЕ зависящий от SQLite.
///
/// Гарантии:
/// - Мутации (Record/Remove) держат МЕЖПРОЦЕССНЫЙ mutex и читают файл заново. Если mutex не
///   получен или файл не прочитан надёжно — операция ОТМЕНЯЕТСЯ (возвращает false), журнал
///   не перезаписывается пустым/частичным снимком и существующие записи не теряются.
/// - Чтения (GetAll/TryGet) делаются без блокировки: запись атомарна (.tmp → replace),
///   поэтому читатель видит либо старый, либо новый цельный файл.
/// - Запись: FileStream + Flush(true) + атомарная замена. На NTFS переименование журналируется,
///   что сводит к минимуму потерю при сбое питания (абсолютной гарантии flush директории нет).
///
/// Файл: %LOCALAPPDATA%\DesktopOrganizer\hide-journal.txt, строки "&lt;битыInt&gt;\t&lt;путь&gt;".
/// </summary>
public static class HideJournal
{
    private static string FilePath => Path.Combine(Db.AppDataDir, "hide-journal.txt");
    private static string MutexName => $"Local\\DesktopOrganizer_Journal_{Environment.UserName}";

    /// <summary>Читает журнал. false — чтение НЕ удалось (нельзя перезаписывать journal!).</summary>
    private static bool TryLoad(out Dictionary<string, FileAttributes> map)
    {
        map = new Dictionary<string, FileAttributes>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(FilePath)) return true; // пустой журнал — валидное состояние
            foreach (var line in File.ReadAllLines(FilePath))
            {
                var tab = line.IndexOf('\t');
                if (tab <= 0) continue;
                if (int.TryParse(line[..tab], out var bits))
                    map[line[(tab + 1)..]] = (FileAttributes)bits;
            }
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("HideJournal.Load", ex);
            return false;
        }
    }

    /// <summary>Запись на диск с fsync и атомарной заменой. true — только при успехе.</summary>
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
    /// Атомарная мутация под межпроцессным mutex. change возвращает, нужно ли сохранять.
    /// Любой сбой (mutex/чтение/запись) → false, и журнал НЕ перезаписывается «вслепую».
    /// </summary>
    private static bool Mutate(Func<Dictionary<string, FileAttributes>, bool> change)
    {
        using var mtx = new Mutex(false, MutexName);
        var acquired = false;
        try
        {
            try { acquired = mtx.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { acquired = true; } // прежний владелец упал — продолжаем
            if (!acquired)
            {
                Logger.Log("HideJournal: не удалось получить mutex — операция отменена");
                return false;
            }
            if (!TryLoad(out var map))
            {
                Logger.Log("HideJournal: журнал не прочитан — мутация отменена (не теряем записи)");
                return false;
            }
            if (!change(map)) return true; // изменений нет — успех без записи
            return SaveDurable(map);
        }
        finally
        {
            if (acquired) mtx.ReleaseMutex();
        }
    }

    /// <summary>Фиксирует запись. true — ТОЛЬКО при подтверждённой записи; вызывать ДО SetAttributes.</summary>
    public static bool Record(string path, FileAttributes added) =>
        Mutate(map => { map[path] = added; return true; });

    /// <summary>Удаляет запись. true — удаление зафиксировано (или записи не было).</summary>
    public static bool Remove(string path) =>
        Mutate(map => map.Remove(path));

    public static bool TryGet(string path, out FileAttributes added)
    {
        added = 0;
        return TryLoad(out var map) && map.TryGetValue(path, out added);
    }

    public static IReadOnlyList<KeyValuePair<string, FileAttributes>> GetAll() =>
        TryLoad(out var map) ? map.ToList() : new List<KeyValuePair<string, FileAttributes>>();
}
