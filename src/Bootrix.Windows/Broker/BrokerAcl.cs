// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;

namespace Bootrix.Windows.Broker;

/// <summary>
/// The access rules of the broker's pipe and of the file that carries the secret, as SDDL strings.
/// Building them is plain text work, so it can be checked anywhere; applying them is done by the
/// callers with the Windows security classes.
/// </summary>
internal static partial class BrokerAcl
{
    /// <summary>
    /// FILE_GENERIC_READ | FILE_GENERIC_WRITE, which is what a .NET pipe client asks for when it opens
    /// the pipe for reading and writing. A narrower mask would lock out the client itself.
    /// </summary>
    private const string ClientPipeAccess = "0x12019f";

    /// <summary>
    /// Pipe: the user who started the broker may read and write, SYSTEM and the broker's own account have full
    /// control. The broker's account needs it to create the next instance of the pipe, and it differs from the
    /// user's when somebody else's administrator password was entered in the UAC prompt. Nobody else is listed,
    /// and "P" stops any inheritance, so everyone else is denied.
    /// </summary>
    public static string PipeSddl(string userSid, string brokerSid)
    {
        RequireSid(userSid);
        RequireSid(brokerSid);

        var rules = $"(A;;FA;;;SY)(A;;FA;;;{brokerSid})";
        if (!string.Equals(userSid, brokerSid, StringComparison.OrdinalIgnoreCase))
        {
            rules += $"(A;;{ClientPipeAccess};;;{userSid})";
        }

        return "D:P" + rules;
    }

    /// <summary>The secret file: only the user, SYSTEM and the Administrators group (the elevated broker runs as one of them).</summary>
    public static string SecretFileSddl(string userSid)
    {
        RequireSid(userSid);
        return $"D:P(A;;FA;;;{userSid})(A;;FA;;;SY)(A;;FA;;;BA)";
    }

    public static bool IsSid(string? value) => value is not null && SidPattern().IsMatch(value);

    private static void RequireSid(string value)
    {
        if (!IsSid(value))
        {
            throw new ArgumentException("Not a SID.", nameof(value));
        }
    }

    [GeneratedRegex(@"^S-1-\d{1,10}(?:-\d{1,10}){1,15}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SidPattern();
}
