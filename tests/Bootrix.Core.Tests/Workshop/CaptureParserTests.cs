// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Bootrix.Core.Workshop;
using Bootrix.Core.Workshop.Capture;

namespace Bootrix.Core.Tests.Workshop;

public class CaptureParserTests
{
    private const string EnglishNetsh = """

        Profiles on interface Wi-Fi:

        Group policy profiles (read only)
        ---------------------------------
            <None>

        User profiles
        -------------
            All User Profile     : HomeNet
            All User Profile     : Office: 5 GHz
            All User Profile     : Café
        """;

    private const string GermanNetsh = """

        Profile auf Schnittstelle WLAN:

        Gruppenrichtlinienprofile (schreibgeschützt)
        ---------------------------------
            <Kein>

        Benutzerprofile
        ---------------
            Profil für alle Benutzer    : Müller-WLAN
            Profil für alle Benutzer    : FRITZ!Box 7590 XY
        """;

    private const string FrenchNetsh = """

        Profils sur l'interface Wi-Fi :

        Profils de stratégie de groupe (lecture seule)
        ---------------------------------
            <Aucun>

        Profils utilisateur
        -------------------
            Profil Tous les utilisateurs : Maison
        """;

    [Fact]
    public void Netsh_English_ReturnsTheProfileNames()
    {
        Assert.Equal(["HomeNet", "Office: 5 GHz", "Café"], NetshWlanParser.ParseProfileNames(EnglishNetsh));
    }

    [Fact]
    public void Netsh_German_DoesNotDependOnTheTranslatedLabel()
    {
        Assert.Equal(["Müller-WLAN", "FRITZ!Box 7590 XY"], NetshWlanParser.ParseProfileNames(GermanNetsh));
    }

    [Fact]
    public void Netsh_French_NamesWithTheLabelAfterAColon()
    {
        Assert.Equal(["Maison"], NetshWlanParser.ParseProfileNames(FrenchNetsh));
    }

    [Fact]
    public void Netsh_WindowsLineEndings_AreHandled()
    {
        Assert.Equal(["HomeNet", "Office: 5 GHz", "Café"], NetshWlanParser.ParseProfileNames(EnglishNetsh.Replace("\n", "\r\n", StringComparison.Ordinal)));
    }

