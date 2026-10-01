// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Windows.Interop;

namespace Bootrix.Windows.Broker;

/// <summary>
/// Decides whether the process on the other end of the pipe is the one that started the broker.
/// The ACL already keeps other users out; this keeps other programs of the same user out: the
/// client has to be the very process named on the command line, and it has to run the same program
/// file as the broker.
/// </summary>
internal sealed class ClientProcessVerifier
{
    private readonly string _ownImagePath;
    private readonly int _expectedProcessId;
    private readonly Func<int, string?> _imagePathOf;

    public ClientProcessVerifier(string ownImagePath, int expectedProcessId, Func<int, string?> imagePathOf)
    {
        _ownImagePath = ownImagePath;
        _expectedProcessId = expectedProcessId;
        _imagePathOf = imagePathOf;
    }

    /// <summary>The verifier for the running broker: its own program file, read the same way as the client's.</summary>
    public static ClientProcessVerifier ForCurrentProcess(int expectedProcessId) => new(
        ProcessApi.GetImagePath(Environment.ProcessId) ?? throw new InvalidOperationException("The image path of the broker is unknown."),
        expectedProcessId,
        ProcessApi.GetImagePath);

    public bool IsTrusted(int clientProcessId) =>
        clientProcessId == _expectedProcessId
        && _imagePathOf(clientProcessId) is { } path
        && string.Equals(path, _ownImagePath, StringComparison.OrdinalIgnoreCase);
}
