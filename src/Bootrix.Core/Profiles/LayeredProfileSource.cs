// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Profiles;

/// <summary>Several profile folders as one source; for a name that exists in more than one, the first source wins.</summary>
public sealed class LayeredProfileSource(params IProfileSource[] sources) : IProfileSource
{
    public ProfileFile? Load(string name) => sources.Select(source => source.Load(name)).FirstOrDefault(profile => profile is not null);

    public IEnumerable<string> List() => sources.SelectMany(source => source.List()).Distinct(StringComparer.OrdinalIgnoreCase);
}
