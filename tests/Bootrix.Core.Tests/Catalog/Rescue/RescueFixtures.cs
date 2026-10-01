// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Bootrix.Core.Catalog.Rescue;

namespace Bootrix.Core.Tests.Catalog.Rescue;

internal static class RescueFixtures
{
    private const string ResourceName = "Bootrix.Core.Catalog.Rescue.rescue-catalog.json";

    /// <summary>A small valid catalog; the reader tests change one detail at a time and expect that detail to be named.</summary>
    public const string Minimal = """
        {
          "schemaVersion": 1,
          "version": 3,
          "issued": "2026-10-01",
          "expires": "2027-04-01",
          "entries": [
            {
              "id": "sample",
              "name": "Sample",
              "description": { "de": "Beispiel", "en": "Example" },
              "category": "partitioning",
              "license": "GPL-2.0-or-later",
              "homepage": "https://example.org/",
              "variants": [
                {
                  "id": "1.0",
                  "version": "1.0",
                  "architecture": "x64",
                  "writeMode": "iso-hybrid",
                  "size": 1000,
                  "sha256": "3a4c9877b483ab46d7c3fbe165a0db275e1ae3cfe56a5657e5a47c2f99a99d1e",
                  "hashSource": "vendor",
                  "sources": [ { "url": "https://example.org/a.iso", "priority": 1 } ]
                }
              ]
            }
          ]
        }
        """;

    public static string EmbeddedText()
    {
        using var stream = typeof(RescueCatalogDocument).Assembly.GetManifestResourceStream(ResourceName)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>The embedded catalog with another version number: what a newer publication of the same data looks like.</summary>
    public static string EmbeddedWithVersion(long version)
    {
        var text = EmbeddedText();
        const string marker = "\"version\": 1,";
        var at = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, "the embedded catalog no longer starts with version 1");

        return string.Concat(text.AsSpan(0, at), $"\"version\": {version},", text.AsSpan(at + marker.Length));
    }

    public static RescueCatalogDocument Read(string json) => RescueCatalogReader.Read(new MemoryStream(Encoding.UTF8.GetBytes(json)));
}
