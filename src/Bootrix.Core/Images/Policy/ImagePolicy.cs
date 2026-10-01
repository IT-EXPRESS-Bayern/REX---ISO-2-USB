// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using Bootrix.Core.Json;
using Bootrix.Core.Model;

namespace Bootrix.Core.Images.Policy;

/// <summary>
/// Decides how an image should be written: raw copy (DD), extraction (ISO mode) or ask the person. The knowledge about
/// distribution families lives in the embedded <c>image-policy.json</c>; this class applies it to an
/// <see cref="ImageProfile"/> and adds the checks that follow from the image's structure and the target.
/// </summary>
public sealed class ImagePolicy
{
    private const string Resource = "Bootrix.Core.Images.Policy.image-policy.json";

    private readonly Dictionary<string, FamilyPolicy> _families;

    private ImagePolicy(Dictionary<string, FamilyPolicy> families) => _families = families;

    public static ImagePolicy Default { get; } = LoadEmbedded();

    public IReadOnlyCollection<string> Families => _families.Keys;

    public bool Knows(string family) => _families.ContainsKey(family);

    /// <summary>The behaviour switches of a family; <see cref="FamilyTraits.None"/> for an unknown or missing one.</summary>
    public FamilyTraits TraitsOf(string? family)
    {
        if (family is null || !_families.TryGetValue(family, out var policy))
        {
            return FamilyTraits.None;
        }

        bool Has(string flag) => policy.Flags.Contains(flag, StringComparer.OrdinalIgnoreCase);
        return new FamilyTraits(Has("casper"), Has("liveBoot"), policy.LabelPatch == true, policy.Persistence?.Extract == true);
    }

    public static ImagePolicy Load(Stream json)
    {
        var document = JsonSerializer.Deserialize<PolicyDocument>(json, CoreJson.Options)
            ?? throw new InvalidDataException("The image policy is empty.");
        return new ImagePolicy(ResolveInheritance(document.Families));
    }

    private static ImagePolicy LoadEmbedded()
    {
        using var stream = typeof(ImagePolicy).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException("The embedded image policy is missing.");
        return Load(stream);
    }

    private static Dictionary<string, FamilyPolicy> ResolveInheritance(Dictionary<string, FamilyPolicy> raw)
    {
        var source = new Dictionary<string, FamilyPolicy>(raw, StringComparer.OrdinalIgnoreCase);
        var resolved = new Dictionary<string, FamilyPolicy>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in source.Keys)
        {
            Resolve(name, source, resolved, []);
        }

