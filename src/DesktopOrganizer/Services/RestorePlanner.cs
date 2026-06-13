using System.IO;

namespace DesktopOrganizer.Services;

/// <summary>Один путь к восстановлению: какие биты снять и какие строки БД пометить восстановленными.</summary>
public sealed record RestoreTarget(string Path, FileAttributes Bits, IReadOnlyList<long> ItemIds);

/// <summary>
/// Чистая (без ввода-вывода) логика объединения источников для восстановления: durable-журнала и
/// флагов БД. Вынесена отдельно, чтобы покрыть тестами edge-кейсы (пустой журнал + fallback из БД,
/// один путь в нескольких коробках, мусорные биты в БД). См. tests/DesktopOrganizer.Tests.
/// </summary>
public static class RestorePlanner
{
    /// <summary>
    /// Строит план восстановления. Биты журнала приоритетнее (он уже провалидирован при чтении);
    /// для путей, которых в журнале нет, берутся биты из БД, отфильтрованные до <see cref="DesktopIconService.HideMask"/>.
    /// Все строки БД с одинаковым путём (файл в нескольких коробках) собираются в один список Id.
    /// </summary>
    public static List<RestoreTarget> BuildUnion(
        IEnumerable<KeyValuePair<string, FileAttributes>> journal,
        IEnumerable<(long Id, string FullPath, FileAttributes Added)> dbItems)
    {
        var byPath = new Dictionary<string, (FileAttributes Bits, List<long> Ids)>(StringComparer.OrdinalIgnoreCase);

        foreach (var kv in journal)
            byPath[kv.Key] = (kv.Value & DesktopIconService.HideMask, new List<long>());

        foreach (var it in dbItems)
        {
            if (byPath.TryGetValue(it.FullPath, out var existing))
            {
                existing.Ids.Add(it.Id); // биты из журнала оставляем, накапливаем все id этого пути
            }
            else
            {
                byPath[it.FullPath] = (it.Added & DesktopIconService.HideMask, new List<long> { it.Id });
            }
        }

        return byPath.Select(kv => new RestoreTarget(kv.Key, kv.Value.Bits, kv.Value.Ids)).ToList();
    }
}
