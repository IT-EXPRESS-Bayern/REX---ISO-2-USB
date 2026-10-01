// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Optical.Reading;

namespace Bootrix.Core.Tests.Optical.Support;

/// <summary>A disc held in memory with the kinds of defects a scratched one has.</summary>
internal sealed class FakeSectorReader(byte[] image, long? sectorCount = null) : ISectorReader, IDiscInspector
{
    private readonly Dictionary<long, int> _transientFailures = [];

    public string Name => "fake drive";

    /// <summary>What the drive reports; it can differ from what is readable, as on rewritable media.</summary>
    public long SectorCount { get; set; } = sectorCount ?? image.Length / 2048;

    /// <summary>Sectors that never read.</summary>
    public HashSet<long> PermanentlyBad { get; } = [];

    /// <summary>Sectors that fail the given number of reads that touch them and then work.</summary>
    public void FailTransiently(long lba, int times) => _transientFailures[lba] = times;

    /// <summary>The longest run of sectors a single request returns; 0 means unlimited.</summary>
    public int MaxSectorsPerRead { get; set; }

    public Dictionary<long, SectorReadStatus> FailureStatus { get; } = [];

    public DiscToc? Toc { get; set; }

    public DiscCopyrightInfo? Copyright { get; set; }

    public bool SupportsSpeedControl { get; set; }

    public int? RequestedSpeed { get; private set; }

    public List<(long Lba, int Count)> Requests { get; } = [];

    /// <summary>Called before every request; tests use it to cancel or swap discs at a chosen moment.</summary>
    public Action<long, int>? BeforeRead { get; set; }

    public SectorReadResult Read(long lba, int count, Span<byte> buffer)
    {
        Requests.Add((lba, count));
        BeforeRead?.Invoke(lba, count);

        var limit = MaxSectorsPerRead > 0 ? Math.Min(count, MaxSectorsPerRead) : count;
        for (var i = 0; i < limit; i++)
        {
            var sector = lba + i;
            if ((sector + 1) * 2048 > image.Length)
            {
                return SectorReadResult.Failure(i);
            }

            if (PermanentlyBad.Contains(sector))
            {
                return SectorReadResult.Failure(i, FailureStatus.GetValueOrDefault(sector, SectorReadStatus.Unreadable));
            }

            if (_transientFailures.TryGetValue(sector, out var left) && left > 0)
            {
                _transientFailures[sector] = left - 1;
                return SectorReadResult.Failure(i);
            }

            image.AsSpan((int)(sector * 2048), 2048).CopyTo(buffer.Slice(i * 2048, 2048));
        }

        return SectorReadResult.Success(limit);
    }

    public bool TrySetReadSpeed(int kilobytesPerSecond)
    {
        if (!SupportsSpeedControl)
        {
            return false;
        }

        RequestedSpeed = kilobytesPerSecond;
        return true;
    }

    public DiscToc? ReadToc() => Toc;

    public DiscCopyrightInfo? ReadCopyrightInfo() => Copyright;

    public void Dispose()
    {
    }
}
