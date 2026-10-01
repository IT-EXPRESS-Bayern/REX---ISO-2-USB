// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images.Integrity;

namespace Bootrix.Core.Tests.Images.Integrity;

public class VolumePathsTests
{
    private static readonly string[] OtherDrives = [@"C:\", @"D:\", @"F:\"];

    [Theory]
    [InlineData(@"E:\iso\ubuntu.iso", @"E:\", true)]
    [InlineData(@"e:\iso\ubuntu.iso", @"E:\", true)]
    [InlineData(@"E:/iso/ubuntu.iso", @"E:", true)]
    [InlineData(@"\\?\E:\iso\ubuntu.iso", @"E:\", true)]
    [InlineData(@"E:\iso\ubuntu.iso", @"\\?\E:\", true)]
    [InlineData(@"D:\iso\ubuntu.iso", @"E:\", false)]
    [InlineData(@"E:\", @"E:\", true)]
    [InlineData(@"EE:\iso", @"E:\", false)]
    public void IsImageOnTarget_ComparesDriveSpellings(string image, string targetRoot, bool expected)
    {
        Assert.Equal(expected, ImageIntegrityChecker.IsImageOnTarget(image, [targetRoot], OtherDrives));
    }

    [Fact]
    public void IsImageOnTarget_ImageOnAVolumeMountedBelowTheTarget_BelongsToTheMountedVolume()
    {
        // The USB stick is E:, but a data disk is mounted at E:\mnt\data; an image there is not on the stick.
        string[] target = [@"E:\"];
        string[] others = [@"C:\", @"E:\mnt\data"];

        Assert.False(ImageIntegrityChecker.IsImageOnTarget(@"E:\mnt\data\images\a.iso", target, others));
        Assert.True(ImageIntegrityChecker.IsImageOnTarget(@"E:\images\a.iso", target, others));
    }

    [Fact]
    public void IsImageOnTarget_TargetMountedBelowAnotherVolume_OwnsItsFiles()
    {
        string[] target = [@"C:\Mounts\Stick"];
        string[] others = [@"C:\"];

        Assert.True(ImageIntegrityChecker.IsImageOnTarget(@"C:\Mounts\Stick\a.iso", target, others));
        Assert.False(ImageIntegrityChecker.IsImageOnTarget(@"C:\Mounts\Stick2\a.iso", target, others));
        Assert.False(ImageIntegrityChecker.IsImageOnTarget(@"C:\Users\me\a.iso", target, others));
    }

    [Fact]
    public void IsImageOnTarget_AnyRootOfTheTargetCounts()
    {
        string[] target = [@"E:\", @"\\?\Volume{12345678-1234-1234-1234-123456789abc}\"];

        Assert.True(ImageIntegrityChecker.IsImageOnTarget(@"\\?\Volume{12345678-1234-1234-1234-123456789ABC}\dir\a.iso", target));
        Assert.True(ImageIntegrityChecker.IsImageOnTarget(@"E:\a.iso", target));
        Assert.False(ImageIntegrityChecker.IsImageOnTarget(@"F:\a.iso", target));
    }

    [Fact]
    public void IsImageOnTarget_NetworkShare_IsNeverOnALocalTarget()
    {
        Assert.False(ImageIntegrityChecker.IsImageOnTarget(@"\\nas\share\a.iso", [@"E:\"], [@"C:\"]));
        Assert.True(ImageIntegrityChecker.IsImageOnTarget(@"\\?\UNC\nas\share\a.iso", [@"\\nas\share"]));
    }

    [Fact]
    public void IsImageOnTarget_NoTargetRoots_IsFalse()
    {
        Assert.False(ImageIntegrityChecker.IsImageOnTarget(@"E:\a.iso", []));
        Assert.False(ImageIntegrityChecker.IsImageOnTarget(@"E:\a.iso", [""]));
    }

    [Theory]
    [InlineData(@"C:\", @"c:", true)]
    [InlineData(@"\\?\C:\", @"C:\", true)]
    [InlineData(@"C:\", @"D:\", false)]
    public void IsSameVolume_IgnoresSpelling(string left, string right, bool expected) =>
        Assert.Equal(expected, ImageIntegrityChecker.IsSameVolume(left, right));
}
