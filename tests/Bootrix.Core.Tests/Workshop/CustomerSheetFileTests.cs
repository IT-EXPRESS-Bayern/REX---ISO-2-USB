// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bootrix.Core.Errors;
using Bootrix.Core.Localization;
using Bootrix.Core.Presentation;
using Bootrix.Core.Workshop.Capture;

namespace Bootrix.Core.Tests.Workshop;

public class CustomerSheetFileTests
{
    private static readonly Localizer English = new() { Culture = CultureInfo.GetCultureInfo("en-US") };

    private static CustomerPcCapture Capture() => new()
    {
        CapturedAt = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero),
        WlanProfiles = [new WlanProfile { Name = "Büro-WLAN", Authentication = "WPA2-Personal", Key = "geheim-123" }, new WlanProfile { Name = "Gast", Authentication = "Open" }],
        InstalledPrograms = [new InstalledProgram("Zebra", "1.0", "Z"), new InstalledProgram("Anwendung", null, null)],
        ThirdPartyDrivers = [new ThirdPartyDriver { PublishedName = "oem12.inf", Provider = "Intel", ClassName = "Net", Version = "22.1" }],
        BitLockerRecoveryKeys = [new BitLockerRecoveryKey { Volume = "C:", ProtectorId = "{12345678-aaaa-bbbb-cccc-1234567890ab}", RecoveryPassword = "111111-222222-333333-444444-555555-666666-777777-888888" }],
        WindowsProductKey = new WindowsProductKeyInfo { MaskedKey = "XXXXX-XXXXX-XXXXX-XXXXX-ABCDE", PlainKey = "AAAAA-BBBBB-CCCCC-DDDDD-ABCDE" },
    };

    private static byte[] Write(CustomerPcCapture capture, string password)
    {
        using var stream = new MemoryStream();
        CustomerSheetFile.Write(stream, capture, password);
        return stream.ToArray();
    }

    [Fact]
    public void ASheetComesBackWithItsSecrets()
    {
        var file = Write(Capture(), "ein gutes Kennwort");

        var back = CustomerSheetFile.Read(new MemoryStream(file), "ein gutes Kennwort");

        Assert.Equal("geheim-123", back.WlanProfiles![0].Key);
        Assert.Equal("AAAAA-BBBBB-CCCCC-DDDDD-ABCDE", back.WindowsProductKey!.PlainKey);
        Assert.StartsWith("111111", back.BitLockerRecoveryKeys![0].RecoveryPassword, StringComparison.Ordinal);
        Assert.Equal(2, back.InstalledPrograms!.Count);
    }

    [Fact]
    public void TheFileHoldsNoSecretInClearText()
    {
        var file = Write(Capture(), "ein gutes Kennwort");
        var text = System.Text.Encoding.UTF8.GetString(file);

        Assert.DoesNotContain("geheim-123", text, StringComparison.Ordinal);
        Assert.DoesNotContain("AAAAA-BBBBB", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Büro-WLAN", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoFilesOfTheSameSheetDiffer()
    {
        Assert.NotEqual(Write(Capture(), "ein gutes Kennwort"), Write(Capture(), "ein gutes Kennwort"));
    }

    [Fact]
    public void AWrongPasswordIsRefused()
    {
        var file = Write(Capture(), "ein gutes Kennwort");

        var error = Assert.Throws<BootrixException>(() => CustomerSheetFile.Read(new MemoryStream(file), "ein falsches Kennwort"));

        Assert.Equal(ErrorCode.InvalidSpec, error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(13)]
    [InlineData(40)]
    [InlineData(-1)]
    public void ChangingAnyByteBreaksTheSheet(int index)
    {
        var file = Write(Capture(), "ein gutes Kennwort");
        file[index < 0 ? ^1 : index] ^= 0x01;

        Assert.Throws<BootrixException>(() => CustomerSheetFile.Read(new MemoryStream(file), "ein gutes Kennwort"));
    }

    [Fact]
    public void AShortPasswordIsRefusedWhenWriting()
    {
        Assert.Throws<BootrixException>(() => Write(Capture(), "kurz"));
    }

    [Fact]
    public void AFileThatIsNoSheetIsRefused()
    {
        Assert.Throws<BootrixException>(() => CustomerSheetFile.Read(new MemoryStream("just some text, not a sheet at all"u8.ToArray()), "ein gutes Kennwort"));
        Assert.Throws<BootrixException>(() => CustomerSheetFile.Read(new MemoryStream([]), "ein gutes Kennwort"));
    }

    [Fact]
    public void TheSheetListsEverythingAndShowsTheSecretsItHolds()
    {
        var text = CustomerSheetView.ToText(Capture(), English);

        Assert.Contains("Customer sheet", text, StringComparison.Ordinal);
        Assert.Contains("Büro-WLAN  ·  geheim-123", text, StringComparison.Ordinal);
        Assert.Contains("Gast  ·  Open", text, StringComparison.Ordinal);
        Assert.Contains("AAAAA-BBBBB-CCCCC-DDDDD-ABCDE", text, StringComparison.Ordinal);
        Assert.Contains("C:  (12345678)  111111-", text, StringComparison.Ordinal);
        Assert.Contains("Installed programs (2)", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("Anwendung", StringComparison.Ordinal) < text.IndexOf("Zebra", StringComparison.Ordinal));
        Assert.Contains("Intel  ·  Net  ·  22.1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutOptInSecretsTheSheetShowsOnlyTheMaskedKey()
    {
        var capture = Capture() with
        {
            WlanProfiles = [new WlanProfile { Name = "Büro-WLAN", Authentication = "WPA2-Personal" }],
            BitLockerRecoveryKeys = null,
            WindowsProductKey = new WindowsProductKeyInfo { MaskedKey = "XXXXX-XXXXX-XXXXX-XXXXX-ABCDE" },
        };

        var text = CustomerSheetView.ToText(capture, English);

        Assert.Contains("XXXXX-XXXXX-XXXXX-XXXXX-ABCDE", text, StringComparison.Ordinal);
        Assert.DoesNotContain("geheim", text, StringComparison.Ordinal);
        Assert.DoesNotContain("BitLocker", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSuggestedFileNameCarriesOnlyTheDate()
    {
        Assert.Equal("kundenblatt-20261001-1205.bootrixsheet", CustomerSheetView.SuggestedFileName(new DateTimeOffset(2026, 10, 1, 12, 5, 0, TimeSpan.Zero)));
    }
}
