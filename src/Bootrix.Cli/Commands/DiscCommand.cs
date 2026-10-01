// SPDX-License-Identifier: GPL-3.0-or-later
using System.CommandLine;
using Bootrix.Cli.Output;
using Bootrix.Core.Errors;
using Bootrix.Core.Jobs;
using Bootrix.Core.Optical;
using Bootrix.Core.Optical.Images;
using Bootrix.Core.Optical.Jobs;
using Bootrix.Core.Text;

namespace Bootrix.Cli.Commands;

internal static class DiscCommand
{
    public static Command Create(Lazy<IOpticalService> optical, Lazy<BurnImageJob> burn, Lazy<RipDiscJob> rip, Lazy<EraseDiscJob> erase, JobRunner runner)
    {
        return new Command("disc", "Burn, read and erase CDs, DVDs and Blu-rays.")
        {
            CreateDrives(optical),
            CreateBurn(optical, burn, runner),
            CreateRip(optical, rip, runner),
            CreateErase(optical, erase, runner),
            CreateEject(optical),
        };
    }

    private static Command CreateDrives(Lazy<IOpticalService> optical)
    {
        var json = new Option<bool>("--json") { Description = "Machine readable output (one JSON object per line)." };
        var command = new Command("drives", "List the optical drives and the discs in them.") { json };
        command.SetAction(async (parse, cancellationToken) =>
        {
            var writer = new ConsoleWriter(parse.GetValue(json));
            try
            {
                var drives = await optical.Value.EnumerateDrivesAsync(cancellationToken).ConfigureAwait(false);
                var rows = new List<IReadOnlyList<string>>();
                foreach (var drive in drives)
                {
                    var media = await optical.Value.QueryMediaAsync(drive, cancellationToken).ConfigureAwait(false);
                    if (writer.Json)
                    {
                        writer.WriteObject(new
                        {
                            id = drive.Id,
                            name = drive.Name,
                            letter = drive.DriveLetter,
                            canRecord = drive.CanRecord,
                            media = media.IsPresent ? media.Type.ToString() : null,
                            state = media.IsPresent ? media.State.ToString() : null,
                            freeBytes = media.IsPresent ? media.FreeSectors * SectorMath.SectorSize : (long?)null,
                        });
                    }
                    else
                    {
                        rows.Add([drive.DriveLetter ?? "", drive.Name, drive.CanRecord ? "yes" : "no", media.IsPresent ? $"{media.Type}, {media.State}" : "empty"]);
                    }
                }

                if (!writer.Json)
                {
                    writer.WriteTable(["Drive", "Name", "Recorder", "Disc"], rows);
                }

                return ExitCodes.Success;
            }
            catch (Exception ex)
            {
                writer.WriteError(ex);
                return ExitCodes.For(ex);
            }
        });
        return command;
    }

