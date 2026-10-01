// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Tests.Catalog.Distros;

/// <summary>
/// Files under <c>Catalog/Distros/Fixtures</c> are real answers of the vendors' servers, shortened where the format
/// allows it (lists, directory pages) and byte-identical where a signature covers them (checksum files, signatures).
/// They are marked <c>-text</c> in .gitattributes so that signatures stay valid on every platform.
/// </summary>
internal static class DistroFixtures
{
    /// <summary>The day the fixtures were fetched; keys and signatures are judged as of this date.</summary>
    public static readonly DateTimeOffset CapturedOn = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    public static string PathOf(string relative) =>
        Path.Combine(AppContext.BaseDirectory, "Catalog", "Distros", "Fixtures", relative);

    public static byte[] Bytes(string relative) => File.ReadAllBytes(PathOf(relative));

    public static string Text(string relative) => File.ReadAllText(PathOf(relative));
}
