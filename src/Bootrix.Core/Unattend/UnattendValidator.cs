// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Unattend;

/// <summary>Checks names against the rules Windows applies, so mistakes show up in the form and not half way through Setup.</summary>
public static class UnattendValidator
{
    public const int MaxAccountNameLength = 20;
    public const int MaxComputerNameLength = 15;

    private const string InvalidAccountChars = "\"/\\[]:;|=,+*?<>@";

    private static readonly HashSet<string> ReservedAccountNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Administrator",
        "Administrators",
        "Guest",
        "Guests",
        "DefaultAccount",
        "WDAGUtilityAccount",
        "defaultuser0",
        "Users",
        "SYSTEM",
        "Everyone",
        "NONE",
        "LOCAL SERVICE",
        "NETWORK SERVICE",
    };

    public static IReadOnlyList<ValidationIssue> ValidateAccountName(string? name, string? computerName = null)
    {
        var issues = new List<ValidationIssue>();
        var trimmed = (name ?? "").Trim();

        if (trimmed.Length == 0)
        {
            issues.Add(new ValidationIssue("Validation.Account.Empty"));
            return issues;
        }

        if (trimmed.Length > MaxAccountNameLength)
        {
            issues.Add(new ValidationIssue("Validation.Account.TooLong"));
        }

        var invalid = trimmed.Where(c => InvalidAccountChars.Contains(c, StringComparison.Ordinal) || char.IsControl(c)).Distinct().ToList();
        if (invalid.Count > 0)
        {
            issues.Add(new ValidationIssue("Validation.Account.InvalidChars", true, string.Concat(invalid.Where(c => !char.IsControl(c)))));
        }

        if (trimmed.All(c => c is '.' or ' '))
        {
            issues.Add(new ValidationIssue("Validation.Account.OnlyDotsOrSpaces"));
        }

        if (ReservedAccountNames.Contains(trimmed))
        {
            issues.Add(new ValidationIssue("Validation.Account.Reserved", true, trimmed));
        }

        if (computerName is not null && string.Equals(trimmed, computerName, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new ValidationIssue("Validation.Account.SameAsComputer"));
        }

        return issues;
    }

    public static IReadOnlyList<ValidationIssue> ValidateComputerName(string? name)
    {
        var issues = new List<ValidationIssue>();
        if (string.IsNullOrEmpty(name))
        {
            issues.Add(new ValidationIssue("Validation.Computer.Empty"));
            return issues;
        }

        if (name.Length > MaxComputerNameLength)
        {
            issues.Add(new ValidationIssue("Validation.Computer.TooLong"));
        }

        if (!name.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-'))
        {
            issues.Add(new ValidationIssue("Validation.Computer.InvalidChars"));
        }

        if (name.All(char.IsAsciiDigit))
        {
            issues.Add(new ValidationIssue("Validation.Computer.OnlyDigits"));
        }

        if (name[0] == '-' || name[^1] == '-')
        {
            issues.Add(new ValidationIssue("Validation.Computer.HyphenEdge"));
        }

        return issues;
    }

    /// <summary>Trailing and leading blanks are removed; Windows would reject them or silently strip them.</summary>
    public static string NormalizeAccountName(string name) => name.Trim();
}
