// SPDX-License-Identifier: GPL-3.0-or-later
using System.Reflection;
using System.Text.Json;
using Bootrix.Core.Json;
using Bootrix.Core.Workshop;
using Bootrix.Core.Workshop.Advice;
using Bootrix.Core.Workshop.Capture;
using Bootrix.Core.Workshop.Firmware;
using Bootrix.Core.Workshop.Hardware;

namespace Bootrix.Core.Tests.Workshop;

public class WorkshopJsonTests
{
    private const string PlainKey = "BCDFG-HJKMP-QRTVN-WXY23-46789";
    private const string WlanKey = "correct horse battery staple";
    private const string RecoveryKey = "000011-000022-720885-000000-000033-065483-000044-000055";

    private static TargetPcInfo InfoWithKey() => TargetPcProfiles.ModernLaptop() with
    {
        OemLicense = new OemLicenseInfo
        {
            HasFirmwareKey = true,
            MaskedKey = ProductKeysMask(),
            PlainKey = PlainKey,
            EditionId = "Professional",
            Channel = "OEM",
        },
    };

    private static string ProductKeysMask() => Bootrix.Core.Workshop.Licensing.ProductKeys.Mask(PlainKey)!;

    private static CustomerPcCapture CaptureWithSecrets() => new()
    {
        WlanProfiles = [new WlanProfile { Name = "HomeNet", Key = WlanKey, KeyProtected = false }],
        BitLockerRecoveryKeys = [new BitLockerRecoveryKey { Volume = "C:", ProtectorId = "{1234}", RecoveryPassword = RecoveryKey }],
        WindowsProductKey = new WindowsProductKeyInfo { MaskedKey = ProductKeysMask(), PlainKey = PlainKey },
    };

