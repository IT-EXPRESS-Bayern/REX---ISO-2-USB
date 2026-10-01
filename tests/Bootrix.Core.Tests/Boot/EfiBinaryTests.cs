// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using Bootrix.Core.Boot;
using Bootrix.Core.Errors;

namespace Bootrix.Core.Tests.Boot;

public class EfiBinaryTests
{
    // Offsets inside the images written by PeBuilder (PE32+, e_lfanew = 0x80).
    private const int OptionalHeader = 0x98;
    private const int SecurityDirectory = OptionalHeader + 112 + 4 * 8;
    private const int FirstSectionHeader = OptionalHeader + 0xF0;

    private static void Patch(byte[] image, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset), value);

    private static void AssertInvalid(byte[] data)
    {
        var ex = Assert.Throws<BootrixException>(() => EfiBinary.Parse(data));
        Assert.Equal(ErrorCode.EfiBinaryInvalid, ex.Code);
    }

    [Fact]
    public void Parse_Pe32Plus_ReadsHeaderAndSections()
    {
        var image = PeBuilder.Typical().Build();

        var binary = EfiBinary.Parse(image);

        Assert.Equal(EfiMachine.X64, binary.Machine);
        Assert.Equal(Subsystem.EfiApplication, binary.Subsystem);
        Assert.True(binary.IsEfiImage);
        Assert.Equal(image.Length, binary.Size);
        Assert.False(binary.IsSigned);
        Assert.Empty(binary.Certificates);
        Assert.Equal([".text", ".data"], binary.Sections.Select(s => s.Name));
        Assert.Equal(0x300u, binary.Sections[0].VirtualSize);
        Assert.Equal(0x200u, binary.Sections[0].RawOffset);
        Assert.Equal(0x400u, binary.Sections[0].RawSize);
    }

    [Fact]
    public void Parse_Pe32_ReadsMachineAndSubsystem()
    {
        var image = new PeBuilder { Pe32Plus = false, Machine = 0x14C, Subsystem = 11 }
            .AddSection(".text", PeBuilder.Pattern(0x100, 3))
            .Build();

        var binary = EfiBinary.Parse(image);

        Assert.Equal(EfiMachine.X86, binary.Machine);
        Assert.Equal(Subsystem.EfiBootServiceDriver, binary.Subsystem);
        Assert.True(binary.IsEfiImage);
    }

    [Theory]
    [InlineData(0x014C, EfiMachine.X86)]
    [InlineData(0x8664, EfiMachine.X64)]
    [InlineData(0x01C2, EfiMachine.Arm)]
    [InlineData(0x01C4, EfiMachine.Arm)]
    [InlineData(0xAA64, EfiMachine.Arm64)]
    [InlineData(0x5064, EfiMachine.RiscV64)]
    [InlineData(0x6264, EfiMachine.LoongArch64)]
    [InlineData(0x1234, EfiMachine.Unknown)]
    public void Machine_IsMappedFromTheCoffField(int raw, EfiMachine expected)
    {
        var binary = EfiBinary.Parse(new PeBuilder { Machine = (ushort)raw }.AddSection(".text", [1, 2, 3]).Build());

        Assert.Equal(expected, binary.Machine);
        Assert.Equal(raw, binary.RawMachine);
    }

    [Fact]
    public void Parse_WindowsGuiImage_IsNotAnEfiImage()
    {
        var binary = EfiBinary.Parse(new PeBuilder { Subsystem = 2 }.AddSection(".text", [1]).Build());

        Assert.False(binary.IsEfiImage);
    }

    [Fact]
    public void TryGetSectionData_StopsAtVirtualSize()
    {
        var content = "sbat,1,SBAT Version\n"u8.ToArray();
        var image = new PeBuilder().AddSection(".sbat", [.. content, .. new byte[100]], virtualSize: (uint)content.Length).Build();

        var binary = EfiBinary.Parse(image);

        Assert.True(binary.TryGetSectionData(".sbat", out var data));
        Assert.Equal(content, data.ToArray());
        Assert.False(binary.TryGetSectionData(".nothing", out _));
    }

    [Fact]
    public void Parse_CertificateTable_IsSplitIntoEntries()
    {
        var first = PeBuilder.Pattern(37, 5);
        var second = PeBuilder.Pattern(64, 6);
        var image = PeBuilder.Typical().AddCertificate(first).AddCertificate(second).Build();

        var binary = EfiBinary.Parse(image);

        Assert.True(binary.IsSigned);
        Assert.Equal(2, binary.Certificates.Count);
        Assert.All(binary.Certificates, c => Assert.Equal(WinCertificate.TypePkcsSignedData, c.Type));
        Assert.Equal(first, binary.Certificates[0].Data.ToArray());
        Assert.Equal(second, binary.Certificates[1].Data.ToArray());
    }

    [Fact]
    public void Parse_ImageWithoutSecurityDirectoryEntry_IsUnsigned()
    {
        var image = new PeBuilder { NumberOfRvaAndSizes = 4 }.AddSection(".text", PeBuilder.Pattern(100, 1)).Build();

        var binary = EfiBinary.Parse(image);

        Assert.False(binary.IsSigned);
    }

    [Fact]
    public void Parse_ZeroPaddingAfterTheLastCertificate_IsIgnored()
    {
        var image = PeBuilder.Typical().AddCertificate(PeBuilder.Pattern(20, 1)).Build();
        var padded = new byte[image.Length + 24];
        image.CopyTo(padded, 0);
        Patch(padded, SecurityDirectory + 4, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(SecurityDirectory + 4)) + 24);

        var binary = EfiBinary.Parse(padded);

        Assert.Single(binary.Certificates);
    }

    [Fact]
    public void Parse_NonZeroGarbageAfterTheLastCertificate_IsRejected()
    {
        var image = PeBuilder.Typical().AddCertificate(PeBuilder.Pattern(20, 1)).Build();
        var padded = new byte[image.Length + 16];
        image.CopyTo(padded, 0);
        padded[image.Length + 3] = 0x55;
        Patch(padded, SecurityDirectory + 4, BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(SecurityDirectory + 4)) + 16);

        AssertInvalid(padded);
    }

    [Fact]
    public void Parse_EmptyAndTinyInput_IsRejected()
    {
        AssertInvalid([]);
        AssertInvalid([0x4D, 0x5A]);
        AssertInvalid(new byte[1024]);
        AssertInvalid("MZ"u8.ToArray().Concat(new byte[200]).ToArray());
    }

    [Fact]
    public void Parse_RandomBytes_IsRejected()
    {
        var data = PeBuilder.Pattern(4096, 99);

        AssertInvalid(data);
    }

    [Fact]
    public void Parse_SectionBeyondEndOfFile_IsRejected()
    {
        var image = PeBuilder.Typical().Build();
        Patch(image, FirstSectionHeader + 16, 0x7FFFFFFF);

        AssertInvalid(image);
    }

    [Fact]
    public void Parse_SectionOffsetThatOverflowsWhenAdded_IsRejected()
    {
        var image = PeBuilder.Typical().Build();
        Patch(image, FirstSectionHeader + 16, 0x400);
        Patch(image, FirstSectionHeader + 20, 0xFFFFFFF0);

        AssertInvalid(image);
    }

    [Theory]
    [InlineData(0x7FFFFFFFu)]
    [InlineData(0xFFFFFFFFu)]
    [InlineData(0x00800000u)]
    public void Parse_HugeCertificateTable_IsRejected(uint size)
    {
        var image = PeBuilder.Typical().AddCertificate(PeBuilder.Pattern(20, 1)).Build();
        Patch(image, SecurityDirectory + 4, size);

        AssertInvalid(image);
    }

    [Theory]
    [InlineData(0xFFFFFFF8u)]
    [InlineData(0x7FFFFFFFu)]
    [InlineData(0x00000010u)]
    public void Parse_CertificateTableOutsideTheFile_IsRejected(uint offset)
    {
        var image = PeBuilder.Typical().AddCertificate(PeBuilder.Pattern(20, 1)).Build();
        Patch(image, SecurityDirectory, offset);

        AssertInvalid(image);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(7u)]
    [InlineData(0xFFFFFFFFu)]
    [InlineData(0x10000u)]
    public void Parse_CertificateEntryWithBadLength_IsRejected(uint length)
    {
        var image = PeBuilder.Typical().AddCertificate(PeBuilder.Pattern(20, 1)).Build();
        var tableOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(SecurityDirectory));
        Patch(image, tableOffset, length);
        image[tableOffset + 8] = 1; // keeps a zero length from being mistaken for padding

        AssertInvalid(image);
    }

    [Fact]
    public void Parse_TooManyCertificates_IsRejected()
    {
        var builder = PeBuilder.Typical();
        for (var i = 0; i < 40; i++)
        {
            builder.AddCertificate([1, 2, 3, 4]);
        }

        AssertInvalid(builder.Build());
    }

    [Fact]
    public void Parse_EveryTruncationOfASignedImage_FailsCleanlyOrParses()
    {
        var image = PeBuilder.Typical().AddCertificate(PeBuilder.Pattern(300, 8)).Build();

        for (var length = 0; length < image.Length; length += 3)
        {
            var cut = image.AsSpan(0, length).ToArray();
            try
            {
                var binary = EfiBinary.Parse(cut);
                binary.ComputeAuthenticodeHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            }
            catch (BootrixException ex)
            {
                Assert.Equal(ErrorCode.EfiBinaryInvalid, ex.Code);
            }
        }
    }

    [Fact]
    public void Parse_RandomlyDamagedImages_NeverThrowAnythingButBootrixException()
    {
        var original = PeBuilder.Typical().AddBootmgrSecurityVersion(9, 0).AddCertificate(PeBuilder.Pattern(200, 4)).Build();
        var random = new Random(20261001);

        for (var round = 0; round < 3000; round++)
        {
            var copy = (byte[])original.Clone();
            for (var change = 0; change < 1 + random.Next(6); change++)
            {
                // Concentrate on the headers, where the parser takes its decisions.
                var at = random.Next(3) == 0 ? random.Next(copy.Length) : random.Next(0x2A0);
                copy[at] = (byte)random.Next(256);
            }

            try
            {
                var binary = EfiBinary.Parse(copy);
                binary.ComputeAuthenticodeHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
                binary.ReadSbat();
                binary.ReadBootmgrSecurityVersion();
            }
            catch (BootrixException ex)
            {
                Assert.Equal(ErrorCode.EfiBinaryInvalid, ex.Code);
            }
        }
    }

    [Fact]
    public void Load_StreamLongerThanTheLimit_IsRejected()
    {
        var image = PeBuilder.Typical().Build();

        var seekable = Assert.Throws<BootrixException>(() => EfiBinary.Load(new MemoryStream(image), maxFileSize: image.Length - 1));
        var forward = Assert.Throws<BootrixException>(() => EfiBinary.Load(new ForwardOnlyStream(image), maxFileSize: image.Length - 1));

        Assert.Equal(ErrorCode.EfiBinaryInvalid, seekable.Code);
        Assert.Equal(ErrorCode.EfiBinaryInvalid, forward.Code);
    }

    [Fact]
    public void Load_NonSeekableStream_ParsesTheWholeContent()
    {
        var image = PeBuilder.Typical().Build();

        var binary = EfiBinary.Load(new ForwardOnlyStream(image));

        Assert.Equal(image.Length, binary.Size);
    }

    [Fact]
    public void Load_StartsAtTheCurrentPosition()
    {
        var image = PeBuilder.Typical().Build();
        var stream = new MemoryStream([.. new byte[10], .. image]) { Position = 10 };

        Assert.Equal(image.Length, EfiBinary.Load(stream).Size);
    }

    [Fact]
    public void Load_CancelledToken_Throws()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => EfiBinary.Load(new MemoryStream(PeBuilder.Typical().Build()), cancellationToken: cts.Token));
    }

    private sealed class ForwardOnlyStream(byte[] data) : Stream
    {
        private readonly MemoryStream _inner = new(data);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
