// SPDX-License-Identifier: GPL-3.0-or-later
using System.Reflection;

namespace Bootrix.Core.Boot.Syslinux;

public enum SyslinuxMatch
{
    /// <summary>Same release and same build tag as the image's own Syslinux.</summary>
    Exact,

    /// <summary>Same major and minor number, built from a different snapshot (distribution rebuilds of 6.04).</summary>
    SameRelease,

    /// <summary>Same major version only; ldlinux.c32 and the modules of the image are a close but untested pairing.</summary>
    SameMajor,

    /// <summary>The image uses another generation of Syslinux (or none could be identified).</summary>
    Different,
}

public sealed record SyslinuxChoice(SyslinuxBundle Bundle, SyslinuxMatch Match);

/// <summary>
/// One official Syslinux release's files as Bootrix ships them: the core (ldlinux.sys), the boot sector
/// (ldlinux.bss), ldlinux.c32 and a few modules. The files come unchanged from the release tarball on kernel.org
/// (see assets/third-party/SOURCES.md).
/// </summary>
public sealed class SyslinuxBundle
{
    private const string ResourcePrefix = "Bootrix.Core.Boot.Syslinux.";
    private static readonly string[] Releases = ["6.03", "6.04-pre1"];

    private readonly Dictionary<string, byte[]> _modules;

    private SyslinuxBundle(string id, byte[] core, byte[] bootSector, Dictionary<string, byte[]> modules)
    {
        Id = id;
        Core = core;
        BootSector = bootSector;
        _modules = modules;
        Version = SyslinuxVersion.FromBinary(core) ?? throw new InvalidDataException($"The Syslinux core of release {id} carries no version banner.");
    }

    public static IReadOnlyList<SyslinuxBundle> Shipped { get; } = [.. Releases.Select(Load)];

    /// <summary>The release name, e.g. "6.04-pre1".</summary>
    public string Id { get; }

    public SyslinuxVersion Version { get; }

    /// <summary>ldlinux.sys exactly as released: the loader that the boot sector starts, without the ADV sectors.</summary>
    public ReadOnlyMemory<byte> Core { get; }

    /// <summary>ldlinux.bss: the boot sector template, 512 bytes.</summary>
    public ReadOnlyMemory<byte> BootSector { get; }

    public ReadOnlyMemory<byte> LdlinuxModule => _modules["ldlinux.c32"];

    public IReadOnlyCollection<string> ModuleNames => _modules.Keys;

    public bool TryGetModule(string name, out ReadOnlyMemory<byte> content)
    {
        var found = _modules.TryGetValue(name, out var bytes);
        content = bytes;
        return found;
    }

    /// <summary>
    /// Picks the release whose core goes with the files of an image. Versions 5 and 6 of ldlinux.sys and ldlinux.c32
    /// have to belong together; across the 6.03 and 6.04 builds that Bootrix ships the pairing was tried with the
    /// Debian and Ubuntu snapshots, which is why a same-major match is still accepted.
    /// </summary>
    public static SyslinuxChoice Select(SyslinuxVersion? image)
    {
        var newest = Shipped.OrderBy(bundle => bundle.Version.Major).ThenBy(bundle => bundle.Version.Minor).Last();
        if (image is not { } wanted)
        {
            return new SyslinuxChoice(newest, SyslinuxMatch.Different);
        }

        var exact = Shipped.FirstOrDefault(bundle => bundle.Version.SameRelease(wanted) && string.Equals(bundle.Version.Tag, wanted.Tag, StringComparison.Ordinal));
        if (exact is not null)
        {
            return new SyslinuxChoice(exact, SyslinuxMatch.Exact);
        }

        var release = Shipped.FirstOrDefault(bundle => bundle.Version.SameRelease(wanted));
        if (release is not null)
        {
            return new SyslinuxChoice(release, SyslinuxMatch.SameRelease);
        }

        var sameMajor = Shipped
            .Where(bundle => bundle.Version.Major == wanted.Major)
            .OrderBy(bundle => Math.Abs(bundle.Version.Minor - wanted.Minor))
            .FirstOrDefault();
        return sameMajor is not null
            ? new SyslinuxChoice(sameMajor, SyslinuxMatch.SameMajor)
            : new SyslinuxChoice(newest, SyslinuxMatch.Different);
    }

    private static SyslinuxBundle Load(string release)
    {
        var assembly = typeof(SyslinuxBundle).Assembly;
        var prefix = ResourcePrefix + release + "/";
        var files = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
            .ToDictionary(name => name[prefix.Length..], name => ReadResource(assembly, name), StringComparer.OrdinalIgnoreCase);

        var core = files.GetValueOrDefault("ldlinux.sys") ?? throw new InvalidDataException($"Syslinux {release}: ldlinux.sys is not embedded.");
        var bootSector = files.GetValueOrDefault("ldlinux.bss") ?? throw new InvalidDataException($"Syslinux {release}: ldlinux.bss is not embedded.");
        var modules = files.Where(file => file.Key.EndsWith(".c32", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(file => file.Key, file => file.Value, StringComparer.OrdinalIgnoreCase);
        if (!modules.ContainsKey("ldlinux.c32"))
        {
            throw new InvalidDataException($"Syslinux {release}: ldlinux.c32 is not embedded.");
        }

        return new SyslinuxBundle(release, core, bootSector, modules);
    }

    internal static byte[] ReadResource(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name) ?? throw new InvalidDataException($"The embedded resource {name} is missing.");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }
}
