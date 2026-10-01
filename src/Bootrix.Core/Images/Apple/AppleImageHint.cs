// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Images.Apple;

/// <summary>A note for the user about an Apple image; the text is looked up with <see cref="AppleImageHintExtensions.ResourceKey"/>.</summary>
public enum AppleImageHint
{
    MacOnly,
    BootableMacVolume,
    DataVolumeOnly,
    BareVolume,
    HybridDisc,
    IsoHybridWithApm,
    ClassicHfs,
    ApfsContainer,
    T2Restriction,
    AppleSiliconUnsupported,
    PartitionExceedsImage,
}

public static class AppleImageHintExtensions
{
    /// <summary>The key of the localized text in the string resources.</summary>
    public static string ResourceKey(this AppleImageHint hint) => "Apple.Hint." + hint;
}
