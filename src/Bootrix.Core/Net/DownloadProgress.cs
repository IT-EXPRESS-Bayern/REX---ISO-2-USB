// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Net;

public enum DownloadPhase
{
    Connecting,
    Downloading,
    Verifying,
}

/// <param name="BytesDone">Bytes on disk while downloading; bytes hashed while verifying.</param>
/// <param name="BytesTotal">Null when the server did not announce a length.</param>
/// <param name="ActiveSegments">Connections currently transferring data.</param>
public readonly record struct DownloadProgress(
    DownloadPhase Phase,
    long BytesDone,
    long? BytesTotal,
    double BytesPerSecond,
    TimeSpan? Eta,
    int ActiveSegments);
