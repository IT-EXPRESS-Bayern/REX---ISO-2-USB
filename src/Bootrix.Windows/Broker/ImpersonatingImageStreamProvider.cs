// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Windows.Jobs;

namespace Bootrix.Windows.Broker;

/// <summary>Runs code with the rights of the client on the other end of the broker pipe.</summary>
public interface IClientImpersonator
{
    /// <summary>
    /// Runs <paramref name="action"/>, including everything it awaits, as the connected client.
    /// Throws when no client is connected or the client cannot be impersonated: there is no fallback to the broker's own rights.
    /// </summary>
    Task<T> RunAsClientAsync<T>(Func<Task<T>> action);
}

/// <summary>
/// Opens images as the user who asked for them. The broker is elevated, so an image path from the GUI
/// opened with the broker's own token would let a standard user read any file on the machine; as the
/// client, the path only opens when the client could have opened it itself. Reading continues with the open stream, the
/// access check is done by then.
/// </summary>
public sealed class ImpersonatingImageStreamProvider(IImageStreamProvider inner, IClientImpersonator impersonator) : IImageStreamProvider
{
    public Task<OpenedImage> OpenAsync(string path, CancellationToken cancellationToken) =>
        impersonator.RunAsClientAsync(() => inner.OpenAsync(path, cancellationToken));

    public Task<OpenedImage> OpenAsync(string path, ImageOpenOptions options, CancellationToken cancellationToken) =>
        impersonator.RunAsClientAsync(() => inner.OpenAsync(path, options, cancellationToken));

    public Task<OpenedImage> OpenForInspectionAsync(string path, ImageOpenOptions options, CancellationToken cancellationToken) =>
        impersonator.RunAsClientAsync(() => inner.OpenForInspectionAsync(path, options, cancellationToken));
}
