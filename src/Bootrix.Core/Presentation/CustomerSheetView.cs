// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using Bootrix.Core.Localization;
using Bootrix.Core.Workshop.Capture;

namespace Bootrix.Core.Presentation;

public sealed record SheetSection(string Title, IReadOnlyList<string> Lines);

/// <summary>The inventory of a customer PC as a sheet that can be read on screen or printed; secrets are shown when the capture holds them.</summary>
public static class CustomerSheetView
{
    public static IReadOnlyList<SheetSection> Describe(CustomerPcCapture capture, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(localizer);

        var culture = localizer.Culture;
        var sections = new List<SheetSection>();

        if (capture.CapturedAt is { } at)
        {
            sections.Add(new SheetSection(localizer.Get("Sheet.Captured"), [at.ToLocalTime().ToString("g", culture)]));
        }

        if (capture.WindowsProductKey is { } productKey)
        {
            sections.Add(new SheetSection(
                localizer.Get("Sheet.ProductKey"),
                [productKey.PlainKey ?? productKey.MaskedKey ?? localizer.Get("Sheet.None")]));
        }

        if (capture.BitLockerRecoveryKeys is { } keys)
        {
            sections.Add(new SheetSection(
                localizer.Get("Sheet.BitLocker"),
                keys.Count == 0
                    ? [localizer.Get("Sheet.None")]
                    : [.. keys.Select(k => $"{k.Volume}  ({KeyId(k.ProtectorId)})  {k.RecoveryPassword ?? localizer.Get("Sheet.NotRead")}")]));
        }

        if (capture.WlanProfiles is { } wlan)
        {
            sections.Add(new SheetSection(
                localizer.Get("Sheet.Wlan"),
                wlan.Count == 0
                    ? [localizer.Get("Sheet.None")]
                    : [.. wlan.Select(p => p.Key is { } key ? $"{p.Name}  ·  {key}" : p.Authentication is { } auth ? $"{p.Name}  ·  {auth}" : p.Name)]));
        }

        if (capture.InstalledPrograms is { } programs)
        {
            sections.Add(new SheetSection(
                localizer.Get("Sheet.Programs", programs.Count.ToString(culture)),
                [.. programs.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).Select(p => p.Version is { Length: > 0 } v ? $"{p.Name}  {v}" : p.Name)]));
        }

        if (capture.ThirdPartyDrivers is { } drivers)
        {
            sections.Add(new SheetSection(
                localizer.Get("Sheet.Drivers", drivers.Count.ToString(culture)),
                [.. drivers.Select(d => string.Join("  ·  ", new[] { d.Provider, d.ClassName, d.Version }.Where(s => !string.IsNullOrWhiteSpace(s))))]));
        }

        var notes = capture.Notes.Select(n => n.Describe(localizer)).Concat(capture.Issues.Select(i => $"{i.Source}: {i.Message}")).ToList();
        if (notes.Count > 0)
        {
            sections.Add(new SheetSection(localizer.Get("Sheet.Notes"), notes));
        }

        return sections;
    }

    public static string ToText(CustomerPcCapture capture, Localizer localizer)
    {
        var text = new StringBuilder();
        text.AppendLine(localizer.Get("Sheet.Title"));
        text.AppendLine(new string('=', 40));
        foreach (var section in Describe(capture, localizer))
        {
            text.AppendLine().AppendLine(section.Title);
            foreach (var line in section.Lines)
            {
                text.Append("  ").AppendLine(line);
            }
        }

        return text.ToString();
    }

    /// <summary>The lock screen shows the first eight characters of the protector GUID as the key ID.</summary>
    private static string KeyId(string protectorId)
    {
        var id = protectorId.Trim('{', '}');
        return id[..Math.Min(8, id.Length)];
    }

    /// <summary>A name for the file that does not give away the customer: only the date.</summary>
    public static string SuggestedFileName(DateTimeOffset now) =>
        string.Create(CultureInfo.InvariantCulture, $"kundenblatt-{now:yyyyMMdd-HHmm}{CustomerSheetFile.Extension}");
}
