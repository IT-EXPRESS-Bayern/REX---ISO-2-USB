// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json.Nodes;

namespace Bootrix.Core.Profiles;

/// <summary>
/// A team profile can lock settings ("windows.bypassTpm"). Whatever the form of a person says, the locked
/// settings are taken from the profile in the end, so the lock does not depend on what the window offers.
/// </summary>
public static class ProfileLocks
{
    public static JobSpec Enforce(JobSpec spec, ResolvedProfile profile)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.LockedPaths.Count == 0)
        {
            return spec;
        }

        var node = JobSpecJson.ToNode(spec);
        var reference = JobSpecJson.ToNode(profile.Spec);
        foreach (var path in profile.LockedPaths)
        {
            Copy(reference, node, path.Split('.'));
        }

        return JobSpecJson.FromNode(node);
    }

    private static void Copy(JsonObject from, JsonObject to, string[] path)
    {
        var source = from;
        var target = to;
        for (var i = 0; i < path.Length - 1; i++)
        {
            if (source[Key(source, path[i])] is not JsonObject nextSource)
            {
                // The profile has nothing there, which means the default; the whole branch of the person's form is dropped.
                to.Remove(Key(to, path[0]));
                return;
            }

            if (target[Key(target, path[i])] is not JsonObject nextTarget)
            {
                nextTarget = [];
                target[path[i]] = nextTarget;
            }

            source = nextSource;
            target = nextTarget;
        }

        var last = path[^1];
        var value = source[Key(source, last)];
        target.Remove(Key(target, last));
        if (value is not null)
        {
            target[Key(source, last)] = value.DeepClone();
        }
    }

    private static string Key(JsonObject node, string name) =>
        node.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) ?? name;
}
