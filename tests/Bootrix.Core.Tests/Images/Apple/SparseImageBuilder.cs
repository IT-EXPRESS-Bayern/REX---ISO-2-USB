// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace Bootrix.Core.Tests.Images.Apple;

/// <summary>Writes .sparseimage files and .sparsebundle directories the way hdiutil lays them out.</summary>
internal static class SparseImageBuilder
{
    /// <summary>
    /// Builds a .sparseimage. Bands that are entirely zero are left out; <paramref name="physicalOrder"/> may list
    /// the logical band numbers (0-based) in the order they are stored in the file.
    /// </summary>
    public static byte[] BuildImage(byte[] volume, int sectorsPerBand, int[]? physicalOrder = null)
    {
        var bandBytes = sectorsPerBand * 512;
        var present = PresentBands(volume, bandBytes);
        var order = physicalOrder ?? present.ToArray();

        var file = new byte[4096 + (order.Length * bandBytes)];
        "sprs"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(8), (uint)sectorsPerBand);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(16), (uint)(volume.Length / 512));

        for (var slot = 0; slot < order.Length; slot++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(64 + (slot * 4)), (uint)order[slot] + 1);
            var source = volume.AsSpan(order[slot] * bandBytes, Math.Min(bandBytes, volume.Length - (order[slot] * bandBytes)));
            source.CopyTo(file.AsSpan(4096 + (slot * bandBytes)));
        }

        return file;
    }

    /// <summary>Builds a .sparsebundle directory with an Info.plist and one file per non-empty band.</summary>
    public static void BuildBundle(string directory, byte[] volume, int bandBytes, bool integerValues = true)
    {
        Directory.CreateDirectory(Path.Combine(directory, "bands"));
        var size = integerValues ? $"<integer>{volume.Length}</integer>" : $"<string>{volume.Length}</string>";
        var band = integerValues ? $"<integer>{bandBytes}</integer>" : $"<string>{bandBytes}</string>";
        File.WriteAllText(Path.Combine(directory, "Info.plist"), $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>CFBundleInfoDictionaryVersion</key>
                <string>6.0</string>
                <key>band-size</key>
                {band}
                <key>bundle-backingstore-version</key>
                <integer>1</integer>
                <key>diskimage-bundle-type</key>
                <string>com.apple.diskimage.sparsebundle</string>
                <key>size</key>
                {size}
            </dict>
            </plist>
            """);
        File.WriteAllBytes(Path.Combine(directory, "token"), []);

        foreach (var index in PresentBands(volume, bandBytes))
        {
            var length = Math.Min(bandBytes, volume.Length - (index * bandBytes));
            File.WriteAllBytes(Path.Combine(directory, "bands", index.ToString("x", System.Globalization.CultureInfo.InvariantCulture)),
                volume.AsSpan(index * bandBytes, length).ToArray());
        }
    }

    private static List<int> PresentBands(byte[] volume, int bandBytes)
    {
        var present = new List<int>();
        for (var band = 0; band * bandBytes < volume.Length; band++)
        {
            var span = volume.AsSpan(band * bandBytes, Math.Min(bandBytes, volume.Length - (band * bandBytes)));
            if (span.ContainsAnyExcept((byte)0))
            {
                present.Add(band);
            }
        }

        return present;
    }

    /// <summary>A volume of random and empty bands: band 0 data, band 1 zeros, band 2 data, band 3 zeros, and so on.</summary>
    public static byte[] SparseVolume(int bands, int bandBytes, int lastBandBytes, ulong seed)
    {
        var volume = new byte[((bands - 1) * bandBytes) + lastBandBytes];
        for (var band = 0; band < bands; band++)
        {
            if (band % 2 == 1)
            {
                continue;
            }

            var length = Math.Min(bandBytes, volume.Length - (band * bandBytes));
            ImageTestData.Random(length, seed + (ulong)band).CopyTo(volume, band * bandBytes);
        }

        return volume;
    }
}
