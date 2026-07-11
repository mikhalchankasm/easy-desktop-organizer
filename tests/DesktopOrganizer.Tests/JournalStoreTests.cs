using System.IO;
using DesktopOrganizer.Services;
using Xunit;

namespace DesktopOrganizer.Tests;

/// <summary>
/// Файловый слой durable-журнала (JournalStore в Core) — самая дорогая нетестированная часть
/// ядра «безопасности данных»: mutex, fsync, атомарная замена, fail-closed. Тестируется на
/// temp-каталоге без Win32/WPF и без сборки WinExe.
/// </summary>
public class JournalStoreTests : IDisposable
{
    private static readonly FileAttributes HideMask = FileAttributes.Hidden | FileAttributes.System;

    private readonly string _dir;
    private readonly string _file;
    private int _id;

    public JournalStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"doj_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "hide-journal.txt");
    }

    // Уникальное имя mutex на каждый тест — иначе параллельные тесты (xUnit) дерутся за один mutex.
    private JournalStore NewStore() =>
        new(_file, $@"Local\doj_test_{_id++}_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* temp мог уже убраться */ }
    }

    [Fact]
    public void NoFile_LookupReturnsNotFound_TryGetAllReturnsEmptyTrue()
    {
        var s = NewStore();
        Assert.Equal(JournalLookup.NotFound, s.Lookup(@"C:\x\a.lnk", out var bits));
        Assert.Equal(FileAttributes.None, bits);
        Assert.True(s.TryGetAll(out var entries));
        Assert.Empty(entries);
    }

    [Fact]
    public void RecordThenLookup_FoundWithBits()
    {
        var s = NewStore();
        Assert.True(s.Record(@"C:\d\a.lnk", HideMask));
        Assert.Equal(JournalLookup.Found, s.Lookup(@"C:\d\a.lnk", out var bits));
        Assert.Equal(HideMask, bits);
    }

    [Fact]
    public void RecordThenRemove_LookupReturnsNotFound()
    {
        var s = NewStore();
        Assert.True(s.Record(@"C:\d\a.lnk", FileAttributes.Hidden));
        Assert.True(s.Remove(@"C:\d\a.lnk"));
        Assert.Equal(JournalLookup.NotFound, s.Lookup(@"C:\d\a.lnk", out _));
    }

    [Fact]
    public void RemoveNonexistent_ReturnsTrue()
    {
        var s = NewStore();
        // Удаления того, чего не было, — успех (записи нет и так).
        Assert.True(s.Remove(@"C:\never\here.lnk"));
    }

    [Fact]
    public void RecordPersistsAcrossNewStoreInstance()
    {
        // Durability: новый экземпляр (как при перезапуске приложения) читает ту же запись.
        Assert.True(NewStore().Record(@"C:\d\a.lnk", HideMask));
        var fresh = NewStore();
        Assert.Equal(JournalLookup.Found, fresh.Lookup(@"C:\d\a.lnk", out var bits));
        Assert.Equal(HideMask, bits);
        Assert.True(fresh.TryGetAll(out var entries));
        Assert.Single(entries);
    }

    [Fact]
    public void RecordOverwritesSamePath()
    {
        var s = NewStore();
        Assert.True(s.Record(@"C:\d\a.lnk", FileAttributes.Hidden));
        Assert.True(s.Record(@"C:\d\a.lnk", HideMask));
        Assert.Equal(JournalLookup.Found, s.Lookup(@"C:\d\a.lnk", out var bits));
        Assert.Equal(HideMask, bits); // вторая запись затёрла первую
        Assert.True(s.TryGetAll(out var entries));
        Assert.Single(entries); // дубликата не появилось
    }

    [Fact]
    public void MultiplePaths_AllRoundTripCaseInsensitive()
    {
        var s = NewStore();
        Assert.True(s.Record(@"C:\d\a.lnk", HideMask));
        Assert.True(s.Record(@"C:\d\b.lnk", FileAttributes.System));
        Assert.True(s.TryGetAll(out var entries));
        Assert.Equal(2, entries.Count);
        // Lookup нечувствителен к регистру пути.
        Assert.Equal(JournalLookup.Found, s.Lookup(@"c:\D\A.lnk", out _));
    }

    [Fact]
    public void Record_ForeignBits_ReturnsFalse()
    {
        var s = NewStore();
        // ReadOnly (1) — вне маски; Hidden|System|ReadOnly (7) — примесь. Оба отклоняются (fail-closed).
        Assert.False(s.Record(@"C:\d\a.lnk", FileAttributes.ReadOnly));
        Assert.False(s.Record(@"C:\d\a.lnk", (FileAttributes)7));
        Assert.Equal(JournalLookup.NotFound, s.Lookup(@"C:\d\a.lnk", out _));
        Assert.True(s.TryGetAll(out var entries));
        Assert.Empty(entries);
    }

    [Fact]
    public void Record_ZeroBits_ReturnsFalse()
    {
        var s = NewStore();
        Assert.False(s.Record(@"C:\d\a.lnk", FileAttributes.None));
        Assert.True(s.TryGetAll(out var entries));
        Assert.Empty(entries);
    }

    [Fact]
    public void Record_EmptyOrNullPath_ReturnsFalse()
    {
        var s = NewStore();
        Assert.False(s.Record("", HideMask));
        Assert.False(s.Record(null!, HideMask));
    }

    [Fact]
    public void CorruptedLine_LookupReturnsReadFailed_FailClosed()
    {
        // Битая строка (примесь ReadOnly) трактуется как сбой чтения — НЕ как «записей нет».
        File.WriteAllLines(_file, new[] { "7\tC:\\d\\bad.lnk" });
        var s = NewStore();
        Assert.Equal(JournalLookup.ReadFailed, s.Lookup(@"C:\d\bad.lnk", out _));
        Assert.False(s.TryGetAll(out var entries));
        Assert.Empty(entries);
    }

    [Fact]
    public void CorruptedLine_BlocksMutation_FailClosed()
    {
        // Если журнал не читается — мутация отменяется: существующая (пусть битая) запись не теряется.
        File.WriteAllLines(_file, new[] { "7\tC:\\d\\bad.lnk" });
        var s = NewStore();
        Assert.False(s.Record(@"C:\d\good.lnk", HideMask));
        // Файл не перезаписан вслепую.
        var lines = File.ReadAllLines(_file);
        Assert.Single(lines);
        Assert.Contains("bad.lnk", lines[0]);
    }

    [Fact]
    public void GoodThenBadLine_LoadFails_DoesNotPartialRead()
    {
        // Хорошая + плохая строки: разбор прерывается, ничего частично не возвращаем.
        File.WriteAllLines(_file, new[] { "6\tC:\\d\\a.lnk", "7\tC:\\d\\bad.lnk" });
        var s = NewStore();
        Assert.False(s.TryGetAll(out var entries));
        Assert.Empty(entries);
    }

    [Fact]
    public void BlankLinesAreIgnored()
    {
        var lines = new[] { "", "6\tC:\\d\\a.lnk", "", "" };
        File.WriteAllLines(_file, lines);
        var s = NewStore();
        Assert.True(s.TryGetAll(out var entries));
        Assert.Single(entries);
        Assert.Equal(JournalLookup.Found, s.Lookup(@"C:\d\a.lnk", out _));
    }
}
