// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Workshop.Licensing;

/// <summary>Edition IDs as Windows and the firmware license name them, and the image names Setup media use for them.</summary>
public static class WindowsEditions
{
    private static readonly Dictionary<string, string> ImageSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Core"] = "Home",
        ["CoreN"] = "Home N",
        ["CoreSingleLanguage"] = "Home Single Language",
        ["CoreCountrySpecific"] = "Home China",
        ["Professional"] = "Pro",
        ["ProfessionalN"] = "Pro N",
        ["ProfessionalEducation"] = "Pro Education",
        ["ProfessionalEducationN"] = "Pro Education N",
        ["ProfessionalWorkstation"] = "Pro for Workstations",
        ["ProfessionalWorkstationN"] = "Pro N for Workstations",
        ["Education"] = "Education",
        ["EducationN"] = "Education N",
        ["Enterprise"] = "Enterprise",
        ["EnterpriseN"] = "Enterprise N",
    };

    /// <summary>Returns the canonical spelling of a client edition ID, or null for anything else (servers, unknown tokens).</summary>
    public static string? Canonical(string? editionId)
    {
        if (string.IsNullOrWhiteSpace(editionId))
        {
            return null;
        }

        var trimmed = editionId.Trim();
        foreach (var known in ImageSuffixes.Keys)
        {
            if (known.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }

        return null;
    }

    /// <summary>The name of the edition inside install.wim, e.g. "Windows 11 Pro" for ("Windows 11", "Professional").</summary>
    public static string? ImageName(string productName, string? editionId) =>
        Canonical(editionId) is { } canonical ? $"{productName} {ImageSuffixes[canonical]}" : null;
}
