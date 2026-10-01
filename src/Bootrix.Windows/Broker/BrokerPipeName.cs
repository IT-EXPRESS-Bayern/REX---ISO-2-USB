// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Bootrix.Windows.Broker;

/// <summary>
/// Names of the broker pipe: bootrix-broker-{user SID}-{128 random bits as hex}. The random part is
/// chosen by the GUI for every start, so another process cannot guess it and take the name before the broker does.
/// </summary>
internal static partial class BrokerPipeName
{
    public const string Prefix = "bootrix-broker-";

    private const int RandomBytes = 16;

    public static string Create(string userSid) => Create(userSid, RandomNumberGenerator.GetBytes(RandomBytes));

    public static string Create(string userSid, ReadOnlySpan<byte> random)
    {
        if (!BrokerAcl.IsSid(userSid))
        {
            throw new ArgumentException("Not a SID.", nameof(userSid));
        }

        if (random.Length != RandomBytes)
        {
            throw new ArgumentException($"The random part has to be {RandomBytes} bytes.", nameof(random));
        }

        return $"{Prefix}{userSid}-{Convert.ToHexStringLower(random)}";
    }

    /// <summary>True for a name of the form above; <paramref name="userSid"/> is the SID inside it.</summary>
    public static bool TryParse(string? name, out string userSid)
    {
        var match = name is null ? null : NamePattern().Match(name);
        if (match is { Success: true })
        {
            userSid = match.Groups["sid"].Value;
            return true;
        }

        userSid = "";
        return false;
    }

    /// <summary>The path other programs see: .NET takes only the part after \\.\pipe\.</summary>
    public static string FullPath(string name) => @"\\.\pipe\" + name;

    [GeneratedRegex(@"^bootrix-broker-(?<sid>S-1-\d{1,10}(?:-\d{1,10}){1,15})-(?<random>[0-9a-f]{32})\z", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}
