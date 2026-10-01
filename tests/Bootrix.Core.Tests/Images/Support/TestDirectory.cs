// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace Bootrix.Core.Tests.Images.Support;

public sealed class TestDirectory : IDisposable
{
    public TestDirectory(string prefix = "bootrix-img")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public string Write(string name, byte[] content)
    {
        var path = File(name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, content);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A reference tool may still hold a handle for a moment; the OS cleans temp later.
        }
    }

    /// <summary>
    /// Text-like data with a vocabulary large enough to fill LZW and LZ77 dictionaries, interleaved
    /// with incompressible and all-zero stretches so that table resets and long matches both occur.
    /// </summary>
    public static byte[] Compressible(int length, int seed = 7)
    {
        var random = new Random(seed);
        var words = Enumerable.Range(0, 4000).Select(_ => RandomWord(random)).ToArray();
        var data = new byte[length];
        var position = 0;
        while (position < length)
        {
            var section = random.Next(3);
            var count = random.Next(2_000, 90_000);
            count = Math.Min(count, length - position);
            switch (section)
            {
                case 0:
                    random.NextBytes(data.AsSpan(position, count));
                    break;
                case 1:
                    break;
                default:
                    var written = 0;
                    while (written < count)
                    {
                        var word = Encoding.ASCII.GetBytes(words[random.Next(words.Length)] + " ");
                        var take = Math.Min(word.Length, count - written);
                        word.AsSpan(0, take).CopyTo(data.AsSpan(position + written));
                        written += take;
                    }

                    break;
            }

            position += count;
        }

        return data;
    }

    private static string RandomWord(Random random)
    {
        var chars = new char[random.Next(3, 12)];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = (char)('a' + random.Next(26));
        }

        return new string(chars);
    }
}
