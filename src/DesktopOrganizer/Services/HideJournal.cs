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
/// - Чтения (Lookup/TryGetAll) делаются без блокировки: запись атомарна (.tmp → replace),
///   поэтому читатель видит либо старый, либо новый цельный файл.
/// - Запись: FileStream + Flush(true) + атомарная замена. На NTFS переименование журналируется,
///   что сводит к минимуму потерю при сбое питания (абсолютной гарантии flush директории нет).
///
/// Файл: %LOCALAPPDATA%\DesktopOrganizer\hide-journal.txt, строки "&lt;битыInt&gt;\t&lt;путь&gt;".
/// </summary>
/// <summary>Результат точечного поиска в журнале (различаем «не прочитан» и «нет записи»).</summary>
public enum JournalLookup { Found, NotFound, ReadFailed }

public static class HideJournal
{
    private static string FilePath => Path.Combine(Db.AppDataDir, "hide-journal.txt");
    private static string MutexName => $"Local\\DesktopOrganizer_Journal_{Environment.UserName}";

    /// <summary>Читает все строки с разделяемым доступом (не мешает параллельной атомарной замене).</summary>
    private static List<string> ReadAllLinesShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        var lines = new List<string>();
        string? line;
        while ((line = sr.ReadLine()) != null) lines.Add(line);
        return lines;
    }

    /// <summary>
    /// Читает журнал. false — чтение НЕ удалось (нельзя перезаписывать journal — потеряем записи!).
    /// Fail-closed: непустая строка некорректного формата трактуется как сбой, а не молча отбрасывается.
    /// </summary>
    private static bool TryLoad(out Dictionary<string, FileAttributes> map)
    {
        map = new Dictionary<string, FileAttributes>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(FilePath)) return true; // пустой журнал — валидное состояние
            foreach (var line in ReadAllLinesShared(FilePath))
            {
                if (line.Length == 0) continue; // пустые строки игнорируем
                var tab = line.IndexOf('\t');
                var path = tab > 0 ? line[(tab + 1)..] : "";
                // Fail-closed: формат, непустой путь, ненулевые биты И только из HideMask
                // (повреждённое валидное число не должно дать снятие произвольных атрибутов).
                if (tab <= 0 || path.Length == 0 || !int.TryParse(line[..tab], out var bitsInt)
                    || (FileAttributes)bitsInt == 0
                    || ((FileAttributes)bitsInt & ~DesktopIconService.HideMask) != 0)
                {
                    Logger.Log($"HideJournal: некорректная строка, чтение прервано (fail-closed): {line}");
                    map = new Dictionary<string, FileAttributes>(StringComparer.OrdinalIgnoreCase);
                    return false;
                }
                map[path] = (FileAttributes)bitsInt;
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

    /// <summary>
    /// Фиксирует запись. true — ТОЛЬКО при подтверждённой записи; вызывать ДО SetAttributes.
    /// Биты маскируются до <see cref="DesktopIconService.HideMask"/> и валидируются здесь, на входе:
    /// иначе повреждённый источник (например, seed из БД с битым AddedAttributes) мог бы записать
    /// строку, которую потом fail-closed <see cref="TryLoad"/> забракует, сделав ВЕСЬ журнал
    /// нечитаемым и заблокировав восстановление. Биты вне маски/нулевые → запись отклоняется.
    /// </summary>
    public static bool Record(string path, FileAttributes added)
    {
        var masked = added & DesktopIconService.HideMask;
        if (masked == 0 || string.IsNullOrEmpty(path))
        {
            Logger.Log($"HideJournal.Record: биты ({added}) вне HideMask/пусты или пустой путь — запись отклонена (fail-closed): {path}");
            return false;
        }
        return Mutate(map => { map[path] = masked; return true; });
    }

    /// <summary>Удаляет запись. true — удаление зафиксировано (или записи не было).</summary>
    public static bool Remove(string path) =>
        Mutate(map => map.Remove(path));

    /// <summary>Tri-state поиск: Found(+биты) / NotFound / ReadFailed. Не путать «нет» и «не прочитан».</summary>
    public static JournalLookup Lookup(string path, out FileAttributes added)
    {
        added = 0;
        if (!TryLoad(out var map)) return JournalLookup.ReadFailed;
        return map.TryGetValue(path, out added) ? JournalLookup.Found : JournalLookup.NotFound;
    }

    /// <summary>Все записи. false — журнал не прочитан (НЕ трактовать как «пусто»).</summary>
    public static bool TryGetAll(out IReadOnlyList<KeyValuePair<string, FileAttributes>> entries)
    {
        if (TryLoad(out var map))
        {
            entries = map.ToList();
            return true;
        }
        entries = new List<KeyValuePair<string, FileAttributes>>();
        return false;
    }
}
