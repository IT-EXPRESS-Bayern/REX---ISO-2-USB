// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Tiny;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Bootrix.Core.Writing.Windows.Customization;

/// <summary>
/// Applies registry changes to the Windows Setup image (boot.wim) of a medium. Windows Setup reads
/// HKLM\SYSTEM of that image when it starts, so values set here are in effect before Setup looks at any answer file.
/// </summary>
public sealed class BootImagePatcher(StagedImageEditor editor, IImageFileSystem files, ILogger? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger.Instance;

    /// <returns>False when there was nothing to patch: no boot.wim, or no Setup image in it.</returns>
    public async Task<bool> PatchAsync(
        string bootImagePath,
        IReadOnlyList<RegistryChange> changes,
        string workDirectory,
        IProgress<double> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            return false;
        }

        if (!File.Exists(bootImagePath))
        {
            _logger.LogWarning("No {Image} on the medium; the Setup image is not patched", Path.GetFileName(bootImagePath));
            return false;
        }

        var index = BootImageIndex.FindSetup(bootImagePath);
        if (index is null)
        {
            // Unofficial images sometimes carry a single PE image only; patching the wrong one would change nothing Setup reads.
            _logger.LogWarning("{Image} has no Windows Setup image; it is not patched", Path.GetFileName(bootImagePath));
            return false;
        }

        await editor.EditAsync(
            bootImagePath,
            [index.Value],
            workDirectory,
            (_, mount, token) =>
            {
                RegistryChangeApplier.Apply(files, mount, changes, token);
                return Task.CompletedTask;
            },
            progress,
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Set {Count} value(s) in the registry of image {Index} of {Image}", changes.Count, index, Path.GetFileName(bootImagePath));
        return true;
    }
}
