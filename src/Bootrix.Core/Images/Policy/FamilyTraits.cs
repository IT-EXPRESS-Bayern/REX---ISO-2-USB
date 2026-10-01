// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Images.Policy;

/// <summary>
/// What the extract-mode writer needs to know about a distribution family, taken from the policy data:
/// which persistence mechanism its live system understands and whether its boot configuration refers to
/// the volume label. A family the policy does not know has none of these.
/// </summary>
/// <param name="Casper">Ubuntu's casper: persistence is the kernel parameter "persistent" and a partition labelled "writable".</param>
/// <param name="LiveBoot">Debian's live-boot: the parameter "persistence" and a "persistence.conf" in the root of the partition.</param>
/// <param name="LabelPatch">The configuration names the volume label, so it has to follow when the label changes.</param>
/// <param name="ExtractPersistence">The family's persistence is supported when the files are extracted.</param>
public sealed record FamilyTraits(bool Casper, bool LiveBoot, bool LabelPatch, bool ExtractPersistence)
{
    public static FamilyTraits None { get; } = new(false, false, false, false);
}
