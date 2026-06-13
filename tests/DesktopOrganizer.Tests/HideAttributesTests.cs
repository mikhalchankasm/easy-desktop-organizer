using System.IO;
using DesktopOrganizer.Services;
using Xunit;

namespace DesktopOrganizer.Tests;

public class HideAttributesTests
{
    [Theory]
    [InlineData(FileAttributes.Hidden | FileAttributes.System)] // полный набор
    [InlineData(FileAttributes.Hidden)]                          // только Hidden
    [InlineData(FileAttributes.System)]                          // только System
    public void IsValidAddedBits_AcceptsNonEmptySubsetOfMask(FileAttributes bits)
    {
        Assert.True(HideAttributes.IsValidAddedBits(bits));
    }

    [Theory]
    [InlineData((FileAttributes)0)]                                          // пусто
    [InlineData(FileAttributes.ReadOnly)]                                    // вне маски
    [InlineData(FileAttributes.Hidden | FileAttributes.ReadOnly)]            // примесь к допустимым
    [InlineData(FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive)]
    public void IsValidAddedBits_RejectsZeroOrForeignBits(FileAttributes bits)
    {
        Assert.False(HideAttributes.IsValidAddedBits(bits));
    }

    [Fact]
    public void Mask_IsHiddenPlusSystem()
    {
        Assert.Equal(FileAttributes.Hidden | FileAttributes.System, HideAttributes.Mask);
    }
}
