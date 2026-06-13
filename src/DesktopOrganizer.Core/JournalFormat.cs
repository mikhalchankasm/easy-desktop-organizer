using System.IO;

namespace DesktopOrganizer.Services;

/// <summary>
/// Чистый (без ввода-вывода) формат строк durable-журнала: «&lt;битыInt&gt;\t&lt;путь&gt;».
/// Разбор строго fail-closed — некорректная непустая строка означает сбой чтения, а не молчаливый
/// пропуск (иначе потеряли бы recovery-запись). Вынесено в Core, чтобы контракт записи/чтения
/// журнала покрывался тестами без файлового ввода-вывода и без сборки WinExe.
/// Сам файловый слой (mutex, fsync, атомарная замена) остаётся в HideJournal.
/// </summary>
public static class JournalFormat
{
    /// <summary>Сериализует одну запись (без завершающего перевода строки).</summary>
    public static string FormatLine(string path, FileAttributes bits) => $"{(int)bits}\t{path}";

    /// <summary>
    /// Разбирает одну непустую строку. false — строка некорректна (нет таба, пустой путь, биты не
    /// число или не ненулевое подмножество <see cref="HideAttributes.Mask"/>); вызывающий обязан
    /// прервать загрузку (fail-closed), а не пропускать строку.
    /// </summary>
    public static bool TryParseLine(string line, out string path, out FileAttributes bits)
    {
        path = "";
        bits = 0;
        var tab = line.IndexOf('\t');
        if (tab <= 0) return false;
        var p = line[(tab + 1)..];
        if (p.Length == 0) return false;
        if (!int.TryParse(line[..tab], out var bitsInt)) return false;
        var b = (FileAttributes)bitsInt;
        if (!HideAttributes.IsValidAddedBits(b)) return false;
        path = p;
        bits = b;
        return true;
    }

    /// <summary>
    /// Разбирает все строки. Пустые строки пропускаются; любая некорректная непустая строка →
    /// false и пустая карта (fail-closed: не теряем записи частичным разбором). Пути сравниваются
    /// без учёта регистра.
    /// </summary>
    public static bool TryParse(IEnumerable<string> lines, out Dictionary<string, FileAttributes> map)
    {
        map = new Dictionary<string, FileAttributes>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            if (line.Length == 0) continue;
            if (!TryParseLine(line, out var path, out var bits))
            {
                map = new Dictionary<string, FileAttributes>(StringComparer.OrdinalIgnoreCase);
                return false;
            }
            map[path] = bits;
        }
        return true;
    }
}
