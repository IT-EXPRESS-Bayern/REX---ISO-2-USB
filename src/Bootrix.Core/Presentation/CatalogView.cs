// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Catalog;
using Bootrix.Core.Localization;
using Bootrix.Core.Text;

namespace Bootrix.Core.Presentation;

/// <summary>One catalog variant as a list entry: a title and a line of details such as language, architecture and size.</summary>
public sealed record VariantDescription(string Title, string Details, bool IsManual, bool SupportEnded);

public static class CatalogView
{
    public static string FamilyName(CatalogFamily family, Localizer localizer) => localizer.Get("Cat.Family." + family);

    public static VariantDescription Describe(CatalogVariant variant, Localizer localizer, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(variant);
        ArgumentNullException.ThrowIfNull(localizer);

        var today = DateOnly.FromDateTime((time ?? TimeProvider.System).GetUtcNow().UtcDateTime);
        var details = new List<string>();

        if (!string.IsNullOrWhiteSpace(variant.Language))
        {
            details.Add(variant.Language);
        }

        if (variant.Architectures.Count > 0)
        {
            details.Add(string.Join(", ", variant.Architectures));
        }

        if (variant.SizeBytes is { } size)
        {
            details.Add(ByteSize.Format(size, localizer.Culture));
        }

        var ended = false;
        if (variant.EndOfSupport is { } end)
        {
            ended = end < today;
            details.Add(localizer.Get(ended ? "Cat.SupportEnded" : "Cat.SupportEnds", end.ToString("d", localizer.Culture)));
        }

        if (variant.IsRecommended)
        {
            details.Add(localizer.Get("Cat.Recommended"));
        }

        if (variant.ManualUrl is not null)
        {
            details.Add(localizer.Get("Cat.Manual"));
        }

        return new VariantDescription(variant.Name, string.Join("  ·  ", details), variant.ManualUrl is not null, ended);
    }

    /// <summary>The file name a download gets: the last part of the address, or a neutral name when it has none.</summary>
    public static string FileNameFor(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        var name = Path.GetFileName(Uri.UnescapeDataString(url.AbsolutePath));
        var invalid = Path.GetInvalidFileNameChars();
        return string.IsNullOrWhiteSpace(name) || name.IndexOfAny(invalid) >= 0 ? "download.bin" : name;
    }
}
