// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Bootrix.Core.Errors;
using Bootrix.Core.Net;
using Bootrix.Core.Tests.Net.Support;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.Net;

/// <summary>
/// The signing script in tools/manifest-signing is the independent implementation of the envelope format: it uses
/// openssl and shell only, so a manifest it signs proves that the verifier does not just agree with its own test signer.
/// </summary>
public class ManifestSigningToolTests
{
    // Public half of a key generated once for the committed test vector; its private half was discarded.
    private const string VectorPublicKey =
        "MHYwEAYHKoZIzj0CAQYFK4EEACIDYgAE9iQYGbR+RFqP+3x1cfWfAUiANWQNUpLIAVFiKGzFbXMkS1CdlSqL4eGnlABofDTE6WPEP+epylagqBrJeD89+UceTJ+bct3gQN1CVXchRelw0YKmTdxGcHsWVn0sBg0j";

    private static string? FindRepositoryFile(string relative) => SigningToolFactAttribute.FindRepositoryFile(relative);

    private static string Run(string program, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return process.ExitCode == 0 ? output : throw new InvalidOperationException($"{program} failed: {error.GetAwaiter().GetResult()}");
    }

    [Fact]
    public void CommittedTestVectorVerifiesAtItsIssueTime()
    {
        var verifier = new SignedManifestVerifier(
            [ManifestPublicKey.FromBase64("test-2026", VectorPublicKey)],
            new InMemoryVersionStore(),
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero)));

        var manifest = verifier.Verify(NetFixtures.Bytes("manifest/vector-envelope.json"), "catalog");

        Assert.Equal(17, manifest.Version);
        Assert.Equal("test-2026", manifest.SignedByKeyId);
        Assert.Equal("linux.json", Assert.Single(manifest.Artifacts).Name);
        Assert.Equal("4.0.0", manifest.ReadPayload<Dictionary<string, System.Text.Json.JsonElement>>()!["minimumAppVersion"].GetString());
    }

    [Fact]
    public void CommittedTestVectorIsRejectedAfterItExpires()
    {
        var verifier = new SignedManifestVerifier(
            [ManifestPublicKey.FromBase64("test-2026", VectorPublicKey)],
            new InMemoryVersionStore(),
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero)));

        var ex = Assert.Throws<BootrixException>(() => verifier.Verify(NetFixtures.Bytes("manifest/vector-envelope.json"), "catalog"));

        Assert.Equal(ErrorCode.SignatureInvalid, ex.Code);
    }

    [Fact]
    public void EnvelopeEmbedsTheManifestFileByteForByte()
    {
        using var document = System.Text.Json.JsonDocument.Parse(NetFixtures.Bytes("manifest/vector-envelope.json"));

        var embedded = Convert.FromBase64String(document.RootElement.GetProperty("signed").GetString()!);

        Assert.Equal(NetFixtures.Bytes("manifest/vector-manifest.json"), embedded);
    }

    [SigningToolFact]
    public void ManifestSignedByTheOpensslScriptVerifies()
    {
        using var dir = new TempDirectory();
        var script = FindRepositoryFile("tools/manifest-signing/sign-manifest.sh")!;
        Run("openssl", ["ecparam", "-name", "secp384r1", "-genkey", "-noout", "-out", dir.File("key.pem")]);
        Run("openssl", ["ec", "-in", dir.File("key.pem"), "-pubout", "-outform", "DER", "-out", dir.File("pub.der")]);
        File.WriteAllText(dir.File("manifest.json"), ManifestSigner.Manifest(version: 8, channel: "revocation"));

        var envelope = Run("bash", [script, dir.File("manifest.json"), dir.File("key.pem"), "ci-key.1"]);

        var verifier = new SignedManifestVerifier(
            [new ManifestPublicKey("ci-key.1", File.ReadAllBytes(dir.File("pub.der")))],
            new InMemoryVersionStore(),
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 2, 0, 0, 0, TimeSpan.Zero)));
        var manifest = verifier.Verify(System.Text.Encoding.UTF8.GetBytes(envelope), "revocation");

        Assert.Equal(8, manifest.Version);
    }

    [SigningToolFact]
    public void ScriptRefusesAKeyIdThatCouldBreakTheJson()
    {
        using var dir = new TempDirectory();
        var script = FindRepositoryFile("tools/manifest-signing/sign-manifest.sh")!;
        File.WriteAllText(dir.File("manifest.json"), "{}");

        Assert.Throws<InvalidOperationException>(() => Run("bash", [script, dir.File("manifest.json"), dir.File("key.pem"), "bad\"id"]));
    }
}

/// <summary>Needs bash, openssl and the signing script from the repository; skipped where one is missing (a Windows runner, for example).</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class SigningToolFactAttribute : FactAttribute
{
    public SigningToolFactAttribute()
    {
        if (!Available())
        {
            Skip = "bash, openssl or tools/manifest-signing/sign-manifest.sh not available.";
        }
    }

    public static string? FindRepositoryFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool Available()
    {
        if (OperatingSystem.IsWindows() || FindRepositoryFile("tools/manifest-signing/sign-manifest.sh") is null)
        {
            return false;
        }

        try
        {
            foreach (var tool in new[] { "openssl", "bash" })
            {
                using var process = Process.Start(new ProcessStartInfo(tool, tool == "bash" ? "-c true" : "version") { RedirectStandardOutput = true });
                process?.WaitForExit();
                if (process?.ExitCode != 0)
                {
                    return false;
                }
            }

            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}
