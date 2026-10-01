// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Windows.Tiny;

/// <summary>
/// Moves files between the places of the user who asked for a job and the engine's own work area. The elevated
/// engine never opens a path from a request with its own rights; whoever implements this does it as that user.
/// </summary>
public interface IUserFiles
{
    /// <summary>Copies a file of the user into the work area. Fails when the user could not read it.</summary>
    Task CopyInAsync(string userPath, string localPath, Action<long, long> progress, CancellationToken cancellationToken);

    /// <summary>Copies a file from the work area to a place of the user. Fails when the user could not write there; a half written file is removed.</summary>
    Task CopyOutAsync(string localPath, string userPath, Action<long, long> progress, CancellationToken cancellationToken);
}
