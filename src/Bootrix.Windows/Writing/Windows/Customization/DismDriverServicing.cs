// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Writing.Windows.Customization;
using Bootrix.Windows.Dism;
using MsDism = Microsoft.Dism;

namespace Bootrix.Windows.Writing.Windows.Customization;

/// <summary>Adds drivers to a mounted image through the DISM API, one INF at a time.</summary>
public sealed class DismDriverServicing : IDriverServicing
{
    public Task<DriverInjectionResult> AddDriversAsync(
        string mountDirectory,
        IReadOnlyList<string> infFiles,
        bool forceUnsigned,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        DismImageServicing.EnsureInitialised();
        return Task.Run(
            () =>
            {
                using var session = OpenSession(mountDirectory);
                var failed = new List<DriverFailure>();
                var added = 0;
                for (var i = 0; i < infFiles.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        MsDism.DismApi.AddDriver(session, infFiles[i], forceUnsigned);
                        added++;
                    }
                    catch (MsDism.DismRebootRequiredException)
                    {
                        // An offline image has nothing to restart; the driver is in the store.
                        added++;
                    }
                    catch (MsDism.DismException ex)
                    {
                        failed.Add(new DriverFailure(infFiles[i], $"0x{ex.ErrorCode:X8} {ex.Message}"));
                    }

                    progress?.Report((i + 1) / (double)infFiles.Count);
                }

                return new DriverInjectionResult(added, failed);
            },
            cancellationToken);
    }

    private static MsDism.DismSession OpenSession(string mountDirectory)
    {
        try
        {
            return MsDism.DismApi.OpenOfflineSession(mountDirectory);
        }
        catch (MsDism.DismException ex)
        {
            throw new BootrixException(ErrorCode.ExternalToolFailed, ex.Message, ex)
            {
                Arguments = ["DISM", $"0x{ex.ErrorCode:X8} {ex.Message}"],
            };
        }
    }
}
