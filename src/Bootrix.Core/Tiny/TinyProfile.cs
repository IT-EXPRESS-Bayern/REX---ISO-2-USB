// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bootrix.Core.Errors;
using Bootrix.Core.Json;

namespace Bootrix.Core.Tiny;

public enum RegistryHive
{
    /// <summary>Windows\System32\config\SOFTWARE</summary>
    Software,

    /// <summary>Windows\System32\config\SYSTEM</summary>
    System,

    /// <summary>Windows\System32\config\DEFAULT</summary>
    Default,

    /// <summary>Users\Default\NTUSER.DAT, the template for every new user</summary>
    NtUser,
}

public enum RegistryValueKind
{
    DWord,
    Text,
}

public enum RegistryAction
{
    SetValue,
    DeleteKey,
    DeleteValue,
}

public sealed record RegistryChange
{
    public RegistryHive Hive { get; init; }

    public string Key { get; init; } = "";

    public string? Name { get; init; }

    public RegistryValueKind Kind { get; init; } = RegistryValueKind.DWord;

    public string? Value { get; init; }

    public RegistryAction Action { get; init; } = RegistryAction.SetValue;

    public string? Group { get; init; }
}

public sealed record AppxRemoval
{
    public string Name { get; init; } = "";

    public string? Group { get; init; }
}

public sealed record FileRemoval
{
    /// <summary>Path relative to the image root, with backslashes.</summary>
    public string Path { get; init; } = "";

    /// <summary>A path with * is expanded inside its parent folder (WinSxS component folders).</summary>
    public bool TakeOwnership { get; init; }

    public string? Group { get; init; }
}

public sealed record PackageRemoval
{
    /// <summary>Start of the package identity. "{lang}" stands for the image's UI language tag (e.g. de-DE).</summary>
    public string Pattern { get; init; } = "";

    public string? Group { get; init; }
}

public sealed record CapabilityRemoval
{
    /// <summary>Start of the capability name, e.g. "Browser.InternetExplorer".</summary>
    public string Pattern { get; init; } = "";

    public string? Group { get; init; }
}

public sealed record WinSxsPlan
{
    /// <summary>Folders and patterns that survive when the component store is rebuilt; everything else is deleted.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> KeepByArchitecture { get; init; } = new Dictionary<string, IReadOnlyList<string>>();
}

public sealed record TinyGroup
{
    public string Id { get; init; } = "";

    /// <summary>Whether the group is active unless the user switches it off.</summary>
    public bool Default { get; init; } = true;
}

/// <summary>
/// What is removed from or changed in a Windows image. A profile is plain data so that it can be
/// reviewed, tested and extended without touching the engine. The lists were compiled from the
/// public behaviour of NTDEV's tiny11 and Tiny10 builds; the code that applies them is Bootrix' own.
/// </summary>
public sealed record TinyProfile
{
    public string Id { get; init; } = "";

    public string Name { get; init; } = "";

    public string? Extends { get; init; }

    /// <summary>"10" or "11"</summary>
    public string WindowsFamily { get; init; } = "11";

    /// <summary>Remove packages that make the image impossible to service afterwards (Core builds).</summary>
    public bool BreaksServicing { get; init; }

    public IReadOnlyList<TinyGroup> Groups { get; init; } = [];

    public IReadOnlyList<AppxRemoval> Appx { get; init; } = [];

    public IReadOnlyList<PackageRemoval> Packages { get; init; } = [];

    public IReadOnlyList<CapabilityRemoval> Capabilities { get; init; } = [];

    public IReadOnlyList<FileRemoval> Files { get; init; } = [];

    public IReadOnlyList<RegistryChange> Registry { get; init; } = [];

    /// <summary>Changes applied to the Windows PE image (boot.wim, index 2) so that Setup accepts unsupported hardware.</summary>
    public IReadOnlyList<RegistryChange> BootWimRegistry { get; init; } = [];

    /// <summary>Task definition files below Windows\System32\Tasks.</summary>
    public IReadOnlyList<string> ScheduledTasks { get; init; } = [];

    public WinSxsPlan? WinSxs { get; init; }

    /// <summary>Replace winre.wim with an empty file (frees about 500 MB; no recovery environment afterwards).</summary>
    public bool EmptyWinRe { get; init; }

