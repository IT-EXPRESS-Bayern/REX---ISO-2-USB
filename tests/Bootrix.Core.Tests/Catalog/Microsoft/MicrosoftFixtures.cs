// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Tests.Catalog.Microsoft;

/// <summary>
/// Files under <c>Catalog/Microsoft/Fixtures</c> were recorded from Microsoft's servers on 2026-10-01 (pages,
/// API answers, catalogs). Download addresses carry a placeholder instead of the signed query string; nothing
/// else was altered. They are marked <c>-text</c> in .gitattributes.
/// </summary>
internal static class MicrosoftFixtures
{
    public static string PathOf(string relative) =>
        Path.Combine(AppContext.BaseDirectory, "Catalog", "Microsoft", "Fixtures", relative);

    public static byte[] Bytes(string relative) => File.ReadAllBytes(PathOf(relative));

    public static string Text(string relative) => File.ReadAllText(PathOf(relative));
}