        return resolved;
    }

    private static FamilyPolicy Resolve(string name, Dictionary<string, FamilyPolicy> source, Dictionary<string, FamilyPolicy> resolved, HashSet<string> visiting)
    {
        if (resolved.TryGetValue(name, out var done))
        {
            return done;
        }

        if (!source.TryGetValue(name, out var family))
        {
            throw new InvalidDataException($"The image policy refers to the unknown family '{name}'.");
        }

        if (!visiting.Add(name))
        {
            throw new InvalidDataException($"The image policy families inherit from each other in a cycle at '{name}'.");
        }

        var result = family.Inherits is { } parent ? Merge(Resolve(parent, source, resolved, visiting), family) : family;
        visiting.Remove(name);
        resolved[name] = result;
        return result;
    }

    private static FamilyPolicy Merge(FamilyPolicy parent, FamilyPolicy child) => child with
    {
        Name = child.Name ?? parent.Name,
        Modes = child.Modes ?? parent.Modes,
        Default = child.Default ?? parent.Default,
        Persistence = child.Persistence ?? parent.Persistence,
        LabelPatch = child.LabelPatch ?? parent.LabelPatch,
        Raw = MergeBoot(parent.Raw, child.Raw),
        Extract = MergeBoot(parent.Extract, child.Extract),
        Reasons = [.. parent.Reasons, .. child.Reasons],
        Warnings = [.. parent.Warnings, .. child.Warnings],
        Flags = [.. parent.Flags, .. child.Flags],
    };

    private static BootOverride? MergeBoot(BootOverride? parent, BootOverride? child) =>
        parent is null || child is null
            ? child ?? parent
            : new BootOverride { Bios = child.Bios ?? parent.Bios, Uefi = child.Uefi ?? parent.Uefi };

    public ImageDecision Evaluate(ImageProfile profile, PolicyRequest? request = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        request ??= new PolicyRequest();

        var family = profile.Family is { } name && _families.TryGetValue(name, out var found) ? found : null;
        var optical = profile.Container is ImageContainer.Iso9660 or ImageContainer.IsoUdfBridge or ImageContainer.Udf;
        var reasons = new List<string>();

        var allowed = AllowedModes(profile, family, optical, reasons);
        var support = PersistenceSupportOf(family, allowed);
        var mode = Choose(profile, family, request, allowed, support, reasons);
        var (bios, uefi) = BootSupportFor(mode, profile, family, optical);

        var label = profile.VolumeLabel;
        var patchLabel = mode == PolicyMode.Extract && family?.LabelPatch == true && FatLabel.WouldChange(label);
        var persistencePossible = mode switch
        {
            PolicyMode.RawCopy => support.HasFlag(PersistenceSupport.RawCopyPartition),
            PolicyMode.Extract => support.HasFlag(PersistenceSupport.ExtractMode),
            _ => false,
        };

        var warnings = new PolicyWarnings(profile, family, request, mode, optical, patchLabel, persistencePossible).Collect();

        return new ImageDecision
        {
            Mode = mode,
            AllowedModes = allowed,
            ReasonKeys = [.. reasons.Distinct()],
            Warnings = warnings,
            Persistence = support,
            PersistencePossible = persistencePossible,
            LabelPatchRequired = patchLabel,
            BiosBoot = bios,
            UefiBoot = uefi,
        };
    }

    /// <summary>
    /// The modes that can produce a bootable medium at all. Structure comes first: only hybrid images survive a raw copy,
    /// non-optical images are never extracted, Windows ISOs are never raw-copied. Within what is possible the family
    /// data narrows the choice.
    /// </summary>
    private static List<WriteMode> AllowedModes(ImageProfile p, FamilyPolicy? family, bool optical, List<string> reasons)
    {
        if (p.Kind is ImageKind.WindowsSetup or ImageKind.WindowsPe)
        {
            if (optical)
            {
                reasons.Add(ImagePolicyKeys.WindowsSetupExtract);
                reasons.AddRange(family?.Reasons ?? []);
                return [WriteMode.Extract];
            }

            if (p.Container == ImageContainer.Wim)
            {
                reasons.Add(ImagePolicyKeys.WimApplied);
                return [WriteMode.Extract];
            }

            reasons.Add(ImagePolicyKeys.WindowsStickRawCopy);
            return [WriteMode.RawCopy];
        }

        if (!optical)
        {
            reasons.Add(p.Container == ImageContainer.FatVolume ? ImagePolicyKeys.FloppyImageRawCopy : ImagePolicyKeys.RawImageOnly);
            reasons.AddRange(family?.Reasons ?? []);
            return [WriteMode.RawCopy];
        }

        var modes = family?.Modes?.ToList() ?? (p.IsHybrid ? [WriteMode.RawCopy, WriteMode.Extract] : [WriteMode.Extract]);
        if (!p.IsHybrid && modes.Remove(WriteMode.RawCopy))
        {
            reasons.Add(ImagePolicyKeys.NotBootableAsDisk);
        }

        if (modes.Count == 0)
        {
            modes.Add(WriteMode.Extract);
        }

        reasons.AddRange(family?.Reasons ?? []);
        return modes;
    }

    private static PersistenceSupport PersistenceSupportOf(FamilyPolicy? family, List<WriteMode> allowed)
    {
        if (family?.Persistence is not { } persistence)
        {
            return PersistenceSupport.None;
        }

        var support = PersistenceSupport.None;
        if (persistence.RawCopy && allowed.Contains(WriteMode.RawCopy))
        {
            support |= PersistenceSupport.RawCopyPartition;
        }

        if (persistence.Extract && allowed.Contains(WriteMode.Extract))
        {
            support |= PersistenceSupport.ExtractMode;
        }

        return support;
    }

    private static PolicyMode Choose(ImageProfile p, FamilyPolicy? family, PolicyRequest request, List<WriteMode> allowed, PersistenceSupport support, List<string> reasons)
    {
        PolicyMode mode;
        if (allowed.Count == 1)
        {
            mode = ToPolicyMode(allowed[0]);
        }
        else if (p.Kind == ImageKind.Data)
        {
            reasons.Add(ImagePolicyKeys.DataImageAsk);
            mode = PolicyMode.Ask;
        }
        else
        {
            var preferred = family?.Default ?? (p.IsHybrid ? WriteMode.RawCopy : WriteMode.Extract);
            mode = ToPolicyMode(allowed.Contains(preferred) ? preferred : allowed[0]);
            if (family is null && mode == PolicyMode.RawCopy)
            {
                reasons.Add(ImagePolicyKeys.HybridRawCopy);
            }
        }

        if (mode != PolicyMode.Ask && request.WantsPersistence && family?.Persistence is { } persistence && support != PersistenceSupport.None)
        {
            var viaPartition = support.HasFlag(PersistenceSupport.RawCopyPartition);
            var viaExtract = support.HasFlag(PersistenceSupport.ExtractMode);
            var wanted = viaPartition && viaExtract ? ToPolicyMode(persistence.Preferred) : viaExtract ? PolicyMode.Extract : PolicyMode.RawCopy;
            if (wanted == PolicyMode.Extract && mode != wanted)
            {
                reasons.Add(ImagePolicyKeys.PersistenceNeedsExtract);
            }
            else if (wanted == PolicyMode.RawCopy)
            {
                reasons.Add(ImagePolicyKeys.PersistenceViaPartition);
            }

            mode = wanted;
        }

        if (request.PreferredMode != WriteMode.Auto)
        {
            if (allowed.Contains(request.PreferredMode))
            {
                mode = ToPolicyMode(request.PreferredMode);
                reasons.Add(ImagePolicyKeys.PreferredModeUsed);
            }
            else
            {
                reasons.Add(ImagePolicyKeys.PreferredModeRefused);
            }
        }

        return mode;
    }

    private static PolicyMode ToPolicyMode(WriteMode mode) => mode == WriteMode.Extract ? PolicyMode.Extract : PolicyMode.RawCopy;

    /// <summary>
    /// Whether BIOS and UEFI can boot the medium in the chosen mode. A raw copy keeps what the image brings (MBR boot code,
    /// ESP in the partition table); extraction can install syslinux, GRUB or the NT6 boot sector for BIOS and copy the
    /// EFI loaders for UEFI, so it depends on the files being there.
    /// </summary>
    private static (BootSupport Bios, BootSupport Uefi) BootSupportFor(PolicyMode mode, ImageProfile p, FamilyPolicy? family, bool optical)
    {
        if (mode == PolicyMode.Extract)
        {
            var overrides = family?.Extract;
            return (
                overrides?.Bios ?? (p.HasBiosBootFiles ? BootSupport.Yes : p.HasElToritoBios ? BootSupport.Maybe : BootSupport.No),
                overrides?.Uefi ?? (p.HasEfiBootFiles ? BootSupport.Yes : p.HasElToritoEfi ? BootSupport.Maybe : BootSupport.No));
        }

        var raw = family?.Raw;
        if (optical)
        {
            return (
                raw?.Bios ?? (p.IsHybrid ? (p.HasElToritoBios || p.HasBiosBootFiles ? BootSupport.Yes : BootSupport.Maybe) : BootSupport.No),
                raw?.Uefi ?? (p.HasEspPartition ? BootSupport.Yes : p.HasElToritoEfi || p.HasEfiBootFiles ? BootSupport.Maybe : BootSupport.No));
        }

        var floppy = p.Container == ImageContainer.FatVolume;
        return (
            raw?.Bios ?? (floppy ? BootSupport.Maybe : p.HasBiosBootFiles || p.Kind is ImageKind.Dos or ImageKind.Bsd ? BootSupport.Yes : BootSupport.Maybe),
            raw?.Uefi ?? (p.HasEspPartition || p.HasEfiBootFiles ? BootSupport.Yes : BootSupport.No));
    }
}
