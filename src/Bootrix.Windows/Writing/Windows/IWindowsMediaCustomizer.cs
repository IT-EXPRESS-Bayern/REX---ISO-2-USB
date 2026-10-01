// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images;

namespace Bootrix.Windows.Writing.Windows;

/// <summary>What a customizer gets to work on: the finished copy of the setup files on one target.</summary>
public sealed class WindowsMediaCustomization
{
    public required MediaWriteContext Write { get; init; }

    public required MediaWriteTarget Target { get; init; }

    /// <summary>Root of the main partition as a path that System.IO accepts, e.g. \\?\Volume{guid}\ .</summary>
    public required string MediaRoot { get; init; }

    public required WindowsArch Arch { get; init; }

    /// <summary>Build number of the Windows image, 0 when unknown.</summary>
    public int Build { get; init; }

    public required string WorkDirectory { get; init; }
}

/// <summary>
/// A change to Windows setup media after the files have been copied: answer file, drivers, boot image
/// tweaks, boot manager swap. Each customizer decides for itself whether the job asks for it.
/// </summary>
public interface IWindowsMediaCustomizer
{
    string Id { get; }

    bool Applies(MediaWriteContext write);

    /// <summary>Applies the change. Reports progress from 0 to 1 and honours cancellation between its steps.</summary>
    Task ApplyAsync(WindowsMediaCustomization customization, IProgress<double> progress, CancellationToken cancellationToken);
}
