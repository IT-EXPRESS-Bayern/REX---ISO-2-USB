// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;

namespace Bootrix.Core.Writing.Linux;

/// <summary>
/// Reads the files of a finished Linux medium back and compares them with what the builder meant to write: the
/// image's files against the image, patched and generated files against the bytes the builder kept.
/// </summary>
public static class LinuxMediaVerifier
{
    private const int BufferBytes = 1024 * 1024;

    /// <exception cref="BootrixException">A file is missing, has another length or differs from its source.</exception>
    public static void Verify(
        IImageFileTree image,
        ITargetVolume volume,
        LinuxBuildResult result,
        IProgress<LinuxCopyProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(volume);
        ArgumentNullException.ThrowIfNull(result);

        var files = result.Files.Where(file => file.Source != WrittenFileSource.Generated || file.Content is not null).ToList();
        var total = files.Sum(file => file.Length);
        long done = 0;
        var buffer = new byte[BufferBytes];
        var other = new byte[BufferBytes];

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var actual = OpenOrFail(volume, file.Path);
            if (actual.Length != file.Length)
            {
                throw Mismatch(file.Path, Math.Min(actual.Length, file.Length));
            }

            if (file.Content is { } content)
            {
                CompareToBytes(actual, content, file.Path, buffer);
            }
            else
            {
                using var expected = image.OpenFile(file.Path);
                CompareStreams(actual, expected, file.Path, buffer, other);
            }

            done += file.Length;
            progress?.Report(new LinuxCopyProgress(done, total, file.Path));
        }
    }

    private static Stream OpenOrFail(ITargetVolume volume, string path)
    {
        try
        {
            return volume.OpenRead(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BootrixException(ErrorCode.VerifyMismatch, $"{path} cannot be read back: {ex.Message}", ex) { Arguments = [path + " (missing)"] };
        }
    }

    private static void CompareToBytes(Stream actual, byte[] expected, string path, byte[] buffer)
    {
        long position = 0;
        int read;
        while ((read = actual.Read(buffer, 0, (int)Math.Min(buffer.Length, expected.Length - position))) > 0)
        {
            var difference = buffer.AsSpan(0, read).CommonPrefixLength(expected.AsSpan((int)position, read));
            if (difference < read)
            {
                throw Mismatch(path, position + difference);
            }

            position += read;
        }

        if (position != expected.Length)
        {
            throw Mismatch(path, position);
        }
    }

    private static void CompareStreams(Stream actual, Stream expected, string path, byte[] buffer, byte[] other)
    {
        long position = 0;
        int read;
        while ((read = actual.Read(buffer, 0, buffer.Length)) > 0)
        {
            expected.ReadExactly(other, 0, read);
            var difference = buffer.AsSpan(0, read).CommonPrefixLength(other.AsSpan(0, read));
            if (difference < read)
            {
                throw Mismatch(path, position + difference);
            }

            position += read;
        }
    }

    private static BootrixException Mismatch(string path, long offset) =>
        new(ErrorCode.VerifyMismatch, $"{path} differs at offset {offset}") { Arguments = [$"{path}:{offset}"] };
}
