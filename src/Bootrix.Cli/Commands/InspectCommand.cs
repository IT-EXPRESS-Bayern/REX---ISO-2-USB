// SPDX-License-Identifier: GPL-3.0-or-later
using System.CommandLine;
using Bootrix.Cli.Output;
using Bootrix.Core.Errors;
using Bootrix.Core.Images;
using Bootrix.Core.Localization;
using Bootrix.Core.Text;

namespace Bootrix.Cli.Commands;

internal static class InspectCommand
{
    public static Command Create(ImageInspector inspector)
    {
        var image = new Argument<FileInfo>("image") { Description = "Image to look at." };
        var json = new Option<bool>("--json") { Description = "Machine readable output." };

        var command = new Command("inspect", "Show what an image is: kind, boot files, Windows editions, problems.") { image, json };
        command.SetAction(async (parse, cancellationToken) =>
        {
            var writer = new ConsoleWriter(parse.GetValue(json));
            try
            {
                var file = parse.GetValue(image)!;
                if (!file.Exists)
                {
                    throw new BootrixException(ErrorCode.ImageUnreadable, file.FullName) { Arguments = [file.FullName] };
                }

                var result = await inspector.InspectAsync(file.FullName, cancellationToken: cancellationToken).ConfigureAwait(false);
                var profile = result.Profile;
                var editions = result.Windows?.InstallImages.SelectMany(i => i.Metadata.Editions).ToList() ?? [];

                if (writer.Json)
                {
                    writer.WriteObject(new
                    {
                        file = file.FullName,
                        kind = profile.Kind.ToString(),
                        family = profile.Family,
                        container = result.Container.ToString(),
                        compression = result.Compression.ToString(),
                        label = profile.VolumeLabel,
                        sizeBytes = result.FileLength,
                        imageBytes = result.ImageLength,
                        hybrid = profile.IsHybrid,
                        biosBoot = profile.HasBiosBootFiles || profile.HasElToritoBios,
                        efiBoot = profile.HasEfiBootFiles || profile.HasElToritoEfi,
                        architecture = profile.Arch.ToString(),
                        windowsBuild = profile.WindowsBuild,
                        editions = editions.Select(e => new { index = e.Index, name = e.Name, sizeBytes = e.TotalBytes }),
                        warnings = result.Warnings.Select(w => new { key = w.Key, severity = w.Severity.ToString(), text = w.Format(Localizer.Default) }),
                    });
                    return result.HasErrors ? ExitCodes.ImageProblem : ExitCodes.Success;
                }

                writer.WriteLine($"File:        {file.FullName} ({ByteSize.Format(result.FileLength)})");
                writer.WriteLine($"Kind:        {profile.Kind}{(profile.Family is null ? "" : $" ({profile.Family})")}");
                writer.WriteLine($"Container:   {result.Container}{(result.Compression == Core.Images.Compression.CompressionFormat.None ? "" : $", {result.Compression}")}");
                if (!string.IsNullOrWhiteSpace(profile.VolumeLabel))
                {
                    writer.WriteLine($"Label:       {profile.VolumeLabel}");
                }

                writer.WriteLine($"Hybrid:      {(profile.IsHybrid ? "yes (can be written byte for byte)" : "no")}");
                writer.WriteLine($"Boots:       {(profile.HasBiosBootFiles || profile.HasElToritoBios ? "BIOS " : "")}{(profile.HasEfiBootFiles || profile.HasElToritoEfi ? "UEFI" : "")}".TrimEnd());
                if (profile.Arch != WindowsArch.Unknown || profile.WindowsBuild > 0)
                {
                    writer.WriteLine($"Windows:     {profile.Arch}, build {profile.WindowsBuild}");
                }

                foreach (var edition in editions)
                {
                    writer.WriteLine($"  {edition.Index,3}  {edition.Name}  ({ByteSize.Format(edition.TotalBytes)})");
                }

                foreach (var warning in result.Warnings)
                {
                    writer.WriteLine($"[{warning.Severity}] {warning.Format(Localizer.Default)}");
                }

                return result.HasErrors ? ExitCodes.ImageProblem : ExitCodes.Success;
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
