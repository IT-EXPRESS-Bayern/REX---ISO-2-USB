// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Writing.Windows;

public enum CopyAction
{
    Copy,

    /// <summary>The file is an install image too large for the target; it is written as a set of .swm parts.</summary>
    SplitInstallImage,
}

/// <param name="Source">The file in the image.</param>
/// <param name="Action">What to do with it.</param>
/// <param name="Destination">Path on the target, relative with '/'. For a split this is the first part (install.swm).</param>
public sealed record CopyItem(MediaSourceFile Source, CopyAction Action, string Destination)
{
    /// <summary>Bytes this item adds to the progress total; a split counts the size of the image it cuts up.</summary>
    public long Bytes => Source.Length;
}