    [Fact]
    public void Report_Json_OmitsEverySecret()
    {
        var json = TargetPcReport.Create(InfoWithKey()).ToJson();

        Assert.DoesNotContain(PlainKey, json, StringComparison.Ordinal);
        Assert.DoesNotContain("plainKey", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("XXXXX-XXXXX-XXXXX-XXXXX-46789", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Capture_Json_OmitsEverySecret()
    {
        var json = WorkshopJson.Serialize(CaptureWithSecrets());

        Assert.DoesNotContain(WlanKey, json, StringComparison.Ordinal);
        Assert.DoesNotContain(RecoveryKey, json, StringComparison.Ordinal);
        Assert.DoesNotContain(PlainKey, json, StringComparison.Ordinal);
        Assert.Contains("HomeNet", json, StringComparison.Ordinal);
        Assert.Contains("{1234}", json, StringComparison.Ordinal);
    }

    [Fact]
    public void CoreJson_WithoutRedaction_KeepsTheSecretsForTheEncryptedSheet()
    {
        var json = JsonSerializer.Serialize(CaptureWithSecrets(), CoreJson.Options);

        Assert.Contains(WlanKey, json, StringComparison.Ordinal);
        Assert.Contains(RecoveryKey, json, StringComparison.Ordinal);
        Assert.Contains(PlainKey, json, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactedJson_KeepsTheRestOfTheSettingsOfCoreJson()
    {
        var info = TargetPcProfiles.ModernLaptop();

        using var document = JsonDocument.Parse(WorkshopJson.Serialize(info));
        var root = document.RootElement;

        // camelCase property names, enums as text and no nulls: the same conventions as every other Bootrix document.
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Uefi", root.GetProperty("firmware").GetProperty("type").GetString());
        Assert.False(root.GetProperty("firmware").TryGetProperty("vendor", out var vendor) && vendor.ValueKind == JsonValueKind.Null);
        Assert.False(root.TryGetProperty("secureBootCertificates", out _));
    }

    [Fact]
    public void Info_RoundTripsThroughJson()
    {
        foreach (var (name, info) in TargetPcProfiles.All)
        {
            var json = JsonSerializer.Serialize(info, CoreJson.Options);

            var back = JsonSerializer.Deserialize<TargetPcInfo>(json, CoreJson.Options);

            Assert.NotNull(back);
            Assert.Equal(json, JsonSerializer.Serialize(back, CoreJson.Options));
            Assert.True(name.Length > 0);
        }
    }

    [Fact]
    public void Info_MissingSections_FallBackToDefaults()
    {
        var info = JsonSerializer.Deserialize<TargetPcInfo>("{}", CoreJson.Options);

        Assert.NotNull(info);
        Assert.Equal(TargetPcInfo.CurrentSchemaVersion, info.SchemaVersion);
        Assert.Null(info.Cpu);
        Assert.Empty(info.Issues);
    }

    [Fact]
    public void Assessment_SerializesKeysAndArgumentsNotTexts()
    {
        var json = WorkshopJson.Serialize(TargetPcAdvisor.Evaluate(TargetPcProfiles.All["VmdNotebook"]));

        using var document = JsonDocument.Parse(json);
        var need = document.RootElement.GetProperty("driverNeeds")[0];

        Assert.Equal("IntelVmd", need.GetProperty("kind").GetString());
        Assert.Equal(AdvisorKeys.DriverIntelVmd, need.GetProperty("message").GetProperty("key").GetString());
        Assert.Equal("Critical", need.GetProperty("message").GetProperty("severity").GetString());
        Assert.Equal(2, need.GetProperty("message").GetProperty("arguments").GetArrayLength());
        Assert.Equal("Uefi", document.RootElement.GetProperty("boot").GetProperty("firmware").GetString());
    }

    [Fact]
    public void Assessment_DatesAreIsoStrings()
    {
        using var document = JsonDocument.Parse(WorkshopJson.Serialize(TargetPcAdvisor.Evaluate(TargetPcProfiles.ModernLaptop())));

        Assert.Equal("2026-10-01", document.RootElement.GetProperty("assessedOn").GetString());
        Assert.Equal("2027-10-12", document.RootElement.GetProperty("image").GetProperty("endOfServicing").GetString());
    }

    [Fact]
    public void Features_AreWrittenAsReadableNames()
    {
        var json = WorkshopJson.Serialize(new CpuInfo { Features = CpuFeatures.Popcnt | CpuFeatures.Sse42 });

        Assert.Contains("Sse42", json, StringComparison.Ordinal);
        Assert.Contains("Popcnt", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// A new secret must come with the attribute: every string property of the workshop model whose name suggests a secret has
    /// to carry <see cref="SensitiveAttribute"/> or be a masked form.
    /// </summary>
    [Fact]
    public void SecretLookingStringProperties_AreMarkedSensitiveOrMasked()
    {
        var suspicious = new[] { "key", "password", "secret", "token" };
        var types = typeof(TargetPcInfo).Assembly.GetTypes()
            .Where(t => t.Namespace is { } ns && ns.StartsWith("Bootrix.Core.Workshop", StringComparison.Ordinal))
            // AdvisorMessage.Key is a resource key, not a secret.
            .Where(t => t != typeof(AdvisorMessage));

        var offenders = types
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.PropertyType == typeof(string)).Select(p => (Type: t, Property: p)))
            .Where(x => suspicious.Any(s => x.Property.Name.Contains(s, StringComparison.OrdinalIgnoreCase)))
            .Where(x => !x.Property.Name.StartsWith("Masked", StringComparison.Ordinal))
            // The name of a registry key, not a secret.
            .Where(x => x.Property.Name != nameof(UninstallEntry.ParentKeyName))
            .Where(x => x.Property.GetCustomAttribute<SensitiveAttribute>() is null)
            .Select(x => $"{x.Type.Name}.{x.Property.Name}")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void SensitiveProperties_AreExactlyTheKnownSecrets()
    {
        var marked = typeof(TargetPcInfo).Assembly.GetTypes()
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => (Type: t, Property: p)))
            .Where(x => x.Property.GetCustomAttribute<SensitiveAttribute>() is not null)
            .Select(x => $"{x.Type.Name}.{x.Property.Name}")
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            ["BitLockerRecoveryKey.RecoveryPassword", "MsdmTable.ProductKey", "OemLicenseInfo.PlainKey", "WindowsProductKeyInfo.PlainKey", "WlanProfile.Key"],
            marked);
    }
}
