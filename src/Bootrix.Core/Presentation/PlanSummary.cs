// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Engine;
using Bootrix.Core.Images;
using Bootrix.Core.Localization;
using Bootrix.Core.Model;
using Bootrix.Core.Planning;
using Bootrix.Core.Text;
using Bootrix.Core.Writing;

namespace Bootrix.Core.Presentation;

public sealed record SummaryLine(string Label, string Value);

public sealed record SummaryWarning(string Text, WarningSeverity Severity);

/// <summary>The plan for one image on one device in words: what Bootrix is going to do, and what the user should know first.</summary>
public sealed record PlanSummary(IReadOnlyList<SummaryLine> Lines, IReadOnlyList<SummaryWarning> Warnings)
{
    public bool HasErrors => Warnings.Any(w => w.Severity == WarningSeverity.Error);

    public static PlanSummary From(WritePreview preview, Localizer localizer, WriteSource source = WriteSource.Image)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(localizer);

        var plan = preview.Plan;
        var culture = localizer.Culture;

        var lines = new List<SummaryLine>
        {
            new(localizer.Get("Plan.Label.Image"), source == WriteSource.Format ? localizer.Get("Plan.Kind.Format") : DescribeImage(preview, localizer)),
            new(localizer.Get("Plan.Label.Method"), localizer.Get("Plan.Method." + plan.WriteMethod)),
            new(localizer.Get("Plan.Label.Scheme"), plan.Superfloppy
                ? localizer.Get("Plan.Scheme.NoTable")
                : localizer.Get(plan.Scheme switch { PartitionScheme.Mbr => "Plan.Scheme.Mbr", PartitionScheme.Gpt => "Plan.Scheme.Gpt", _ => "Plan.Scheme.None" })),
        };

        if (plan.Partitions.Count > 0)
        {
            lines.Add(new SummaryLine(
                localizer.Get("Plan.Label.Partitions"),
                string.Join("  ·  ", plan.Partitions.Select(p => DescribePartition(p, localizer, culture)))));
        }

        lines.Add(new SummaryLine(localizer.Get("Plan.Label.Boot"), DescribeBoot(plan.BootMethod, localizer)));

        if (plan.SplitWim)
        {
            lines.Add(new SummaryLine("", localizer.Get("Plan.SplitWim")));
        }

        var warnings = new List<SummaryWarning>();
        warnings.AddRange(preview.Inspection.Warnings.Select(w => new SummaryWarning(w.Format(localizer), w.Severity)));
        warnings.AddRange(plan.Warnings.Select(w => new SummaryWarning(w.Format(localizer), WarningSeverity.Warning)));

        return new PlanSummary(lines, warnings);
    }

    private static string DescribePartition(PlannedPartition partition, Localizer localizer, System.Globalization.CultureInfo culture)
    {
        var fileSystem = FileSystemName(partition.FileSystem);
        var size = ByteSize.Format(partition.LengthBytes, culture);
        var role = localizer.Get("Plan.Role." + partition.Role);
        return fileSystem.Length == 0 ? $"{role} {size}" : $"{role}: {fileSystem} {size}";
    }

    private static string DescribeImage(WritePreview preview, Localizer localizer)
    {
        var profile = preview.Inspection.Profile;
        var kind = localizer.Get("Plan.Kind." + profile.Kind);
        var details = new List<string>();

        if (!string.IsNullOrWhiteSpace(profile.VolumeLabel))
        {
            details.Add(profile.VolumeLabel.Trim());
        }

        if (profile.Arch != WindowsArch.Unknown)
        {
            details.Add(profile.Arch.ToString());
        }

        if (profile.WindowsBuild > 0)
        {
            details.Add($"Build {profile.WindowsBuild}");
        }

        return details.Count == 0 ? kind : $"{kind} ({string.Join(", ", details)})";
    }

    private static string DescribeBoot(BootMethod method, Localizer localizer)
    {
        if (method == BootMethod.None)
        {
            return localizer.Get("Plan.Boot.None");
        }

        return string.Join(" + ", Enum.GetValues<BootMethod>()
            .Where(flag => flag != BootMethod.None && method.HasFlag(flag))
            .Select(flag => localizer.Get("Plan.Boot." + flag)));
    }

    private static string FileSystemName(FileSystemKind? kind) => kind switch
    {
        FileSystemKind.Fat12 => "FAT12",
        FileSystemKind.Fat16 => "FAT16",
        FileSystemKind.Fat32 => "FAT32",
        FileSystemKind.ExFat => "exFAT",
        FileSystemKind.Ntfs => "NTFS",
        FileSystemKind.Udf => "UDF",
        FileSystemKind.ReFs => "ReFS",
        FileSystemKind.Ext3 => "ext3",
        _ => "",
    };
}
