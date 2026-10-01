// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Errors;
using Bootrix.Core.Images;
using Bootrix.Core.Images.Wim;
using Bootrix.Core.Localization;
using Bootrix.Core.Profiles;
using Bootrix.Core.Unattend;

namespace Bootrix.Core.Writing.Windows.Customization;

/// <summary>What is only known when a stick is written: its architecture, serial, position in the batch and the secret.</summary>
public sealed record AnswerFileContext
{
    public WindowsArch Arch { get; init; } = WindowsArch.X64;

    /// <summary>Serial of the stick; feeds the {serial} token of the computer name pattern.</summary>
    public string? DeviceSerial { get; init; }

    /// <summary>Position of the stick in the job, starting at 1; feeds {n} and {n3}.</summary>
    public int TargetNumber { get; init; } = 1;

    public string? LocalAccountPassword { get; init; }

    /// <summary>The editions of the install image, to turn the edition of the job into the exact image name Setup needs.</summary>
    public IReadOnlyList<WimEdition> Editions { get; init; } = [];

    public DateOnly? Date { get; init; }

    public Random? Random { get; init; }
}

/// <summary>Turns the setup options of a job into the options of the answer file.</summary>
public static class AnswerFileOptionsFactory
{
    /// <summary>Whether the job asks for anything an answer file is needed for.</summary>
    public static bool IsRequested(WindowsSetupOptions windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        return HardwareCheckBypass.IsRequested(windows)
            || !string.IsNullOrWhiteSpace(windows.LocalAccountName)
            || !string.IsNullOrWhiteSpace(windows.ComputerNamePattern)
            || !string.IsNullOrWhiteSpace(windows.TimeZone)
            || !string.IsNullOrWhiteSpace(windows.UiLanguage)
            || windows.SkipPrivacyQuestions
            || windows.DisableBitLocker
            || !string.IsNullOrWhiteSpace(windows.Edition);
    }

    /// <exception cref="BootrixException">The architecture cannot be named in an answer file, the edition is not in the image, or a name is invalid.</exception>
    public static UnattendOptions Create(WindowsSetupOptions windows, AnswerFileContext context)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(context);

        // An answer file for the wrong architecture is not rejected by Setup, it is silently ignored.
        if (context.Arch is not (WindowsArch.X64 or WindowsArch.X86 or WindowsArch.Arm64))
        {
            throw new BootrixException(ErrorCode.ImageUnsupported, $"answer file for architecture {context.Arch}")
            {
                Arguments = [$"Windows ({context.Arch})"],
            };
        }

        var account = string.IsNullOrWhiteSpace(windows.LocalAccountName) ? null : UnattendValidator.NormalizeAccountName(windows.LocalAccountName);

        return new UnattendOptions
        {
            Arch = context.Arch,
            Windows = windows with
            {
                LocalAccountName = account,
                Edition = null,
                TimeZone = Clean(windows.TimeZone),
                UiLanguage = Clean(windows.UiLanguage),
            },
            ComputerName = ResolveComputerName(windows.ComputerNamePattern, context),
            ImageName = ResolveEdition(windows.Edition, context.Editions),

            // Without an account there is nobody the password could belong to.
            LocalAccountPassword = account is null || string.IsNullOrEmpty(context.LocalAccountPassword) ? null : context.LocalAccountPassword,
        };
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ResolveComputerName(string? pattern, AnswerFileContext context)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return null;
        }

        return ComputerNames.Resolve(
            pattern.Trim(),
            new ComputerNameContext(context.DeviceSerial, Math.Max(1, context.TargetNumber), context.Date, context.Random));
    }

    /// <summary>
    /// The job names an edition by what the user knows ("Windows 11 Pro", "Professional"); Setup matches the
    /// image name exactly, and a typo only shows up as an error screen on the customer's PC.
    /// </summary>
    private static string? ResolveEdition(string? edition, IReadOnlyList<WimEdition> editions)
    {
        if (string.IsNullOrWhiteSpace(edition))
        {
            return null;
        }

        var wanted = edition.Trim();
        if (editions.Count == 0)
        {
            return wanted;
        }

        if (EditionMatcher.Find(editions, wanted)?.Name is { Length: > 0 } name)
        {
            return name;
        }

        var known = string.Join(", ", editions.Select(e => e.Name).Where(n => !string.IsNullOrEmpty(n)));
        throw new BootrixException(ErrorCode.InvalidSpec, "edition not in image")
        {
            Arguments = [Localizer.Default.Get("Customization.EditionUnknown", wanted, known)],
        };
    }
}
