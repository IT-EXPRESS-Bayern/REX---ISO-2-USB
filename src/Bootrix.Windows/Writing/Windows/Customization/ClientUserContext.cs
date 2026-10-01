// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Writing.Windows.Customization;
using Bootrix.Windows.Broker;

namespace Bootrix.Windows.Writing.Windows.Customization;

/// <summary>Reads the user's files with the rights of the broker's client, the same way images are opened.</summary>
internal sealed class ClientUserContext(IClientImpersonator impersonator) : IUserContext
{
    public Task<T> RunAsync<T>(Func<Task<T>> action) => impersonator.RunAsClientAsync(action);
}
