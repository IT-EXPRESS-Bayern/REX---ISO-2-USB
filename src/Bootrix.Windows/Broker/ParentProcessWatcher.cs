// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Bootrix.Windows.Broker;

/// <summary>
/// The broker belongs to the GUI that started it and must not outlive it. A process id can be reused, so the
/// process is only watched if it runs the same program file as the broker.
/// </summary>
internal static class ParentProcessWatcher
{
    /// <summary>
    /// Completes when the parent has ended, or at once when there is no such parent: a
    /// process that is gone, or one that runs some other program than ours.
    /// </summary>
    public static async Task WaitForExitAsync(
        int processId,
        string ownImagePath,
        Func<int, string?> imagePathOf,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(imagePathOf(processId), ownImagePath, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Process {ProcessId} is not the Bootrix that started this broker", processId);
            return;
        }

        Process parent;
        try
        {
            parent = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            logger.LogWarning("The process {ProcessId} that started this broker is gone", processId);
            return;
        }

        using (parent)
        {
            await parent.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }

        logger.LogInformation("The process {ProcessId} that started this broker ended", processId);
    }
}
