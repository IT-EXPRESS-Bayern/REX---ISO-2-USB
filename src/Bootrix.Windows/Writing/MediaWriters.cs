// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Windows.Jobs;
using Bootrix.Windows.Writing.Dos;

namespace Bootrix.Windows.Writing;

/// <summary>
/// The writers Bootrix ships with, in the order they are asked. One list for the dependency container
/// and for the elevated broker, which builds its engine by hand.
/// </summary>
public static class MediaWriters
{
    public static IReadOnlyList<IMediaWriter> CreateDefault(WriteServices services, IImageStreamProvider images, RawWriteJob rawWrite) =>
    [
        new DosWriter(services),
        new FormatOnlyWriter(services),
        new RawCopyWriter(rawWrite),
    ];
}
