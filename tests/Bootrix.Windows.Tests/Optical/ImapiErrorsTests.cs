// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Bootrix.Core.Errors;
using Bootrix.Windows.Optical;
using Windows.Win32.Foundation;

namespace Bootrix.Windows.Tests.Optical;

public class ImapiErrorsTests
{
    private static COMException Com(HRESULT code) => new("imapi", code.Value);

    private static BootrixException Translated(HRESULT code, ImapiErrorContext? context = null) =>
        Assert.IsType<BootrixException>(ImapiErrors.Translate(Com(code), context));

    [Fact]
    public void CancellationBecomesOperationCanceled()
    {
        using var cts = new CancellationTokenSource();

        var result = ImapiErrors.Translate(Com(HRESULT.E_IMAPI_REQUEST_CANCELLED), new ImapiErrorContext { Cancellation = cts.Token });

        var cancelled = Assert.IsType<OperationCanceledException>(result);
        Assert.Equal(cts.Token, cancelled.CancellationToken);
    }

    [Fact]
    public void NonBlankDiscIsReportedAsSuch()
    {
        Assert.Equal(ErrorCode.MediaNotBlank, Translated(HRESULT.E_IMAPI_DF2DATA_MEDIA_NOT_BLANK).Code);
    }

    [Fact]
    public void LockedRecorderNamesTheProgramThatHoldsIt()
    {
        var ex = Translated(HRESULT.E_IMAPI_RECORDER_LOCKED, new ImapiErrorContext { LockOwner = "Explorer" });

        Assert.Equal(ErrorCode.DeviceBusy, ex.Code);
        Assert.Equal("Explorer", ex.Arguments[0]);
    }

    [Fact]
    public void LockedRecorderWithoutKnownOwnerStillSaysSo()
    {
        Assert.Equal("?", Translated(HRESULT.E_IMAPI_RECORDER_LOCKED).Arguments[0]);
    }

    [Theory]
    [InlineData(0xC0AA0404u)]
    [InlineData(0xC0AAB120u)]
    [InlineData(0xC0AAB121u)]
    public void ImagesThatDoNotFitGiveBothSizes(uint code)
    {
        var context = new ImapiErrorContext { NeededBytes = 5L << 30, AvailableBytes = 4_700_000_000 };

        var ex = Assert.IsType<BootrixException>(ImapiErrors.Translate(new COMException("size", unchecked((int)code)), context));

        Assert.Equal(ErrorCode.DeviceTooSmall, ex.Code);
        Assert.Equal("5 GB", ex.Arguments[0]);
        Assert.Equal("4.4 GB", ex.Arguments[1]);
    }

    [Theory]
    [InlineData(0xC0AA0202u)]
    [InlineData(0xC0AA0203u)]
    [InlineData(0xC0AA0406u)]
    [InlineData(0xC0AA0407u)]
    [InlineData(0xC0AA0909u)]
    [InlineData(0x80AA0904u)]
    public void UnusableMediaIsMediaNotSupported(uint code)
    {
        var ex = Assert.IsType<BootrixException>(ImapiErrors.Translate(new COMException("media", unchecked((int)code))));

        Assert.Equal(ErrorCode.MediaNotSupported, ex.Code);
    }

    [Fact]
    public void WriteProtectedDiscIsReportedForItsDrive()
    {
        var ex = Translated(HRESULT.E_IMAPI_RECORDER_MEDIA_WRITE_PROTECTED, new ImapiErrorContext { DriveName = "E: Test" });

        Assert.Equal(ErrorCode.DeviceWriteProtected, ex.Code);
    }

    [Fact]
    public void DriveThatIsStillSpinningUpIsBusy()
    {
        Assert.Equal(ErrorCode.DeviceBusy, Translated(HRESULT.E_IMAPI_RECORDER_MEDIA_BECOMING_READY).Code);
    }

    [Fact]
    public void FailedDriveVerificationIsAVerifyError()
    {
        Assert.Equal(ErrorCode.VerifyMismatch, Translated(HRESULT.E_IMAPI_BURN_VERIFICATION_FAILED).Code);
    }

    [Fact]
    public void FileBeyondFourGigabytesNamesTheFile()
    {
        var ex = Translated(HRESULT.IMAPI_E_DATA_TOO_BIG, new ImapiErrorContext { LargestFile = "install.wim" });

        Assert.Equal(ErrorCode.DiscFileTooLarge, ex.Code);
        Assert.Equal("install.wim", ex.Arguments[0]);
    }

    [Fact]
    public void BadVolumeLabelIsAnInvalidSpec()
    {
        var ex = Translated(HRESULT.IMAPI_E_INVALID_VOLUME_NAME, new ImapiErrorContext { VolumeLabel = "a:b" });

        Assert.Equal(ErrorCode.InvalidSpec, ex.Code);
        Assert.Contains("a:b", (string?)ex.Arguments[0]);
    }

    [Fact]
    public void MissingImapiRegistrationMeansTheFeatureIsUnavailable()
    {
        var ex = Assert.IsType<BootrixException>(ImapiErrors.Translate(new COMException("class not registered", unchecked((int)0x80040154))));

        Assert.Equal(ErrorCode.OpticalUnavailable, ex.Code);
    }

    [Fact]
    public void AccessDeniedPointsToPolicyOrElevation()
    {
        var ex = Assert.IsType<BootrixException>(ImapiErrors.Translate(new UnauthorizedAccessException("denied") { HResult = unchecked((int)0x80070005) }));

        Assert.Equal(ErrorCode.OpticalUnavailable, ex.Code);
    }

    [Theory]
    [InlineData(0xC0AA0300u, "buffer underrun")]
    [InlineData(0xC0AA0403u, "2048")]
    [InlineData(0xC0AA020Du, "did not answer")]
    [InlineData(0xC0AAB131u, "deeper than ISO 9660")]
    [InlineData(0x80AA0906u, "longer than an hour")]
    public void KnownRecorderFailuresGetAnExplanation(uint code, string fragment)
    {
        var ex = Assert.IsType<BootrixException>(ImapiErrors.Translate(new COMException("raw text", unchecked((int)code))));

        Assert.Equal(ErrorCode.BurnFailed, ex.Code);
        Assert.Contains(fragment, (string?)ex.Arguments[0]);
    }

    [Fact]
    public void UnknownFailuresKeepTheRecorderTextAndTheCode()
    {
        var ex = Assert.IsType<BootrixException>(ImapiErrors.Translate(new COMException("Something odd", unchecked((int)0x80004005))));

        Assert.Equal(ErrorCode.BurnFailed, ex.Code);
        var text = (string?)ex.Arguments[0];
        Assert.Contains("Something odd", text);
        Assert.Contains("80004005", text);
        Assert.IsType<COMException>(ex.InnerException);
    }

    [Fact]
    public void BootrixErrorsAndCancellationsPassThroughUntouched()
    {
        var own = new BootrixException(ErrorCode.NoRecorder, "x");
        var cancelled = new OperationCanceledException();

        Assert.Same(own, ImapiErrors.Translate(own));
        Assert.Same(cancelled, ImapiErrors.Translate(cancelled));
    }
}