    [Fact]
    public void Netsh_SameProfileOnTwoInterfaces_IsListedOnce()
    {
        var output = EnglishNetsh + "\n\nProfiles on interface Wi-Fi 2:\n\nUser profiles\n-------------\n    All User Profile     : HomeNet\n";

        Assert.Equal(3, NetshWlanParser.ParseProfileNames(output).Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("The Wireless AutoConfig Service (wlansvc) is not running.")]
    [InlineData("There is no wireless interface on the system.")]
    public void Netsh_ErrorsAndEmptyOutput_ReturnNoProfiles(string output)
    {
        Assert.Empty(NetshWlanParser.ParseProfileNames(output));
    }

    private const string ClearProfile = """
        <?xml version="1.0"?>
        <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
          <name>HomeNet</name>
          <SSIDConfig><SSID><hex>486F6D654E6574</hex><name>HomeNet</name></SSID></SSIDConfig>
          <connectionType>ESS</connectionType>
          <connectionMode>auto</connectionMode>
          <MSM><security>
            <authEncryption><authentication>WPA2PSK</authentication><encryption>AES</encryption><useOneX>false</useOneX></authEncryption>
            <sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>correct horse battery</keyMaterial></sharedKey>
          </security></MSM>
        </WLANProfile>
        """;

    [Fact]
    public void WlanProfile_WithClearKey_ReadsTheKey()
    {
        var profile = WlanProfileXml.Parse(ClearProfile, @"D:\wlan\WLAN-HomeNet.xml");

        Assert.NotNull(profile);
        Assert.Equal("HomeNet", profile.Name);
        Assert.Equal("WPA2PSK", profile.Authentication);
        Assert.Equal("AES", profile.Encryption);
        Assert.Equal("auto", profile.ConnectionMode);
        Assert.False(profile.KeyProtected);
        Assert.Equal("correct horse battery", profile.Key);
        Assert.Equal(@"D:\wlan\WLAN-HomeNet.xml", profile.ExportedFile);
    }

    [Fact]
    public void WlanProfile_ProtectedKey_IsNotTreatedAsAPassword()
    {
        var xml = ClearProfile.Replace("<protected>false</protected>", "<protected>true</protected>", StringComparison.Ordinal)
            .Replace("correct horse battery", "01000000D08C9DDF0115D1118C7A00C04FC297EB", StringComparison.Ordinal);

        var profile = WlanProfileXml.Parse(xml);

        Assert.True(profile!.KeyProtected);
        Assert.Null(profile.Key);
    }

    [Fact]
    public void WlanProfile_EnterpriseWithoutSharedKey_HasNoKeyInformation()
    {
        const string xml = """
            <WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
              <name>Corp</name>
              <MSM><security><authEncryption><authentication>WPA2</authentication><encryption>AES</encryption><useOneX>true</useOneX></authEncryption></security></MSM>
            </WLANProfile>
            """;

        var profile = WlanProfileXml.Parse(xml);

        Assert.Equal("Corp", profile!.Name);
        Assert.Null(profile.KeyProtected);
        Assert.Null(profile.Key);
    }

    [Theory]
    [InlineData("not xml at all")]
    [InlineData("<Other><name>x</name></Other>")]
    [InlineData("<WLANProfile></WLANProfile>")]
    [InlineData("")]
    public void WlanProfile_OtherDocuments_ReturnNull(string xml)
    {
        Assert.Null(WlanProfileXml.Parse(xml));
    }

    [Fact]
    public void WlanProfile_DoctypeIsRejectedInsteadOfResolved()
    {
        const string xml = "<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><WLANProfile><name>&e;</name></WLANProfile>";

        Assert.Null(WlanProfileXml.Parse(xml));
    }

    [Fact]
    public void WlanProfile_ToString_NeverContainsTheKey()
    {
        var profile = WlanProfileXml.Parse(ClearProfile)!;

        Assert.DoesNotContain("horse", profile.ToString(), StringComparison.Ordinal);
        Assert.Contains("HomeNet", profile.ToString(), StringComparison.Ordinal);
    }

    private const string InfText = """
        ; Intel(R) Rapid Storage Technology
        [Version]
        Signature   = "$WINDOWS NT$"
        Class       = SCSIAdapter
        ClassGuid   = {4D36E97B-E325-11CE-BFC1-08002BE10318}
        Provider    = %INTEL%   ; the vendor
        CatalogFile = iaStorVD.cat
        DriverVer   = 03/15/2023,19.5.2.1049

        [Strings]
        INTEL = "Intel Corporation"
        """;

    [Fact]
    public void Inf_ReadsTheVersionSectionAndExpandsStrings()
    {
        var driver = InfFileParser.Parse("oem42.inf", Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(InfText)).ToArray());

        Assert.NotNull(driver);
        Assert.Equal("oem42.inf", driver.PublishedName);
        Assert.Equal("Intel Corporation", driver.Provider);
        Assert.Equal("SCSIAdapter", driver.ClassName);
        Assert.Equal("19.5.2.1049", driver.Version);
        Assert.Equal(new DateOnly(2023, 3, 15), driver.Date);
        Assert.Equal("iaStorVD.cat", driver.CatalogFile);
    }

    [Fact]
    public void Inf_AnsiAndUtf8_AreDecoded()
    {
        const string text = "[Version]\nProvider = \"Müller GmbH\"\nClass = Net\nDriverVer = 1/2/2020,1.0.0.0\n";

        var ansi = InfFileParser.Parse("oem1.inf", Encoding.Latin1.GetBytes(text));
        var utf8 = InfFileParser.Parse("oem1.inf", Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray());

        Assert.Equal("Müller GmbH", ansi!.Provider);
        Assert.Equal("Müller GmbH", utf8!.Provider);
        Assert.Equal(new DateOnly(2020, 1, 2), ansi.Date);
    }

    [Fact]
    public void Inf_SemicolonInsideQuotes_IsNotAComment()
    {
        var driver = InfFileParser.Parse("oem2.inf", Encoding.ASCII.GetBytes("[Version]\nProvider = \"A;B\" ; comment\n"));

        Assert.Equal("A;B", driver!.Provider);
    }

    [Fact]
    public void Inf_UnknownStringToken_IsKeptAsWritten()
    {
        var driver = InfFileParser.Parse("oem3.inf", Encoding.ASCII.GetBytes("[Version]\nProvider = %Missing%\n"));

        Assert.Equal("%Missing%", driver!.Provider);
    }

    [Fact]
    public void Inf_WithoutVersionSection_ReturnsNull()
    {
        Assert.Null(InfFileParser.Parse("oem4.inf", Encoding.ASCII.GetBytes("[Manufacturer]\n%x% = y\n")));
    }

    [Fact]
    public void Inf_MalformedDriverVer_LeavesDateAndVersionEmpty()
    {
        var driver = InfFileParser.Parse("oem5.inf", Encoding.ASCII.GetBytes("[Version]\nDriverVer = soon\n"));

        Assert.Null(driver!.Date);
        Assert.Null(driver.Version);
    }

