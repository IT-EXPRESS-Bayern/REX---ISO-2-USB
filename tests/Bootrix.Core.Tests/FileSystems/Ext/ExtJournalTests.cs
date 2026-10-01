// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using Bootrix.Core.FileSystems.Ext;

namespace Bootrix.Core.Tests.FileSystems.Ext;

public class ExtJournalTests
{
    [Fact]
    public void BuildSuperblock_WritesEmptyVersion2JournalInBigEndian()
    {
        var uuid = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();

        var block = ExtJournal.BuildSuperblock(4096, 8192, uuid);

        Assert.Equal(4096, block.Length);
        Assert.Equal(0xC03B3998u, BinaryPrimitives.ReadUInt32BigEndian(block));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(0x04)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(0x08)));
        Assert.Equal(4096u, BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(0x0C)));
        Assert.Equal(8192u, BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(0x10)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(0x14)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(0x18)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(0x1C)));
        Assert.Equal(uuid, block.AsSpan(0x30, 16).ToArray());
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32BigEndian(block.AsSpan(0x40)));
        Assert.All(block[0x44..], b => Assert.Equal(0, b));
    }

    [ExtToolFact]
    public void Debugfs_FindsAnEmptyJournalStartingAtTransactionOne()
    {
        using var image = new TempImage(64 * 1024 * 1024);
        ExtFormatter.Format(image.Stream, new ExtFormatOptions());
        var path = image.Close();

        var logdump = ExtTools.Run("debugfs", "-R", "logdump", path);

        Assert.Equal(0, logdump.ExitCode);
        Assert.Contains("Journal starts at block 0, transaction 1", logdump.All);
    }
}
