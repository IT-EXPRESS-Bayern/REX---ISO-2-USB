// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Runtime.InteropServices;
using Bootrix.Core.Errors;
using Bootrix.Core.Optical;
using Windows.Win32.Foundation;

namespace Bootrix.Windows.Optical;

/// <summary>Facts about a failed call that the HRESULT alone does not contain but the message needs.</summary>
internal sealed record ImapiErrorContext
{
    public string? DriveName { get; init; }

    /// <summary>Who holds the recorder when it is locked, as IDiscRecorder2.ExclusiveAccessOwner reports it.</summary>
    public string? LockOwner { get; init; }

    public long? NeededBytes { get; init; }

    public long? AvailableBytes { get; init; }

    public string? VolumeLabel { get; init; }

    public string? LargestFile { get; init; }

    public CancellationToken Cancellation { get; init; }
}

/// <summary>Turns IMAPI failures into the errors Bootrix shows. Anything not listed becomes a general burn failure with the HRESULT in the text.</summary>
internal static class ImapiErrors
{
    private const int RegDbClassNotRegistered = unchecked((int)0x80040154);
    private const int AccessDenied = unchecked((int)0x80070005);

    private static readonly int[] MediaUnusable =
    [
        HRESULT.E_IMAPI_RECORDER_MEDIA_NO_MEDIA.Value,
        HRESULT.E_IMAPI_RECORDER_MEDIA_INCOMPATIBLE.Value,
        HRESULT.E_IMAPI_DF2DATA_MEDIA_IS_NOT_SUPPORTED.Value,
        HRESULT.E_IMAPI_DF2DATA_RECORDER_NOT_SUPPORTED.Value,
        HRESULT.E_IMAPI_ERASE_MEDIA_IS_NOT_SUPPORTED.Value,
        HRESULT.E_IMAPI_ERASE_MEDIA_IS_NOT_ERASABLE.Value,
        HRESULT.E_IMAPI_RECORDER_MEDIA_NOT_FORMATTED.Value,
    ];

    private static readonly int[] DiscTooSmall =
    [
        HRESULT.E_IMAPI_DF2DATA_STREAM_TOO_LARGE_FOR_CURRENT_MEDIA.Value,
        HRESULT.IMAPI_E_IMAGE_SIZE_LIMIT.Value,
        HRESULT.IMAPI_E_IMAGE_TOO_BIG.Value,
    ];

    public static Exception Translate(Exception exception, ImapiErrorContext? context = null)
    {
        context ??= new ImapiErrorContext();
        if (exception is BootrixException or OperationCanceledException)
        {
            return exception;
        }

        var code = exception is COMException com ? com.HResult : exception.HResult;
        var hex = $"0x{code:X8}";

        if (code == HRESULT.E_IMAPI_REQUEST_CANCELLED.Value)
        {
            return new OperationCanceledException("IMAPI request cancelled", exception, context.Cancellation);
        }

        if (code == HRESULT.E_IMAPI_DF2DATA_MEDIA_NOT_BLANK.Value)
        {
            return new BootrixException(ErrorCode.MediaNotBlank, hex, exception);
        }

        if (code == HRESULT.E_IMAPI_RECORDER_MEDIA_WRITE_PROTECTED.Value)
        {
            return new BootrixException(ErrorCode.DeviceWriteProtected, context.DriveName ?? hex, exception);
        }

        if (code == HRESULT.E_IMAPI_RECORDER_LOCKED.Value)
        {
            return new BootrixException(ErrorCode.DeviceBusy, hex, exception) { Arguments = [context.LockOwner ?? "?"] };
        }

        if (code == HRESULT.E_IMAPI_RECORDER_MEDIA_BECOMING_READY.Value)
        {
            return new BootrixException(ErrorCode.DeviceBusy, hex, exception) { Arguments = ["the drive is still getting ready"] };
        }

        if (code == HRESULT.E_IMAPI_BURN_VERIFICATION_FAILED.Value)
        {
            return new BootrixException(ErrorCode.VerifyMismatch, hex, exception) { Arguments = ["?"] };
        }

        if (code == HRESULT.IMAPI_E_DATA_TOO_BIG.Value)
        {
            return new BootrixException(ErrorCode.DiscFileTooLarge, hex, exception) { Arguments = [context.LargestFile ?? "?"] };
        }

        if (code == HRESULT.IMAPI_E_INVALID_VOLUME_NAME.Value)
        {
            return new BootrixException(ErrorCode.InvalidSpec, hex, exception) { Arguments = [$"invalid volume label '{context.VolumeLabel}'"] };
        }

        if (code is RegDbClassNotRegistered)
        {
            return new BootrixException(ErrorCode.OpticalUnavailable, hex, exception) { Arguments = ["imapi2.dll is not registered (the Windows media features may have been removed)"] };
        }

        if (code is AccessDenied)
        {
            return new BootrixException(ErrorCode.OpticalUnavailable, hex, exception) { Arguments = ["access denied; burning may be blocked by group policy or the program is not elevated"] };
        }

        if (Array.IndexOf(DiscTooSmall, code) >= 0)
        {
            return new BootrixException(ErrorCode.DeviceTooSmall, hex, exception)
            {
                Arguments = [Size(context.NeededBytes), Size(context.AvailableBytes)],
            };
        }

        if (Array.IndexOf(MediaUnusable, code) >= 0)
        {
            return new BootrixException(ErrorCode.MediaNotSupported, hex, exception);
        }

        return new BootrixException(ErrorCode.BurnFailed, hex, exception) { Arguments = [Describe(code, exception)] };
    }

    /// <summary>The recorder text for the user: what IMAPI says, or something short for the codes where its text is just the code.</summary>
    private static string Describe(int code, Exception exception)
    {
        if (code == HRESULT.E_IMAPI_LOSS_OF_STREAMING.Value)
        {
            return "buffer underrun, the data did not arrive fast enough (try a lower speed or a local source)";
        }

        if (code == HRESULT.E_IMAPI_DF2DATA_STREAM_NOT_SUPPORTED.Value)
        {
            return "the image is not a whole number of 2048-byte sectors";
        }

        if (code == HRESULT.E_IMAPI_RECORDER_COMMAND_TIMEOUT.Value)
        {
            return "the drive did not answer in time";
        }

        if (code == HRESULT.IMAPI_E_ISO9660_LEVELS.Value)
        {
            return "the folder tree is deeper than ISO 9660 allows";
        }

        if (code == HRESULT.E_IMAPI_ERASE_TOOK_LONGER_THAN_ONE_HOUR.Value)
        {
            return "the erase took longer than an hour, the disc is probably worn out";
        }

        return string.IsNullOrWhiteSpace(exception.Message)
            ? string.Create(CultureInfo.InvariantCulture, $"IMAPI error 0x{code:X8}")
            : $"{exception.Message.Trim()} (0x{code:X8})";
    }

    private static string Size(long? bytes) => bytes is { } value ? SectorMath.FormatBytes(value) : "?";
}
