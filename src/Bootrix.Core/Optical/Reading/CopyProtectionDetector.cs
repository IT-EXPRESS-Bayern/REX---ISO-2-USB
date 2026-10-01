// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using DiscUtils;
using DiscUtils.Iso9660;
using DiscUtils.Udf;
using Microsoft.Extensions.Logging;

namespace Bootrix.Core.Optical.Reading;

public enum ProtectionSystem
{
    None,

    /// <summary>Content Scramble System (and CPPM on DVD-Audio).</summary>
    Css,

    /// <summary>Content Protection for Recordable Media.</summary>
    Cprm,

    Aacs,

    /// <summary>BD+ on top of AACS.</summary>
    BdPlus,

    /// <summary>A protection type the drive reports but Bootrix does not know.</summary>
    Other,
}

/// <summary>The copyright structure of a DVD (READ DVD STRUCTURE format 1).</summary>
public sealed record DiscCopyrightInfo(ProtectionSystem System, byte RegionInformation)
{
    public static DiscCopyrightInfo FromDescriptor(byte protectionType, byte regionInformation) => new(
        protectionType switch
        {
            0 => ProtectionSystem.None,
            1 => ProtectionSystem.Css,
            2 => ProtectionSystem.Cprm,
            _ => ProtectionSystem.Other,
        },
        regionInformation);
}

public sealed record CopyProtectionFinding(ProtectionSystem System, string Reason);

/// <summary>
/// Recognises discs that carry copy protection so they can be refused. This only reads what the
/// drive reports openly (the copyright structure) and which top-level folders the disc has; nothing
/// is decrypted or worked around, which German law (UrhG §95a) would not allow either.
/// </summary>
public static class CopyProtectionDetector
{
    public static CopyProtectionFinding? Inspect(ISectorReader reader, ILogger? logger = null)
    {
        if (reader is IDiscInspector inspector
            && inspector.ReadCopyrightInfo() is { System: not ProtectionSystem.None } copyright)
        {
            return new CopyProtectionFinding(copyright.System, $"copyright structure reports {copyright.System}");
        }

        return InspectFileSystem(reader, logger);
    }

    private static CopyProtectionFinding? InspectFileSystem(ISectorReader reader, ILogger? logger)
    {
        try
        {
            using var stream = new SectorReaderStream(reader);
            using var fileSystem = OpenFileSystem(stream);
            if (fileSystem is null)
            {
                return null;
            }

            var folders = fileSystem.GetDirectories(string.Empty)
                .Select(path => Path.GetFileName(path.TrimEnd('\\', '/')))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Both folders are written by the AACS and BD+ authoring tools and are required by every player to decrypt the disc.
            if (folders.Contains("BDSVM"))
            {
                return new CopyProtectionFinding(ProtectionSystem.BdPlus, "BDSVM folder (BD+) on the disc");
            }

            if (folders.Contains("AACS"))
            {
                return new CopyProtectionFinding(ProtectionSystem.Aacs, "AACS folder on the disc");
            }
        }
        catch (Exception ex) when (ex is not (BootrixException or OperationCanceledException))
        {
            // File system parsers throw anything from IOException to IndexOutOfRangeException on damaged or unusual discs.
            // A disc whose structure cannot be read is not thereby protected, and the rip reports unreadable sectors by itself;
            // only a lost drive (BootrixException) is worth stopping for.
            logger?.LogDebug(ex, "File system of {Reader} could not be inspected for copy protection", reader.Name);
        }

        return null;
    }

    private static DiscFileSystem? OpenFileSystem(Stream stream)
    {
        if (CDReader.Detect(stream))
        {
            return new CDReader(stream, joliet: true);
        }

        stream.Position = 0;
        return UdfReader.Detect(stream) ? new UdfReader(stream) : null;
    }
}
