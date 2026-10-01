// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Windows.Tiny;

/// <summary>
/// For a process that already runs as the user it works for (the administrator's command line): the files are
/// reached directly, there is no one to impersonate.
/// </summary>
public sealed class DirectUserFiles : IUserFiles
{
    public Task CopyInAsync(string userPath, string localPath, Action<long, long> progress, CancellationToken cancellationToken) =>
        CopyAsync(userPath, localPath, progress, cancellationToken);

    public Task CopyOutAsync(string localPath, string userPath, Action<long, long> progress, CancellationToken cancellationToken) =>
        CopyAsync(localPath, userPath, progress, cancellationToken);

    private static Task CopyAsync(string from, string to, Action<long, long> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Copy(from, to, overwrite: true);
        var length = new FileInfo(to).Length;
        progress(length, length);
        return Task.CompletedTask;
    }
}
