// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Tests.Net;

/// <summary>
/// Files under <c>Net/Fixtures</c> are real downloads from distribution servers (checksum lists, detached
/// signatures, public keys, metalinks). They are marked <c>-text</c> in .gitattributes so that signatures stay valid.
/// </summary>
internal static class NetFixtures
{
    public static string PathOf(string relative) =>
        Path.Combine(AppContext.BaseDirectory, "Net", "Fixtures", relative);

    public static byte[] Bytes(string relative) => File.ReadAllBytes(PathOf(relative));

    public static string Text(string relative) => File.ReadAllText(PathOf(relative));
}
