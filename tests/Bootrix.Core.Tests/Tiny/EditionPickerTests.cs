// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Tiny;

namespace Bootrix.Core.Tests.Tiny;

public class EditionPickerTests
{
    private static readonly InstallEdition[] Retail =
    [
        new(1, "Windows 11 Home", null, 1),
        new(2, "Windows 11 Home N", null, 1),
        new(6, "Windows 11 Pro", null, 1),
        new(7, "Windows 11 Pro N", null, 1),
    ];

    [Fact]
    public void SingleEditionNeedsNoChoice()
    {
        Assert.Equal(1, EditionPicker.Pick([new(1, "Windows 10 Pro", null, 1)], null));
    }

    [Fact]
    public void ManyEditionsWithoutChoiceIsAnError()
    {
        var ex = Assert.Throws<BootrixException>(() => EditionPicker.Pick(Retail, " "));

        Assert.Equal(ErrorCode.InvalidSpec, ex.Code);
    }

    [Theory]
    [InlineData("6", 6)]
    [InlineData(" 2 ", 2)]
    [InlineData("windows 11 pro n", 7)]
    [InlineData("Home N", 2)]
    public void IndexAndNameSelectAnEdition(string wanted, int expected)
    {
        Assert.Equal(expected, EditionPicker.Pick(Retail, wanted));
    }

    [Theory]
    [InlineData("Pro")]
    [InlineData("Enterprise")]
    [InlineData("99")]
    public void AmbiguousOrUnknownIsRejected(string wanted)
    {
        Assert.Throws<BootrixException>(() => EditionPicker.Pick(Retail, wanted));
    }

    [Fact]
    public void ExactIndexWinsOverANameThatHappensToContainTheDigits()
    {
        Assert.Equal(1, EditionPicker.Pick([new(1, "x", null, 1), new(11, "Windows 11", null, 1)], "1"));
    }

    [Fact]
    public void NoEditionsMeansUnsupportedImage()
    {
        var ex = Assert.Throws<BootrixException>(() => EditionPicker.Pick([], null));

        Assert.Equal(ErrorCode.ImageUnsupported, ex.Code);
    }
}
