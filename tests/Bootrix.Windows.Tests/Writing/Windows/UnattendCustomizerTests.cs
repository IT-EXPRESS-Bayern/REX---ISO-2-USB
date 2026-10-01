// SPDX-License-Identifier: GPL-3.0-or-later
using System.Xml.Linq;
using Bootrix.Core.Errors;
using Bootrix.Core.Images;
using Bootrix.Core.Model;
using Bootrix.Core.Profiles;
using Bootrix.Windows.Writing;
using Bootrix.Windows.Writing.Windows.Customization;

namespace Bootrix.Windows.Tests.Writing.Windows;

public sealed class UnattendCustomizerTests : IDisposable
{
    private static readonly XNamespace Unattend = "urn:schemas-microsoft-com:unattend";

    private readonly MediaFolder _folder = new();
    private readonly CapturingLogger<UnattendCustomizer> _log = new();

    public void Dispose() => _folder.Dispose();

    private UnattendCustomizer Customizer() => new(_log);

    public static TheoryData<string, ImageKind, WindowsSetupOptions, bool> Requests => new()
    {
        { "setup, bypass", ImageKind.WindowsSetup, new WindowsSetupOptions { BypassTpm = true }, true },
        { "setup, account", ImageKind.WindowsSetup, new WindowsSetupOptions { LocalAccountName = "Techniker" }, true },
        { "setup, computer name", ImageKind.WindowsSetup, new WindowsSetupOptions { ComputerNamePattern = "PC-{n}" }, true },
        { "setup, time zone", ImageKind.WindowsSetup, new WindowsSetupOptions { TimeZone = "UTC" }, true },
        { "setup, language", ImageKind.WindowsSetup, new WindowsSetupOptions { UiLanguage = "de-DE" }, true },
        { "setup, privacy", ImageKind.WindowsSetup, new WindowsSetupOptions { SkipPrivacyQuestions = true }, true },
        { "setup, bitlocker", ImageKind.WindowsSetup, new WindowsSetupOptions { DisableBitLocker = true }, true },
        { "setup, edition", ImageKind.WindowsSetup, new WindowsSetupOptions { Edition = "Windows 11 Pro" }, true },
        { "setup, nothing", ImageKind.WindowsSetup, new WindowsSetupOptions(), false },
        { "setup, drivers only", ImageKind.WindowsSetup, new WindowsSetupOptions { DriverFolders = [@"C:\x"] }, false },
        { "pe, bypass", ImageKind.WindowsPe, new WindowsSetupOptions { BypassTpm = true }, false },
        { "linux, account", ImageKind.LinuxHybrid, new WindowsSetupOptions { LocalAccountName = "Techniker" }, false },
        { "raw disk, account", ImageKind.RawDisk, new WindowsSetupOptions { LocalAccountName = "Techniker" }, false },
    };

    [Theory]
    [MemberData(nameof(Requests))]
    public void Applies_OnlyForWindowsSetupWithOptionsThatNeedAnAnswerFile(string name, ImageKind kind, WindowsSetupOptions options, bool expected)
    {
        _ = name;
        var write = CustomizationKit.Write(_folder.Work, options, CustomizationKit.Profile(kind));

        Assert.Equal(expected, Customizer().Applies(write));
    }

    private async Task<XDocument> ApplyAsync(MediaWriteContext write, int target = 0, ProgressLog? progress = null)
    {
        await Customizer().ApplyAsync(CustomizationKit.Customization(write, _folder.Media, target), progress ?? new ProgressLog(), CancellationToken.None);
        return XDocument.Load(_folder.Full("autounattend.xml"));
    }

