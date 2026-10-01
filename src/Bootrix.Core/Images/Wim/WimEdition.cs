// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Images.Wim;

/// <summary>One image inside a WIM as described by the &lt;IMAGE&gt; element of its XML data.</summary>
public sealed record WimEdition
{
    public required int Index { get; init; }

    public string? Name { get; init; }

    public string? Description { get; init; }

    public string? DisplayName { get; init; }

    /// <summary>The EDITIONID, e.g. "Professional" or "Core"; "WindowsPE" in boot images.</summary>
    public string? EditionId { get; init; }

    /// <summary>"Client", "Server" or "WindowsPE".</summary>
    public string? InstallationType { get; init; }

    public string? ProductName { get; init; }

    /// <summary>Uncompressed size of the image's file tree.</summary>
    public long TotalBytes { get; init; }

    public long FileCount { get; init; }

    public long DirectoryCount { get; init; }

    public WindowsArch Arch { get; init; } = WindowsArch.Unknown;

    /// <summary>The raw ARCH value (PROCESSOR_ARCHITECTURE_*), kept for architectures that <see cref="WindowsArch"/> does not name.</summary>
    public int? ArchCode { get; init; }

    public int MajorVersion { get; init; }

    public int MinorVersion { get; init; }

    /// <summary>The build number from WINDOWS/VERSION/BUILD, 0 when the image carries no version data.</summary>
    public int Build { get; init; }

    /// <summary>The service pack build; for current Windows releases this is the cumulative update revision.</summary>
    public int ServicePackBuild { get; init; }

    public int ServicePackLevel { get; init; }

    public IReadOnlyList<string> Languages { get; init; } = [];

    public string? DefaultLanguage { get; init; }

    public DateTimeOffset? Created { get; init; }

    public DateTimeOffset? LastModified { get; init; }

    public bool IsBootImage =>
        string.Equals(InstallationType, "WindowsPE", StringComparison.OrdinalIgnoreCase)
        || string.Equals(EditionId, "WindowsPE", StringComparison.OrdinalIgnoreCase)
        || Name?.StartsWith("Microsoft Windows PE", StringComparison.OrdinalIgnoreCase) == true
        || Name?.StartsWith("Microsoft Windows Setup", StringComparison.OrdinalIgnoreCase) == true;
}
