// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Images;
using Bootrix.Core.Images.Wim;
using Bootrix.Core.Profiles;
using Bootrix.Core.Tests.Writing.Windows.Support;
using Bootrix.Core.Writing.Windows.Customization;

namespace Bootrix.Core.Tests.Writing.Windows;

public class AnswerFileOptionsFactoryTests
{
    private static readonly AnswerFileContext Context = new() { Date = new DateOnly(2026, 10, 1), Random = new Random(1) };

    public static TheoryData<string, WindowsSetupOptions, bool> Requests => new()
    {
        { "nothing", new WindowsSetupOptions(), false },
        { "tpm", new WindowsSetupOptions { BypassTpm = true }, true },
        { "secure boot", new WindowsSetupOptions { BypassSecureBoot = true }, true },
        { "ram", new WindowsSetupOptions { BypassRam = true }, true },
        { "cpu", new WindowsSetupOptions { BypassCpu = true }, true },
        { "storage", new WindowsSetupOptions { BypassStorage = true }, true },
        { "account", new WindowsSetupOptions { LocalAccountName = "Techniker" }, true },
        { "computer name", new WindowsSetupOptions { ComputerNamePattern = "PC-{serial}" }, true },
        { "time zone", new WindowsSetupOptions { TimeZone = "W. Europe Standard Time" }, true },
        { "language", new WindowsSetupOptions { UiLanguage = "de-DE" }, true },
        { "privacy", new WindowsSetupOptions { SkipPrivacyQuestions = true }, true },
        { "bitlocker", new WindowsSetupOptions { DisableBitLocker = true }, true },
        { "edition", new WindowsSetupOptions { Edition = "Windows 11 Pro" }, true },
        { "blank strings", new WindowsSetupOptions { LocalAccountName = " ", ComputerNamePattern = "", TimeZone = "  ", UiLanguage = "", Edition = " " }, false },
        { "drivers only", new WindowsSetupOptions { DriverFolders = [@"C:\Treiber"] }, false },
        { "certificate only", new WindowsSetupOptions { BootCertificate = Bootrix.Core.Model.BootCertificate.Windows2023 }, false },
    };

    [Theory]
    [MemberData(nameof(Requests))]
    public void IsRequested_FollowsTheOptionsThatNeedAnAnswerFile(string name, WindowsSetupOptions options, bool expected)
    {
        _ = name;

        Assert.Equal(expected, AnswerFileOptionsFactory.IsRequested(options));
    }

    [Fact]
    public void Create_CarriesTheSettingsOver()
    {
        var windows = new WindowsSetupOptions
        {
            BypassTpm = true,
            BypassRam = true,
            LocalAccountName = "  Techniker ",
            TimeZone = "W. Europe Standard Time",
            UiLanguage = "de-DE",
            SkipPrivacyQuestions = true,
            DisableBitLocker = true,
        };

        var options = AnswerFileOptionsFactory.Create(windows, Context with { LocalAccountPassword = "geheim" });

        Assert.Equal(WindowsArch.X64, options.Arch);
        Assert.Equal("Techniker", options.Windows.LocalAccountName);
        Assert.Equal("geheim", options.LocalAccountPassword);
        Assert.True(options.Windows.BypassTpm);
        Assert.False(options.Windows.BypassCpu);
        Assert.Equal("W. Europe Standard Time", options.Windows.TimeZone);
        Assert.Equal("de-DE", options.Windows.UiLanguage);
        Assert.True(options.Windows.SkipPrivacyQuestions);
        Assert.True(options.Windows.DisableBitLocker);
    }

    [Theory]
    [InlineData(WindowsArch.X64)]
    [InlineData(WindowsArch.X86)]
    [InlineData(WindowsArch.Arm64)]
    public void Create_TakesTheArchitectureOfTheMedium(WindowsArch arch)
    {
        var options = AnswerFileOptionsFactory.Create(new WindowsSetupOptions { BypassTpm = true }, Context with { Arch = arch });

        Assert.Equal(arch, options.Arch);
    }

    [Theory]
    [InlineData(WindowsArch.Unknown)]
    [InlineData(WindowsArch.Arm)]
    public void Create_ArchitectureThatAnswerFilesCannotName_IsRefused(WindowsArch arch)
    {
        var ex = Assert.Throws<BootrixException>(() => AnswerFileOptionsFactory.Create(new WindowsSetupOptions { BypassTpm = true }, Context with { Arch = arch }));

        Assert.Equal(ErrorCode.ImageUnsupported, ex.Code);
    }

