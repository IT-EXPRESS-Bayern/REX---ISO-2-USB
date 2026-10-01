// SPDX-License-Identifier: GPL-3.0-or-later
using System.Xml.Linq;
using System.Xml.XPath;
using Bootrix.Core.Errors;
using Bootrix.Core.Images;
using Bootrix.Core.Profiles;
using Bootrix.Core.Unattend;

namespace Bootrix.Core.Tests.Unattend;

public class UnattendBuilderTests
{
    private static readonly XNamespace U = "urn:schemas-microsoft-com:unattend";
    private static readonly XNamespace Wcm = "http://schemas.microsoft.com/WMIConfig/2002/State";

    private static XDocument Parse(UnattendOptions options) => XDocument.Parse(UnattendBuilder.ToXml(options));

    private static IEnumerable<string> Values(XDocument doc, string xpath)
    {
        var manager = new System.Xml.XmlNamespaceManager(new System.Xml.NameTable());
        manager.AddNamespace("u", U.NamespaceName);
        manager.AddNamespace("wcm", Wcm.NamespaceName);
        return doc.XPathSelectElements(xpath, manager).Select(e => e.Value);
    }

    [Fact]
    public void OutputIsWellFormedAndDeclaresTheWcmNamespace()
    {
        var xml = UnattendBuilder.ToXml(new UnattendOptions
        {
            Windows = new WindowsSetupOptions { BypassTpm = true, LocalAccountName = "Kunde" },
        });

        var doc = XDocument.Parse(xml);

        Assert.Equal(U + "unattend", doc.Root!.Name);
        Assert.Equal(Wcm.NamespaceName, doc.Root.GetNamespaceOfPrefix("wcm")!.NamespaceName);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"utf-8\"?>", xml);
        Assert.DoesNotContain("﻿", xml, StringComparison.Ordinal);
    }

    [Fact]
    public void BypassOptionsBecomeLabConfigCommandsInTheWindowsPePass()
    {
        var doc = Parse(new UnattendOptions
        {
            Windows = new WindowsSetupOptions { BypassTpm = true, BypassSecureBoot = true, BypassRam = true, BypassCpu = true, BypassStorage = true },
        });

        var commands = Values(doc, "/u:unattend/u:settings[@pass='windowsPE']/u:component[@name='Microsoft-Windows-Setup']/u:RunSynchronous/u:RunSynchronousCommand/u:Path").ToList();

        Assert.Equal(5, commands.Count);
        Assert.Contains(@"reg add HKLM\SYSTEM\Setup\LabConfig /v BypassTPMCheck /t REG_DWORD /d 1 /f", commands);
        Assert.Contains(commands, c => c.Contains("BypassCPUCheck", StringComparison.Ordinal));
        Assert.Equal(["1", "2", "3", "4", "5"], Values(doc, "//u:RunSynchronousCommand/u:Order"));
    }

    [Fact]
    public void NoBypassMeansNoRunSynchronous()
    {
        var doc = Parse(new UnattendOptions());

        Assert.Empty(Values(doc, "//u:RunSynchronous"));
    }

    [Fact]
    public void LocalAccountReplacesTheOnlineAccountScreens()
    {
        var doc = Parse(new UnattendOptions
        {
            Windows = new WindowsSetupOptions { LocalAccountName = "Kunde" },
            LocalAccountPassword = "Geheim 1",
        });

        Assert.Equal(["Kunde"], Values(doc, "//u:LocalAccount/u:Name"));
        Assert.Equal(["Administrators"], Values(doc, "//u:LocalAccount/u:Group"));
        Assert.Equal(["Geheim 1"], Values(doc, "//u:LocalAccount/u:Password/u:Value"));
        Assert.Equal(["true"], Values(doc, "//u:OOBE/u:HideOnlineAccountScreens"));
        Assert.DoesNotContain("BypassNRO", UnattendBuilder.ToXml(new UnattendOptions { Windows = new WindowsSetupOptions { LocalAccountName = "x" } }));
    }

    [Fact]
    public void AccountWithoutPasswordHasNoPasswordElement()
    {
        var doc = Parse(new UnattendOptions { Windows = new WindowsSetupOptions { LocalAccountName = "Kunde" } });

        Assert.Empty(Values(doc, "//u:LocalAccount/u:Password"));
    }

    [Fact]
    public void AutoLogonRunsOnce()
    {
        var doc = Parse(new UnattendOptions
        {
            Windows = new WindowsSetupOptions { LocalAccountName = "Kunde" },
            AutoLogonOnce = true,
        });

        Assert.Equal(["1"], Values(doc, "//u:AutoLogon/u:LogonCount"));
        Assert.Equal(["Kunde"], Values(doc, "//u:AutoLogon/u:Username"));
    }

    [Fact]
    public void BitLockerPreventionIsSetInTheSpecializePass()
    {
        var doc = Parse(new UnattendOptions { Windows = new WindowsSetupOptions { DisableBitLocker = true } });

        var path = Assert.Single(Values(doc, "//u:settings[@pass='specialize']//u:RunSynchronousCommand/u:Path"));
        Assert.Contains("PreventDeviceEncryption", path, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(WindowsArch.X64, "amd64")]
    [InlineData(WindowsArch.Arm64, "arm64")]
    [InlineData(WindowsArch.X86, "x86")]
    public void ComponentsCarryTheTargetArchitecture(WindowsArch arch, string expected)
    {
        var doc = Parse(new UnattendOptions { Arch = arch, Windows = new WindowsSetupOptions { BypassTpm = true } });

        var attributes = doc.Descendants(U + "component").Select(c => (string?)c.Attribute("processorArchitecture")).ToList();

        Assert.NotEmpty(attributes);
        Assert.All(attributes, a => Assert.Equal(expected, a));
    }

    [Fact]
    public void LanguageIsAppliedToSetupAndTheInstalledSystem()
    {
        var doc = Parse(new UnattendOptions { Windows = new WindowsSetupOptions { UiLanguage = "de-DE", TimeZone = "W. Europe Standard Time" } });

        Assert.Equal(["de-DE"], Values(doc, "//u:component[@name='Microsoft-Windows-International-Core-WinPE']/u:SetupUILanguage/u:UILanguage"));
        Assert.Equal(["de-DE"], Values(doc, "//u:component[@name='Microsoft-Windows-International-Core']/u:UserLocale"));
        Assert.Equal(["W. Europe Standard Time", "W. Europe Standard Time"], Values(doc, "//u:TimeZone"));
    }

    [Fact]
    public void EditionIsSelectedByNameOrIndex()
    {
        var byName = Parse(new UnattendOptions { ImageName = "Windows 11 Pro" });
        var byIndex = Parse(new UnattendOptions { ImageName = "ignored", ImageIndex = 6 });

        Assert.Equal(["/IMAGE/NAME", "Windows 11 Pro"], Values(byName, "//u:MetaData/u:Key | //u:MetaData/u:Value"));
        Assert.Equal(["/IMAGE/INDEX", "6"], Values(byIndex, "//u:MetaData/u:Key | //u:MetaData/u:Value"));
    }

    [Fact]
    public void ProductKeyIsOnlyWrittenWhenGiven()
    {
        Assert.Empty(Values(Parse(new UnattendOptions()), "//u:ProductKey"));
        Assert.Equal(["AAAAA-BBBBB-CCCCC-DDDDD-EEEEE"], Values(Parse(new UnattendOptions { ProductKey = " AAAAA-BBBBB-CCCCC-DDDDD-EEEEE " }), "//u:ProductKey/u:Key"));
    }

    [Fact]
    public void FirstLogonCommandsKeepTheirOrder()
    {
        var doc = Parse(new UnattendOptions { FirstLogonCommands = ["cmd /c echo one", "cmd /c echo two"] });

        Assert.Equal(["cmd /c echo one", "cmd /c echo two"], Values(doc, "//u:FirstLogonCommands/u:SynchronousCommand/u:CommandLine"));
        Assert.Equal(["1", "2"], Values(doc, "//u:FirstLogonCommands/u:SynchronousCommand/u:Order"));
    }

    [Fact]
    public void BrandingIsWrittenAsOemInformation()
    {
        var doc = Parse(new UnattendOptions
        {
            Branding = new OemBranding { SupportProvider = "IT-EXPRESS Bayern", SupportUrl = "https://it-express-bayern.de" },
        });

        Assert.Equal(["IT-EXPRESS Bayern"], Values(doc, "//u:OEMInformation/u:SupportProvider"));
        Assert.Equal(["https://it-express-bayern.de"], Values(doc, "//u:OEMInformation/u:SupportURL"));
        Assert.Empty(Values(doc, "//u:OEMInformation/u:Model"));
    }

    [Fact]
    public void SpecialCharactersAreEscapedAndSurviveARoundTrip()
    {
        const string command = "cmd /c echo \"a & b\" < in > out & del 'x'";
        var doc = Parse(new UnattendOptions
        {
            Windows = new WindowsSetupOptions { LocalAccountName = "Müller ß" },
            FirstLogonCommands = [command],
            LocalAccountPassword = "p<a>&ss\"word'",
        });

        Assert.Equal([command], Values(doc, "//u:FirstLogonCommands/u:SynchronousCommand/u:CommandLine"));
        Assert.Equal(["Müller ß"], Values(doc, "//u:LocalAccount/u:Name"));
        Assert.Equal(["p<a>&ss\"word'"], Values(doc, "//u:LocalAccount/u:Password/u:Value"));
    }

    [Fact]
    public void RandomUnicodeNamesAlwaysProduceWellFormedXmlOrAreRejected()
    {
        var random = new Random(42);
        for (var i = 0; i < 500; i++)
        {
            var chars = Enumerable.Range(0, random.Next(1, 25)).Select(_ => (char)random.Next(0x20, 0x2FFF)).ToArray();
            var name = new string(chars);
            var options = new UnattendOptions { Windows = new WindowsSetupOptions { LocalAccountName = name } };

            if (UnattendValidator.ValidateAccountName(name).Count > 0)
            {
                Assert.Throws<BootrixException>(() => UnattendBuilder.ToXml(options));
                continue;
            }

            var doc = Parse(options);
            Assert.Equal([name.Trim()], Values(doc, "//u:LocalAccount/u:Name"));
        }
    }

    [Fact]
    public void InvalidAccountNameIsRejectedWithErrorCode()
    {
        var ex = Assert.Throws<BootrixException>(() =>
            UnattendBuilder.ToXml(new UnattendOptions { Windows = new WindowsSetupOptions { LocalAccountName = "Admin@Home" } }));

        Assert.Equal(ErrorCode.InvalidSpec, ex.Code);
    }

    [Fact]
    public void PrivacyQuestionsAreSkippedWithProtectYourPc()
    {
        var doc = Parse(new UnattendOptions { Windows = new WindowsSetupOptions { SkipPrivacyQuestions = true } });

        Assert.Equal(["3"], Values(doc, "//u:OOBE/u:ProtectYourPC"));
    }

    [Fact]
    public void BytesAreUtf8WithoutBom()
    {
        var bytes = UnattendBuilder.ToBytes(new UnattendOptions { Windows = new WindowsSetupOptions { LocalAccountName = "Müller" } });

        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Contains("Müller", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
    }
}
