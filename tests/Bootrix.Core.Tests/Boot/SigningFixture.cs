// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Tests.Boot;

/// <summary>
/// Throw-away certificates and signing through osslsigncode, shared by the tests of one class. Everything is created
/// on first use so a machine without openssl or osslsigncode can still construct the fixture.
/// </summary>
public sealed class SigningFixture : IDisposable
{
    private readonly Lazy<string> _root = new(() => Directory.CreateTempSubdirectory("bootrix-sign-").FullName);
    private readonly Dictionary<string, Identity> _identities = [];

    internal sealed record Identity(string Chain, string Leaf, string Key, string Ca);

    public static bool Available => ExternalTools.IsAvailable("openssl") && ExternalTools.IsAvailable("osslsigncode");

    public void Dispose()
    {
        if (!_root.IsValueCreated)
        {
            return;
        }

        try
        {
            Directory.Delete(_root.Value, recursive: true);
        }
        catch (IOException)
        {
            // Leftover temp files are harmless.
        }
    }

    /// <summary>Signs a PE image with Authenticode; <paramref name="nest"/> adds the signature next to an existing one.</summary>
    internal byte[] Sign(byte[] image, string digest = "sha256", bool nest = false, string caName = "Bootrix Test CA", bool includeCa = true, string? extraCertificatePem = null)
    {
        var identity = GetIdentity(caName);
        var input = WriteTemp(image);
        var output = input + ".signed";

        var args = new List<string> { "sign" };
        if (nest)
        {
            args.Add("-nest");
        }

        args.AddRange(["-certs", includeCa ? identity.Chain : identity.Leaf, "-key", identity.Key, "-h", digest]);
        if (extraCertificatePem is not null)
        {
            var extra = Path.Combine(_root.Value, Guid.NewGuid().ToString("N") + ".pem");
            File.WriteAllText(extra, extraCertificatePem);
            args.AddRange(["-ac", extra]);
        }

        args.AddRange(["-in", input, "-out", output]);
        ExternalTools.RunChecked("osslsigncode", [.. args]);
        return File.ReadAllBytes(output);
    }

    /// <summary>Writes the image to a temp file for tools that need a path.</summary>
    internal string WriteTemp(byte[] image, string name = "image.efi")
    {
        var path = Path.Combine(_root.Value, Guid.NewGuid().ToString("N") + "-" + name);
        File.WriteAllBytes(path, image);
        return path;
    }

    /// <summary>A CA with the given common name and a leaf signed by it; the chain file holds the leaf first, then the CA.</summary>
    internal Identity GetIdentity(string caName = "Bootrix Test CA")
    {
        if (_identities.TryGetValue(caName, out var existing))
        {
            return existing;
        }

        var prefix = "id" + _identities.Count + "-";
        string P(string name) => Path.Combine(_root.Value, prefix + name);

        ExternalTools.RunChecked("openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-keyout", P("ca.key"), "-out", P("ca.pem"), "-subj", "/CN=" + caName, "-days", "3650");
        ExternalTools.RunChecked("openssl", "req", "-newkey", "rsa:2048", "-nodes", "-keyout", P("leaf.key"), "-out", P("leaf.csr"), "-subj", "/CN=Bootrix Test Signer");
        ExternalTools.RunChecked("openssl", "x509", "-req", "-in", P("leaf.csr"), "-CA", P("ca.pem"), "-CAkey", P("ca.key"), "-CAcreateserial", "-out", P("leaf.pem"), "-days", "3650");
        File.WriteAllText(P("chain.pem"), File.ReadAllText(P("leaf.pem")) + File.ReadAllText(P("ca.pem")));

        var identity = new Identity(P("chain.pem"), P("leaf.pem"), P("leaf.key"), P("ca.pem"));
        _identities[caName] = identity;
        return identity;
    }
}
