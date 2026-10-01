// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Bootrix.Core.Diagnostics;

public static class SystemReport
{
    /// <summary>Program version and the system it runs on, without names of the machine or the user.</summary>
    public static string Describe(bool? elevated = null)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"{AppInfo.Name} {AppInfo.Version}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Created: {DateTimeOffset.UtcNow:u}");
        text.AppendLine(CultureInfo.InvariantCulture, $"System: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        text.AppendLine(CultureInfo.InvariantCulture, $"Process: {RuntimeInformation.ProcessArchitecture}, {RuntimeInformation.FrameworkDescription}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Language: {CultureInfo.CurrentUICulture.Name}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Processors: {Environment.ProcessorCount}");
        if (elevated is { } value)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Elevated: {value}");
        }

        return text.ToString();
    }
}
