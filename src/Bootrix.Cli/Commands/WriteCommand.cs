// SPDX-License-Identifier: GPL-3.0-or-later
using System.CommandLine;
using Bootrix.Cli.Output;
using Bootrix.Core.Engine;
using Bootrix.Core.Errors;
using Bootrix.Core.Jobs;
using Bootrix.Core.Model;
using Bootrix.Core.Profiles;
using Bootrix.Core.Storage;

namespace Bootrix.Cli.Commands;

internal static class WriteCommand
{
    public static Command Create(Lazy<IEngine> engine, Lazy<IDiskService> disks) => CreateCore("write", WriteSource.Image, engine, disks);

    /// <summary>Partitions and formats a drive and leaves it empty.</summary>
    public static Command CreateFormat(Lazy<IEngine> engine, Lazy<IDiskService> disks) => CreateCore("format", WriteSource.Format, engine, disks);

    /// <summary>Makes a bootable DOS stick or diskette: FreeDOS, or MS-DOS from Microsoft's own download.</summary>
    public static Command CreateDos(Lazy<IEngine> engine, Lazy<IDiskService> disks) => CreateCore("dos", WriteSource.Dos, engine, disks);

    private static Command CreateCore(string name, WriteSource source, Lazy<IEngine> engine, Lazy<IDiskService> disks)
    {
        var image = new Argument<FileInfo>("image") { Description = "ISO, disk image, or a compressed one (.gz, .xz, .zst, .bz2, .zip, .dmg)." };
        var dosFlavor = new Option<string>("--system") { Description = "freedos (default) or msdos. MS-DOS is fetched from Microsoft's own download.", DefaultValueFactory = _ => "freedos" };
        var acceptMicrosoft = new Option<bool>("--accept-microsoft-download") { Description = "Confirm that Bootrix may download the MS-DOS files from Microsoft, under Microsoft's terms." };
        var diskOption = new Option<string[]>("--disk", "-d")
        {
            Description = "Target disk: number (3), name (disk3) or serial number. Repeat for several sticks.",
            Required = true,
            AllowMultipleArgumentsPerToken = true,
        };
        var confirm = new Option<string[]>("--confirm")
        {
            Description = "Serial number of each target (or diskN when it has none). Required instead of the interactive question.",
            AllowMultipleArgumentsPerToken = true,
        };
        var spec = SpecOptions.Create();
        var noVerify = new Option<bool>("--no-verify") { Description = "Skip reading the data back." };
        var json = new Option<bool>("--json") { Description = "Machine readable output (one JSON object per line)." };

        var description = source switch
        {
            WriteSource.Dos => "Make a bootable DOS stick or diskette (FreeDOS or MS-DOS).",
            WriteSource.Format => "Partition and format a drive; it is left empty.",
            _ => "Write an image to one or more drives the way its plan decides (see 'plan').",
        };
        var command = new Command(name, description) { diskOption, confirm, noVerify, json };
        if (source == WriteSource.Image)
        {
            command.Add(image);
        }

        if (source == WriteSource.Dos)
        {
            command.Add(dosFlavor);
            command.Add(acceptMicrosoft);
        }

        spec.AddTo(command);
        command.SetAction(async (parse, cancellationToken) =>
        {
            var writer = new ConsoleWriter(parse.GetValue(json));
            using var abort = new CancellationTokenSource();
            using var soft = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                if (soft.IsCancellationRequested)
                {
                    abort.Cancel();
                }
                else
                {
                    soft.Cancel();
                }
            };

            try
            {
                var file = source == WriteSource.Image ? parse.GetValue(image)! : null;
                if (file is { Exists: false })
                {
                    throw new BootrixException(ErrorCode.ImageUnreadable, file.FullName) { Arguments = [file.FullName] };
                }

                var devices = (parse.GetValue(diskOption) ?? []).Select(s => DeviceSelector.Resolve(disks.Value, s)).DistinctBy(d => d.DiskNumber).ToList();
                var confirmations = parse.GetValue(confirm) ?? [];

                var targets = new List<EngineTarget>();
                foreach (var device in devices)
                {
                    if (device.IsBlocked)
                    {
                        throw new BootrixException(ErrorCode.DeviceProtected, device.DevicePath) { Arguments = [device.Protection.ToString()] };
                    }

                    if (!Confirmed(device, confirmations, writer))
                    {
                        writer.WriteLine("Not confirmed, nothing was written.");
                        return ExitCodes.Usage;
                    }

                    targets.Add(new EngineTarget(device.DevicePath, await engine.Value.CaptureIdentityAsync(device.DevicePath, soft.Token).ConfigureAwait(false)));
                }

                var request = new WriteImageJobRequest
                {
                    ImagePath = file?.FullName,
                    Source = source,
                    Targets = targets,
                    Spec = spec.Build(parse, verify: !parse.GetValue(noVerify)) with
                    {
                        Dos = new Core.Boot.Dos.DosOptions
                        {
                            Flavor = string.Equals(parse.GetValue(dosFlavor), "msdos", StringComparison.OrdinalIgnoreCase) ? Core.Boot.Dos.DosFlavor.MsDos : Core.Boot.Dos.DosFlavor.FreeDos,
                            AcceptMicrosoftDownload = parse.GetValue(acceptMicrosoft),
                        },
                    },
                    LocalAccountPassword = spec.Password(parse),
                };

                var result = await engine.Value.RunJobAsync(
                    request,
                    new Progress<ProgressReport>(report => writer.WriteProgress(report)),
                    soft.Token,
                    abort.Token).ConfigureAwait(false);
                writer.EndProgress();

                if (result.Succeeded)
                {
                    writer.WriteLine(writer.Json
                        ? string.Empty
                        : $"Done in {result.Duration:hh\\:mm\\:ss}.{(result.ImageSha256 is { } hash ? $" Image SHA-256 {hash}" : "")}");
                    return ExitCodes.Success;
                }

                var error = result.ToException()!;
                writer.WriteError(error);
                return ExitCodes.For(error);
            }
            catch (Exception ex)
            {
                writer.EndProgress();
                writer.WriteError(ex);
                return ExitCodes.For(ex);
            }
        });

        return command;
    }

    private static bool Confirmed(StorageDevice device, string[] confirmations, ConsoleWriter writer)
    {
        var expected = DeviceConfirmation.TextFor(device);
        if (confirmations.Length > 0)
        {
            return confirmations.Any(c => string.Equals(c, expected, StringComparison.OrdinalIgnoreCase));
        }

        if (Console.IsInputRedirected)
        {
            writer.WriteLine($"Disk {device.DiskNumber} ({device.DisplayName}) needs --confirm {expected} when input is not interactive.");
            return false;
        }

        writer.WriteLine();
        writer.WriteLine($"  Disk {device.DiskNumber}: {device.DisplayName}, {ConsoleWriter.FormatSize(device.SizeBytes)}, {device.Bus}");
        writer.WriteLine($"  Drive letters: {(device.DriveLetters.Length > 0 ? device.DriveLetters : "none")}, partitions: {device.Partitions.Count}");
        writer.WriteLine("  ALL DATA ON THIS DISK WILL BE ERASED.");
        Console.Write($"  Type {expected} to continue: ");
        var typed = Console.ReadLine();
        return string.Equals(typed?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
    }
}
