// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;

namespace Bootrix.Core.Images;

/// <summary>Builds the image-related exceptions with the arguments the localized texts expect.</summary>
internal static class ImageFailures
{
    public static BootrixException Unreadable(string detail, Exception? inner = null) =>
        new(ErrorCode.ImageUnreadable, detail, inner) { Arguments = [detail] };

    public static BootrixException Unsupported(string what, Exception? inner = null) =>
        new(ErrorCode.ImageUnsupported, what, inner) { Arguments = [what] };

    public static BootrixException Truncated(long expectedBytes, long actualBytes) =>
        new(ErrorCode.ImageTruncated, $"expected {expectedBytes} bytes, found {actualBytes}")
        {
            Arguments = [$"{expectedBytes:N0} Bytes", $"{actualBytes:N0} Bytes"],
        };

    public static BootrixException Encrypted(string detail) =>
        new(ErrorCode.ImageEncrypted, detail);
}
