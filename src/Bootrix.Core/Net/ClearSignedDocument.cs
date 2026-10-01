// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;

namespace Bootrix.Core.Net;

/// <summary>
/// The parts of a clear-signed OpenPGP document (RFC 4880 section 7): the text that is covered, in the
/// canonical form the signature is computed over, and the armored signature block.
/// </summary>
internal sealed record ClearSignedDocument(string Text, byte[] CanonicalText, byte[] SignatureArmor)
{
    private const string BeginMessage = "-----BEGIN PGP SIGNED MESSAGE-----";
    private const string BeginSignature = "-----BEGIN PGP SIGNATURE-----";
    private const string EndSignature = "-----END PGP SIGNATURE-----";

    /// <summary>
    /// Parses strictly: anything outside the signed region would otherwise be taken for signed content by a
    /// caller that reads the whole file, so text before the header or after the signature is rejected.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> document, out ClearSignedDocument? parsed, out string reason)
    {
        parsed = null;
        var lines = Encoding.UTF8.GetString(document).TrimStart('\uFEFF').Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        var index = 0;
        while (index < lines.Count && lines[index].Trim().Length == 0)
        {
            index++;
        }

        if (index >= lines.Count || lines[index].TrimEnd() != BeginMessage)
        {
            reason = "not a clear-signed document";
            return false;
        }

        // Armor headers (Hash: SHA256 and the like) run up to the first empty line.
        for (index++; index < lines.Count && lines[index].Trim().Length > 0; index++)
        {
            if (!lines[index].Contains(':', StringComparison.Ordinal))
            {
                reason = "malformed armor header";
                return false;
            }
        }

        var text = new List<string>();
        for (index++; index < lines.Count && lines[index].TrimEnd() != BeginSignature; index++)
        {
            var line = lines[index];
            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                line = line[2..];
            }
            else if (line.StartsWith("-----", StringComparison.Ordinal))
            {
                reason = "unescaped armor line inside the signed text";
                return false;
            }

            text.Add(line);
        }

        if (index >= lines.Count)
        {
            reason = "no signature block";
            return false;
        }

        var signature = new List<string>();
        for (; index < lines.Count; index++)
        {
            signature.Add(lines[index]);
            if (lines[index].TrimEnd() == EndSignature)
            {
                index++;
                break;
            }
        }

        if (signature[^1].TrimEnd() != EndSignature)
        {
            reason = "unterminated signature block";
            return false;
        }

        if (lines.Skip(index).Any(l => l.Trim().Length > 0))
        {
            reason = "data after the signature block";
            return false;
        }

        // The line break in front of the signature block belongs to the armor, not to the text; trailing blanks are not signed.
        var canonical = string.Join("\r\n", text.Select(l => l.TrimEnd(' ', '\t')));

        reason = string.Empty;
        parsed = new ClearSignedDocument(
            text.Count == 0 ? string.Empty : string.Join('\n', text) + "\n",
            Encoding.UTF8.GetBytes(canonical),
            Encoding.ASCII.GetBytes(string.Join('\n', signature) + "\n"));
        return true;
    }
}
