// SPDX-License-Identifier: GPL-3.0-or-later
using System.IO.Pipes;
using System.Security.AccessControl;
using Bootrix.Core.Ipc;
using Bootrix.Windows.Interop;
using Bootrix.Windows.Platform;
using Microsoft.Extensions.Logging;

namespace Bootrix.Windows.Broker;

/// <summary>
/// The broker's pipe. It is created with an ACL that lets in only the user who started the broker (and
/// SYSTEM), the first instance has to be created by us so a name that is already taken fails loudly, and every client that
/// connects is checked against the process that launched the broker before anything is read from it.
/// PipeOptions.CurrentUserOnly would be wrong here: it compares with the account of the broker, which is
/// another one when an administrator password was entered in the UAC prompt.
/// </summary>
internal sealed class SecurePipeListener : NamedPipeConnectionListener
{
    private readonly string _userSid;
    private readonly string _brokerSid;
    private readonly ClientProcessVerifier _verifier;
    private readonly ILogger _logger;
    private volatile NamedPipeServerStream? _current;

    public SecurePipeListener(string pipeName, string userSid, ClientProcessVerifier verifier, ILogger logger)
        : base(pipeName, maxClients: 1, logger)
    {
        _userSid = userSid;
        _brokerSid = ProcessElevation.CurrentUserSid();
        _verifier = verifier;
        _logger = logger;
    }

    /// <summary>The pipe of the client that was accepted last, which is the only one there can be.</summary>
    public NamedPipeServerStream? CurrentPipe => _current;

    protected override NamedPipeServerStream CreateServer(bool isFirst)
    {
        var security = new PipeSecurity();
        security.SetSecurityDescriptorSddlForm(BrokerAcl.PipeSddl(_userSid, _brokerSid), AccessControlSections.Access);

        var options = PipeOptions.Asynchronous | (isFirst ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, MaxClients + 1, PipeTransmissionMode.Byte, options, 0, 0, security);
    }

    protected override bool Authorize(NamedPipeServerStream pipe)
    {
        if (!ProcessApi.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientProcessId))
        {
            _logger.LogWarning("The process of a pipe client could not be determined");
            return false;
        }

        if (!_verifier.IsTrusted((int)clientProcessId))
        {
            _logger.LogWarning("The process {ProcessId} that connected is not the one that started the broker", clientProcessId);
            return false;
        }

        _current = pipe;
        return true;
    }
}
