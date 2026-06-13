using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using DesktopOrganizer.Models;

namespace DesktopOrganizer.Services;

public enum HideResult
{
    /// <summary>Мы добавили хотя бы один атрибут (см. out added).</summary>
    Hidden,
    /// <summary>Файл уже был скрыт (пользователем или прошлым сеансом) — мы ничего не добавляли.</summary>
    AlreadyHidden,
    NotFound,
    /// <summary>Нет прав менять атрибуты — например, общий ярлык в Public Desktop.</summary>
    AccessDenied,
    Error,
}

public enum RestoreResult
{
    /// <summary>Атрибуты сняты успешно.</summary>
    Restored,
    /// <summary>Файла больше нет — восстанавливать нечего, путь можно забыть.</summary>
    FileGone,
    /// <summary>Файл есть, но снять атрибуты не удалось — НЕ терять из recovery-списка.</summary>
    Failed,
}

/// <summary>
/// «Перемещение» ярлыка с рабочего стола в коробку без физического переноса файла:
/// файлу добавляются атрибуты Hidden+System — Проводник перестаёт показывать его на столе,
/// путь не меняется, OneDrive-синхронизация не затрагивается (раздел 19 ТЗ).
/// Снимаются ТОЛЬКО те биты, которые поставило приложение (исходные атрибуты пользователя
/// не портятся). При выходе/удалении из коробки ярлык возвращается на рабочий стол.
/// </summary>
public static class DesktopIconService
{
    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    private const int SHCNE_UPDATEDIR = 0x00001000;
    private const uint SHCNF_PATHW = 0x0005;

    // Hidden+System: только Hidden оставляет ярлык видимым (полупрозрачным), если включён показ
    // скрытых файлов. Пара Hidden|System («защищённый файл ОС») прячет его, пока выключен
    // ShowSuperHidden (по умолчанию выключен).
    public const FileAttributes HideMask = FileAttributes.Hidden | FileAttributes.System;

