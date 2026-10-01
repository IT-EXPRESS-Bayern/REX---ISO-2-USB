// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace Bootrix.Core.FileSystems.Ext;

/// <summary>The root directory and the regular files placed directly in it.</summary>
internal static class ExtRootDirectory
{
    private const string LostFoundName = "lost+found";

    // fsck needs room in lost+found without allocating; mke2fs preallocates the same 16 KiB.
    private const int LostFoundBytes = 16 * 1024;

    private const ushort ModeRootDirectory = ExtInode.ModeDirectory | 0x1ED;
    private const ushort ModeLostFound = ExtInode.ModeDirectory | 0x1C0;

    public static void Create(ExtVolume volume)
    {
        for (var number = 1u; number <= volume.ReservedInodes; number++)
        {
            volume.MarkInodeUsed(number, directory: number == ExtVolume.RootInode);
        }

        var blockSize = volume.BlockSize;
        var rootBlocks = ExtFileAllocator.Allocate(volume, 1);
        var lostFound = volume.AllocateInode(directory: true);
        var lostFoundBlocks = ExtFileAllocator.Allocate(volume, Math.Max(1, LostFoundBytes / blockSize));

        volume.WriteBlock(rootBlocks.DataBlocks[0], ExtDirectory.Build(blockSize,
        [
            new ExtDirectoryEntry(ExtVolume.RootInode, ExtDirectory.TypeDirectory, "."),
            new ExtDirectoryEntry(ExtVolume.RootInode, ExtDirectory.TypeDirectory, ".."),
            new ExtDirectoryEntry(lostFound, ExtDirectory.TypeDirectory, LostFoundName),
        ]));

        volume.WriteBlock(lostFoundBlocks.DataBlocks[0], ExtDirectory.Build(blockSize,
        [
            new ExtDirectoryEntry(lostFound, ExtDirectory.TypeDirectory, "."),
            new ExtDirectoryEntry(ExtVolume.RootInode, ExtDirectory.TypeDirectory, ".."),
        ]));

        for (var i = 1; i < lostFoundBlocks.DataBlocks.Length; i++)
        {
            volume.WriteBlock(lostFoundBlocks.DataBlocks[i], ExtDirectory.EmptyBlock(blockSize));
        }

        // The root counts "." plus the ".." of lost+found on top of the link from its own parent entry.
        volume.WriteNewInode(ExtVolume.RootInode, NewInode(volume, ModeRootDirectory, 3, rootBlocks, blockSize));
        volume.WriteNewInode(lostFound, NewInode(volume, ModeLostFound, 2, lostFoundBlocks, lostFoundBlocks.DataBlocks.Length * blockSize));
    }

    public static void AddFile(ExtVolume volume, ExtRootFile file)
    {
        var name = ValidateName(file.Name);
        var blockSize = volume.BlockSize;
        var root = volume.ReadInode(ExtVolume.RootInode);
        if (!root.IsDirectory)
        {
            throw new InvalidDataException("Inode 2 is not a directory; run e2fsck.");
        }

        if ((root.Flags & ExtInode.FlagIndexed) != 0)
        {
            throw new NotSupportedException("The root directory has an htree index; adding entries to indexed directories is not supported.");
        }

        var directoryBlocks = ReadDirectoryBlocks(volume, root);
        var buffers = new List<byte[]>(directoryBlocks.Count);
        foreach (var block in directoryBlocks)
        {
            var buffer = new byte[blockSize];
            volume.ReadBlock(block, buffer);
            buffers.Add(buffer);
        }

        if (buffers.Any(buffer => ExtDirectory.Contains(buffer, name)))
        {
            throw new ArgumentException($"A file named '{file.Name}' already exists in the root directory.", nameof(file));
        }

        var target = buffers.FindIndex(buffer => ExtDirectory.HasRoomFor(buffer, name.Length));
        if (target < 0)
        {
            throw new NotSupportedException("The root directory block is full; growing a directory is not supported.");
        }

        var content = file.Content.Span;
        var dataBlocks = (content.Length + blockSize - 1L) / blockSize;
        volume.EnsureCapacity(ExtFileAllocator.EstimateBlocks(volume, dataBlocks), 1);

        var blocks = ExtFileAllocator.Allocate(volume, dataBlocks);
        WriteContent(volume, blocks.DataBlocks, content);

        var number = volume.AllocateInode(directory: false);
        var inode = NewInode(volume, (ushort)(ExtInode.ModeRegular | (file.Permissions & 0xFFF)), 1, blocks, content.Length);
        volume.WriteNewInode(number, inode);

        ExtDirectory.TryAdd(buffers[target], name, number, ExtDirectory.TypeRegular);
        volume.WriteBlock(directoryBlocks[target], buffers[target]);

        root.ModificationTime = root.ChangeTime = volume.Now;
        volume.WriteInode(ExtVolume.RootInode, root);
    }

    private static ExtInode NewInode(ExtVolume volume, ushort mode, ushort links, ExtFileBlocks blocks, long size)
    {
        var now = volume.Now;
        return new ExtInode
        {
            Mode = mode,
            LinksCount = links,
            Size = size,
            Sectors = blocks.TotalBlocks * (volume.BlockSize / 512),
            Flags = blocks.InodeFlags,
            IBlock = blocks.IBlock,
            AccessTime = now,
            ChangeTime = now,
            ModificationTime = now,
            CreationTime = now,
            ExtraSize = volume.InodeExtraSize,
        };
    }

    private static void WriteContent(ExtVolume volume, long[] dataBlocks, ReadOnlySpan<byte> content)
    {
        var blockSize = volume.BlockSize;
        var buffer = new byte[blockSize];
        for (var i = 0; i < dataBlocks.Length; i++)
        {
            var chunk = content.Slice(i * blockSize, Math.Min(blockSize, content.Length - i * blockSize));
            buffer.AsSpan().Clear();
            chunk.CopyTo(buffer);
            volume.WriteBlock(dataBlocks[i], buffer);
        }
    }

    private static List<long> ReadDirectoryBlocks(ExtVolume volume, ExtInode directory)
    {
        var count = (directory.Size + volume.BlockSize - 1) / volume.BlockSize;
        var read = (long block, byte[] buffer) => volume.ReadBlock(block, buffer);
        var blocks = (directory.Flags & ExtInode.FlagExtents) != 0
            ? ExtExtentTree.Read(directory.IBlock, count, volume.BlockSize, read)
            : ExtBlockMap.Read(directory.IBlock, count, volume.BlockSize, read);

        if (blocks.Count != count || blocks.Contains(0))
        {
            throw new InvalidDataException("The root directory has holes or a truncated block map; run e2fsck.");
        }

        return blocks;
    }

    private static byte[] ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var bytes = Encoding.UTF8.GetBytes(name);
        if (bytes.Length > ExtDirectory.MaxNameLength || name is "." or ".." || name.Contains('/') || name.Contains('\0'))
        {
            throw new ArgumentException($"'{name}' is not a valid file name.", nameof(name));
        }

        return bytes;
    }
}
