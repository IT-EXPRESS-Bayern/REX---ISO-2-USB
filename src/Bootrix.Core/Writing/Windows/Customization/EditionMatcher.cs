// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Images.Wim;

namespace Bootrix.Core.Writing.Windows.Customization;

internal static class EditionMatcher
{
    /// <summary>
    /// Finds the edition a job names, by image name first ("Windows 11 Pro"), then by edition id ("Professional")
    /// and display name. The comparison ignores case, because users type what they see.
    /// </summary>
    public static WimEdition? Find(IReadOnlyList<WimEdition> editions, string wanted)
    {
        var name = wanted.Trim();
        return editions.FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? editions.FirstOrDefault(e => string.Equals(e.EditionId, name, StringComparison.OrdinalIgnoreCase))
            ?? editions.FirstOrDefault(e => string.Equals(e.DisplayName, name, StringComparison.OrdinalIgnoreCase));
    }
}
