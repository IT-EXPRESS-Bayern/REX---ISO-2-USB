// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Ipc;

/// <summary>Hands out one connected stream at a time to whoever asks for the next client.</summary>
public interface IConnectionListener : IAsyncDisposable
{
    Task<Stream> AcceptAsync(CancellationToken cancellationToken);
}
