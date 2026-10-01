// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;

namespace Bootrix.Cli.Output;

/// <summary>Stable exit codes so scripts can react to the kind of failure without parsing text.</summary>
public static class ExitCodes
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int Usage = 2;
    public const int DeviceBusy = 3;
    public const int DeviceProtected = 4;
    public const int VerifyFailed = 5;
    public const int DeviceNotFound = 6;
    public const int ImageProblem = 7;
    public const int NetworkProblem = 8;
    public const int Canceled = 130;

    public static int For(Exception exception) => exception switch
    {
        OperationCanceledException => Canceled,
        BootrixException bx => For(bx.Code),
        _ => Failure,
    };

    public static int For(ErrorCode code) => code switch
    {
        ErrorCode.Canceled => Canceled,
        ErrorCode.DeviceBusy => DeviceBusy,
        ErrorCode.DeviceProtected => DeviceProtected,
        ErrorCode.DeviceNotFound or ErrorCode.DeviceChanged => DeviceNotFound,
        ErrorCode.VerifyMismatch => VerifyFailed,
        ErrorCode.ImageUnreadable or ErrorCode.ImageTruncated or ErrorCode.ImageUnsupported or ErrorCode.ImageEncrypted
            or ErrorCode.ImageHashMismatch or ErrorCode.ImageTooLarge => ImageProblem,
        ErrorCode.DownloadFailed or ErrorCode.DownloadBlocked or ErrorCode.DownloadHashMismatch or ErrorCode.CatalogUnavailable => NetworkProblem,
        _ => Failure,
    };
}
