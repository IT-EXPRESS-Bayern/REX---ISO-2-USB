// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Bootrix.Core.Errors;
using Bootrix.Core.Optical;
using Bootrix.Windows.Optical;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using Windows.Win32.System.Com;

namespace Bootrix.Windows.Tests.Optical;

/// <summary>Runs on Windows only; the checks need the real COM runtime. They need no drive, a CI runner without one passes them.</summary>
public sealed class OpticalWindowsFactAttribute : FactAttribute
{
    public OpticalWindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "needs Windows (COM)";
        }
    }
}

public class ImapiSmokeTests
{
    private static readonly Guid StreamInterface = new("0000000C-0000-0000-C000-000000000046");

    [OpticalWindowsFact]
    public void ImapiClassesAreFreeThreadedSoTheyNeedNoStaThread()
    {
        // The design runs every IMAPI call on an MTA thread; an apartment-threaded class would force a proxy and a message loop.
        foreach (var clsid in new[]
        {
            "{2735412A-7F64-5B0F-8F00-5D77AFBE261E}",
            "{2735412D-7F64-5B0F-8F00-5D77AFBE261E}",
            "{2735412E-7F64-5B0F-8F00-5D77AFBE261E}",
            "{2C941FC5-975B-59BE-A960-9A2A262853A5}",
        })
        {
            using var key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{clsid}\InprocServer32");
            if (key is null)
            {
                // IMAPI is not installed on this image (Server Core or a stripped-down Windows).
                return;
            }

            var model = key.GetValue("ThreadingModel") as string;
            Assert.True(model is "Free" or "Both" or "Neutral", $"{clsid} has ThreadingModel '{model}'");
        }
    }

    [OpticalWindowsFact]
    public async Task WorkerThreadsAreInTheMultithreadedApartment()
    {
        var state = await MtaWorker.RunAsync(() => Thread.CurrentThread.GetApartmentState(), "test worker");

        Assert.Equal(ApartmentState.MTA, state);
    }

    [OpticalWindowsFact]
    public async Task WorkerFailuresReachTheCaller()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => MtaWorker.RunAsync<int>(() => throw new InvalidOperationException("boom"), "test worker"));
    }

    [OpticalWindowsFact]
    public async Task DriveEnumerationRunsWithoutADrive()
    {
        using var service = new ImapiOpticalService(NullLogger<ImapiOpticalService>.Instance);

        try
        {
            var drives = await service.EnumerateDrivesAsync();

            // A runner has no optical drive; a developer machine may have several. Either is a valid answer.
            Assert.NotNull(drives);
            Assert.All(drives, d => Assert.False(string.IsNullOrEmpty(d.Id)));
        }
        catch (BootrixException ex) when (ex.Code == ErrorCode.OpticalUnavailable)
        {
            // IMAPI is not registered on this image.
        }
    }

    [OpticalWindowsFact]
    public void ReaderForAMissingDriveFailsCleanly()
    {
        var drive = new OpticalDrive { Id = "none", DriveLetter = "?:" };

        var ex = Assert.Throws<BootrixException>(() => WindowsSectorReader.Open(drive));

        Assert.Equal(ErrorCode.DeviceNotFound, ex.Code);
    }

    [OpticalWindowsFact]
    public unsafe void ManagedStreamAnswersTheIStreamVtableInTheOrderOfTheInterface()
    {
        // IMAPI calls IStream through its vtable: IUnknown (3 slots), then Read, Write, Seek, SetSize, CopyTo, Commit, Revert, LockRegion, UnlockRegion, Stat, Clone.
        var data = new byte[8192];
        new Random(5).NextBytes(data);
        var unknown = Marshal.GetIUnknownForObject(new ManagedStream(new MemoryStream(data)));
        try
        {
            var iid = StreamInterface;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out var stream));
            try
            {
                var vtable = *(nint**)stream;
                var read = (delegate* unmanaged[Stdcall]<nint, byte*, uint, uint*, int>)vtable[3];
                var seek = (delegate* unmanaged[Stdcall]<nint, long, uint, ulong*, int>)vtable[5];
                var stat = (delegate* unmanaged[Stdcall]<nint, STATSTG*, uint, int>)vtable[12];

                var buffer = new byte[100];
                uint got = 0;
                ulong position = 0;
                fixed (byte* pointer = buffer)
                {
                    Assert.Equal(0, seek(stream, 1000, 0, &position));
                    Assert.Equal(1000ul, position);
                    Assert.Equal(0, read(stream, pointer, 100, &got));
                }

                Assert.Equal(100u, got);
                Assert.Equal(data[1000..1100], buffer);

                STATSTG info = default;
                Assert.Equal(0, stat(stream, &info, 1));
                Assert.Equal(8192ul, info.cbSize);
            }
            finally
            {
                Marshal.Release(stream);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }
}
