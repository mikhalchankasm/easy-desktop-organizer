using System.IO;
using DesktopOrganizer.Services;
using Xunit;

namespace DesktopOrganizer.Tests;

public class RecoveryPlanTests
{
    private const FileAttributes HideMask = FileAttributes.Hidden | FileAttributes.System;

    private static RestoreTarget Target(string path, params long[] ids) =>
        new(path, HideMask, ids);

    [Fact]
    public void AllRestored_ClearsEveryId_AndSucceeds()
    {
        var plan = new[] { Target(@"C:\a", 1), Target(@"C:\b", 2, 3) };
        var r = RecoveryPlan.Resolve(
            plan,
            new[] { RestoreOutcome.Restored, RestoreOutcome.Restored },
            journalReadOk: true, dbReadOk: true);

        Assert.Equal(0, r.Failed);
        Assert.True(r.Success);
        Assert.Equal(new long[] { 1, 2, 3 }, r.ClearedIds);
    }

    [Fact]
    public void FileGone_CountsAsRestored_AndClearsIds()
    {
        var plan = new[] { Target(@"C:\a", 1) };
        var r = RecoveryPlan.Resolve(
            plan, new[] { RestoreOutcome.FileGone },
            journalReadOk: true, dbReadOk: true);

        Assert.Equal(0, r.Failed);
        Assert.True(r.Success);
        Assert.Equal(new long[] { 1 }, r.ClearedIds);
    }

    [Fact]
    public void Failed_NotCleared_AndReportsFailure()
    {
        var plan = new[] { Target(@"C:\a", 1), Target(@"C:\b", 2) };
        var r = RecoveryPlan.Resolve(
            plan, new[] { RestoreOutcome.Failed, RestoreOutcome.Restored },
            journalReadOk: true, dbReadOk: true);

        Assert.Equal(1, r.Failed);
        Assert.False(r.Success);
        Assert.Equal(new long[] { 2 }, r.ClearedIds); // id неудачного НЕ чистится
    }

    [Fact]
    public void JournalReadFailed_IsMinusOne_AndClearsNothing()
    {
        var plan = new[] { Target(@"C:\a", 1) };
        var r = RecoveryPlan.Resolve(
            plan, new[] { RestoreOutcome.Restored },
            journalReadOk: false, dbReadOk: true);

        Assert.Equal(-1, r.Failed);
        Assert.False(r.Success);
        Assert.Empty(r.ClearedIds);
    }

    [Fact]
    public void DbReadFailed_IsMinusOne_AndClearsNothing()
    {
        // Сбой чтения БД → не рапортуем успех (деинсталлятор не сотрёт данные) И ничего не чистим:
        // в реальной интеграции при таком сбое план строится только из журнала, у journal-only
        // targets ItemIds пусты. Resolve фиксирует тот же инвариант даже если ему передали id.
        var plan = new[] { Target(@"C:\a", 1) }; // у реального journal-only target id бы не было
        var r = RecoveryPlan.Resolve(
            plan, new[] { RestoreOutcome.Restored },
            journalReadOk: true, dbReadOk: false);

        Assert.Equal(-1, r.Failed);
        Assert.False(r.Success);
        Assert.Empty(r.ClearedIds);
    }

    [Fact]
    public void MismatchedLengths_Throws()
    {
        var plan = new[] { Target(@"C:\a", 1) };
        Assert.Throws<ArgumentException>(() =>
            RecoveryPlan.Resolve(plan, System.Array.Empty<RestoreOutcome>(), true, true));
    }
}
