// SPDX-License-Identifier: GPL-3.0-or-later
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Bootrix.Windows.Broker;

/// <summary>
/// The secret travels from the GUI to the broker in a file. It cannot go on the command line, which
/// other processes can read, and an elevated process started through the shell does not inherit
/// handles or pipes. The file is created with a protected ACL (only the user, SYSTEM and Administrators),
/// the broker reads it once and deletes it, and the GUI deletes it too when the broker never came.
/// </summary>
internal sealed partial class BrokerSecretFile : IDisposable
{
    public const int SecretBytes = 32;

    private BrokerSecretFile(string path) => FilePath = path;

    public string FilePath { get; }

    /// <summary>Writes the secret to a new file with the restricted ACL, already in place when the file appears.</summary>
    public static BrokerSecretFile Create(ReadOnlySpan<byte> secret, string userSid, string? directory = null)
    {
        if (secret.Length != SecretBytes)
        {
            throw new ArgumentException($"The secret has to be {SecretBytes} bytes.", nameof(secret));
        }

        var name = $"bootrix-broker-{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16))}.key";
        var path = Path.Combine(directory ?? Path.GetTempPath(), name);

        var security = new FileSecurity();
        security.SetSecurityDescriptorSddlForm(BrokerAcl.SecretFileSddl(userSid), AccessControlSections.Access);
        using (var stream = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, security))
        {
            stream.Write(secret);
        }

        return new BrokerSecretFile(path);
    }

    /// <summary>
    /// Whether a path from the command line may be read and deleted by the broker: absolute, no
    /// relative parts, and exactly the file name the GUI generates. Anything else is somebody trying to make an elevated
    /// process delete a file of their choice.
    /// </summary>
    public static bool IsAcceptablePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 32_767 || path.Contains('\0'))
        {
            return false;
        }

        var driveRooted = path.Length > 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/';
        if (!driveRooted)
        {
            return false;
        }

        var parts = path[3..].Split('\\', '/');
        return parts.All(p => p.Length > 0 && p is not ("." or ".."))
            && FileNamePattern().IsMatch(parts[^1]);
    }

    /// <summary>Reads the secret and removes the file. The file is deleted even when its content is wrong.</summary>
    public static byte[] ReadAndDelete(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            if (stream.Length != SecretBytes)
            {
                throw new InvalidDataException("The secret file has the wrong size.");
            }

            var secret = new byte[SecretBytes];
            stream.ReadExactly(secret);
            return secret;
        }
        finally
        {
            TryDelete(path);
        }
    }

    public void Dispose() => TryDelete(FilePath);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Another cleanup (the broker's or ours) is still holding it; whichever comes last removes it.
        }
    }

    [GeneratedRegex(@"^bootrix-broker-[0-9a-f]{32}\.key\z", RegexOptions.CultureInvariant)]
    private static partial Regex FileNamePattern();
}
