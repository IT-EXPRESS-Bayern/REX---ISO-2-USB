// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;
using Bootrix.Core.Storage;
using Bootrix.Windows.Interop;
using Bootrix.Windows.Storage;

namespace Bootrix.Windows.Tests.Storage;

public class ParsingTests
{
    [Fact]
    public void SplitsMultiStrings()
    {
        var buffer = "C:\\\0D:\\Mount\\\0\0\0\0".ToCharArray();

        Assert.Equal(["C:\\", "D:\\Mount\\"], VolumeCatalog.SplitMultiString(buffer));
    }

    [Fact]
    public void EmptyMultiStringYieldsNothing()
    {
        Assert.Empty(VolumeCatalog.SplitMultiString(new char[8]));
    }

    [Fact]
    public void ParsesStorageDeviceDescriptor()
    {
        var data = new byte[128];
        BinaryPrimitives.WriteUInt32LittleEndian(data, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 128);
        data[10] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 40);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), 50);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), 70);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), 80);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28), (int)BusType.Usb);
        Encoding.ASCII.GetBytes("SanDisk ").CopyTo(data, 40);
        Encoding.ASCII.GetBytes("Ultra USB 3.0   ").CopyTo(data, 50);
        Encoding.ASCII.GetBytes("1.00").CopyTo(data, 70);
        Encoding.ASCII.GetBytes("4C530001220125117313").CopyTo(data, 80);

        var descriptor = StorageQueries.ParseDeviceDescriptor(data);

        Assert.NotNull(descriptor);
        Assert.Equal("SanDisk", descriptor.Vendor);
        Assert.Equal("Ultra USB 3.0", descriptor.Product);
        Assert.Equal("1.00", descriptor.Revision);
        Assert.Equal("4C530001220125117313", descriptor.Serial);
        Assert.True(descriptor.RemovableMedia);
        Assert.Equal(BusType.Usb, descriptor.Bus);
    }

    [Fact]
    public void MissingStringOffsetsGiveEmptyValues()
    {
        var data = new byte[64];
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(28), (int)BusType.Sd);

        var descriptor = StorageQueries.ParseDeviceDescriptor(data);

        Assert.NotNull(descriptor);
        Assert.Equal("", descriptor.Vendor);
        Assert.Null(descriptor.Serial);
    }

    [Fact]
    public void OffsetsPointingOutsideTheBufferAreIgnored()
    {
        var data = new byte[64];
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 9999);

        Assert.Equal("", StorageQueries.ParseDeviceDescriptor(data)!.Vendor);
    }

    [Fact]
    public void NotifyFilterHasTheSizeWindowsExpects()
    {
        Assert.Equal(416, System.Runtime.CompilerServices.Unsafe.SizeOf<Cfgmgr32.NotifyFilter>());
    }
}
