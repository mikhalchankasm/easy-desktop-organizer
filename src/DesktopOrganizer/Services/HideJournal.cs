using System.IO;
using DesktopOrganizer.Data;

namespace DesktopOrganizer.Services;

/// <summary>
/// Durable-журнал скрытых приложением файлов: полный путь → добавленные биты атрибутов.
/// Запись делается ДО изменения атрибутов файла и удаляется ПОСЛЕ его возврата на стол.
/// Это источник правды для восстановления, НЕ зависящий от SQLite: даже если запись в БД
/// упадёт или БД будет удалена, файлы всё равно можно вернуть на рабочий стол по журналу
/// (App `--restore-hidden`, выход, деинсталляция).
///
/// Файл: %LOCALAPPDATA%\DesktopOrganizer\hide-journal.txt, строки вида "&lt;битыInt&gt;\t&lt;путь&gt;".
/// </summary>
public static class HideJournal
{
    private static readonly object _lock = new();
    private static Dictionary<string, FileAttributes>? _map;

    private static string FilePath => Path.Combine(Db.AppDataDir, "hide-journal.txt");

    private static Dictionary<string, FileAttributes> Map()
    {
        if (_map != null) return _map;
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
        return _map = map;
    }

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(Db.AppDataDir);
            var tmp = FilePath + ".tmp";
            File.WriteAllLines(tmp, Map().Select(kv => $"{(int)kv.Value}\t{kv.Key}"));
            // Атомарная замена.
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
        }
        catch (Exception ex)
        {
            Logger.Error("HideJournal.Save", ex);
        }
    }

    /// <summary>Фиксирует намерение/факт скрытия. Вызывать ДО File.SetAttributes.</summary>
    public static void Record(string path, FileAttributes added)
    {
        lock (_lock)
        {
            Map()[path] = added;
            Save();
        }
    }

    public static void Remove(string path)
    {
        lock (_lock)
        {
            if (Map().Remove(path)) Save();
        }
    }

    public static bool TryGet(string path, out FileAttributes added)
    {
        lock (_lock) return Map().TryGetValue(path, out added);
    }

    public static IReadOnlyList<KeyValuePair<string, FileAttributes>> GetAll()
    {
        lock (_lock) return Map().ToList();
    }
}