    [Fact]
    public async Task Apply_WritesAWellFormedAnswerFileWithTheJobsSettings()
    {
        var write = CustomizationKit.Write(
            _folder.Work,
            new WindowsSetupOptions
            {
                BypassTpm = true,
                BypassSecureBoot = true,
                LocalAccountName = "Techniker",
                ComputerNamePattern = "PC-{serial}",
                TimeZone = "W. Europe Standard Time",
                UiLanguage = "de-DE",
                SkipPrivacyQuestions = true,
                DisableBitLocker = true,
            },
            password: "Gehe1m!");

        var document = await ApplyAsync(write);
        var text = document.ToString(SaveOptions.DisableFormatting);

        Assert.Equal(Unattend + "unattend", document.Root!.Name);
        Assert.Contains("BypassTPMCheck", text, StringComparison.Ordinal);
        Assert.Contains("BypassSecureBootCheck", text, StringComparison.Ordinal);
        Assert.DoesNotContain("BypassCPUCheck", text, StringComparison.Ordinal);
        Assert.Equal("Techniker", document.Descendants(Unattend + "LocalAccount").Single().Element(Unattend + "Name")!.Value);
        Assert.Equal("Gehe1m!", document.Descendants(Unattend + "LocalAccount").Single().Descendants(Unattend + "Value").Single().Value);
        Assert.Equal("PC-000001", document.Descendants(Unattend + "ComputerName").Single().Value);
        Assert.All(document.Descendants(Unattend + "TimeZone"), zone => Assert.Equal("W. Europe Standard Time", zone.Value));
        Assert.Contains("PreventDeviceEncryption", text, StringComparison.Ordinal);
        Assert.Equal("3", document.Descendants(Unattend + "ProtectYourPC").Single().Value);
        Assert.All(document.Descendants(Unattend + "component"), c => Assert.Equal("amd64", (string?)c.Attribute("processorArchitecture")));
    }

    [Fact]
    public async Task Apply_NoPasswordInTheJob_CreatesTheAccountWithoutOne()
    {
        var write = CustomizationKit.Write(_folder.Work, new WindowsSetupOptions { LocalAccountName = "Techniker" });

        var document = await ApplyAsync(write);

        Assert.Empty(document.Descendants(Unattend + "Password"));
    }

