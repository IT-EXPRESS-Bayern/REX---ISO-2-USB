// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bootrix.Core.Engine;
using Bootrix.Core.Profiles;
using Bootrix.Core.Tiny;
using Bootrix.Core.Unattend;

namespace Bootrix.Core.Presentation;

/// <summary>What the user chose on the Tiny page.</summary>
public sealed record TinySelection
{
    public required string SourcePath { get; init; }

    public required string OutputPath { get; init; }

    public required string ProfileId { get; init; }

    /// <summary>Index of the edition inside the install image; null when the image holds only one.</summary>
    public int? EditionIndex { get; init; }

    /// <summary>For each group of the profile whether it is applied (removed or switched off); groups that are not listed keep the profile's default.</summary>
    public IReadOnlyDictionary<string, bool> Groups { get; init; } = new Dictionary<string, bool>();

    public string VolumeLabel { get; init; } = "TINY";

    public bool SkipHardwareChecks { get; init; } = true;

    /// <summary>Compress so that the install image can be split for FAT32 media.</summary>
    public bool ForFat32 { get; init; }

    public string? LocalAccountName { get; init; }

    public bool AcknowledgeNoServicing { get; init; }
}

public static class TinyRequestBuilder
{
    public static TinyBuildJobRequest Create(TinySelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        var profile = TinyProfiles.Load(selection.ProfileId);

        var keep = new List<string>();
        var include = new List<string>();
        foreach (var group in profile.Groups)
        {
            if (string.Equals(group.Id, TinyView.HardwareBypassGroup, StringComparison.OrdinalIgnoreCase))
            {
                // Follows the single switch for the hardware check.
                if (!selection.SkipHardwareChecks)
                {
                    keep.Add(group.Id);
                }

                continue;
            }

            var applied = selection.Groups.TryGetValue(group.Id, out var chosen) ? chosen : group.Default;
            if (group.Default && !applied)
            {
                keep.Add(group.Id);
            }
            else if (!group.Default && applied)
            {
                include.Add(group.Id);
            }
        }

        return new TinyBuildJobRequest
        {
            IsoPath = selection.SourcePath,
            OutputIsoPath = selection.OutputPath,
            ProfileId = profile.Id,
            Edition = selection.EditionIndex?.ToString(CultureInfo.InvariantCulture),
            KeepGroups = keep,
            IncludeGroups = include,
            VolumeLabel = string.IsNullOrWhiteSpace(selection.VolumeLabel) ? "TINY" : selection.VolumeLabel.Trim(),
            Compression = selection.ForFat32 ? InstallImageCompression.Maximum : InstallImageCompression.Recovery,
            BypassHardwareChecks = selection.SkipHardwareChecks,
            Unattend = string.IsNullOrWhiteSpace(selection.LocalAccountName)
                ? null
                : new UnattendOptions { Windows = new WindowsSetupOptions { LocalAccountName = selection.LocalAccountName.Trim() } },
            AcknowledgeNoServicing = selection.AcknowledgeNoServicing,
        };
    }
}