    public string? Description { get; init; }
}

public static class TinyProfiles
{
    public static readonly string[] BuiltInIds = ["tiny11", "tiny11core", "tiny10"];

    public static TinyProfile Load(string id)
    {
        var resource = $"Bootrix.Core.Tiny.Profiles.{id}.json";
        using var stream = typeof(TinyProfiles).Assembly.GetManifestResourceStream(resource)
            ?? throw new BootrixException(ErrorCode.InvalidSpec, $"unknown tiny profile '{id}'") { Arguments = [$"unknown profile '{id}'"] };

        var profile = Normalize(JsonSerializer.Deserialize<TinyProfile>(stream, CoreJson.Options)
            ?? throw new BootrixException(ErrorCode.InvalidSpec, $"empty profile '{id}'"));

        return profile.Extends is { } parent ? Merge(Load(parent), profile) : profile;
    }

    /// <summary>
    /// Which groups are switched off for a build: the ones that are off by default unless asked for with
    /// <paramref name="include"/>, plus the ones the user wants to <paramref name="keep"/>. A group name the profile
    /// does not know is an error, because a typo would otherwise silently remove something the user wanted to keep.
    /// </summary>
    public static IReadOnlySet<string> DisabledGroups(TinyProfile profile, IEnumerable<string> keep, IEnumerable<string> include)
    {
        var known = profile.Groups.Select(g => g.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var keepList = keep.ToList();
        var includeList = include.ToList();
        var unknown = keepList.Concat(includeList).Where(g => !known.Contains(g)).ToList();
        if (unknown.Count > 0)
        {
            throw new BootrixException(ErrorCode.InvalidSpec, "unknown group")
            {
                Arguments = [$"Unknown option group '{unknown[0]}' for {profile.Name}. Known groups: {string.Join(", ", profile.Groups.Select(g => g.Id))}."],
            };
        }

        var disabled = profile.Groups
            .Where(g => !g.Default && !includeList.Contains(g.Id, StringComparer.OrdinalIgnoreCase))
            .Select(g => g.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        disabled.UnionWith(keepList.Select(k => profile.Groups.First(g => g.Id.Equals(k, StringComparison.OrdinalIgnoreCase)).Id));
        return disabled;
    }

    /// <summary>Lists that are absent from the JSON arrive as null; later code can rely on empty lists instead.</summary>
    internal static TinyProfile Normalize(TinyProfile profile) => profile with
    {
        Groups = profile.Groups ?? [],
        Appx = profile.Appx ?? [],
        Packages = profile.Packages ?? [],
        Capabilities = profile.Capabilities ?? [],
        Files = profile.Files ?? [],
        Registry = profile.Registry ?? [],
        BootWimRegistry = profile.BootWimRegistry ?? [],
        ScheduledTasks = profile.ScheduledTasks ?? [],
    };

    /// <summary>A child profile adds to its parent; everything is additive, nothing in a parent can be undone by a child.</summary>
    internal static TinyProfile Merge(TinyProfile parent, TinyProfile child) => child with
    {
        WindowsFamily = child.WindowsFamily,
        Groups = [.. parent.Groups, .. child.Groups.Where(g => parent.Groups.All(p => p.Id != g.Id))],
        Appx = [.. parent.Appx, .. child.Appx.Where(a => parent.Appx.All(p => !p.Name.Equals(a.Name, StringComparison.OrdinalIgnoreCase)))],
        Packages = [.. parent.Packages, .. child.Packages],
        Capabilities = [.. parent.Capabilities, .. child.Capabilities],
        Files = [.. parent.Files, .. child.Files],
        Registry = [.. parent.Registry, .. child.Registry],
        BootWimRegistry = [.. parent.BootWimRegistry, .. child.BootWimRegistry.Where(c => !parent.BootWimRegistry.Contains(c))],
        ScheduledTasks = [.. parent.ScheduledTasks, .. child.ScheduledTasks.Where(t => !parent.ScheduledTasks.Contains(t, StringComparer.OrdinalIgnoreCase))],
        WinSxs = child.WinSxs ?? parent.WinSxs,
        EmptyWinRe = child.EmptyWinRe || parent.EmptyWinRe,
        BreaksServicing = child.BreaksServicing || parent.BreaksServicing,
    };
}