    [Fact]
    public void Programs_DropComponentsUpdatesAndNamelessEntries()
    {
        var entries = new[]
        {
            new UninstallEntry("Mozilla Firefox", "128.0", "Mozilla", false, null, null),
            new UninstallEntry("7-Zip 23.01 (x64)", "23.01", "Igor Pavlov", false, null, null),
            new UninstallEntry("Microsoft Visual C++ 2015-2022 Redistributable (x64)", "14.38", "Microsoft Corporation", true, null, null),
            new UninstallEntry("Update for Windows (KB5034441)", null, "Microsoft Corporation", false, "Windows 11", "Update"),
            new UninstallEntry("Security Update for Office", null, "Microsoft", false, null, "Security Update"),
            new UninstallEntry("Hotfix X", null, null, false, null, "Hotfix"),
            new UninstallEntry("", "1.0", null, false, null, null),
            new UninstallEntry(null, null, null, false, null, null),
            new UninstallEntry("  mozilla firefox ", "128.0", "Mozilla", false, null, null),
        };

        var programs = InstalledProgramFilter.Normalize(entries);

        Assert.Equal(["7-Zip 23.01 (x64)", "Mozilla Firefox"], programs.Select(p => p.Name));
        Assert.Equal("Mozilla", programs[1].Publisher);
    }

    [Fact]
    public void Programs_SameNameInTwoVersions_BothStay()
    {
        var programs = InstalledProgramFilter.Normalize(
        [
            new UninstallEntry("Java", "8.0", "Oracle", false, null, null),
            new UninstallEntry("Java", "17.0", "Oracle", false, null, null),
        ]);

        Assert.Equal(2, programs.Count);
    }

    [Fact]
    public void RecoveryPassword_ValidatesTheBlockChecksum()
    {
        // Each block is a multiple of 11 below 11 * 65536.
        const string valid = "000011-000022-720885-000000-000033-065483-000044-000055";
        Assert.True(RecoveryPassword.IsValid(valid));
        Assert.False(RecoveryPassword.IsValid(valid.Replace("000011", "000012", StringComparison.Ordinal)));
        Assert.False(RecoveryPassword.IsValid(valid.Replace("720885", "720896", StringComparison.Ordinal)));
        Assert.False(RecoveryPassword.IsValid("000011-000022"));
        Assert.False(RecoveryPassword.IsValid("00011-000022-720885-000000-000033-065483-000044-000055"));
        Assert.False(RecoveryPassword.IsValid("abcdef-000022-720885-000000-000033-065483-000044-000055"));
        Assert.False(RecoveryPassword.IsValid(null));
    }

    [Fact]
    public void RecoveryPassword_Mask_HidesEverything()
    {
        Assert.Equal("XXXXXX-XXXXXX-XXXXXX-XXXXXX-XXXXXX-XXXXXX-XXXXXX-XXXXXX", RecoveryPassword.Mask());
    }

    [Fact]
    public void Capture_ContainsSecrets_OnlyWithClearTextFields()
    {
        var empty = new CustomerPcCapture { WlanProfiles = [new WlanProfile { Name = "A", KeyProtected = true }] };
        var withKey = new CustomerPcCapture { WlanProfiles = [new WlanProfile { Name = "A", Key = "secret" }] };
        var withProductKey = new CustomerPcCapture { WindowsProductKey = new WindowsProductKeyInfo { MaskedKey = "XXXXX", PlainKey = "BCDFG-HJKMP-QRTVN-WXY23-46789" } };
        var withRecovery = new CustomerPcCapture { BitLockerRecoveryKeys = [new BitLockerRecoveryKey { Volume = "C:", ProtectorId = "{1}", RecoveryPassword = "000011" }] };

        Assert.False(empty.ContainsSecrets);
        Assert.True(withKey.ContainsSecrets);
        Assert.True(withProductKey.ContainsSecrets);
        Assert.True(withRecovery.ContainsSecrets);
    }

    [Fact]
    public void SecretCarryingRecords_NeverPrintTheirSecrets()
    {
        var product = new WindowsProductKeyInfo { MaskedKey = "XXXXX-XXXXX-XXXXX-XXXXX-46789", PlainKey = "BCDFG-HJKMP-QRTVN-WXY23-46789" };
        var recovery = new BitLockerRecoveryKey { Volume = "C:", ProtectorId = "{ABC}", RecoveryPassword = "123456-654321" };
        var license = new OemLicenseInfo { HasFirmwareKey = true, MaskedKey = "XXXXX-XXXXX-XXXXX-XXXXX-46789", PlainKey = "BCDFG-HJKMP-QRTVN-WXY23-46789" };

        Assert.DoesNotContain("BCDFG", product.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("123456", recovery.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("BCDFG", license.ToString(), StringComparison.Ordinal);
        Assert.Contains("C:", recovery.ToString(), StringComparison.Ordinal);
    }
}
