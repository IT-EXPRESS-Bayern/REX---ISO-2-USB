// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.RegularExpressions;

namespace Bootrix.Core.Logging;

/// <summary>Removes product keys and passwords from text before it is written to a log or report.</summary>
public static partial class LogRedactor
{
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        text = ProductKey().Replace(text, "*****-*****-*****-*****-*****");
        text = Secret().Replace(text, "$1=***");
        return text;
    }

    [GeneratedRegex(@"\b[A-Z0-9]{5}(?:-[A-Z0-9]{5}){4}\b")]
    private static partial Regex ProductKey();

    [GeneratedRegex(@"(?i)\b(password|passwort|pwd|secret|key)\s*[=:]\s*[^\s;,]+")]
    private static partial Regex Secret();
}
