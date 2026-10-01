// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Boot.Dos;

public enum DosFlavor
{
    FreeDos,

    /// <summary>The MS-DOS 8.0 boot disk of Windows ME. Its files are not part of Bootrix and are fetched from Microsoft on request.</summary>
    MsDos,
}

public enum DosKernel
{
    /// <summary>The 386 kernel on sticks and hard disks, the 8086 kernel on diskettes, whose drives sit in PCs of any age.</summary>
    Auto,

    I386,

    I8086,
}

public sealed record DosOptions
{
    public DosFlavor Flavor { get; init; } = DosFlavor.FreeDos;

    /// <summary>FreeDOS only.</summary>
    public DosKernel Kernel { get; init; } = DosKernel.Auto;

    /// <summary>MS-DOS only: the user has accepted that Bootrix downloads the files from Microsoft, under Microsoft's terms.</summary>
    public bool AcceptMicrosoftDownload { get; init; }
}