    [Fact]
    public void Create_PasswordWithoutAnAccount_IsDropped()
    {
        var options = AnswerFileOptionsFactory.Create(new WindowsSetupOptions { BypassTpm = true }, Context with { LocalAccountPassword = "geheim" });

        Assert.Null(options.LocalAccountPassword);
    }

    [Fact]
    public void Create_EmptyPassword_MeansNoPassword()
    {
        var options = AnswerFileOptionsFactory.Create(new WindowsSetupOptions { LocalAccountName = "Techniker" }, Context with { LocalAccountPassword = "" });

        Assert.Null(options.LocalAccountPassword);
    }

    [Theory]
    [InlineData("PC-{serial}", "0123456789AB", 1, "PC-6789AB")]
    [InlineData("KUNDE-{n}", null, 4, "KUNDE-4")]
    [InlineData("KUNDE-{n3}", null, 4, "KUNDE-004")]
    [InlineData("PC-{date}", null, 1, "PC-261001")]
    [InlineData("fest", null, 1, "FEST")]
    public void Create_ExpandsTheComputerNamePattern(string pattern, string? serial, int number, string expected)
    {
        var options = AnswerFileOptionsFactory.Create(
            new WindowsSetupOptions { ComputerNamePattern = pattern },
            Context with { DeviceSerial = serial, TargetNumber = number });

        Assert.Equal(expected, options.ComputerName);
    }

    [Fact]
    public void Create_SticksOfOneJob_GetDifferentNamesFromTheirNumber()
    {
        var windows = new WindowsSetupOptions { ComputerNamePattern = "PC-{n3}" };

        var names = Enumerable.Range(1, 3).Select(n => AnswerFileOptionsFactory.Create(windows, Context with { TargetNumber = n }).ComputerName).ToList();

        Assert.Equal(["PC-001", "PC-002", "PC-003"], names);
    }

    [Fact]
    public void Create_NoPattern_LeavesTheNameToSetup()
    {
        var options = AnswerFileOptionsFactory.Create(new WindowsSetupOptions { BypassTpm = true }, Context);

        Assert.Null(options.ComputerName);
    }

    [Fact]
    public void Create_EditionIsTurnedIntoTheExactImageName()
    {
        var editions = new[]
        {
            FakeWims.Edition(1, "Windows 11 Home", "Core"),
            FakeWims.Edition(2, "Windows 11 Pro", "Professional"),
        };

        var byName = AnswerFileOptionsFactory.Create(new WindowsSetupOptions { Edition = "windows 11 pro" }, Context with { Editions = editions });
        var byId = AnswerFileOptionsFactory.Create(new WindowsSetupOptions { Edition = "Professional" }, Context with { Editions = editions });

        Assert.Equal("Windows 11 Pro", byName.ImageName);
        Assert.Equal("Windows 11 Pro", byId.ImageName);
        Assert.Null(byName.Windows.Edition);
    }

    [Fact]
    public void Create_EditionThatTheImageLacks_NamesWhatIsThere()
    {
        var editions = new[] { FakeWims.Edition(1, "Windows 11 Home", "Core") };

        var ex = Assert.Throws<BootrixException>(() =>
            AnswerFileOptionsFactory.Create(new WindowsSetupOptions { Edition = "Windows 11 Enterprise" }, Context with { Editions = editions }));

        Assert.Equal(ErrorCode.InvalidSpec, ex.Code);
        var text = Assert.IsType<string>(ex.Arguments[0]);
        Assert.Contains("Windows 11 Enterprise", text, StringComparison.Ordinal);
        Assert.Contains("Windows 11 Home", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_EditionWithoutKnownEditions_IsPassedThrough()
    {
        var options = AnswerFileOptionsFactory.Create(new WindowsSetupOptions { Edition = "Windows 11 Pro" }, Context);

        Assert.Equal("Windows 11 Pro", options.ImageName);
    }

    [Fact]
    public void Create_EditionFromAnImageWithoutNames_IsRefusedWithAnEmptyList()
    {
        var editions = new[] { new WimEdition { Index = 1 } };

        Assert.Throws<BootrixException>(() =>
            AnswerFileOptionsFactory.Create(new WindowsSetupOptions { Edition = "Pro" }, Context with { Editions = editions }));
    }
}