    private static Command CreateBurn(Lazy<IOpticalService> optical, Lazy<BurnImageJob> burn, JobRunner runner)
    {
        var image = new Argument<FileInfo>("image") { Description = "ISO, IMG or BIN/CUE file with a data track." };
        var drive = new Option<string[]>("--drive", "-d") { Description = "Drive letter (G:). Repeat to burn the same image on several recorders at once.", Required = true, AllowMultipleArgumentsPerToken = true };
        var speed = new Option<int?>("--speed") { Description = "Write speed as a multiple of 1x; the drive's fastest speed when omitted." };
        var noFinalize = new Option<bool>("--no-finalize") { Description = "Keep the disc open for another session (not for boot media)." };
        var verify = new Option<string>("--verify") { Description = "none, quick or full (the drive's own check).", DefaultValueFactory = _ => "quick" };
        var readBack = new Option<bool>("--read-back") { Description = "Also read the disc back through Windows and compare it with the image by SHA-256." };
        var eject = new Option<bool>("--eject") { Description = "Eject the disc when done." };
        var force = new Option<bool>("--overwrite") { Description = "Write into a rewritable disc that already holds data." };
        var json = new Option<bool>("--json") { Description = "Machine readable output (one JSON object per line)." };

        var command = new Command("burn", "Burn an image to a disc.") { image, drive, speed, noFinalize, verify, readBack, eject, force, json };
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

                var drives = await ResolveAsync(optical.Value, parse.GetValue(drive)!, cancellationToken).ConfigureAwait(false);
                var request = new BurnImageRequest
                {
                    Drives = drives,
                    Image = DiscImageDetector.Open(file.FullName),
                    Options = new BurnOptions
                    {
                        WriteSpeedFactor = parse.GetValue(speed),
                        Finalize = !parse.GetValue(noFinalize),
                        Verify = Enum.TryParse<BurnVerifyLevel>(parse.GetValue(verify), ignoreCase: true, out var level)
                            ? level
                            : throw new BootrixException(ErrorCode.InvalidSpec, "verify level") { Arguments = ["--verify must be none, quick or full."] },
                        ReadBackSha256 = parse.GetValue(readBack),
                        EjectWhenDone = parse.GetValue(eject),
                        ForceOverwrite = parse.GetValue(force),
                    },
                };

                return await RunAsync(runner, burn.Value.Create(request), writer, cancellationToken).ConfigureAwait(false);
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

    private static Command CreateRip(Lazy<IOpticalService> optical, Lazy<RipDiscJob> rip, JobRunner runner)
    {
        var drive = new Option<string>("--drive", "-d") { Description = "Drive letter (G:).", Required = true };
        var output = new Option<FileInfo>("--output", "-o") { Description = "ISO file to create. An unfinished rip of the same file is continued.", Required = true };
        var eject = new Option<bool>("--eject") { Description = "Eject the disc when done." };
        var json = new Option<bool>("--json") { Description = "Machine readable output (one JSON object per line)." };

        var command = new Command("rip", "Read a data disc into an ISO file; unreadable sectors are retried and listed.") { drive, output, eject, json };
        command.SetAction(async (parse, cancellationToken) =>
        {
            var writer = new ConsoleWriter(parse.GetValue(json));
            try
            {
                var found = (await ResolveAsync(optical.Value, [parse.GetValue(drive)!], cancellationToken).ConfigureAwait(false))[0];
                var request = new RipDiscRequest { Drive = found, IsoPath = parse.GetValue(output)!.FullName, EjectWhenDone = parse.GetValue(eject) };
                return await RunAsync(runner, rip.Value.Create(request), writer, cancellationToken).ConfigureAwait(false);
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

    private static Command CreateErase(Lazy<IOpticalService> optical, Lazy<EraseDiscJob> erase, JobRunner runner)
    {
        var drive = new Option<string>("--drive", "-d") { Description = "Drive letter (G:).", Required = true };
        var full = new Option<bool>("--full") { Description = "Overwrite the whole disc instead of clearing only its table of contents." };
        var eject = new Option<bool>("--eject") { Description = "Eject the disc when done." };
        var json = new Option<bool>("--json") { Description = "Machine readable output (one JSON object per line)." };

        var command = new Command("erase", "Erase a rewritable disc.") { drive, full, eject, json };
        command.SetAction(async (parse, cancellationToken) =>
        {
            var writer = new ConsoleWriter(parse.GetValue(json));
            try
            {
                var found = (await ResolveAsync(optical.Value, [parse.GetValue(drive)!], cancellationToken).ConfigureAwait(false))[0];
                var request = new EraseDiscRequest { Drive = found, Mode = parse.GetValue(full) ? EraseMode.Full : EraseMode.Quick, EjectWhenDone = parse.GetValue(eject) };
                return await RunAsync(runner, erase.Value.Create(request), writer, cancellationToken).ConfigureAwait(false);
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

    private static Command CreateEject(Lazy<IOpticalService> optical)
    {
        var drive = new Option<string>("--drive", "-d") { Description = "Drive letter (G:).", Required = true };
        var close = new Option<bool>("--close") { Description = "Close the tray instead of opening it." };
        var command = new Command("eject", "Open or close the tray.") { drive, close };
        command.SetAction(async (parse, cancellationToken) =>
        {
            var writer = new ConsoleWriter(json: false);
            try
            {
                var found = (await ResolveAsync(optical.Value, [parse.GetValue(drive)!], cancellationToken).ConfigureAwait(false))[0];
                if (parse.GetValue(close))
                {
                    await optical.Value.CloseTrayAsync(found, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await optical.Value.EjectAsync(found, cancellationToken).ConfigureAwait(false);
                }

                return ExitCodes.Success;
            }
            catch (Exception ex)
            {
                writer.WriteError(ex);
                return ExitCodes.For(ex);
            }
        });
        return command;
    }

    private static async Task<int> RunAsync(JobRunner runner, IJob job, ConsoleWriter writer, CancellationToken cancellationToken)
    {
        var result = await runner.RunAsync(job, new DelegateProgressSink(writer.WriteProgress), cancellationToken).ConfigureAwait(false);
        writer.EndProgress();

        if (result.Succeeded)
        {
            writer.WriteLine(writer.Json ? string.Empty : $"Done in {result.Duration:hh\\:mm\\:ss}.");
            return ExitCodes.Success;
        }

        writer.WriteError(result.Error!);
        return ExitCodes.For(result.Error!);
    }

    /// <summary>Finds recorders by drive letter ("G" or "G:") or by the platform's id.</summary>
    private static async Task<IReadOnlyList<OpticalDrive>> ResolveAsync(IOpticalService optical, IEnumerable<string> specs, CancellationToken cancellationToken)
    {
        var drives = await optical.EnumerateDrivesAsync(cancellationToken).ConfigureAwait(false);
        var found = new List<OpticalDrive>();
        foreach (var spec in specs)
        {
            var wanted = spec.Trim().TrimEnd(':', '\\');
            var match = drives.FirstOrDefault(d =>
                string.Equals(d.DriveLetter?.TrimEnd(':'), wanted, StringComparison.OrdinalIgnoreCase)
                || string.Equals(d.Id, spec, StringComparison.OrdinalIgnoreCase))
                ?? throw new BootrixException(ErrorCode.NoRecorder, spec);
            found.Add(match);
        }

        return found;
    }
}
