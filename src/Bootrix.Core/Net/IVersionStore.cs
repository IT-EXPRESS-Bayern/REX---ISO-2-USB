// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Net;

/// <summary>
/// Remembers the highest manifest version seen per channel, which is what makes a rollback to older, validly signed
/// data detectable. Where it is kept decides who can reset it: a file next to the settings protects against a
/// stale server, a machine-wide location also against a user who restores an old profile.
/// </summary>
public interface IVersionStore
{
    /// <summary>Highest version recorded for the channel, 0 if none.</summary>
    long GetHighestVersion(string channel);

    /// <summary>Records <paramref name="version"/> if it is higher than the stored one; never lowers the value.</summary>
    void Record(string channel, long version);
}
