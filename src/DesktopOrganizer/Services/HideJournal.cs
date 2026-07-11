using System.IO;
using DesktopOrganizer.Data;

namespace DesktopOrganizer.Services;

/// <summary>
/// Durable-журнал скрытых приложением файлов — тонкая обёртка над <see cref="JournalStore"/>
/// (файловый движок в Core, тестируется отдельно), фиксирующая конкретные пути и имя mutex.
/// Запись делается ДО изменения атрибутов файла и удаляется ПОСЛЕ его возврата на стол.
/// Источник правды для восстановления, НЕ зависящий от SQLite.
///
/// Mutex — Global\: журнал (файл в %LOCALAPPDATA%) один на пользователя во ВСЕХ его сессиях
/// (консоль + RDP), и запись с взаимным исключением должна защищать именно его. Имя mutex
/// содержит Environment.UserName, так что Global\ + UserName = «один файл на пользователя».
/// (Единственный экземпляр приложения, напротив, остаётся Local\ — в разных RDP-сессиях одного
/// пользователя отдельные экземпляры приложения допустимы, гонки за окнами между ними нет.)
///
/// Файл: %LOCALAPPDATA%\DesktopOrganizer\hide-journal.txt, строки "&lt;битыInt&gt;\t&lt;путь&gt;".
/// </summary>
public static class HideJournal
{
    private static string FilePath => Path.Combine(Db.AppDataDir, "hide-journal.txt");
    private static string MutexName => $"Global\\DesktopOrganizer_Journal_{Environment.UserName}";

    private static JournalStore Store => new(FilePath, MutexName, Logger.Log);

    /// <summary>
    /// Фиксирует запись. true — ТОЛЬКО при подтверждённой записи; вызывать ДО SetAttributes.
    /// Строго fail-closed: принимаются ТОЛЬКО ненулевое подмножество
    /// <see cref="DesktopIconService.HideMask"/> без посторонних бит. См. <see cref="JournalStore.Record"/>.
    /// </summary>
    public static bool Record(string path, FileAttributes added) => Store.Record(path, added);

    /// <summary>Удаляет запись. true — удаление зафиксировано (или записи не было).</summary>
    public static bool Remove(string path) => Store.Remove(path);

    /// <summary>Tri-state поиск: Found(+биты) / NotFound / ReadFailed. Не путать «нет» и «не прочитан».</summary>
    public static JournalLookup Lookup(string path, out FileAttributes added) =>
        Store.Lookup(path, out added);

    /// <summary>Все записи. false — журнал не прочитан (НЕ трактовать как «пусто»).</summary>
    public static bool TryGetAll(out IReadOnlyList<KeyValuePair<string, FileAttributes>> entries) =>
        Store.TryGetAll(out entries);
}
