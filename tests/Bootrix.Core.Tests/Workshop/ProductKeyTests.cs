// SPDX-License-Identifier: GPL-3.0-or-later
using System.Numerics;
using Bootrix.Core.Workshop.Licensing;

namespace Bootrix.Core.Tests.Workshop;

public class ProductKeyTests
{
    private const string Alphabet = "BCDFGHJKMPQRTVWXY2346789";

    [Theory]
    [InlineData("BCDFG-HJKMP-QRTVW-XY234-6789N", "XXXXX-XXXXX-XXXXX-XXXXX-6789N")]
    [InlineData("BCDFGHJKMPQRTVWXY2346789N", "XXXXXXXXXXXXXXXXXXXX6789N")]
    [InlineData("ABCDEF", "XBCDEF")]
    [InlineData("SHORT", "XXXXX")]
    public void Mask_KeepsOnlyTheLastFiveCharacters(string key, string expected)
    {
        Assert.Equal(expected, ProductKeys.Mask(key));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Mask_NullOrEmpty_IsNull(string? key)
    {
        Assert.Null(ProductKeys.Mask(key));
    }

    [Fact]
    public void Mask_LeavesNoCharacterOfTheHiddenGroups()
    {
        // No X in the hidden part, because X is what replaces the characters.
        const string key = "BCDFG-HJKMP-QRTVW-Y2346-6789N";

        var masked = ProductKeys.Mask(key)!;

        foreach (var hidden in "BCDFGHJKMPQRTVWY2346")
        {
            Assert.DoesNotContain(hidden, masked[..^5]);
        }
    }

    [Theory]
    [InlineData("BCDFG-HJKMP-QRTVW-XY234-6789N", true)]
    [InlineData("BCDFG-HJKMP-QRTVW-XY234-6789", false)]
    [InlineData("BCDFGxHJKMPxQRTVWxXY234x6789N", false)]
    [InlineData("BCDFG-HJKMP-QRTVW-XY234-6789A", false)]
    [InlineData("BCDFG-HJKMP-QRTVW-XY234-67890", false)]
    [InlineData("bcdfg-hjkmp-qrtvw-xy234-6789n", false)]
    [InlineData(null, false)]
    public void IsValidFormat_AcceptsOnlyFiveGroupsFromTheKeyAlphabet(string? key, bool expected)
    {
        Assert.Equal(expected, ProductKeys.IsValidFormat(key));
    }

    // The N of the new format can stand anywhere but at the very end: its position is a base-24 digit.
    [Theory]
    [InlineData("NBCDF-GHJKM-PQRTV-WXY23-46789")]
    [InlineData("BCDFG-HJKMP-QRTVN-WXY23-46789")]
    [InlineData("W269N-WFGWX-YVC9B-4J6C9-T83GX")]
    [InlineData("NPPR9-FWDCX-D2C8J-H872K-2YT43")]
    [InlineData("VK7JG-NPHTM-C97JM-9MPGT-3V66T")]
    [InlineData("YYYYY-YYYYY-YYYYY-YYYYY-YYYNY")]
    [InlineData("BBBBB-BBBBB-BBBBB-BBBBB-BBNBB")]
    public void DigitalProductId_RoundTripsKeysOfTheNewFormat(string key)
    {
        var blob = EncodeWindows8(key);

        Assert.Equal(key, DigitalProductIdDecoder.Decode(blob));
    }

    [Theory]
    [InlineData("BCDFG-HJKMP-QRTVW-XY234-67892")]
    [InlineData("2222B-CDFGH-JKMPQ-RTVWX-Y2346")]
    public void DigitalProductId_RoundTripsKeysOfTheOldFormat(string key)
    {
        var blob = EncodeNumber(key.Replace("-", "", StringComparison.Ordinal).Select(c => Alphabet.IndexOf(c, StringComparison.Ordinal)).ToArray(), windows8: false);

        Assert.Equal(key, DigitalProductIdDecoder.Decode(blob));
    }

    [Fact]
    public void DigitalProductId_FlagBitDoesNotDisturbTheKey()
    {
        // The most significant key byte can be up to 6 for a 25-digit number in base 24, so bit 3 is free for the flag.
        var withFlag = EncodeWindows8("YYYYY-YYYYY-YYYYY-YYYYY-YYYNY");

        Assert.NotEqual(0, withFlag[66] & 0x08);
        Assert.Equal("YYYYY-YYYYY-YYYYY-YYYYY-YYYNY", DigitalProductIdDecoder.Decode(withFlag));
    }

    [Fact]
    public void DigitalProductId_DoesNotModifyTheCallersBuffer()
    {
        var blob = EncodeWindows8("BCDFG-HJKMP-QRTVN-WXY23-46789");
        var copy = blob.ToArray();

        _ = DigitalProductIdDecoder.Decode(blob);

        Assert.Equal(copy, blob);
    }

    [Fact]
    public void DigitalProductId_TooShort_ReturnsNull()
    {
        Assert.Null(DigitalProductIdDecoder.Decode(new byte[66]));
    }

    [Fact]
    public void DigitalProductId_AllZero_IsNoKey()
    {
        Assert.Null(DigitalProductIdDecoder.Decode(new byte[164]));
    }

    /// <summary>
    /// Independent of the decoder's byte-wise long division: the key is a 25-digit base-24 number, written here with BigInteger
    /// into bytes 52 to 66 in little-endian order. For the new format the 'N' is removed and its position becomes the first digit.
    /// </summary>
    private static byte[] EncodeWindows8(string key)
    {
        var compact = key.Replace("-", "", StringComparison.Ordinal);
        var position = compact.IndexOf('N', StringComparison.Ordinal);
        var digits = new List<int> { position };
        digits.AddRange(compact.Remove(position, 1).Select(c => Alphabet.IndexOf(c, StringComparison.Ordinal)));
        return EncodeNumber([.. digits], windows8: true);
    }

    private static byte[] EncodeNumber(int[] digits, bool windows8)
    {
        BigInteger value = 0;
        foreach (var digit in digits)
        {
            value = (value * 24) + digit;
        }

        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: false);
        var blob = new byte[164];
        bytes.CopyTo(blob, 52);
        if (windows8)
        {
            blob[66] |= 0x08;
        }

        return blob;
    }
}
