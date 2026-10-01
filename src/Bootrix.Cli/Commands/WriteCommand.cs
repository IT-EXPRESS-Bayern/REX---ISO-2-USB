// SPDX-License-Identifier: GPL-3.0-or-later
using System.CommandLine;
using Bootrix.Cli.Output;
using Bootrix.Core.Errors;
using Bootrix.Core.Jobs;
using Bootrix.Core.Storage;
using Bootrix.Windows.Jobs;
using Bootrix.Windows.Storage;

namespace Bootrix.Cli.Commands;

internal static class WriteCommand
{
    public static Command Create(IDiskService disks, RawWriteJob job, JobRunner runner)
    {
        var image = new Argument<FileInfo>("image") { Description = "Disk image or hybrid ISO to write." };
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
        var noVerify = new Option<bool>("--no-verify") { Description = "Skip reading the data back." };
        var json = new Option<bool>("--json") { Description = "Machine readable output (one JSON object per line)." };

        var command = new Command("write", "Write an image to one or more USB drives, byte for byte.") { image, diskOption, confirm, noVerify, json };
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
                var file = parse.GetValue(image)!;
                if (!file.Exists)
                {
                    throw new BootrixException(ErrorCode.ImageUnreadable, file.FullName) { Arguments = [file.FullName] };
                }

                var devices = (parse.GetValue(diskOption) ?? []).Select(spec => DeviceSelector.Resolve(disks, spec)).DistinctBy(d => d.DiskNumber).ToList();
                var confirmations = parse.GetValue(confirm) ?? [];

                var targets = new List<RawWriteTargetRequest>();
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

                    targets.Add(new RawWriteTargetRequest(device, DiskIdentityReader.Capture(device)));
                }

                var request = new RawWriteRequest { ImagePath = file.FullName, Targets = targets, Verify = !parse.GetValue(noVerify) };
                var result = await runner.RunAsync(
                    job.Create(request),
                    new DelegateProgressSink(writer.WriteProgress),
                    soft.Token,
                    abort.Token).ConfigureAwait(false);
                writer.EndProgress();

                if (result.Succeeded)
                {
                    writer.WriteLine(writer.Json ? string.Empty : $"Done in {result.Duration:hh\\:mm\\:ss}.");
                    return ExitCodes.Success;
                }

                writer.WriteError(result.Error!);
                return ExitCodes.For(result.Error!);
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
