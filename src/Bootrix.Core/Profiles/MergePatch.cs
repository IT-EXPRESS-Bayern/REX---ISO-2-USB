// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json.Nodes;

namespace Bootrix.Core.Profiles;

/// <summary>JSON Merge Patch as defined in RFC 7396: null removes a member, objects merge, everything else replaces.</summary>
public static class MergePatch
{
    public static JsonNode? Apply(JsonNode? target, JsonNode? patch)
    {
        if (patch is not JsonObject patchObject)
        {
            return patch?.DeepClone();
        }

        var result = target as JsonObject ?? [];
        foreach (var (name, value) in patchObject)
        {
            if (value is null)
            {
                result.Remove(name);
            }
            else
            {
                var merged = Apply(result[name], value);
                result.Remove(name);
                result.Add(name, merged);
            }
        }

        return result;
    }

    /// <summary>Returns every property path (dot separated) that the patch sets or removes.</summary>
    public static IEnumerable<string> ChangedPaths(JsonNode? patch, string prefix = "")
    {
        if (patch is not JsonObject obj)
        {
            if (prefix.Length > 0)
            {
                yield return prefix;
            }

            yield break;
        }

        foreach (var (name, value) in obj)
        {
            var path = prefix.Length == 0 ? name : prefix + "." + name;
            if (value is JsonObject)
            {
                foreach (var inner in ChangedPaths(value, path))
                {
                    yield return inner;
                }
            }
            else
            {
                yield return path;
            }
        }
    }
}
