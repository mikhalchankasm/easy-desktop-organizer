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

            // Durable-запись ДО мутации. Если журнал не записан на диск — НЕ скрываем файл,
            // иначе при падении до OnExit получится «осиротевший» скрытый файл без recovery-записи.
            if (!HideJournal.Record(path, toAdd))
            {
                Logger.Log($"Hide: журнал не записан, атрибуты не меняем: {path}");
                return HideResult.Error;
            }
            try
            {
                File.SetAttributes(path, attrs | toAdd);
                added = toAdd;
                return HideResult.Hidden;
            }
            catch
            {
                HideJournal.Remove(path); // не применили — снимаем запись из журнала
                throw;
            }
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
    /// Возвращает элемент на стол, снимая РОВНО те биты, что поставило приложение.
    /// Журнал авторитетнее переданного <paramref name="added"/> (тот мог устареть в БД).
    /// </summary>
    public static RestoreResult Restore(string path, FileAttributes added)
    {
        try
        {
            // Журнал авторитетнее БД. Но если он НЕ прочитан — не угадываем биты из БД
            // (можно ошибочно признать restore успешным, не сняв атрибуты): считаем неудачей.
            var lk = HideJournal.Lookup(path, out var journaled);
            if (lk == JournalLookup.ReadFailed)
            {
                Logger.Log($"Restore: журнал не прочитан — откладываем восстановление: {path}");
                return RestoreResult.Failed;
            }
            var bits = lk == JournalLookup.Found ? journaled : added; // NotFound → запасной вариант из БД
            // Защита от повреждённого источника (особенно fallback из БД): снимаем ТОЛЬКО Hidden/System,
            // никогда произвольные атрибуты.
            var masked = bits & HideMask;
            if (masked != bits)
                Logger.Log($"Restore: биты вне HideMask отброшены ({bits} → {masked}): {path}");
            bits = masked;

            if (!File.Exists(path) && !Directory.Exists(path))
            {
                // Файл удалён. Если запись журнала не убрать — НЕ считаем успехом: иначе stale-запись
                // позже снимет Hidden/System с нового файла, появившегося по тому же пути.
                if (!HideJournal.Remove(path))
                {
                    Logger.Log($"Restore: файл отсутствует, но журнал не очищен — повторим позже: {path}");
                    return RestoreResult.Failed;
                }
                return RestoreResult.FileGone;
            }
            if (bits != 0)
            {
                var attrs = File.GetAttributes(path);
                File.SetAttributes(path, attrs & ~bits);
            }
            if (!HideJournal.Remove(path))
            {
                // Запись журнала не убрана — возвращаем файл в скрытое состояние, чтобы инвариант
                // «запись в журнале ⟺ файл скрыт нами» сохранялся и повторный recovery не снял
                // потом потенциально пользовательские биты. Считаем восстановление неуспешным.
                Logger.Log($"Restore: журнал не очищен, возвращаем файл в скрытое состояние: {path}");
                try
                {
                    if (bits != 0)
                    {
                        var a = File.GetAttributes(path);
                        File.SetAttributes(path, a | bits);
                    }
                }
                catch (Exception ex) { Logger.Error("Restore.re-hide", ex); }
                return RestoreResult.Failed;
            }
            return RestoreResult.Restored;
        }
        catch (Exception ex)
        {
            Logger.Error("DesktopIcon.Restore", ex);
            return RestoreResult.Failed; // запись остаётся в журнале — восстановим позже
        }
    }


    /// <summary>Скопированный на личный стол элемент: модель, исходный (Public) путь, путь копии.</summary>
    public readonly record struct CopyOp(BoxItem Item, string Src, string Dst);

    public enum DeleteElevatedResult
    {
        /// <summary>Процесс удаления завершился — фактический итог сверять по File.Exists каждого пути.</summary>
        Completed,
        /// <summary>Пользователь отклонил UAC или процесс не запустился — НИЧЕГО не удалено.</summary>
        Declined,
        /// <summary>Процесс не завершился за таймаут — мог удалить позже, откатывать НЕЛЬЗЯ.</summary>
        TimedOut,
        Error,
    }

    /// <summary>
    /// Копирует элементы на личный рабочий стол под уникальными именами (чужой файл не подменяем).
    /// Оригиналы НЕ удаляет — это отдельный шаг (см. DeleteElevated). Возвращает выполненные копии.
    /// </summary>
    public static List<CopyOp> CopyToUserDesktop(IReadOnlyList<BoxItem> items)
    {
        var userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var ops = new List<CopyOp>();
        foreach (var it in items)
        {
            try
            {
                var dst = UniqueDestination(userDesktop, Path.GetFileName(it.FullPath));
                File.Copy(it.FullPath, dst); // dst гарантированно не существует
                ops.Add(new CopyOp(it, it.FullPath, dst));
            }
            catch (Exception ex)
            {
                Logger.Error("CopyToUserDesktop", ex);
            }
        }
        return ops;
    }

    public static void TryDelete(string path)
    {
        try { if (File.Exists(path) || Directory.Exists(path)) File.Delete(path); } catch { /* не критично */ }
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
    /// Удаляет переданные пути с повышением прав. Список и скрипт пишутся во временные файлы,
    /// удаление идёт через PowerShell -LiteralPath (без склейки/экранирования в командной строке —
    /// безопасно даже если путь профиля содержит апостроф).
    /// </summary>
    public static DeleteElevatedResult DeleteElevated(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        if (list.Count == 0) return DeleteElevatedResult.Completed;

        var listFile = Path.Combine(Path.GetTempPath(), $"ied_del_{Guid.NewGuid():N}.txt");
        var scriptFile = Path.Combine(Path.GetTempPath(), $"ied_del_{Guid.NewGuid():N}.ps1");
        var result = DeleteElevatedResult.Error;
        try
        {
            File.WriteAllLines(listFile, list, new UTF8Encoding(false));
            // Путь к списку встраиваем в .ps1 как литерал с экранированием апострофа ('→'').
            var listLiteral = listFile.Replace("'", "''");
            File.WriteAllText(scriptFile,
                $"Get-Content -LiteralPath '{listLiteral}' -Encoding UTF8 | " +
                "ForEach-Object { if ($_) { Remove-Item -LiteralPath $_ -Force -ErrorAction SilentlyContinue } }",
                new UTF8Encoding(false));

            var psi = new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptFile}\"")
            {
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi);
            if (p == null) return result = DeleteElevatedResult.Error;
            if (!p.WaitForExit(60000))
            {
                Logger.Log("DeleteElevated: процесс не завершился за 60с");
                return result = DeleteElevatedResult.TimedOut;
            }
            return result = DeleteElevatedResult.Completed;
        }
        catch (Win32Exception)
        {
            Logger.Log("DeleteElevated: повышение прав отклонено (UAC)");
            return result = DeleteElevatedResult.Declined;
        }
        catch (Exception ex)
        {
            Logger.Error("DeleteElevated", ex);
            return result = DeleteElevatedResult.Error;
        }
        finally
        {
            // На таймауте процесс может ещё читать файлы — временные файлы не трогаем.
            if (result != DeleteElevatedResult.TimedOut)
            {
                TryDelete(listFile);
                TryDelete(scriptFile);
            }
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
