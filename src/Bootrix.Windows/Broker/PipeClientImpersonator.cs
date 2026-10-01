// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Pipes;
using System.Security.Principal;
using Bootrix.Core.Errors;

namespace Bootrix.Windows.Broker;

/// <summary>
/// Impersonates the client of a named pipe. The client token is taken from the pipe at the moment of the call,
/// not kept: it is read by NamedPipeServerStream.RunAsClient, which needs the client to have sent data already
/// (the hello did that), and then handed to WindowsIdentity.RunImpersonatedAsync so that the impersonation also
/// holds across the awaits of an asynchronous open. The client has to connect with impersonation level
/// "Impersonation"; with a lower level this fails, and nothing is opened.
/// </summary>
internal sealed class PipeClientImpersonator(Func<NamedPipeServerStream?> currentPipe) : IClientImpersonator
{
    public async Task<T> RunAsClientAsync<T>(Func<Task<T>> action)
    {
        var pipe = currentPipe();
        if (pipe is not { IsConnected: true })
        {
            throw new BootrixException(ErrorCode.BrokerDisconnected, "no client is connected");
        }

        using var identity = CaptureClient(pipe);
        return await WindowsIdentity.RunImpersonatedAsync(identity.AccessToken, action).ConfigureAwait(false);
    }

    private static WindowsIdentity CaptureClient(NamedPipeServerStream pipe)
    {
        WindowsIdentity? identity = null;
        pipe.RunAsClient(() => identity = WindowsIdentity.GetCurrent(ifImpersonating: true));
        return identity ?? throw new InvalidOperationException("The pipe client could not be impersonated.");
    }
}
