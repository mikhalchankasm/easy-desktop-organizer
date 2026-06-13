using System.IO;
using DesktopOrganizer.Services;
using Xunit;

namespace DesktopOrganizer.Tests;

public class JournalFormatTests
{
    private const FileAttributes HideMask = FileAttributes.Hidden | FileAttributes.System;

    [Fact]
    public void FormatThenParse_RoundTrips()
    {
        var line = JournalFormat.FormatLine(@"C:\d\a.lnk", HideMask);
        Assert.True(JournalFormat.TryParseLine(line, out var path, out var bits));
        Assert.Equal(@"C:\d\a.lnk", path);
        Assert.Equal(HideMask, bits);
    }

    [Fact]
    public void FormatLine_HasNoTrailingNewline()
    {
        var line = JournalFormat.FormatLine(@"C:\d\a.lnk", FileAttributes.Hidden);
        Assert.Equal($"{(int)FileAttributes.Hidden}\tC:\\d\\a.lnk", line);
    }

    [Theory]
    [InlineData("")]                       // нет таба (пустую строку фильтрует вызывающий, но TryParseLine тоже не примет)
    [InlineData("6")]                      // нет таба/пути
    [InlineData("\tC:\\d\\a.lnk")]         // пустые биты слева
    [InlineData("6\t")]                    // пустой путь
    [InlineData("abc\tC:\\d\\a.lnk")]      // биты не число
    [InlineData("0\tC:\\d\\a.lnk")]        // нулевые биты
    [InlineData("1\tC:\\d\\a.lnk")]        // ReadOnly — вне маски
    [InlineData("7\tC:\\d\\a.lnk")]        // Hidden|System|ReadOnly — примесь
    public void TryParseLine_RejectsMalformedOrForeignBits(string line)
    {
        Assert.False(JournalFormat.TryParseLine(line, out _, out _));
    }

    [Fact]
    public void TryParse_SkipsBlankLines_AndAccumulates()
    {
        var lines = new[]
        {
            JournalFormat.FormatLine(@"C:\d\a.lnk", HideMask),
            "",
            JournalFormat.FormatLine(@"C:\d\b.lnk", FileAttributes.Hidden),
        };
        Assert.True(JournalFormat.TryParse(lines, out var map));
        Assert.Equal(2, map.Count);
        Assert.Equal(HideMask, map[@"C:\d\a.lnk"]);
        Assert.Equal(FileAttributes.Hidden, map[@"C:\d\b.lnk"]);
    }

    [Fact]
    public void TryParse_OneBadLine_FailsClosed_EmptyMap()
    {
        var lines = new[]
        {
            JournalFormat.FormatLine(@"C:\d\a.lnk", HideMask),
            "7\tC:\\d\\bad.lnk", // примесь ReadOnly → весь разбор прерывается
        };
        Assert.False(JournalFormat.TryParse(lines, out var map));
        Assert.Empty(map); // ничего не возвращаем частично — не теряем записи молча
    }

    [Fact]
    public void TryParse_IsCaseInsensitiveOnPaths()
    {
        var lines = new[]
        {
            JournalFormat.FormatLine(@"C:\d\A.lnk", HideMask),
            JournalFormat.FormatLine(@"c:\d\a.lnk", FileAttributes.Hidden),
        };
        Assert.True(JournalFormat.TryParse(lines, out var map));
        Assert.Single(map); // тот же путь в разном регистре
    }
}
