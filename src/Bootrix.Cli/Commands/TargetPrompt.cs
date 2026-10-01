// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Cli.Output;
using Bootrix.Core.Engine;
using Bootrix.Core.Errors;
using Bootrix.Core.Storage;

namespace Bootrix.Cli.Commands;

/// <summary>Resolves the disks named on the command line, makes the user confirm each one that will be changed, and takes its fingerprint.</summary>
internal static class TargetPrompt
{
    /// <returns>The confirmed targets, or null when a disk was not confirmed (the message has been printed).</returns>
    public static async Task<IReadOnlyList<EngineTarget>?> ResolveAsync(
        IEngine engine,
        IDiskService disks,
        IEnumerable<string> specs,
        string[] confirmations,
        ConsoleWriter writer,
        bool destructive,
        CancellationToken cancellationToken)
    {
        var devices = specs.Select(spec => DeviceSelector.Resolve(disks, spec)).DistinctBy(d => d.DiskNumber).ToList();
        var targets = new List<EngineTarget>();
        foreach (var device in devices)
        {
            if (device.IsBlocked)
            {
                throw new BootrixException(ErrorCode.DeviceProtected, device.DevicePath) { Arguments = [device.Protection.ToString()] };
            }

            if (destructive && !Confirmed(device, confirmations, writer))
            {
                writer.WriteLine("Not confirmed, nothing was changed.");
                return null;
            }

            targets.Add(new EngineTarget(device.DevicePath, await engine.CaptureIdentityAsync(device.DevicePath, cancellationToken).ConfigureAwait(false)));
        }

        return targets;
    }

    /// <summary>Ctrl+C asks the job to stop at the next safe point; a second one ends it at once.</summary>
    public static (CancellationTokenSource Soft, CancellationTokenSource Abort) HookCancel(CancellationToken cancellationToken)
    {
        var abort = new CancellationTokenSource();
        var soft = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
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
        return (soft, abort);
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
