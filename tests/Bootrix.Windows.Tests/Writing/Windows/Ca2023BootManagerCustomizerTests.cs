// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Images;
using Bootrix.Core.Model;
using Bootrix.Core.Profiles;
using Bootrix.Core.Writing.Windows.Customization;
using Bootrix.Windows.Writing.Windows.Customization;

namespace Bootrix.Windows.Tests.Writing.Windows;

public sealed class Ca2023BootManagerCustomizerTests : IDisposable
{
    private readonly MediaFolder _folder = new();
    private readonly CapturingLogger<Ca2023BootManagerCustomizer> _log = new();

    public void Dispose() => _folder.Dispose();

    /// <summary>An image whose Setup image has no 2023 boot files: nothing is extracted.</summary>
    private sealed class EmptyExtractor : IBootFileExtractor
    {
        public int Calls { get; private set; }

        public Task ExtractAsync(string imagePath, int imageIndex, IReadOnlyList<string> imagePaths, string destination, CancellationToken cancellationToken)
        {
            Calls++;
            Directory.CreateDirectory(destination);
            return Task.CompletedTask;
        }
    }

    private Ca2023BootManagerCustomizer Customizer(IBootFileExtractor? extractor = null) =>
        new(new Ca2023BootManagerSwap(extractor ?? new EmptyExtractor()), _log);

    public static TheoryData<string, ImageKind, BootCertificate, int, TargetFirmware, bool> Requests => new()
    {
        { "explicit 2023 on setup", ImageKind.WindowsSetup, BootCertificate.Windows2023, 26200, TargetFirmware.BiosAndUefi, true },
        { "explicit 2023 on an old build", ImageKind.WindowsSetup, BootCertificate.Windows2023, 26100, TargetFirmware.BiosAndUefi, true },
        { "explicit 2023, build unknown", ImageKind.WindowsSetup, BootCertificate.Windows2023, 0, TargetFirmware.Uefi, true },
        { "explicit 2023 on pe", ImageKind.WindowsPe, BootCertificate.Windows2023, 26200, TargetFirmware.Uefi, true },
        { "auto on 25H2", ImageKind.WindowsSetup, BootCertificate.Auto, 26200, TargetFirmware.Uefi, true },
        { "auto on a newer build", ImageKind.WindowsSetup, BootCertificate.Auto, 26300, TargetFirmware.BiosAndUefi, true },
        { "auto on 24H2", ImageKind.WindowsSetup, BootCertificate.Auto, 26100, TargetFirmware.Uefi, false },
        { "auto, build unknown", ImageKind.WindowsSetup, BootCertificate.Auto, 0, TargetFirmware.Uefi, false },
        { "2011 requested", ImageKind.WindowsSetup, BootCertificate.Windows2011, 26200, TargetFirmware.Uefi, false },
        { "explicit 2023 for a bios-only target", ImageKind.WindowsSetup, BootCertificate.Windows2023, 26200, TargetFirmware.Bios, false },
        { "explicit 2023 on a linux image", ImageKind.LinuxHybrid, BootCertificate.Windows2023, 26200, TargetFirmware.Uefi, false },
        { "explicit 2023 on a raw disk", ImageKind.RawDisk, BootCertificate.Windows2023, 26200, TargetFirmware.Uefi, false },
        { "firmware not decided yet", ImageKind.WindowsSetup, BootCertificate.Windows2023, 26200, TargetFirmware.Auto, true },
    };

    [Theory]
    [MemberData(nameof(Requests))]
    public void Applies_FollowsTheJobTheImageAndTheFirmware(string name, ImageKind kind, BootCertificate certificate, int build, TargetFirmware firmware, bool expected)
    {
        _ = name;
        var write = CustomizationKit.Write(_folder.Work, new WindowsSetupOptions { BootCertificate = certificate }, CustomizationKit.Profile(kind, build), firmware: firmware);

        Assert.Equal(expected, Customizer().Applies(write));
    }

    [Fact]
    public async Task Apply_ExplicitRequestForAnImageWithoutTheFiles_FailsWithAClearError()
    {
        _folder.Write("sources/boot.wim", FakeWim.BootWim());
        var write = CustomizationKit.Write(_folder.Work, new WindowsSetupOptions { BootCertificate = BootCertificate.Windows2023 }, CustomizationKit.Profile(build: 26100));

        var ex = await Assert.ThrowsAsync<BootrixException>(() =>
            Customizer().ApplyAsync(CustomizationKit.Customization(write, _folder.Media), new ProgressLog(), CancellationToken.None));

        Assert.Equal(ErrorCode.BootManager2023Unavailable, ex.Code);
        Assert.Equal("26100", ex.Arguments[0]);
    }

    [Fact]
    public async Task Apply_AutoOnAnImageWithoutTheFiles_KeepsTheMediumAndFinishes()
    {
        _folder.Write("sources/boot.wim", FakeWim.BootWim());
        _folder.Write("efi/boot/bootx64.efi", "loader");
        var write = CustomizationKit.Write(_folder.Work, new WindowsSetupOptions { BootCertificate = BootCertificate.Auto }, CustomizationKit.Profile(build: 26200));
        var progress = new ProgressLog();

        await Customizer().ApplyAsync(CustomizationKit.Customization(write, _folder.Media), progress, CancellationToken.None);

        Assert.Equal("loader", _folder.Read("efi/boot/bootx64.efi"));
        Assert.Contains("keeps the boot manager of the image", _log.All, StringComparison.Ordinal);
        progress.AssertMonotonicToOne();
    }

    [Fact]
    public async Task Apply_AutoOnAMediumWithoutBootWim_KeepsTheMedium()
    {
        var write = CustomizationKit.Write(_folder.Work, new WindowsSetupOptions { BootCertificate = BootCertificate.Auto }, CustomizationKit.Profile(build: 26200));
        var extractor = new EmptyExtractor();

        await Customizer(extractor).ApplyAsync(CustomizationKit.Customization(write, _folder.Media), new ProgressLog(), CancellationToken.None);

        Assert.Equal(0, extractor.Calls);
    }
}
