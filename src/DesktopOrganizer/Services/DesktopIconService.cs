using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using DesktopOrganizer.Models;

namespace DesktopOrganizer.Services;

public enum HideResult
{
    Hidden,
    AlreadyHidden,
    NotFound,
    /// <summary>Нет прав менять атрибуты — например, общий ярлык в Public Desktop.</summary>
    AccessDenied,
    Error,
}

/// <summary>
/// «Перемещение» ярлыка с рабочего стола в коробку без физического переноса файла:
/// файлу ставится атрибут Hidden — Проводник перестает показывать его на столе,
/// путь не меняется, OneDrive-синхронизация не затрагивается (раздел 19 ТЗ).
/// При выходе из приложения или удалении элемента из коробки атрибут снимается,
/// и ярлык возвращается на рабочий стол.
/// </summary>
public static class DesktopIconService
{
    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    private const int SHCNE_UPDATEDIR = 0x00001000;
    private const uint SHCNF_PATHW = 0x0005;

    /// <summary>Лежит ли элемент непосредственно на рабочем столе (текущем или Public).</summary>
    public static bool IsOnDesktop(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir == null) return false;
        return AutoSortService.DesktopFolders()
            .Any(d => string.Equals(d, dir, StringComparison.OrdinalIgnoreCase));
    }

    // Hidden + System: файлы только с Hidden видны полупрозрачными, если в Проводнике
    // включен показ скрытых файлов (частая настройка). Пара Hidden|System («защищенный
    // файл ОС», как у desktop.ini) скрывает ярлык полностью, пока выключен ShowSuperHidden.
    private const FileAttributes HideMask = FileAttributes.Hidden | FileAttributes.System;

    /// <summary>Скрывает элемент с рабочего стола.</summary>
    public static HideResult Hide(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return HideResult.NotFound;
            var attrs = File.GetAttributes(path);
            if ((attrs & HideMask) == HideMask) return HideResult.AlreadyHidden; // скрыт не нами — не присваиваем
            File.SetAttributes(path, attrs | HideMask);
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

    /// <summary>
    /// Переносит общие ярлыки (Public Desktop) на личный рабочий стол пользователя:
    /// копия делается без прав, оригиналы удаляются одной командой с повышением (один запрос UAC
    /// на всю пачку). Возвращает элементы, у которых FullPath уже указывает на новое место.
    /// </summary>
    public static List<BoxItem> MigratePublicItemsToUserDesktop(IReadOnlyList<BoxItem> items)
    {
        var userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var ops = new List<(BoxItem Item, string Src, string Dst, bool CopiedByUs)>();

        foreach (var it in items)
        {
            var dst = Path.Combine(userDesktop, Path.GetFileName(it.FullPath));
            try
            {
                var copied = false;
                if (!File.Exists(dst))
                {
                    File.Copy(it.FullPath, dst);
                    copied = true;
                }
                ops.Add((it, it.FullPath, dst, copied));
            }
            catch (Exception ex)
            {
                Logger.Error("MigratePublic.Copy", ex);
            }
        }
        if (ops.Count == 0) return new List<BoxItem>();

        // Одно повышение прав на все удаления.
        var delCmd = string.Join(" & ", ops.Select(o => $"del /f \"{o.Src}\""));
        var psi = new ProcessStartInfo("cmd.exe", "/c " + delCmd)
        {
            Verb = "runas",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        try
        {
            using var p = Process.Start(psi);
            p?.WaitForExit(30000);
        }
        catch (Win32Exception)
        {
            // Пользователь отклонил UAC — откатываем созданные нами копии.
            foreach (var o in ops.Where(o => o.CopiedByUs))
                try { File.Delete(o.Dst); } catch { /* не критично */ }
            Logger.Log("MigratePublic: отменено пользователем (UAC)");
            return new List<BoxItem>();
        }

        var migrated = new List<BoxItem>();
        foreach (var o in ops)
        {
            if (File.Exists(o.Src))
            {
                // Удаление оригинала не прошло — откатываем копию, оставляем ссылку на оригинал.
                if (o.CopiedByUs) try { File.Delete(o.Dst); } catch { }
                Logger.Log($"MigratePublic: оригинал не удален: {o.Src}");
                continue;
            }
            o.Item.FullPath = o.Dst;
            migrated.Add(o.Item);
        }
        Logger.Log($"MigratePublic: перенесено {migrated.Count} из {items.Count}");
        return migrated;
    }

    /// <summary>Возвращает элемент на рабочий стол (снимает атрибуты Hidden и System).</summary>
    public static void Restore(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return;
            var attrs = File.GetAttributes(path);
            if ((attrs & HideMask) != 0)
                File.SetAttributes(path, attrs & ~HideMask);
        }
        catch (Exception ex)
        {
            Logger.Error("DesktopIcon.Restore", ex);
        }
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
