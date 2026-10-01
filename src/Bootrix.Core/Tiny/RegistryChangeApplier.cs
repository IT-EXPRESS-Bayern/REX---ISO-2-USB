// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Tiny;

/// <summary>Writes a list of registry changes into the hives of a mounted image.</summary>
public static class RegistryChangeApplier
{
    /// <summary>
    /// Changes are grouped by hive so that each hive is loaded once; a hive is unloaded again before the next one
    /// is touched, also when a change fails halfway.
    /// </summary>
    public static void Apply(IImageFileSystem files, string mountDirectory, IEnumerable<RegistryChange> changes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(changes);

        foreach (var group in changes.GroupBy(c => c.Hive))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var hive = files.LoadHive(mountDirectory, group.Key);
            foreach (var change in group)
            {
                switch (change.Action)
                {
                    case RegistryAction.SetValue:
                        hive.SetValue(change.Key, change.Name!, change.Kind, change.Value!);
                        break;
                    case RegistryAction.DeleteKey:
                        hive.DeleteKey(change.Key);
                        break;
                    case RegistryAction.DeleteValue:
                        hive.DeleteValue(change.Key, change.Name!);
                        break;
                }
            }
        }
    }
}
