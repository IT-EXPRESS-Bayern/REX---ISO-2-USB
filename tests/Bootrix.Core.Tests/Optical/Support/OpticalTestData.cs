// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Diagnostics;
using Bootrix.Core.Optical;

namespace Bootrix.Core.Tests.Optical.Support;

internal static class OpticalTestData
{
    /// <summary>Random sectors with a valid ISO 9660 primary volume descriptor at sector 16 that claims <paramref name="volumeSectors"/>.</summary>
    public static byte[] DiscImage(int sectors, int seed = 1, int? volumeSectors = null)
    {
        var data = new byte[sectors * 2048];
        new Random(seed).NextBytes(data);
        if (sectors > 17)
        {
            WritePrimaryDescriptor(data.AsSpan(16 * 2048, 2048), volumeSectors ?? sectors);
        }

        return data;
    }

    public static void WritePrimaryDescriptor(Span<byte> sector, long volumeSectors)
    {
        sector.Clear();
        sector[0] = 1;
        "CD001"u8.CopyTo(sector[1..]);
        sector[6] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(sector[80..], (uint)volumeSectors);
        BinaryPrimitives.WriteUInt32BigEndian(sector[84..], (uint)volumeSectors);
    }

    /// <summary>Builds the answer to READ TOC format 2 (full TOC). Tracks are (number, isData, startLba) per session.</summary>
    public static byte[] FullToc(params (int Session, (int Number, bool IsData, int StartLba)[] Tracks, int LeadOutLba, byte DiscType)[] sessions)
    {
        var descriptors = new List<byte[]>();
        foreach (var session in sessions)
        {
            var first = session.Tracks.Min(t => t.Number);
            var last = session.Tracks.Max(t => t.Number);
            descriptors.Add(Descriptor(session.Session, 0x14, 0xA0, new Msf(first, session.DiscType, 0)));
            descriptors.Add(Descriptor(session.Session, 0x14, 0xA1, new Msf(last, 0, 0)));
            descriptors.Add(Descriptor(session.Session, 0x14, 0xA2, Msf.FromLba(session.LeadOutLba)));
            foreach (var track in session.Tracks)
            {
                descriptors.Add(Descriptor(session.Session, (byte)(track.IsData ? 0x14 : 0x10), (byte)track.Number, Msf.FromLba(track.StartLba)));
            }
        }

        var result = new byte[4 + descriptors.Count * 11];
        BinaryPrimitives.WriteUInt16BigEndian(result, (ushort)(result.Length - 2));
        result[2] = (byte)sessions.Min(s => s.Session);
        result[3] = (byte)sessions.Max(s => s.Session);
        for (var i = 0; i < descriptors.Count; i++)
        {
            descriptors[i].CopyTo(result, 4 + i * 11);
        }

        return result;
    }

    /// <summary>Builds the answer to READ TOC format 0 with LBA addresses.</summary>
    public static byte[] BasicToc((int Number, bool IsData, int StartLba)[] tracks, int leadOutLba)
    {
        var entries = new List<(int Number, byte Control, int StartLba)>();
        foreach (var track in tracks)
        {
            entries.Add((track.Number, (byte)(track.IsData ? 0x14 : 0x10), track.StartLba));
        }

        entries.Add((0xAA, 0x14, leadOutLba));
        var result = new byte[4 + entries.Count * 8];
        BinaryPrimitives.WriteUInt16BigEndian(result, (ushort)(result.Length - 2));
        result[2] = (byte)tracks.Min(t => t.Number);
        result[3] = (byte)tracks.Max(t => t.Number);
        for (var i = 0; i < entries.Count; i++)
        {
            var offset = 4 + i * 8;
            result[offset + 1] = entries[i].Control;
            result[offset + 2] = (byte)entries[i].Number;
            BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(offset + 4), entries[i].StartLba);
        }

        return result;
    }

    private static byte[] Descriptor(int session, byte adrControl, byte point, Msf position) =>
        [(byte)session, adrControl, 0, point, 0, 0, 0, 0, (byte)position.Minutes, (byte)position.Seconds, (byte)position.Frames];
}

internal static class OpticalTools
{
    public static bool Has(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(Path.PathSeparator).Any(dir => File.Exists(Path.Combine(dir, name)));
    }

    public static (int ExitCode, string Output) Run(string tool, params string[] arguments)
    {
        var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, output);
    }
}

/// <summary>Runs only where the named command line tools are installed; CI images on Windows skip it.</summary>
public sealed class NeedsToolFactAttribute : FactAttribute
{
    public NeedsToolFactAttribute(params string[] tools)
    {
        var missing = tools.FirstOrDefault(t => !OpticalTools.Has(t));
        if (missing is not null)
        {
            Skip = $"{missing} is not installed";
        }
    }
}