    /// <summary>Лежит ли элемент непосредственно на рабочем столе (текущем или Public).</summary>
    public static bool IsOnDesktop(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir == null) return false;
        return AutoSortService.DesktopFolders()
            .Any(d => string.Equals(d, dir, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Символьная ссылка / junction / точка повторного разбора.</summary>
    public static bool IsReparsePoint(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return false;
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch { return false; }
    }

    /// <summary>
    /// Скрывает элемент с рабочего стола, добавляя ТОЛЬКО недостающие биты Hidden/System.
    /// <paramref name="added"/> — какие биты реально добавлены (их и снимать при возврате).
    /// </summary>
    public static HideResult Hide(string path, out FileAttributes added)
    {
        added = 0;
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return HideResult.NotFound;
            var attrs = File.GetAttributes(path);
            var toAdd = HideMask & ~attrs;
            if (toAdd == 0) return HideResult.AlreadyHidden; // уже скрыт — не трогаем чужие биты
            File.SetAttributes(path, attrs | toAdd);
            added = toAdd;
            return HideResult.Hidden;
        }
        catch (UnauthorizedAccessException)
        {
            Logger.Log($"Нет прав скрыть (общий рабочий стол?): {path}");
            return HideResult.AccessDenied;
        }
        catch (Exception ex)
        {
            Logger.Error("DesktopIcon.Hide", ex);
            return HideResult.Error;
        }
    }

    /// <summary>Возвращает элемент на стол, снимая РОВНО те биты, что поставило приложение.</summary>
    public static RestoreResult Restore(string path, FileAttributes added)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return RestoreResult.FileGone;
            if (added != 0)
            {
                var attrs = File.GetAttributes(path);
                File.SetAttributes(path, attrs & ~added);
            }
            return RestoreResult.Restored;
        }
        catch (Exception ex)
        {
            Logger.Error("DesktopIcon.Restore", ex);
            return RestoreResult.Failed;
        }
    }

    /// <summary>
    /// Переносит общие ярлыки (Public Desktop) на личный рабочий стол: копия всегда делается
    /// под уникальным именем (никогда не подменяем чужой существующий файл), затем оригиналы
    /// удаляются одной командой с повышением прав (один запрос UAC на пачку).
    /// Возвращает элементы, у которых FullPath уже указывает на новое место.
    /// </summary>
    public static List<BoxItem> MigratePublicItemsToUserDesktop(IReadOnlyList<BoxItem> items)
    {
        var userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var ops = new List<(BoxItem Item, string Src, string Dst)>();

        foreach (var it in items)
        {
            try
            {
                var dst = UniqueDestination(userDesktop, Path.GetFileName(it.FullPath));
                File.Copy(it.FullPath, dst); // dst гарантированно не существует — чужой файл не затрагиваем
                ops.Add((it, it.FullPath, dst));
            }
            catch (Exception ex)
            {
                Logger.Error("MigratePublic.Copy", ex);
            }
        }
        if (ops.Count == 0) return new List<BoxItem>();

        if (!DeleteElevated(ops.Select(o => o.Src)))
        {
            // UAC отклонён или удаление не запустилось — откатываем наши копии целиком.
            RollbackCopies(ops.Select(o => o.Dst));
            return new List<BoxItem>();
        }

        var migrated = new List<BoxItem>();
        foreach (var o in ops)
        {
            if (File.Exists(o.Src))
            {
                // Конкретный оригинал не удалился — откатываем его копию, оставляем ссылку на оригинал.
                TryDelete(o.Dst);
                Logger.Log($"MigratePublic: оригинал не удалён: {o.Src}");
                continue;
            }
            o.Item.FullPath = o.Dst;
            migrated.Add(o.Item);
        }
        Logger.Log($"MigratePublic: перенесено {migrated.Count} из {items.Count}");
        return migrated;
    }

    private static string UniqueDestination(string dir, string fileName)
    {
        var dst = Path.Combine(dir, fileName);
        if (!File.Exists(dst) && !Directory.Exists(dst)) return dst;
        var name = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 1; ; i++)
        {
            dst = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(dst) && !Directory.Exists(dst)) return dst;
        }
    }

    /// <summary>
    /// Удаляет переданные пути с повышением прав. Список пишется во временный файл и
    /// удаляется через PowerShell -LiteralPath — без склейки и экранирования в командной строке.
    /// </summary>
    private static bool DeleteElevated(IEnumerable<string> paths)
    {
        var listFile = Path.Combine(Path.GetTempPath(), $"ied_del_{Guid.NewGuid():N}.txt");
        try
        {
            File.WriteAllLines(listFile, paths, new UTF8Encoding(false));
            var script =
                $"Get-Content -LiteralPath '{listFile}' -Encoding UTF8 | " +
                "ForEach-Object { if ($_){ Remove-Item -LiteralPath $_ -Force -ErrorAction SilentlyContinue } }";
            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"{script}\"")
            {
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            if (!p.WaitForExit(30000))
            {
                Logger.Log("MigratePublic: удаление с повышением прав не завершилось за 30с");
                return false;
            }
            return true; // фактический успех проверяется по File.Exists каждого оригинала
        }
        catch (Win32Exception)
        {
            Logger.Log("MigratePublic: повышение прав отклонено пользователем (UAC)");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error("MigratePublic.DeleteElevated", ex);
            return false;
        }
        finally
        {
            TryDelete(listFile);
        }
    }

    private static void RollbackCopies(IEnumerable<string> copies)
    {
        foreach (var c in copies) TryDelete(c);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* не критично */ }
    }

    /// <summary>Просит Проводник перечитать папки рабочего стола (иначе иконки обновятся не сразу).</summary>
    public static void RefreshDesktop()
    {
        foreach (var dir in AutoSortService.DesktopFolders())
        {
            if (!Directory.Exists(dir)) continue;
            var ptr = Marshal.StringToHGlobalUni(dir);
            try { SHChangeNotify(SHCNE_UPDATEDIR, SHCNF_PATHW, ptr, IntPtr.Zero); }
            finally { Marshal.FreeHGlobal(ptr); }
        }
    }
}
