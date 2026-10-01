// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Catalog.Microsoft;

/// <summary>A download page of microsoft.com and the architectures its editions are built for.</summary>
/// <param name="Path">Last path segment below <c>/software-download/</c>.</param>
internal sealed record MicrosoftPage(string Path, IReadOnlyList<string> Architectures)
{
    /// <summary>The address the scripts work against; the API answers in English for this locale.</summary>
    public Uri PageUrl => new($"https://www.microsoft.com/en-us/software-download/{Path}");

    /// <summary>The address to hand to people; Microsoft redirects it to their own language.</summary>
    public Uri ManualUrl => new($"https://www.microsoft.com/software-download/{Path}");
}

internal sealed record MicrosoftProduct(string Id, string Name, string Description, DateOnly? EndOfSupport, IReadOnlyList<MicrosoftPage> Pages);

/// <summary>
/// What the ISO pages offer. Windows 11 keeps Arm64 on a page of its own; Windows 10 offers 32- and 64-bit in
/// one download. The editions and languages behind each page are read from the page and the API, not listed here.
/// </summary>
internal static class MicrosoftProducts
{
    public static readonly MicrosoftProduct Windows11 = new(
        "windows11",
        "Windows 11",
        "Multi-edition ISO straight from microsoft.com, for 64-bit PCs and Arm64 devices.",
        null,
        [new("windows11", ["x64"]), new("windows11arm64", ["arm64"])]);

    public static readonly MicrosoftProduct Windows10 = new(
        "windows10",
        "Windows 10",
        "Version 22H2, the last release of Windows 10. Regular support ended on 14 October 2025; only the Extended Security Updates programme still delivers security fixes.",
        WindowsReleases.Windows10EndOfSupport,
        [new("windows10ISO", ["x64", "x86"])]);

    public static readonly IReadOnlyList<MicrosoftProduct> All = [Windows11, Windows10];

    public static MicrosoftProduct? Find(string id) => All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public static MicrosoftPage? FindPage(string path) =>
        All.SelectMany(p => p.Pages).FirstOrDefault(p => p.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
}
