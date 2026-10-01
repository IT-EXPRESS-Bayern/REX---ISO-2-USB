// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images.Wim;

namespace Bootrix.Core.Writing.Windows.Customization;

/// <param name="Description">"boot.wim" or "install.wim", for log lines and error texts.</param>
public sealed record OfflineInjectionTarget(string ImagePath, IReadOnlyList<int> Indexes, string Description);

/// <param name="Skipped">What cannot be serviced and why, for the log.</param>
public sealed record OfflineInjectionPlan(IReadOnlyList<OfflineInjectionTarget> Targets, IReadOnlyList<string> Skipped)
{
    /// <summary>
    /// Decides which images of the medium get the drivers: the Setup image, and the install image for the editions
    /// that will be installed (the one the job names, otherwise all of them). A split or ESD install image cannot be
    /// mounted for servicing; the drivers still reach the installed system through $WinPEDriver$.
    /// </summary>
    public static OfflineInjectionPlan Create(string mediaRoot, string? edition)
    {
        ArgumentException.ThrowIfNullOrEmpty(mediaRoot);
        var sources = Path.Combine(mediaRoot, "sources");
        var targets = new List<OfflineInjectionTarget>();
        var skipped = new List<string>();

        var boot = Path.Combine(sources, "boot.wim");
        if (!File.Exists(boot))
        {
            skipped.Add("boot.wim is missing");
        }
        else if (BootImageIndex.FindSetup(boot) is { } setup)
        {
            targets.Add(new OfflineInjectionTarget(boot, [setup], "boot.wim"));
        }
        else
        {
            skipped.Add("boot.wim has no Windows Setup image");
        }

        AddInstallImage(sources, edition, targets, skipped);
        return new OfflineInjectionPlan(targets, skipped);
    }

    private static void AddInstallImage(string sources, string? edition, List<OfflineInjectionTarget> targets, List<string> skipped)
    {
        var install = Path.Combine(sources, "install.wim");
        if (!File.Exists(install))
        {
            skipped.Add(File.Exists(Path.Combine(sources, "install.swm")) || File.Exists(Path.Combine(sources, "install.esd"))
                ? "the install image is split or an ESD and cannot be mounted for servicing"
                : "install.wim is missing");
            return;
        }

        IReadOnlyList<WimEdition> editions;
        using (var stream = new FileStream(install, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            editions = WimMetadata.Read(stream).Editions;
        }

        if (editions.Count == 0)
        {
            skipped.Add("install.wim lists no images");
            return;
        }

        if (!string.IsNullOrWhiteSpace(edition))
        {
            if (EditionMatcher.Find(editions, edition) is { } match)
            {
                targets.Add(new OfflineInjectionTarget(install, [match.Index], "install.wim"));
            }
            else
            {
                skipped.Add($"install.wim has no edition \"{edition.Trim()}\"");
            }

            return;
        }

        targets.Add(new OfflineInjectionTarget(install, [.. editions.Select(e => e.Index).Order()], "install.wim"));
    }
}
