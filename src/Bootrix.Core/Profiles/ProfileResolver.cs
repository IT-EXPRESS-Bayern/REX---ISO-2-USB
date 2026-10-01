// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Nodes;
using Bootrix.Core.Errors;
using Bootrix.Core.Json;

namespace Bootrix.Core.Profiles;

public interface IProfileSource
{
    ProfileFile? Load(string name);

    IEnumerable<string> List();
}

public sealed class DirectoryProfileSource(string directory) : IProfileSource
{
    public const string Extension = ".bootrixprofile.json";

    public ProfileFile? Load(string name)
    {
        var path = Path.Combine(directory, name + Extension);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ProfileFile>(File.ReadAllText(path), CoreJson.Options);
        }
        catch (JsonException ex)
        {
            throw new BootrixException(ErrorCode.InvalidSpec, path, ex) { Arguments = [ex.Message] };
        }
    }

    public IEnumerable<string> List()
    {
        if (!Directory.Exists(directory))
        {
            yield break;
        }

        foreach (var file in Directory.EnumerateFiles(directory, "*" + Extension))
        {
            var name = Path.GetFileName(file);
            yield return name[..^Extension.Length];
        }
    }
}

public sealed record ResolvedProfile(string Name, JobSpec Spec, IReadOnlyList<string> LockedPaths);

public sealed class ProfileResolver(IProfileSource source)
{
    public ResolvedProfile Resolve(string name)
    {
        var chain = BuildChain(name);

        var document = JobSpecJson.ToNode(new JobSpec());
        var locked = new List<string>();

        foreach (var profile in chain)
        {
            if (profile.Spec is not null)
            {
                foreach (var changed in MergePatch.ChangedPaths(profile.Spec))
                {
                    var violated = locked.FirstOrDefault(l => Overlaps(l, changed));
                    if (violated is not null)
                    {
                        throw new BootrixException(ErrorCode.ProfileLocked, $"{profile.Name}: {changed}")
                        {
                            Arguments = [violated],
                        };
                    }
                }

                document = MergePatch.Apply(document, profile.Spec.DeepClone())!.AsObject();
            }

            locked.AddRange(profile.Locked);
        }

        document["name"] = name;
        return new ResolvedProfile(name, JobSpecJson.FromNode(document), locked);
    }

    private List<ProfileFile> BuildChain(string name)
    {
        var chain = new List<ProfileFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? current = name;

        while (current is not null)
        {
            if (!seen.Add(current))
            {
                throw new BootrixException(ErrorCode.ProfileCycle, string.Join(" -> ", seen))
                {
                    Arguments = [string.Join(" -> ", seen.Append(current))],
                };
            }

            var profile = source.Load(current)
                ?? throw new BootrixException(ErrorCode.InvalidSpec, $"profile '{current}' not found")
                {
                    Arguments = [$"profile '{current}' not found"],
                };

            if (profile.SchemaVersion > JobSpec.CurrentSchemaVersion)
            {
                throw new BootrixException(ErrorCode.UnsupportedSchemaVersion, current)
                {
                    Arguments = [profile.SchemaVersion],
                };
            }

            chain.Add(profile with { Name = current });
            current = profile.Extends;
        }

        chain.Reverse();
        return chain;
    }

    private static bool Overlaps(string lockedPath, string changedPath) =>
        string.Equals(lockedPath, changedPath, StringComparison.OrdinalIgnoreCase)
        || changedPath.StartsWith(lockedPath + ".", StringComparison.OrdinalIgnoreCase)
        || lockedPath.StartsWith(changedPath + ".", StringComparison.OrdinalIgnoreCase);
}
