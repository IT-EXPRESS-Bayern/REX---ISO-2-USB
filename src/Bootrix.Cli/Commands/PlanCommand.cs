// SPDX-License-Identifier: GPL-3.0-or-later
using System.CommandLine;
using Bootrix.Cli.Output;
using Bootrix.Core.Errors;
using Bootrix.Core.Localization;
using Bootrix.Core.Presentation;
using Bootrix.Core.Storage;
using Bootrix.Core.Writing;

namespace Bootrix.Cli.Commands;

internal static class PlanCommand
{
    public static Command Create(MediaPlanService planner, Lazy<IDiskService> disks)
    {
        var image = new Argument<FileInfo>("image") { Description = "Image to plan; leave out for a DOS stick or a plain format.", Arity = ArgumentArity.ZeroOrOne };
        var medium = new Option<string>("--medium") { Description = "image (default), dos or format.", DefaultValueFactory = _ => "image" };
        var disk = new Option<string>("--disk", "-d") { Description = "Target disk: number, name or serial number." };
        var size = new Option<double?>("--size-gb") { Description = "Plan for a drive of this size instead of a real one (a dry run without hardware)." };
        var sector = new Option<int>("--sector-size") { Description = "Logical sector size of the pretend drive (512 or 4096).", DefaultValueFactory = _ => 512 };
        var spec = SpecOptions.Create();
        var json = new Option<bool>("--json") { Description = "Machine readable output." };

        var command = new Command("plan", "Show what 'write' would do for this image and drive, without touching anything.") { image, medium, disk, size, sector, json };
        spec.AddTo(command);
        command.SetAction(async (parse, cancellationToken) =>
        {
            var writer = new ConsoleWriter(parse.GetValue(json));
            try
            {
                var source = parse.GetValue(medium)?.ToLowerInvariant() switch
                {
                    "dos" => Core.Engine.WriteSource.Dos,
                    "format" => Core.Engine.WriteSource.Format,
                    _ => Core.Engine.WriteSource.Image,
                };
                var file = parse.GetValue(image);
                if (source == Core.Engine.WriteSource.Image && file is null)
                {
                    throw new BootrixException(ErrorCode.InvalidSpec, "no image") { Arguments = ["Give an image, or --medium dos / --medium format."] };
                }

                if (file is { Exists: false })
                {
                    throw new BootrixException(ErrorCode.ImageUnreadable, file.FullName) { Arguments = [file.FullName] };
                }

                var device = parse.GetValue(disk) is { } spec1
                    ? DeviceSelector.Resolve(disks.Value, spec1)
                    : parse.GetValue(size) is { } gigabytes
                        ? new StorageDevice
                        {
                            DiskNumber = -1,
                            DevicePath = "pretend",
                            Vendor = "Pretend",
                            Product = $"{gigabytes:0.##} GB drive",
                            Bus = BusType.Usb,
                            IsRemovableMedia = true,
                            SizeBytes = (long)(gigabytes * (1L << 30)),
                            LogicalSectorSize = parse.GetValue(sector),
                        }
                        : throw new BootrixException(ErrorCode.InvalidSpec, "no target") { Arguments = ["Give --disk or --size-gb."] };

                var jobSpec = spec.Build(parse, verify: true);
                var preview = source == Core.Engine.WriteSource.Image
                    ? await planner.PlanAsync(file!.FullName, jobSpec.Target, device, cancellationToken).ConfigureAwait(false)
                    : MediaPlanService.Plan(MediaPlanService.InspectionFor(source), jobSpec.Target, device);
                var summary = PlanSummary.From(preview, Localizer.Default);

                if (writer.Json)
                {
                    var plan = preview.Plan;
                    writer.WriteObject(new
                    {
                        image = preview.Inspection.Profile.Kind.ToString(),
                        method = plan.WriteMethod.ToString(),
                        scheme = plan.Superfloppy ? "none" : plan.Scheme.ToString(),
                        firmware = plan.Firmware.ToString(),
                        boot = plan.BootMethod.ToString(),
                        splitWim = plan.SplitWim,
                        uefiNtfs = plan.UsesUefiNtfs,
                        sectorSize = plan.SectorSize,
                        partitions = plan.Partitions.Select(p => new
                        {
                            role = p.Role.ToString(),
                            fileSystem = p.FileSystem?.ToString(),
                            offsetBytes = p.StartBytes,
                            lengthBytes = p.LengthBytes,
                            label = p.Label,
                            active = p.Active,
                        }),
                        warnings = summary.Warnings.Select(w => new { severity = w.Severity.ToString(), text = w.Text }),
                    });
                    return summary.HasErrors ? ExitCodes.ImageProblem : ExitCodes.Success;
                }

                var width = summary.Lines.Max(l => l.Label.Length) + 3;
                foreach (var line in summary.Lines)
                {
                    writer.WriteLine((line.Label.Length == 0 ? "" : line.Label + ":").PadRight(width) + line.Value);
                }

                foreach (var warning in summary.Warnings)
                {
                    writer.WriteLine($"[{warning.Severity}] {warning.Text}");
                }

                return summary.HasErrors ? ExitCodes.ImageProblem : ExitCodes.Success;
            }
            catch (Exception ex)
            {
                writer.WriteError(ex);
                return ExitCodes.For(ex);
            }
        });
        return command;
    }
}
