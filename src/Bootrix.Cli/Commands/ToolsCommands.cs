// SPDX-License-Identifier: GPL-3.0-or-later
using System.CommandLine;
using Bootrix.Cli.Output;
using Bootrix.Core.Engine;
using Bootrix.Core.Errors;
using Bootrix.Core.Jobs;
using Bootrix.Core.Model;
using Bootrix.Core.Storage;
using Bootrix.Core.Writing.Verify;

namespace Bootrix.Cli.Commands;

/// <summary>Checking a medium against its image, and bringing a drive back to a clean state.</summary>
internal static class ToolsCommands
{
    public static Command CreateVerify(Lazy<IEngine> engine, Lazy<IDiskService> disks)
    {
        var image = new Argument<FileInfo>("image") { Description = "The image the medium was made from (also compressed ones)." };
        var diskOption = new Option<string[]>("--disk", "-d")
        {
            Description = "Disk to check: number (3), name (disk3) or serial number. Repeat for several.",
            Required = true,
            AllowMultipleArgumentsPerToken = true,
        };
        var mode = new Option<string>("--mode") { Description = "auto, raw (byte for byte) or files (the files of the image).", DefaultValueFactory = _ => "auto" };
        var json = new Option<bool>("--json") { Description = "Machine readable output (one JSON object per line)." };

        var command = new Command("verify", "Read a medium and compare it with its image; nothing on the medium is changed.") { image, diskOption, mode, json };
        command.SetAction(async (parse, cancellationToken) =>
        {
            var writer = new ConsoleWriter(parse.GetValue(json));
            var (soft, abort) = TargetPrompt.HookCancel(cancellationToken);
            using (soft)
            using (abort)
            {
                try
                {
                    var file = parse.GetValue(image)!;
                    if (!file.Exists)
                    {
                        throw new BootrixException(ErrorCode.ImageUnreadable, file.FullName) { Arguments = [file.FullName] };
                    }

                    if (!Enum.TryParse<VerifyMode>(parse.GetValue(mode), ignoreCase: true, out var verifyMode))
                    {
                        throw new BootrixException(ErrorCode.InvalidSpec, "verify mode") { Arguments = ["--mode must be auto, raw or files."] };
                    }

                    var targets = await TargetPrompt.ResolveAsync(engine.Value, disks.Value, parse.GetValue(diskOption) ?? [], [], writer, destructive: false, soft.Token).ConfigureAwait(false);
                    var request = new VerifyJobRequest { ImagePath = file.FullName, Targets = targets!, Mode = verifyMode };
                    return await RunAsync(engine.Value, request, writer, result => $"The medium matches the image ({ConsoleWriter.FormatSize(result.ImageBytes)} compared).", soft.Token, abort.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    writer.EndProgress();
                    writer.WriteError(ex);
                    return ExitCodes.For(ex);
                }
            }
        });
        return command;
    }

    public static Command CreateRestore(Lazy<IEngine> engine, Lazy<IDiskService> disks)
    {
        var diskOption = new Option<string[]>("--disk", "-d")
        {
            Description = "Drive to restore: number (3), name (disk3) or serial number. Repeat for several.",
            Required = true,
            AllowMultipleArgumentsPerToken = true,
        };
        var confirm = new Option<string[]>("--confirm")
        {
            Description = "Serial number of each target (or diskN when it has none). Required instead of the interactive question.",
            AllowMultipleArgumentsPerToken = true,
        };
        var scheme = new Option<string>("--scheme") { Description = "mbr (default) or gpt.", DefaultValueFactory = _ => "mbr" };
        var fileSystem = new Option<string>("--fs") { Description = "auto (default), fat32, exfat or ntfs.", DefaultValueFactory = _ => "auto" };
        var label = new Option<string>("--label") { Description = "Volume label." };
        var json = new Option<bool>("--json") { Description = "Machine readable output (one JSON object per line)." };

        var command = new Command("restore-drive", "Erase the partition tables and the start and end of a drive, then create one partition over the whole disk (for sticks written with a hybrid image).")
        {
            diskOption, confirm, scheme, fileSystem, label, json,
        };
        command.SetAction(async (parse, cancellationToken) =>
        {
            var writer = new ConsoleWriter(parse.GetValue(json));
            var (soft, abort) = TargetPrompt.HookCancel(cancellationToken);
            using (soft)
            using (abort)
            {
                try
                {
                    if (!Enum.TryParse<PartitionScheme>(parse.GetValue(scheme), ignoreCase: true, out var partitionScheme) || partitionScheme == PartitionScheme.Auto)
                    {
                        throw new BootrixException(ErrorCode.InvalidSpec, "scheme") { Arguments = ["--scheme must be mbr or gpt."] };
                    }

                    if (!Enum.TryParse<FileSystemKind>(parse.GetValue(fileSystem), ignoreCase: true, out var kind))
                    {
                        throw new BootrixException(ErrorCode.InvalidSpec, "file system") { Arguments = ["--fs must be auto, fat32, exfat or ntfs."] };
                    }

                    var targets = await TargetPrompt.ResolveAsync(engine.Value, disks.Value, parse.GetValue(diskOption) ?? [], parse.GetValue(confirm) ?? [], writer, destructive: true, soft.Token).ConfigureAwait(false);
                    if (targets is null)
                    {
                        return ExitCodes.Usage;
                    }

                    var request = new RestoreDriveJobRequest { Targets = targets, Scheme = partitionScheme, FileSystem = kind, Label = parse.GetValue(label) };
                    return await RunAsync(engine.Value, request, writer, _ => string.Empty, soft.Token, abort.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    writer.EndProgress();
                    writer.WriteError(ex);
                    return ExitCodes.For(ex);
                }
            }
        });
        return command;
    }

    private static async Task<int> RunAsync(
        IEngine engine,
        EngineJobRequest request,
        ConsoleWriter writer,
        Func<EngineJobResult, string> success,
        CancellationToken soft,
        CancellationToken abort)
    {
        var result = await engine.RunJobAsync(request, new Progress<ProgressReport>(report => writer.WriteProgress(report)), soft, abort).ConfigureAwait(false);
        writer.EndProgress();

        if (result.Succeeded)
        {
            writer.WriteLine(writer.Json ? string.Empty : $"Done in {result.Duration:hh\\:mm\\:ss}. {success(result)}".TrimEnd());
            return ExitCodes.Success;
        }

        var error = result.ToException()!;
        writer.WriteError(error);
        return ExitCodes.For(error);
    }
}
