// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using System.Text;
using Bootrix.Core.Errors;
using Bootrix.Core.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Windows.FileSystems;

public sealed record FormatRequest(string VolumePath, FileSystemKind FileSystem, string? Label, int ClusterSizeBytes, bool Quick, bool Removable);

/// <summary>
/// Formats a volume with the file system code that ships with Windows (fmifs.dll), the same engine
/// behind the Explorer format dialog. FormatEx has no context parameter, so only one format can
/// run at a time; the callback finds its state through a static field.
/// </summary>
public static unsafe partial class FmifsFormatter
{
    private const int MediaTypeRemovable = 11;
    private const int MediaTypeFixed = 12;

    private const int CommandProgress = 0;
    private const int CommandDoneWithStructure = 1;
    private const int CommandIncompatibleFileSystem = 3;
    private const int CommandAccessDenied = 6;
    private const int CommandMediaWriteProtected = 7;
    private const int CommandVolumeInUse = 8;
    private const int CommandCantQuickFormat = 9;
    private const int CommandDone = 0xB;
    private const int CommandBadLabel = 0xC;
    private const int CommandOutput = 0xE;
    private const int CommandStructureProgress = 0xF;
    private const int CommandClusterSizeTooSmall = 0x10;
    private const int CommandClusterSizeTooBig = 0x11;
    private const int CommandVolumeTooSmall = 0x12;
    private const int CommandVolumeTooBig = 0x13;
    private const int CommandNoMediaInDrive = 0x14;

    private static readonly Lock Gate = new();
    private static FormatState? _current;

    [LibraryImport("fmifs.dll", EntryPoint = "FormatEx", StringMarshalling = StringMarshalling.Utf16)]
    private static partial void FormatEx(
        string driveRoot,
        int mediaType,
        string fileSystemName,
        string label,
        [MarshalAs(UnmanagedType.U1)] bool quickFormat,
        uint clusterSize,
        delegate* unmanaged<int, uint, nint, byte> callback);

    public static Task FormatAsync(FormatRequest request, IProgress<double>? progress = null, ILogger? logger = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.Factory.StartNew(
            () => Format(request, progress, logger ?? NullLogger.Instance),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    public static string FileSystemName(FileSystemKind kind) => kind switch
    {
        FileSystemKind.Fat12 or FileSystemKind.Fat16 => "FAT",
        FileSystemKind.Fat32 => "FAT32",
        FileSystemKind.ExFat => "exFAT",
        FileSystemKind.Ntfs => "NTFS",
        FileSystemKind.Udf => "UDF",
        FileSystemKind.ReFs => "ReFS",
        _ => throw new NotSupportedException($"{kind} cannot be formatted by Windows."),
    };

    private static void Format(FormatRequest request, IProgress<double>? progress, ILogger logger)
    {
        var name = FileSystemName(request.FileSystem);
        var root = request.VolumePath.EndsWith('\\') ? request.VolumePath : request.VolumePath + "\\";

        lock (Gate)
        {
            var state = new FormatState(progress, logger);
            _current = state;
            try
            {
                FormatEx(
                    root,
                    request.Removable ? MediaTypeRemovable : MediaTypeFixed,
                    name,
                    request.Label ?? "",
                    request.Quick,
                    (uint)request.ClusterSizeBytes,
                    &OnCallback);
            }
            finally
            {
                _current = null;
            }

            if (!state.Succeeded)
            {
                throw new BootrixException(ErrorCode.ExternalToolFailed, $"FormatEx {name} {root}: {state.FailureReason ?? "unknown failure"}\n{state.Output}")
                {
                    Arguments = ["fmifs FormatEx", state.FailureReason ?? "?"],
                };
            }
        }
    }

    [UnmanagedCallersOnly]
    private static byte OnCallback(int command, uint modifier, nint argument)
    {
        var state = _current;
        if (state is null)
        {
            return 1;
        }

        switch (command)
        {
            case CommandProgress:
            case CommandStructureProgress:
                if (argument != 0)
                {
                    state.Progress?.Report(Math.Clamp(*(uint*)argument / 100.0, 0, 1));
                }

                break;
            case CommandDone:
                state.Succeeded = argument != 0 && *(byte*)argument != 0;
                break;
            case CommandOutput:
                if (argument != 0)
                {
                    var text = *(nint*)(argument + sizeof(uint) + (nint.Size == 8 ? 4 : 0));
                    if (text != 0)
                    {
                        state.AppendOutput(Marshal.PtrToStringAnsi(text));
                    }
                }

                break;
            case CommandDoneWithStructure:
                break;
            default:
                state.FailureReason ??= Describe(command);
                if (state.FailureReason is null)
                {
                    state.Logger.LogDebug("fmifs callback command 0x{Command:X} modifier {Modifier}", command, modifier);
                }

                break;
        }

        return 1;
    }

    private static string? Describe(int command) => command switch
    {
        CommandIncompatibleFileSystem => "incompatible file system",
        CommandAccessDenied => "access denied",
        CommandMediaWriteProtected => "media is write protected",
        CommandVolumeInUse => "volume in use",
        CommandCantQuickFormat => "quick format not possible",
        CommandBadLabel => "invalid volume label",
        CommandClusterSizeTooSmall => "cluster size too small",
        CommandClusterSizeTooBig => "cluster size too big",
        CommandVolumeTooSmall => "volume too small for this file system",
        CommandVolumeTooBig => "volume too big for this file system",
        CommandNoMediaInDrive => "no media in drive",
        _ => null,
    };

    private sealed class FormatState(IProgress<double>? progress, ILogger logger)
    {
        private readonly StringBuilder _output = new();

        public IProgress<double>? Progress { get; } = progress;

        public ILogger Logger { get; } = logger;

        public bool Succeeded { get; set; }

        public string? FailureReason { get; set; }

        public string Output => _output.ToString();

        public void AppendOutput(string? text)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                _output.AppendLine(text.Trim());
            }
        }
    }
}
