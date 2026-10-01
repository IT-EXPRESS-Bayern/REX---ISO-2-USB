// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Boot;

namespace Bootrix.Core.Writing.Windows.Customization;

/// <param name="SourcePath">A file extracted from the image's EFI_EX or Fonts_EX folder.</param>
/// <param name="TargetRelativePath">The file on the medium it replaces, relative to the medium root, with the casing the medium uses.</param>
/// <param name="IsEfiBinary">Whether the file is a boot loader that has to carry the new signature.</param>
public sealed record BootFileReplacement(string SourcePath, string TargetRelativePath, bool IsEfiBinary);

/// <summary>
/// Which files of a Windows medium are replaced by their 2023-signed counterparts, and where those come from.
/// The image ships them as <c>Windows\Boot\EFI_EX\name_EX.efi</c> and <c>Windows\Boot\Fonts_EX\name_EX.ttf</c>; on
/// the medium they stand in for <c>name.efi</c> and <c>name.ttf</c>. Only files that already exist on the medium are
/// replaced, so the plan never adds a loader the writer did not put there.
/// </summary>
public sealed class BootManagerSwapPlan
{
    public const string EfiFolder = "EFI_EX";
    public const string FontFolder = "Fonts_EX";
    public const string BootManagerSource = "bootmgfw_EX.efi";
    public const string BootManagerRootSource = "bootmgr_EX.efi";

    private const string ExSuffix = "_EX";

    private BootManagerSwapPlan(IReadOnlyList<BootFileReplacement> replacements, string? unavailableReason, string? unavailableDetail)
    {
        Replacements = replacements;
        UnavailableReason = unavailableReason;
        UnavailableDetail = unavailableDetail;
    }

    public IReadOnlyList<BootFileReplacement> Replacements { get; }

    /// <summary>Resource key of the reason the swap is impossible; null when it is possible.</summary>
    public string? UnavailableReason { get; }

    /// <summary>Argument for the reason text, e.g. the name of the loader the medium lacks.</summary>
    public string? UnavailableDetail { get; }

    public bool IsPossible => UnavailableReason is null;

    /// <param name="extractedRoot">Where the contents of EFI_EX and Fonts_EX were extracted to.</param>
    /// <param name="mediaRoot">The medium; it is only looked at.</param>
    public static BootManagerSwapPlan Create(string extractedRoot, string mediaRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(extractedRoot);
        ArgumentException.ThrowIfNullOrEmpty(mediaRoot);

        var efiFolder = FindDirectory(extractedRoot, EfiFolder);
        var bootManager = efiFolder is null ? null : FindFile(efiFolder, BootManagerSource);
        if (bootManager is null)
        {
            return Unavailable("Boot2023.Reason.NoSourceFiles");
        }

        // The architecture comes from the file itself: it is what the firmware will run, whatever the image profile says.
        var machine = ReadMachine(bootManager);
        if (EfiMachineInfo.FallbackSuffix(machine) is not { } suffix)
        {
            return Unavailable("Boot2023.Reason.Unreadable", BootManagerSource);
        }

        var fallbackName = $"boot{suffix.ToLowerInvariant()}.efi";
        var fallback = MediaPaths.Resolve(mediaRoot, "efi", "boot", fallbackName);
        if (fallback is null)
        {
            return Unavailable("Boot2023.Reason.NoLoader", fallbackName.ToUpperInvariant());
        }

        var replacements = new List<BootFileReplacement> { new(bootManager, fallback, true) };

        foreach (var file in Directory.EnumerateFiles(efiFolder!).Order(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(file);
            if (!name.EndsWith(ExSuffix + ".efi", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var original = name[..^(ExSuffix.Length + ".efi".Length)] + ".efi";

            // bootmgr.efi sits at the root of the medium next to bootmgr; every other boot application lives with the boot manager.
            var target = string.Equals(original, "bootmgr.efi", StringComparison.OrdinalIgnoreCase)
                ? MediaPaths.Resolve(mediaRoot, original)
                : MediaPaths.Resolve(mediaRoot, "efi", "microsoft", "boot", original);
            if (target is not null)
            {
                replacements.Add(new BootFileReplacement(file, target, true));
            }
        }

        if (FindDirectory(extractedRoot, FontFolder) is { } fontFolder)
        {
            foreach (var file in Directory.EnumerateFiles(fontFolder).Order(StringComparer.OrdinalIgnoreCase))
            {
                var stem = Path.GetFileNameWithoutExtension(file);
                var original = (stem.EndsWith(ExSuffix, StringComparison.OrdinalIgnoreCase) ? stem[..^ExSuffix.Length] : stem) + Path.GetExtension(file);
                if (MediaPaths.Resolve(mediaRoot, "efi", "microsoft", "boot", "fonts", original) is { } target)
                {
                    replacements.Add(new BootFileReplacement(file, target, false));
                }
            }
        }

        return new BootManagerSwapPlan(replacements, null, null);
    }

    private static BootManagerSwapPlan Unavailable(string reasonKey, string? detail = null) => new([], reasonKey, detail);

    private static EfiMachine ReadMachine(string efiFile)
    {
        try
        {
            using var stream = File.OpenRead(efiFile);
            return EfiBinary.Load(stream).Machine;
        }
        catch (Errors.BootrixException)
        {
            return EfiMachine.Unknown;
        }
    }

    private static string? FindDirectory(string parent, string name) =>
        Directory.EnumerateDirectories(parent).FirstOrDefault(path => string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase));

    private static string? FindFile(string parent, string name) =>
        Directory.EnumerateFiles(parent).FirstOrDefault(path => string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase));
}
