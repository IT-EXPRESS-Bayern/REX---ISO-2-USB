// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Text;
using Bootrix.Core.Catalog.Rescue;
using Bootrix.Core.Net;
using Bootrix.Core.Tests.Net;
using Bootrix.Core.Tests.Net.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Bootrix.Core.Tests.Catalog.Rescue;

/// <summary>
/// The publishing side of the catalog is shell and openssl, not C#: a catalog that went through those scripts and is
/// accepted by the store shows that the two ends agree on the envelope, the channel name and the payload, and not just
/// the test signer and the verifier with each other.
/// </summary>
public class RescueCatalogSigningToolTests
{
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

    [SigningToolFact]
    public void CatalogWrappedAndSignedByTheScriptsIsAppliedByTheStore()
    {
        using var dir = new TempDirectory();
        var wrap = SigningToolFactAttribute.FindRepositoryFile("tools/manifest-signing/make-catalog-manifest.sh");
        var sign = SigningToolFactAttribute.FindRepositoryFile("tools/manifest-signing/sign-manifest.sh")!;
        Assert.NotNull(wrap);

        Run("openssl", ["ecparam", "-name", "secp384r1", "-genkey", "-noout", "-out", dir.File("key.pem")]);
        Run("openssl", ["ec", "-in", dir.File("key.pem"), "-pubout", "-outform", "DER", "-out", dir.File("pub.der")]);
        File.WriteAllText(dir.File("catalog.json"), RescueFixtures.EmbeddedWithVersion(5));

        File.WriteAllText(dir.File("manifest.json"), Run("bash", [wrap, dir.File("catalog.json"), "9", "2099-01-01T00:00:00Z"]));
        var envelope = Run("bash", [sign, dir.File("manifest.json"), dir.File("key.pem"), "ci-key.1"]);

        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var store = new RescueCatalogStore(
            dir.File("cache"),
            new SignedManifestVerifier([new ManifestPublicKey("ci-key.1", File.ReadAllBytes(dir.File("pub.der")))], new InMemoryVersionStore(), clock),
            NullLogger<RescueCatalogStore>.Instance,
            clock);

        var result = store.Apply(Encoding.UTF8.GetBytes(envelope));

        Assert.Equal(new RescueCatalogUpdate(RescueCatalogUpdateStatus.Updated, 5), result);
        Assert.Equal(RescueCatalogOrigin.Cache, store.Current.Origin);
        Assert.Equal(EmbeddedRescueCatalog.Document.Entries.Count, store.Current.Document.Entries.Count);
    }

    [SigningToolFact]
    public void WrapperRefusesAVersionOrExpiryThatCouldBreakTheManifest()
    {
        using var dir = new TempDirectory();
        var wrap = SigningToolFactAttribute.FindRepositoryFile("tools/manifest-signing/make-catalog-manifest.sh")!;
        File.WriteAllText(dir.File("catalog.json"), RescueFixtures.Minimal);

        Assert.Throws<InvalidOperationException>(() => Run("bash", [wrap, dir.File("catalog.json"), "0", "2099-01-01T00:00:00Z"]));
        Assert.Throws<InvalidOperationException>(() => Run("bash", [wrap, dir.File("catalog.json"), "1; rm -rf /", "2099-01-01T00:00:00Z"]));
        Assert.Throws<InvalidOperationException>(() => Run("bash", [wrap, dir.File("catalog.json"), "1", "tomorrow"]));
        Assert.Throws<InvalidOperationException>(() => Run("bash", [wrap, dir.File("missing.json"), "1", "2099-01-01T00:00:00Z"]));
    }
}
