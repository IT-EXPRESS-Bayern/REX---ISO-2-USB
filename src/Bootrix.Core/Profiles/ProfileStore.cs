// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bootrix.Core.Errors;
using Bootrix.Core.Json;

namespace Bootrix.Core.Profiles;

public enum ProfileOrigin
{
    /// <summary>In the user's own profile folder; can be saved over and deleted.</summary>
    User,

    /// <summary>In the shared team folder (a network share, for instance); read only here.</summary>
    Team,
}

public sealed record ProfileEntry(string Name, ProfileOrigin Origin, string? Description, bool HasLocks);

/// <summary>
/// The profiles a person can pick from: their own, and those of a shared team folder. A profile with the same
/// name in both places is the personal one, so someone can adjust a team profile without touching the original.
/// </summary>
public sealed class ProfileStore(string userDirectory, Func<string?> teamDirectory)
{
    private const int MaxNameLength = 64;

    // The Windows set, whatever system this runs on: a profile folder on a share is read by Windows machines.
    private static readonly System.Buffers.SearchValues<char> ForbiddenInNames = System.Buffers.SearchValues.Create("\\/:*?\"<>|");

    private DirectoryProfileSource User => new(userDirectory);

    private IProfileSource Layered => teamDirectory() is { Length: > 0 } team
        ? new LayeredProfileSource(User, new DirectoryProfileSource(team))
        : User;

    public IReadOnlyList<ProfileEntry> List()
    {
        var entries = new List<ProfileEntry>();
        var user = User;
        foreach (var name in user.List())
        {
            entries.Add(Describe(name, ProfileOrigin.User, SafeLoad(user, name)));
        }

        if (teamDirectory() is { Length: > 0 } team)
        {
            var shared = new DirectoryProfileSource(team);
            foreach (var name in shared.List().Where(n => !entries.Any(e => string.Equals(e.Name, n, StringComparison.OrdinalIgnoreCase))))
            {
                entries.Add(Describe(name, ProfileOrigin.Team, SafeLoad(shared, name)));
            }
        }

        return [.. entries.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    public ResolvedProfile Resolve(string name) => new ProfileResolver(Layered).Resolve(name);

    /// <summary>Stores the settings under a name in the user's folder. The image itself is not part of a profile.</summary>
    public void Save(string name, JobSpec spec, string? description = null)
    {
        name = ValidateName(name);
        var node = JobSpecJson.ToNode(spec with { Source = null, Name = "" });
        node.Remove("name");
        node.Remove("source");

        var profile = new ProfileFile { Name = name, Description = description, Spec = node };
        Directory.CreateDirectory(userDirectory);

        var path = PathOf(name);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(profile, CoreJson.Options));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Deletes one of the user's own profiles; team profiles are left alone.</summary>
    public bool Delete(string name)
    {
        var path = PathOf(ValidateName(name));
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    public static string ValidateName(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length is 0 or > MaxNameLength || trimmed.AsSpan().IndexOfAny(ForbiddenInNames) >= 0 || trimmed.Any(char.IsControl) || trimmed.EndsWith('.'))
        {
            throw new BootrixException(ErrorCode.InvalidSpec, "profile name") { Arguments = [$"A profile name has 1 to {MaxNameLength} characters and no \\ / : * ? \" < > |."] };
        }

        return trimmed;
    }

    private string PathOf(string name) => Path.Combine(userDirectory, name + DirectoryProfileSource.Extension);

    private static ProfileFile? SafeLoad(DirectoryProfileSource source, string name)
    {
        try
        {
            return source.Load(name);
        }
        catch (BootrixException)
        {
            // A file that cannot be read is still listed, so that it can be found and removed.
            return null;
        }
    }

    private static ProfileEntry Describe(string name, ProfileOrigin origin, ProfileFile? file) =>
        new(name, origin, file?.Description, file?.Locked is { Count: > 0 });
}
