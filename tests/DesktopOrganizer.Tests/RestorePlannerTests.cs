using System.IO;
using DesktopOrganizer.Services;
using Xunit;

namespace DesktopOrganizer.Tests;

public class RestorePlannerTests
{
    private const FileAttributes HideMask = FileAttributes.Hidden | FileAttributes.System;

    private static Dictionary<string, FileAttributes> Journal(params (string Path, FileAttributes Bits)[] e)
    {
        var d = new Dictionary<string, FileAttributes>(StringComparer.OrdinalIgnoreCase);
        foreach (var (p, b) in e) d[p] = b;
        return d;
    }

    [Fact]
    public void DbFallback_UsedWhenJournalEmpty()
    {
        var plan = RestorePlanner.BuildUnion(
            Journal(),
            new[] { (1L, @"C:\d\a.lnk", HideMask) });

        var t = Assert.Single(plan);
        Assert.Equal(@"C:\d\a.lnk", t.Path);
        Assert.Equal(HideMask, t.Bits);
        Assert.Equal(new long[] { 1 }, t.ItemIds);
    }

    [Fact]
    public void DbBits_AreMaskedToHideMask()
    {
        var garbage = HideMask | FileAttributes.ReadOnly | FileAttributes.Archive;
        var plan = RestorePlanner.BuildUnion(
            Journal(),
            new[] { (1L, @"C:\d\a.lnk", garbage) });

        Assert.Equal(HideMask, Assert.Single(plan).Bits); // ReadOnly/Archive отброшены
    }

    [Fact]
    public void DuplicatePaths_CollectAllItemIds()
    {
        var plan = RestorePlanner.BuildUnion(
            Journal(),
            new[] { (1L, @"C:\d\a.lnk", HideMask), (2L, @"C:\d\a.lnk", HideMask) });

        var t = Assert.Single(plan);
        Assert.Equal(2, t.ItemIds.Count);
        Assert.Contains(1L, t.ItemIds);
        Assert.Contains(2L, t.ItemIds);
    }

    [Fact]
    public void JournalBits_WinOverDb_AndDbIdCollected()
    {
        var plan = RestorePlanner.BuildUnion(
            Journal((@"C:\d\a.lnk", FileAttributes.Hidden)),
            new[] { (5L, @"C:\d\a.lnk", HideMask) });

        var t = Assert.Single(plan);
        Assert.Equal(FileAttributes.Hidden, t.Bits);      // приоритет у журнала
        Assert.Equal(new long[] { 5 }, t.ItemIds);        // id из БД всё равно собран
    }

    [Fact]
    public void JournalOnly_HasNoItemIds()
    {
        var plan = RestorePlanner.BuildUnion(
            Journal((@"C:\d\a.lnk", HideMask)),
            Array.Empty<(long, string, FileAttributes)>());

        Assert.Empty(Assert.Single(plan).ItemIds);
    }

    [Fact]
    public void DbAddedZero_ProducesZeroBitsTarget()
    {
        // AddedAttributes=0 в БД (потеря/повреждение) → Bits=0. План это пропускает как есть;
        // защиту «файл всё ещё скрыт → Failed» обеспечивает DesktopIconService.Restore.
        var plan = RestorePlanner.BuildUnion(
            Journal(),
            new[] { (1L, @"C:\d\a.lnk", (FileAttributes)0) });

        Assert.Equal((FileAttributes)0, Assert.Single(plan).Bits);
    }

    [Fact]
    public void PathMatch_IsCaseInsensitive()
    {
        var plan = RestorePlanner.BuildUnion(
            Journal((@"C:\d\A.lnk", HideMask)),
            new[] { (1L, @"c:\d\a.lnk", HideMask) });

        var t = Assert.Single(plan);          // один и тот же путь в разном регистре → один target
        Assert.Equal(new long[] { 1 }, t.ItemIds);
    }
}
