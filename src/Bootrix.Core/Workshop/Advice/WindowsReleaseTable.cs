// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Workshop.Advice;

public enum WindowsProduct
{
    Windows10,
    Windows11,
}

/// <param name="Version">Feature update name such as 25H2.</param>
/// <param name="GeneralAvailability">Day the update became generally available.</param>
/// <param name="EndOfServicing">Last day of servicing for Home and Pro (Enterprise and Education get more).</param>
/// <param name="NewDevicesOnly">Scoped to devices that shipped with it; not offered as an update to existing PCs.</param>
public sealed record WindowsRelease(WindowsProduct Product, string Version, DateOnly GeneralAvailability, DateOnly EndOfServicing, bool NewDevicesOnly = false)
{
    public string ProductName => Product == WindowsProduct.Windows11 ? "Windows 11" : "Windows 10";

    /// <summary>Product segment of a catalog query, e.g. "windows11".</summary>
    public string CatalogName => Product == WindowsProduct.Windows11 ? "windows11" : "windows10";
}

/// <summary>
/// Release and lifecycle dates. Microsoft changes them, so this is data to refresh with the signed catalog; the values here are the
/// state of 2026-10-01 from learn.microsoft.com (windows11-release-information and lifecycle/products/windows-11-home-and-pro).
/// </summary>
public static class WindowsReleaseTable
{
    /// <summary>A new release is not recommended before it has been available this long; the first weeks bring the most Setup and driver problems.</summary>
    public const int SettlingDays = 30;

    public static IReadOnlyList<WindowsRelease> Releases { get; } =
    [
        new(WindowsProduct.Windows10, "22H2", new DateOnly(2022, 10, 18), new DateOnly(2025, 10, 14)),
        new(WindowsProduct.Windows11, "22H2", new DateOnly(2022, 9, 20), new DateOnly(2024, 10, 8)),
        new(WindowsProduct.Windows11, "23H2", new DateOnly(2023, 10, 31), new DateOnly(2025, 11, 11)),
        new(WindowsProduct.Windows11, "24H2", new DateOnly(2024, 10, 1), new DateOnly(2026, 10, 13)),
        new(WindowsProduct.Windows11, "25H2", new DateOnly(2025, 9, 30), new DateOnly(2027, 10, 12)),
        new(WindowsProduct.Windows11, "26H1", new DateOnly(2026, 2, 10), new DateOnly(2028, 3, 15), NewDevicesOnly: true),
        new(WindowsProduct.Windows11, "26H2", new DateOnly(2026, 9, 29), new DateOnly(2028, 10, 10)),
    ];

    public static WindowsRelease? Find(WindowsProduct product, string? version) =>
        Releases.FirstOrDefault(r => r.Product == product && string.Equals(r.Version, version, StringComparison.OrdinalIgnoreCase));

    /// <summary>The release to install on an existing PC: the newest one that has settled and is still serviced.</summary>
    public static WindowsRelease? Recommended(WindowsProduct product, DateOnly today)
    {
        var general = Releases
            .Where(r => r.Product == product && !r.NewDevicesOnly)
            .OrderByDescending(r => r.GeneralAvailability)
            .ToList();

        // Windows 10 has a single release that is no longer serviced; it is still the answer for hardware that cannot run Windows 11.
        return general.FirstOrDefault(r => r.GeneralAvailability.AddDays(SettlingDays) <= today && r.EndOfServicing >= today)
            ?? general.FirstOrDefault(r => r.GeneralAvailability <= today)
            ?? general.FirstOrDefault();
    }

    /// <summary>The release Microsoft scoped to new devices, or null when there is none.</summary>
    public static WindowsRelease? ForNewDevices(WindowsProduct product, DateOnly today) =>
        Releases
            .Where(r => r.Product == product && r.NewDevicesOnly && r.GeneralAvailability <= today)
            .OrderByDescending(r => r.GeneralAvailability)
            .FirstOrDefault();
}
