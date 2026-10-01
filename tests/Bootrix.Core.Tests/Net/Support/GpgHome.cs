// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Text;

namespace Bootrix.Core.Tests.Net.Support;

/// <summary>
/// A throw-away GnuPG home directory. gpg is the independent reference implementation: it creates the keys and
/// signatures that Bootrix's managed verifier has to accept, and the tampered variants it has to reject.
/// </summary>
public sealed class GpgHome : IDisposable
{
    public GpgHome()
    {
        // Kept short: gpg puts its agent socket into this directory and socket paths are limited to about 100 characters.
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "bxg" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public static bool Available { get; } = ProbeGpg();

    public string Path { get; }

    /// <summary>Creates a key without passphrase and returns its fingerprint.</summary>
    public string GenerateKey(string name, string algorithm = "rsa2048", string expires = "never")
    {
        Run(["--quick-generate-key", $"{name} <{name.Replace(' ', '.').ToLowerInvariant()}@example.org>", algorithm, "sign", expires]);
        return FingerprintOf(name);
    }

    public string AddSigningSubkey(string fingerprint, string algorithm = "rsa2048")
    {
        Run(["--quick-add-key", fingerprint, algorithm, "sign", "never"]);
        var listing = Encoding.UTF8.GetString(Run(["--list-keys", "--with-colons", "--with-subkey-fingerprints", fingerprint]));
        return listing.Split('\n').Where(l => l.StartsWith("fpr:", StringComparison.Ordinal)).Select(l => l.Split(':')[9]).Last();
    }

    public byte[] ExportPublic(string fingerprint, bool armor = true) =>
        Run(armor ? ["--armor", "--export", fingerprint] : ["--export", fingerprint]);

    public byte[] DetachSign(byte[] data, string signer, bool armor = true, string? digest = null)
    {
        var args = new List<string> { "--local-user", signer, "--detach-sign" };
        if (armor)
        {
            args.Insert(0, "--armor");
        }

        if (digest is not null)
        {
            args.InsertRange(0, ["--digest-algo", digest]);
        }

        return Run(args, data);
    }

    public byte[] ClearSign(string text, string signer) =>
        Run(["--local-user", signer, "--clearsign"], Encoding.UTF8.GetBytes(text));

    /// <summary>Exports the key together with the revocation certificate gpg stored when the key was created.</summary>
    public byte[] ExportRevoked(string fingerprint)
    {
        var certificate = File.ReadAllText(System.IO.Path.Combine(Path, "openpgp-revocs.d", fingerprint + ".rev"));
        // gpg disables the certificate with a colon in front of the armor line, so that it is not imported by accident.
        Run(["--import"], Encoding.UTF8.GetBytes(certificate.Replace(":-----BEGIN", "-----BEGIN", StringComparison.Ordinal)));
        return ExportPublic(fingerprint);
    }

    public void Dispose()
    {
        try
        {
            RunTool("gpgconf", ["--homedir", Path, "--kill", "all"], null);
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // The temporary directory is cleaned by the OS eventually; a leftover agent must not fail a passed test.
        }
    }

    private string FingerprintOf(string name)
    {
        var listing = Encoding.UTF8.GetString(Run(["--list-keys", "--with-colons", name]));
        return listing.Split('\n').First(l => l.StartsWith("fpr:", StringComparison.Ordinal)).Split(':')[9];
    }

    private byte[] Run(IReadOnlyList<string> arguments, byte[]? input = null) =>
        RunTool("gpg", ["--homedir", Path, "--batch", "--yes", "--pinentry-mode", "loopback", "--passphrase", string.Empty, .. arguments], input);

    private static byte[] RunTool(string program, IEnumerable<string> arguments, byte[]? input)
    {
        var start = new ProcessStartInfo(program)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException(program + " did not start");
        var error = process.StandardError.ReadToEndAsync();

        using var output = new MemoryStream();
        var copy = process.StandardOutput.BaseStream.CopyToAsync(output);
        if (input is not null)
        {
            process.StandardInput.BaseStream.Write(input);
        }

        process.StandardInput.Close();
        copy.GetAwaiter().GetResult();
        process.WaitForExit();

        return process.ExitCode == 0
            ? output.ToArray()
            : throw new InvalidOperationException($"{program} failed with {process.ExitCode}: {error.GetAwaiter().GetResult()}");
    }

    private static bool ProbeGpg()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("gpg", "--version") { RedirectStandardOutput = true, RedirectStandardError = true });
            process?.WaitForExit();
            return process?.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

/// <summary>A test that needs gpg on the PATH; it is skipped, not failed, where gpg is missing (for example on a Windows runner).</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class GpgFactAttribute : FactAttribute
{
    public GpgFactAttribute()
    {
        if (!GpgHome.Available)
        {
            Skip = "gpg is not installed.";
        }
    }
}
