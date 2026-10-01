// SPDX-License-Identifier: GPL-3.0-or-later
using System.Xml;
using System.Xml.Linq;

namespace Bootrix.Core.Workshop.Capture;

/// <summary>Reads a profile file written by `netsh wlan export profile`.</summary>
public static class WlanProfileXml
{
    private static readonly XmlReaderSettings Settings = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };

    /// <summary>Returns null when the text is not a WLANProfile document.</summary>
    public static WlanProfile? Parse(string xml, string? exportedFile = null)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), Settings);
            document = XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }

        var root = document.Root;
        if (root is null || root.Name.LocalName != "WLANProfile" || Find(root, "name") is not { Length: > 0 } name)
        {
            return null;
        }

        bool? keyProtected = null;
        string? key = null;
        if (Find(root, "MSM", "security", "sharedKey") is not null)
        {
            // With key=clear and administrator rights protected is false and keyMaterial is the passphrase; otherwise it is encrypted hex.
            keyProtected = string.Equals(Find(root, "MSM", "security", "sharedKey", "protected"), "true", StringComparison.OrdinalIgnoreCase);
            var material = Find(root, "MSM", "security", "sharedKey", "keyMaterial");
            key = keyProtected == true || string.IsNullOrEmpty(material) ? null : material;
        }

        return new WlanProfile
        {
            Name = name,
            Authentication = Find(root, "MSM", "security", "authEncryption", "authentication"),
            Encryption = Find(root, "MSM", "security", "authEncryption", "encryption"),
            ConnectionMode = Find(root, "connectionMode"),
            KeyProtected = keyProtected,
            Key = key,
            ExportedFile = exportedFile,
        };
    }

    /// <summary>Follows child elements by local name, which keeps the code independent of the profile schema version in the namespace.</summary>
    private static string? Find(XElement start, params string[] path)
    {
        var current = start;
        foreach (var step in path)
        {
            current = current.Elements().FirstOrDefault(e => e.Name.LocalName == step);
            if (current is null)
            {
                return null;
            }
        }

        return current.Value;
    }
}
