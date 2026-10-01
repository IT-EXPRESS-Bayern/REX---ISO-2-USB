// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images;
using Bootrix.Core.Localization;
using Bootrix.Core.Text;
using Bootrix.Core.Workshop;
using Bootrix.Core.Workshop.Advice;

namespace Bootrix.Core.Presentation;

public sealed record RequirementLine(string Text, CheckStatus Status, string StatusText);

/// <summary>The check of a PC as it is shown: what was found, how Windows 11 fares, and what to do about it.</summary>
public sealed record TargetPcView(
    IReadOnlyList<SummaryLine> Hardware,
    string Verdict,
    Windows11Verdict VerdictKind,
    IReadOnlyList<RequirementLine> Requirements,
    IReadOnlyList<SummaryLine> Recommendation,
    IReadOnlyList<SummaryWarning> Notes)
{
    public static TargetPcView From(TargetPcInfo info, TargetPcAssessment assessment, Localizer localizer)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(assessment);
        ArgumentNullException.ThrowIfNull(localizer);

        var culture = localizer.Culture;
        string Unknown() => localizer.Get("Pc.Unknown");
        string Or(string? value) => string.IsNullOrWhiteSpace(value) ? Unknown() : value.Trim();

        var hardware = new List<SummaryLine>
        {
            new(localizer.Get("Pc.Label.Machine"), Machine(info.Machine, Unknown)),
            new(localizer.Get("Pc.Label.Cpu"), Cpu(info.Cpu, localizer, Unknown)),
            new(localizer.Get("Pc.Label.Memory"), info.Memory?.InstalledBytes is { } ram ? ByteSize.Format(ram, culture) : Unknown()),
            new(localizer.Get("Pc.Label.Firmware"), info.Firmware?.Type switch
            {
                FirmwareType.Uefi => localizer.Get("Pc.Firmware.Uefi"),
                FirmwareType.Bios => localizer.Get("Pc.Firmware.Bios"),
                _ => Unknown(),
            }),
            new(localizer.Get("Pc.Label.SecureBoot"), info.Firmware?.SecureBoot switch
            {
                SecureBootState.On => localizer.Get("Pc.SecureBoot.On"),
                SecureBootState.Off => localizer.Get("Pc.SecureBoot.Off"),
                _ => Unknown(),
            }),
            new(localizer.Get("Pc.Label.Tpm"), Tpm(info.Tpm, localizer, Unknown)),
        };

        if (info.Disks is { Count: > 0 } disks)
        {
            hardware.Add(new SummaryLine(
                localizer.Get("Pc.Label.Disks"),
                string.Join("  ·  ", disks.Select(d => $"{Or(d.Name)} ({ByteSize.Format(d.SizeBytes, culture)})"))));
        }

        if (info.RunningSystem is { ProductName: { Length: > 0 } product } running)
        {
            hardware.Add(new SummaryLine(
                localizer.Get("Pc.Label.System"),
                running.BuildNumber is { } build ? $"{product} (Build {build})" : product));
        }

        var requirements = assessment.Windows11Checks
            .Select(check => new RequirementLine(check.Message.Describe(localizer), check.Status, localizer.Get("Pc.Status." + check.Status)))
            .ToList();

        var recommendation = new List<SummaryLine>();
        if (assessment.Image is { } image)
        {
            var name = string.Join(' ', new[] { image.Product.ToString(), image.Version, image.Edition }.Where(p => !string.IsNullOrWhiteSpace(p)));
            recommendation.Add(new SummaryLine(localizer.Get("Pc.Label.Recommendation"), $"{name}, {image.Architecture}{(image.Language is null ? "" : $", {image.Language}")}"));
        }

        var boot = assessment.Boot;
        recommendation.Add(new SummaryLine(
            localizer.Get("Pc.Label.Boot"),
            $"{boot.Firmware}, {boot.Scheme}{(boot.FileSystem == Model.FileSystemKind.Auto ? "" : $", {boot.FileSystem}")}"));

        var notes = assessment.Messages
            .Concat(assessment.Boot.Notes)
            .Concat(assessment.DriverNeeds.Select(d => d.Message))
            .Select(m => new SummaryWarning(m.Describe(localizer), m.Severity switch
            {
                AdvisorSeverity.Critical => WarningSeverity.Error,
                AdvisorSeverity.Warning => WarningSeverity.Warning,
                _ => WarningSeverity.Info,
            }))
            .ToList();

        return new TargetPcView(hardware, localizer.Get("Pc.Verdict." + assessment.Windows11), assessment.Windows11, requirements, recommendation, notes);
    }

    private static string Machine(MachineIdentity? machine, Func<string> unknown)
    {
        var name = string.Join(' ', new[] { machine?.Manufacturer, machine?.Model }.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));
        return name.Length == 0 ? unknown() : name;
    }

    private static string Cpu(CpuInfo? cpu, Localizer localizer, Func<string> unknown)
    {
        if (cpu?.Name is not { Length: > 0 } name)
        {
            return unknown();
        }

        return cpu.PhysicalCores is { } cores ? $"{name.Trim()}, {localizer.Get("Pc.Cores", cores)}" : name.Trim();
    }

    private static string Tpm(TpmInfo? tpm, Localizer localizer, Func<string> unknown) => tpm switch
    {
        null or { Present: null } => unknown(),
        { Present: false } => localizer.Get("Pc.Tpm.None"),
        { IsEnabled: false } => localizer.Get("Pc.Tpm.Disabled"),
        { SpecVersion: { Length: > 0 } version } => localizer.Get("Pc.Tpm.Version", version),
        _ => "TPM",
    };
}
