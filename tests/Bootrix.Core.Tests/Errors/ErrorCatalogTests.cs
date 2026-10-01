// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bootrix.Core.Errors;
using Bootrix.Core.Localization;

namespace Bootrix.Core.Tests.Errors;

public class ErrorCatalogTests
{
    private static Localizer For(string culture) => new() { Culture = CultureInfo.GetCultureInfo(culture) };

    [Fact]
    public void EveryErrorCodeHasGermanAndEnglishTexts()
    {
        foreach (var code in Enum.GetValues<ErrorCode>())
        {
            foreach (var culture in new[] { "de", "en" })
            {
                var localizer = For(culture);
                Assert.True(localizer.Has($"Error.{code}.Cause"), $"{code}.Cause missing for {culture}");
                Assert.True(localizer.Has($"Error.{code}.Action"), $"{code}.Action missing for {culture}");
            }
        }
    }

    [Fact]
    public void CodesAreFormattedWithPrefix()
    {
        Assert.Equal("BX2003", ErrorCatalog.FormatCode(ErrorCode.DeviceProtected));
    }

    [Fact]
    public void ArgumentsAreSubstituted()
    {
        var ex = new BootrixException(ErrorCode.DeviceTooSmall) { Arguments = ["8 GB", "4 GB"] };

        var german = ErrorCatalog.Describe(ex, For("de"));
        var english = ErrorCatalog.Describe(ex, For("en"));

        Assert.Contains("8 GB", german.Cause);
        Assert.Contains("zu klein", german.Cause);
        Assert.Contains("too small", english.Cause);
        Assert.Equal("BX2005", english.Code);
    }

    [Fact]
    public void UnknownExceptionsMapToGenericError()
    {
        var description = ErrorCatalog.Describe(new InvalidOperationException("x"), For("en"));

        Assert.Equal("BX0000", description.Code);
    }

    [Fact]
    public void CancellationIsRecognised()
    {
        var description = ErrorCatalog.Describe(new OperationCanceledException(), For("en"));

        Assert.Equal("BX1001", description.Code);
    }

    [Fact]
    public void MissingKeyFallsBackToKey()
    {
        Assert.Equal("No.Such.Key", For("en").Get("No.Such.Key"));
    }
}
