// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using Bootrix.Core.Errors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Core.Writing.Windows.Customization;

public sealed record DriverFailure(string InfFile, string Reason);

public sealed record DriverInjectionResult(int Added, IReadOnlyList<DriverFailure> Failed);

/// <summary>Adds drivers to a mounted Windows image; implemented in the Windows layer on top of the DISM API.</summary>
public interface IDriverServicing
{
    /// <summary>
    /// Adds each INF on its own, so that one unsuitable driver (wrong architecture, no valid signature) does not
    /// stop the others and can be named in the log.
    /// </summary>
    Task<DriverInjectionResult> AddDriversAsync(
        string mountDirectory,
        IReadOnlyList<string> infFiles,
        bool forceUnsigned,
        IProgress<double>? progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// Puts drivers into the images of a medium: the Setup image so that WinPE has them before Setup starts, the
/// install image so that the installed system has them without relying on Setup's scheduling.
/// </summary>
public sealed class OfflineDriverInjector(StagedImageEditor editor, IDriverServicing drivers, ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <summary>Unsigned drivers are never forced in: Windows would refuse to load them on a machine with driver signature enforcement.</summary>
    public const bool ForceUnsigned = false;

    /// <returns>The number of driver packages added per image index, summed up.</returns>
    /// <exception cref="BootrixException">With <see cref="ErrorCode.DriverInjectionFailed"/> when an image accepted none of the drivers.</exception>
    public async Task<int> InjectAsync(
        OfflineInjectionTarget target,
        IReadOnlyList<string> infFiles,
        string workDirectory,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(infFiles);
        if (infFiles.Count == 0)
        {
            return 0;
        }

        var added = 0;
        await editor.EditAsync(
            target.ImagePath,
            target.Indexes,
            workDirectory,
            async (index, mount, token) =>
            {
                var result = await drivers.AddDriversAsync(mount, infFiles, ForceUnsigned, null, token).ConfigureAwait(false);
                foreach (var failure in result.Failed)
                {
                    _logger.LogWarning("Driver {Inf} was not added to image {Index} of {Image}: {Reason}", Path.GetFileName(failure.InfFile), index, target.Description, failure.Reason);
                }

                if (result.Added == 0)
                {
                    throw new BootrixException(ErrorCode.DriverInjectionFailed, $"no driver accepted by image {index} of {target.Description}")
                    {
                        Arguments = [infFiles.Count.ToString(CultureInfo.CurrentCulture), target.Description],
                    };
                }

                added += result.Added;
                _logger.LogInformation("Added {Added} of {Total} drivers to image {Index} of {Image}", result.Added, infFiles.Count, index, target.Description);
            },
            progress,
            cancellationToken).ConfigureAwait(false);

        return added;
    }
}
