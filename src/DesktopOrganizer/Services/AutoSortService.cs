using System.IO;
using DesktopOrganizer.Data;
using DesktopOrganizer.Models;

namespace DesktopOrganizer.Services;

/// <summary>
/// Автоорганизация рабочего стола по категориям расширений (разделы 4.1, 5.4, 17 ТЗ).
/// Файлы НЕ перемещаются физически — в коробки добавляются только ссылки (раздел 5.3).
/// </summary>
public static class AutoSortService
{
    // Категории по умолчанию, включая инженерные форматы (раздел 17).
    private static readonly (string Category, string[] Extensions)[] Categories =
    {
        ("Документы",   new[] { ".doc", ".docx", ".xls", ".xlsx", ".xlsm", ".pdf", ".txt", ".rtf", ".csv", ".pptx", ".ppt", ".odt" }),
        ("Изображения", new[] { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".svg", ".tif", ".tiff" }),
        ("Чертежи",     new[] { ".dwg", ".dxf", ".rvt", ".ifc", ".nwd", ".nwf", ".rvm", ".dgn", ".plt", ".ctb", ".stb" }),
        ("Архивы",      new[] { ".zip", ".rar", ".7z", ".tar", ".gz" }),
        ("Видео",       new[] { ".mp4", ".avi", ".mov", ".mkv", ".wmv" }),
        ("Скрипты",     new[] { ".py", ".ps1", ".bat", ".cmd", ".js", ".cs", ".pml", ".mac" }),
        ("Ярлыки",      new[] { ".lnk", ".url" }),
        ("Программы",   new[] { ".exe", ".msi" }),
    };

    /// <summary>Сканирует рабочие столы (текущий и Public) и возвращает план: категория → список путей.</summary>
    public static Dictionary<string, List<string>> BuildPlan(Db db)
    {
        var known = db.GetAllItemPaths();
        var plan = new Dictionary<string, List<string>>();

        foreach (var dir in DesktopFolders())
        {
            if (!Directory.Exists(dir)) continue;

            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(dir); // материализуем, чтобы один сбойный entry не сорвал всё
            }
            catch (Exception ex)
            {
                Logger.Error($"AutoSort.Enumerate '{dir}'", ex);
                continue;
            }

            foreach (var entry in entries)
            {
                try
                {
                    var name = Path.GetFileName(entry);
                    if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                    if (known.Contains(entry)) continue;

                    var category = Categorize(entry);
                    if (!plan.TryGetValue(category, out var list))
                        plan[category] = list = new List<string>();
                    list.Add(entry);
                }
                catch (Exception ex)
                {
                    Logger.Error($"AutoSort.Entry '{entry}'", ex);
                }
            }
        }
        return plan;
    }

    public static string Categorize(string path)
    {
        if (Directory.Exists(path)) return "Папки";
        var ext = Path.GetExtension(path).ToLowerInvariant();
        foreach (var (cat, exts) in Categories)
            if (exts.Contains(ext)) return cat;
        return "Прочее";
    }

    public static IEnumerable<string> DesktopFolders()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        if (!string.IsNullOrEmpty(common)) yield return common;
    }

    public static BoxItem CreateItemFromPath(string path, long boxId)
    {
        var isDir = Directory.Exists(path);
        var ext = isDir ? "" : Path.GetExtension(path).ToLowerInvariant();
        var type = isDir ? "Folder"
            : ext == ".lnk" ? "Shortcut"
            : ext == ".url" ? "Url"
            : "File";
        var name = isDir ? Path.GetFileName(path) : Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(name)) name = Path.GetFileName(path);

        return new BoxItem
        {
            BoxId = boxId,
            Name = name,
            FullPath = path,
            ItemType = type,
            Extension = ext,
            IsMissing = !isDir && !File.Exists(path),
        };
    }
}
