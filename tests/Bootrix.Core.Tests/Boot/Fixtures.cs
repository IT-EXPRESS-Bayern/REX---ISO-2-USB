// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Tests.Boot;

/// <summary>
/// Small data files kept next to the tests: PKCS#7 signature blocks and .sbat sections taken from real Microsoft,
/// Debian, Ubuntu and AlmaLinux binaries, and signed DBX updates from microsoft/secureboot_objects
/// (BSD-2-Clause-Patent). Whole boot loaders are not stored; see <see cref="RealSamples"/> for those.
/// </summary>
internal static class Fixtures
{
    public static byte[] Read(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Boot", "Fixtures", name));
}
