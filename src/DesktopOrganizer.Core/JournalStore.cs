using System.IO;

namespace DesktopOrganizer.Services;

/// <summary>Результат точечного поиска в журнале (различаем «не прочитан» и «нет записи»).</summary>
public enum JournalLookup { Found, NotFound, ReadFailed }

/// <summary>
/// Файловый движок durable-журнала скрытых приложением файлов: полный путь → добавленные биты
/// атрибутов. Запись делается ДО изменения атрибутов файла и удаляется ПОСЛЕ его возврата на стол.
/// Источник правды для восстановления, НЕ зависящий от SQLite.
///
/// Вынесено в Core (чистый System.IO, без Win32/WPF), чтобы файловый слой (mutex, fsync, атомарная
/// замена) покрывался тестами на temp-каталоге без сборки WinExe. Тонкая обёртка <c>HideJournal</c>
/// в WinExe фиксирует конкретные пути (Db.AppDataDir) и имя mutex.
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
/// Формат строк: «&lt;битыInt&gt;\t&lt;путь&gt;» (см. <see cref="JournalFormat"/>).
/// </summary>
public sealed class JournalStore
{
    private readonly string _filePath;
    private readonly string _mutexName;
    private readonly Action<string>? _log;

    /// <param name="filePath">Полный путь к файлу журнала.</param>
    /// <param name="mutexName">Имя межпроцессного mutex (с пространством имён Local\ или Global\).</param>
    /// <param name="log">Необязательный приёмник диагностических сообщений.</param>
    public JournalStore(string filePath, string mutexName, Action<string>? log = null)
    {
        _filePath = filePath;
        _mutexName = mutexName;
        _log = log;
    }

    /// <summary>Читает все строки с разделяемым доступом (не мешает параллельной атомарной замене).</summary>
    private List<string> ReadAllLinesShared(string path)
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
    private bool TryLoad(out Dictionary<string, FileAttributes> map)
    {
        map = new Dictionary<string, FileAttributes>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(_filePath)) return true; // пустой журнал — валидное состояние
            foreach (var line in ReadAllLinesShared(_filePath))
            {
                if (line.Length == 0) continue; // пустые строки игнорируем
                // Разбор и fail-closed-валидация — общий контракт с записью (JournalFormat).
                if (!JournalFormat.TryParseLine(line, out var path, out var bits))
                {
                    _log?.Invoke($"JournalStore: некорректная строка, чтение прервано (fail-closed): {line}");
                    map = new Dictionary<string, FileAttributes>(StringComparer.OrdinalIgnoreCase);
                    return false;
                }
                map[path] = bits;
            }
            return true;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"ERROR [JournalStore.Load] {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>Запись на диск с fsync и атомарной заменой. true — только при успехе.</summary>
    private bool SaveDurable(Dictionary<string, FileAttributes> map)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            var tmp = _filePath + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var sw = new StreamWriter(fs))
            {
                foreach (var kv in map) { sw.Write(JournalFormat.FormatLine(kv.Key, kv.Value)); sw.Write('\n'); }
                sw.Flush();
                fs.Flush(flushToDisk: true); // буферы ОС → физический диск
            }
            if (File.Exists(_filePath)) File.Replace(tmp, _filePath, null);
            else File.Move(tmp, _filePath);
            return true;
        }
        catch (Exception ex)
        {
            _log?.Invoke($"ERROR [JournalStore.Save] {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Атомарная мутация под межпроцессным mutex. change возвращает, нужно ли сохранять.
    /// Любой сбой (mutex/чтение/запись) → false, и журнал НЕ перезаписывается «вслепую».
    /// </summary>
    private bool Mutate(Func<Dictionary<string, FileAttributes>, bool> change)
    {
        using var mtx = new Mutex(false, _mutexName);
        var acquired = false;
        try
        {
            try { acquired = mtx.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { acquired = true; } // прежний владелец упал — продолжаем
            if (!acquired)
            {
                _log?.Invoke("JournalStore: не удалось получить mutex — операция отменена");
                return false;
            }
            if (!TryLoad(out var map))
            {
                _log?.Invoke("JournalStore: журнал не прочитан — мутация отменена (не теряем записи)");
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
    /// Строго fail-closed (тот же контракт, что и <see cref="TryLoad"/>): принимаются ТОЛЬКО
    /// ненулевое подмножество <see cref="HideAttributes.Mask"/> без посторонних бит. Лишние
    /// биты НЕ маскируются молча, а отклоняются — иначе повреждённый источник (например, seed из БД
    /// с битым AddedAttributes) закрепился бы как «валидная» recovery-запись и скрыл повреждение.
    /// Маскирование как защита остаётся на стороне Restore/RestorePlanner.
    /// </summary>
    public bool Record(string path, FileAttributes added)
    {
        if (string.IsNullOrEmpty(path) || !HideAttributes.IsValidAddedBits(added))
        {
            _log?.Invoke($"JournalStore.Record: недопустимые биты ({added}) или пустой путь — запись отклонена (fail-closed): {path}");
            return false;
        }
        return Mutate(map => { map[path] = added; return true; });
    }

    /// <summary>Удаляет запись. true — удаление зафиксировано (или записи не было).</summary>
    public bool Remove(string path) =>
        Mutate(map => map.Remove(path));

    /// <summary>Tri-state поиск: Found(+биты) / NotFound / ReadFailed. Не путать «нет» и «не прочитан».</summary>
    public JournalLookup Lookup(string path, out FileAttributes added)
    {
        added = 0;
        if (!TryLoad(out var map)) return JournalLookup.ReadFailed;
        return map.TryGetValue(path, out added) ? JournalLookup.Found : JournalLookup.NotFound;
    }

    /// <summary>Все записи. false — журнал не прочитан (НЕ трактовать как «пусто»).</summary>
    public bool TryGetAll(out IReadOnlyList<KeyValuePair<string, FileAttributes>> entries)
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