    [Fact]
    public async Task Apply_NeverLogsThePassword()
    {
        var write = CustomizationKit.Write(_folder.Work, new WindowsSetupOptions { LocalAccountName = "Techniker" }, password: "Sup3rGeheim!");

        await ApplyAsync(write);

        Assert.DoesNotContain("Sup3rGeheim", _log.All, StringComparison.Ordinal);
        Assert.Contains("Wrote autounattend.xml", _log.All, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(WindowsArch.X64, "amd64")]
    [InlineData(WindowsArch.Arm64, "arm64")]
    [InlineData(WindowsArch.X86, "x86")]
    public async Task Apply_UsesTheArchitectureOfTheMedium(WindowsArch arch, string expected)
    {
        var write = CustomizationKit.Write(_folder.Work, new WindowsSetupOptions { BypassTpm = true }, CustomizationKit.Profile(arch: arch));

        var document = await ApplyAsync(write);

        Assert.All(document.Descendants(Unattend + "component"), c => Assert.Equal(expected, (string?)c.Attribute("processorArchitecture")));
    }

    [Fact]
    public async Task Apply_EditionIsWrittenAsTheExactImageName()
    {
        var write = CustomizationKit.Write(
            _folder.Work,
            new WindowsSetupOptions { Edition = "professional" },
            editions: [FakeWim.Edition(1, "Windows 11 Home", "Core"), FakeWim.Edition(2, "Windows 11 Pro", "Professional")]);

        var document = await ApplyAsync(write);

        var metadata = document.Descendants(Unattend + "MetaData").Single();
        Assert.Equal("/IMAGE/NAME", metadata.Element(Unattend + "Key")!.Value);
        Assert.Equal("Windows 11 Pro", metadata.Element(Unattend + "Value")!.Value);
    }

    [Fact]
    public async Task Apply_EditionTheImageLacks_FailsBeforeWritingAnything()
    {
        var write = CustomizationKit.Write(
            _folder.Work,
            new WindowsSetupOptions { Edition = "Windows 11 Enterprise" },
            editions: [FakeWim.Edition(1, "Windows 11 Home", "Core")]);

        var ex = await Assert.ThrowsAsync<BootrixException>(() => ApplyAsync(write));

        Assert.Equal(ErrorCode.InvalidSpec, ex.Code);
        Assert.False(_folder.Exists("autounattend.xml"));
    }

    [Fact]
    public async Task Apply_SticksOfOneJob_GetTheirOwnComputerNames()
    {
        var write = CustomizationKit.Write(_folder.Work, new WindowsSetupOptions { ComputerNamePattern = "PC-{n3}-{serial}" }, targets: 2);

        var first = await ApplyAsync(write, target: 0);
        var second = await ApplyAsync(write, target: 1);

        Assert.Equal("PC-001-000001", first.Descendants(Unattend + "ComputerName").Single().Value);
        Assert.Equal("PC-002-000002", second.Descendants(Unattend + "ComputerName").Single().Value);
    }

    [Theory]
    [InlineData("Administrator")]
    [InlineData("a/b")]
    [InlineData("Dieser Name ist viel zu lang")]
    public async Task Apply_InvalidAccountName_IsRefusedAndNothingIsWritten(string account)
    {
        var write = CustomizationKit.Write(_folder.Work, new WindowsSetupOptions { LocalAccountName = account, BypassTpm = true });

        var ex = await Assert.ThrowsAsync<BootrixException>(() => ApplyAsync(write));

        Assert.Equal(ErrorCode.InvalidSpec, ex.Code);
        Assert.False(_folder.Exists("autounattend.xml"));
    }

    [Fact]
    public async Task Apply_BlankAccountName_MeansNoAccount()
    {
        var write = CustomizationKit.Write(_folder.Work, new WindowsSetupOptions { LocalAccountName = "  ", BypassTpm = true }, password: "ignored");

        var document = await ApplyAsync(write);

        Assert.Empty(document.Descendants(Unattend + "LocalAccount"));
        Assert.DoesNotContain("ignored", document.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_AmpersandAndQuotesInNameAndPassword_AreEscapedNotBroken()
    {
        var write = CustomizationKit.Write(_folder.Work, new WindowsSetupOptions { LocalAccountName = "Tom&Jerry" }, password: "a&b<c>\"d'");

        var document = await ApplyAsync(write);

        Assert.Equal("Tom&Jerry", document.Descendants(Unattend + "LocalAccount").Single().Element(Unattend + "Name")!.Value);
        Assert.Equal("a&b<c>\"d'", document.Descendants(Unattend + "LocalAccount").Single().Descendants(Unattend + "Value").Single().Value);
    }

    [Fact]
    public async Task Apply_AccountNameWithAnglesAndAmpersand_IsRefused()
    {
        var write = CustomizationKit.Write(_folder.Work, new WindowsSetupOptions { LocalAccountName = "Tom & <Jerry>" });

        await Assert.ThrowsAsync<BootrixException>(() => ApplyAsync(write));
    }

    [Fact]
    public async Task Apply_ExistingAnswerFile_DefaultIsReplaceAndKeepTheOriginal()
    {
        _folder.Write("autounattend.xml", "<old/>");
        var write = CustomizationKit.Write(_folder.Work, new WindowsSetupOptions { BypassTpm = true });

        await ApplyAsync(write);

        Assert.Equal("<old/>", _folder.Read("autounattend.xml.original"));
        Assert.Contains("BypassTPMCheck", _folder.Read("autounattend.xml"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_ExistingAnswerFile_KeepLeavesItAlone()
    {
        _folder.Write("autounattend.xml", "<old/>");
        var write = CustomizationKit.Write(_folder.Work, new WindowsSetupOptions { BypassTpm = true, ExistingAnswerFile = ExistingAnswerFilePolicy.Keep });

        await Customizer().ApplyAsync(CustomizationKit.Customization(write, _folder.Media), new ProgressLog(), CancellationToken.None);

        Assert.Equal("<old/>", _folder.Read("autounattend.xml"));
        Assert.False(_folder.Exists("autounattend.xml.original"));
        Assert.Contains("left alone", _log.All, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_ExistingAnswerFile_FailStopsTheJob()
    {
        _folder.Write("autounattend.xml", "<old/>");
        var write = CustomizationKit.Write(_folder.Work, new WindowsSetupOptions { BypassTpm = true, ExistingAnswerFile = ExistingAnswerFilePolicy.Fail });

        var ex = await Assert.ThrowsAsync<BootrixException>(() => ApplyAsync(write));

        Assert.Equal(ErrorCode.AnswerFileExists, ex.Code);
        Assert.Equal("<old/>", _folder.Read("autounattend.xml"));
    }

    [Fact]
    public async Task Apply_ReportsProgressToOne()
    {
        var progress = new ProgressLog();
        var write = CustomizationKit.Write(_folder.Work, new WindowsSetupOptions { BypassTpm = true });

        await ApplyAsync(write, progress: progress);

        progress.AssertMonotonicToOne();
    }

    [Fact]
    public async Task Apply_AlreadyCancelled_WritesNothing()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var write = CustomizationKit.Write(_folder.Work, new WindowsSetupOptions { BypassTpm = true });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Customizer().ApplyAsync(CustomizationKit.Customization(write, _folder.Media), new ProgressLog(), cts.Token));

        Assert.False(_folder.Exists("autounattend.xml"));
    }
}
