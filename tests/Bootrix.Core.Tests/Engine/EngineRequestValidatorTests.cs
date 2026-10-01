// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Engine;
using Bootrix.Core.Errors;

namespace Bootrix.Core.Tests.Engine;

public class EngineRequestValidatorTests
{
    private sealed record UnregisteredRequest : EngineJobRequest;

    [Theory]
    [InlineData(@"C:\images\ubuntu-24.04.iso")]
    [InlineData(@"d:\Downloads\Windows 11 (x64).iso")]
    [InlineData("D:/images/disk.img")]
    [InlineData(@"\\?\C:\images\disk.img")]
    [InlineData(@"\\nas\images\linux\debian.iso")]
    [InlineData(@"\\nas.example.org\share\a.iso")]
    [InlineData(@"\\?\UNC\nas\share\a.iso")]
    [InlineData(@"C:\Users\Jörg Müller\Desktop\neu.iso")]
    public void AbsoluteFilePaths_AreAccepted(string path)
    {
        Assert.True(EnginePathRules.IsAbsoluteFilePath(path), path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("image.iso")]
    [InlineData(@"images\image.iso")]
    [InlineData(@"\image.iso")]
    [InlineData(@"C:image.iso")]
    [InlineData(@"C:\")]
    [InlineData(@"C:\images\")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\.\C:\image.iso")]
    [InlineData(@"\\.\pipe\bootrix")]
    [InlineData(@"\\?\GLOBALROOT\Device\Harddisk0\Partition1")]
    [InlineData(@"\\?\Volume{0f3c1c14-6f2e-4a89-8c63-aaaaaaaaaaaa}\image.iso")]
    [InlineData(@"\\?\usbstor#disk&ven_x#1#{53f56307-b6bf-11d0-94f2-00a0c91efb8b}")]
    [InlineData(@"\\nas\share")]
    [InlineData(@"\\nas")]
    [InlineData(@"\\.\share\a.iso")]
    [InlineData(@"\\?\share\a.iso")]
    [InlineData(@"C:\images\..\Windows\image.iso")]
    [InlineData(@"C:\images\.\image.iso")]
    [InlineData(@"C:\images\\image.iso")]
    [InlineData(@"C:\images\NUL")]
    [InlineData(@"C:\images\nul.iso")]
    [InlineData(@"C:\images\COM1")]
    [InlineData(@"C:\CON\image.iso")]
    [InlineData(@"C:\images\LPT9.txt")]
    [InlineData(@"C:\images\image.iso:hidden")]
    [InlineData(@"C:\images\image.iso.")]
    [InlineData(@"C:\images\image.iso ")]
    [InlineData(@"C:\images\im*ge.iso")]
    [InlineData(@"C:\images\im?ge.iso")]
    [InlineData("C:\\images\\im\"ge.iso")]
    [InlineData(@"C:\images\a<b>.iso")]
    [InlineData(@"C:\images\a|b.iso")]
    [InlineData("C:\\images\\a\u0000b.iso")]
    [InlineData("C:\\images\\a\nb.iso")]
    [InlineData("1:\\a.iso")]
    [InlineData("//./PhysicalDrive0")]
    public void Everything_ThatCouldReachADeviceOrLeaveTheDirectoryIsRefused(string? path)
    {
        Assert.False(EnginePathRules.IsAbsoluteFilePath(path), path);
    }

    [Fact]
    public void ExtremelyLongPaths_AreRefused()
    {
        Assert.False(EnginePathRules.IsAbsoluteFilePath(@"C:\" + new string('a', 40_000)));
    }

    [Theory]
    [InlineData(TestPaths.Disk3)]
    [InlineData(TestPaths.Disk4)]
    [InlineData(@"\\?\scsi#disk&ven_nvme&prod_samsung_ssd_980#4&1c1d5a2f&0&000000#{53f56307-b6bf-11d0-94f2-00a0c91efb8b}")]
    [InlineData(@"\\?\USBSTOR#Disk&Ven_Generic&Prod_Flash_Disk&Rev_8.07#7&2a1b3c4d&0#{53F56307-B6BF-11D0-94F2-00A0C91EFB8B}")]
    public void DiskInterfacePaths_AreRecognised(string path)
    {
        Assert.True(EnginePathRules.IsDeviceInterfacePath(path, EnginePathRules.DiskInterfaceGuid), path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"\\.\PhysicalDrive3")]
    [InlineData(@"C:\")]
    [InlineData(@"\\?\C:")]
    [InlineData(@"\\?\C:\Windows")]
    [InlineData(@"\\?\UNC\nas\share#{53f56307-b6bf-11d0-94f2-00a0c91efb8b}")]
    [InlineData(@"\\?\GLOBALROOT#x#{53f56307-b6bf-11d0-94f2-00a0c91efb8b}")]
    [InlineData(@"\\?\usbstor#disk#1\..\..\x#{53f56307-b6bf-11d0-94f2-00a0c91efb8b}")]
    [InlineData(@"\\?\usbstor#disk#1#{53f56308-b6bf-11d0-94f2-00a0c91efb8b}")]
    [InlineData(@"\\?\usbstor#disk#1")]
    [InlineData(@"\\?\usbstor#disk#1#{53f56307-b6bf-11d0-94f2-00a0c91efb8b}\extra")]
    [InlineData(@"\\?\usbstor#disk 1#1#{53f56307-b6bf-11d0-94f2-00a0c91efb8b}")]
    [InlineData("\\\\?\\usbstor#disk#1#{53f56307-b6bf-11d0-94f2-00a0c91efb8b}\n")]
    public void EverythingElse_IsNoDiskInterfacePath(string? path)
    {
        Assert.False(EnginePathRules.IsDeviceInterfacePath(path, EnginePathRules.DiskInterfaceGuid), path);
    }

    [Fact]
    public void WellFormedRequest_HasNoProblems()
    {
        Assert.Empty(EngineRequestValidator.Validate(TestPaths.ValidRequest(TestPaths.Image, TestPaths.Disk3, TestPaths.Disk4)));
        EngineRequestValidator.EnsureValid(TestPaths.ValidRequest());
    }

    [Fact]
    public void MissingRequest_IsRefused()
    {
        Assert.NotEmpty(EngineRequestValidator.Validate(null));
    }

    [Fact]
    public void RequestTypeWithoutValidator_IsRefused()
    {
        var problems = EngineRequestValidator.Validate(new UnregisteredRequest());

        Assert.Equal(["request type is not supported"], problems);
    }

    [Fact]
    public void RequestWithoutTargets_IsRefused()
    {
        var request = TestPaths.ValidRequest() with { Targets = [] };

        Assert.Contains("no target disk", EngineRequestValidator.Validate(request));
    }

    [Fact]
    public void NullTargets_AreRefusedWithoutCrashing()
    {
        var request = TestPaths.ValidRequest() with { Targets = null! };

        Assert.Contains("no target disk", EngineRequestValidator.Validate(request));
    }

    [Fact]
    public void TargetWithoutIdentityOrWithAnotherDevicesIdentity_IsRefused()
    {
        var other = new RawWriteJobRequest
        {
            ImagePath = TestPaths.Image,
            Targets = [new EngineTarget(TestPaths.Disk3, TestPaths.IdentityOf(TestPaths.Disk4))],
        };
        var none = new RawWriteJobRequest
        {
            ImagePath = TestPaths.Image,
            Targets = [new EngineTarget(TestPaths.Disk3, null!)],
        };

        Assert.Contains("target identity belongs to another device", EngineRequestValidator.Validate(other));
        Assert.Contains("target identity belongs to another device", EngineRequestValidator.Validate(none));
    }

    [Fact]
    public void IdentityPathIsComparedWithoutRegardToCase()
    {
        var upper = TestPaths.ValidRequest() with
        {
            Targets = [new EngineTarget(TestPaths.Disk3, TestPaths.IdentityOf(TestPaths.Disk3) with { DevicePath = TestPaths.Disk3.ToUpperInvariant() })],
        };

        Assert.Empty(EngineRequestValidator.Validate(upper));
    }

    [Fact]
    public void SameDiskTwice_IsRefused()
    {
        var request = TestPaths.ValidRequest(TestPaths.Image, TestPaths.Disk3, TestPaths.Disk3.ToUpperInvariant());

        Assert.Contains("target disk is listed twice", EngineRequestValidator.Validate(request));
    }

    [Fact]
    public void TooManyTargets_AreRefused()
    {
        var targets = Enumerable.Range(0, 65)
            .Select(i => $@"\\?\usbstor#disk&ven_x#{i:D4}#{{53f56307-b6bf-11d0-94f2-00a0c91efb8b}}")
            .Select(path => new EngineTarget(path, TestPaths.IdentityOf(path)))
            .ToList();

        Assert.NotEmpty(EngineRequestValidator.Validate(TestPaths.ValidRequest() with { Targets = targets }));
    }

    [Fact]
    public void AllProblemsAreCollected_AndNoneQuotesTheOffendingValue()
    {
        const string marker = "SECRET-VALUE";
        var request = new RawWriteJobRequest
        {
            ImagePath = marker,
            Targets = [new EngineTarget(marker, TestPaths.IdentityOf(marker))],
        };

        var problems = EngineRequestValidator.Validate(request);

        Assert.True(problems.Count >= 2);
        Assert.DoesNotContain(problems, p => p.Contains(marker, StringComparison.Ordinal));
    }

    [Fact]
    public void EnsureValid_ThrowsInvalidSpecWithTheFirstProblemAsArgument()
    {
        var ex = Assert.Throws<BootrixException>(() => EngineRequestValidator.EnsureValid(TestPaths.ValidRequest(@"relative.iso")));

        Assert.Equal(ErrorCode.InvalidSpec, ex.Code);
        Assert.Equal("image path is not an absolute file path", Assert.Single(ex.Arguments));
    }
}
