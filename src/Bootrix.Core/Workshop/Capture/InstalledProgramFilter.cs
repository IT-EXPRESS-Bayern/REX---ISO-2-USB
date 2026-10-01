// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Workshop.Capture;

/// <summary>One subkey of an Uninstall registry key, as read.</summary>
public sealed record UninstallEntry(string? DisplayName, string? DisplayVersion, string? Publisher, bool SystemComponent, string? ParentKeyName, string? ReleaseType);

/// <summary>Reduces the raw Uninstall entries to what a technician would list: programs, not components and updates.</summary>
public static class InstalledProgramFilter
{
    private static readonly HashSet<string> UpdateReleaseTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Update",
        "Security Update",
        "Critical Update",
        "Hotfix",
        "Update Rollup",
        "Service Pack",
    };

    public static IReadOnlyList<InstalledProgram> Normalize(IEnumerable<UninstallEntry> entries) =>
    [
        .. entries
            .Where(IsProgram)
            .Select(e => new InstalledProgram(e.DisplayName!.Trim(), NullIfEmpty(e.DisplayVersion), NullIfEmpty(e.Publisher)))
            .DistinctBy(p => (p.Name.ToUpperInvariant(), p.Version))
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase),
    ];

    private static bool IsProgram(UninstallEntry entry) =>
        !string.IsNullOrWhiteSpace(entry.DisplayName)
        && !entry.SystemComponent
        && string.IsNullOrEmpty(entry.ParentKeyName)
        && !(entry.ReleaseType is not null && UpdateReleaseTypes.Contains(entry.ReleaseType));

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
