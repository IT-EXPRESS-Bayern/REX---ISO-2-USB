// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Localization;
using Bootrix.Core.Model;
using Bootrix.Core.Profiles;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Bootrix.App.ViewModels;

/// <summary>The choices behind the "Options" expander. <see cref="ToSpec"/> turns them into the job spec the engine receives.</summary>
public sealed partial class WriteOptionsViewModel : ObservableObject
{
    private readonly Localizer _localizer;

    public WriteOptionsViewModel(Localizer localizer)
    {
        _localizer = localizer;
        BuildLists();
        localizer.CultureChanged += (_, _) => BuildLists();
    }

    [ObservableProperty]
    private IReadOnlyList<OptionItem<WriteMode>> _modes = [];

    [ObservableProperty]
    private IReadOnlyList<OptionItem<PartitionScheme>> _schemes = [];

    [ObservableProperty]
    private IReadOnlyList<OptionItem<TargetFirmware>> _firmwares = [];

    [ObservableProperty]
    private IReadOnlyList<OptionItem<FileSystemKind>> _fileSystems = [];

    [ObservableProperty]
    private WriteMode _mode = WriteMode.Auto;

    [ObservableProperty]
    private PartitionScheme _scheme = PartitionScheme.Auto;

    [ObservableProperty]
    private TargetFirmware _firmware = TargetFirmware.Auto;

    [ObservableProperty]
    private FileSystemKind _fileSystem = FileSystemKind.Auto;

    [ObservableProperty]
    private string _label = "";

    [ObservableProperty]
    private bool _legacyBios;

    [ObservableProperty]
    private int _persistenceMegabytes;

    [ObservableProperty]
    private bool _bypassTpm;

    [ObservableProperty]
    private bool _bypassSecureBoot;

    [ObservableProperty]
    private bool _bypassRam;

    [ObservableProperty]
    private bool _bypassCpu;

    [ObservableProperty]
    private bool _bypassStorage;

    [ObservableProperty]
    private string _localAccountName = "";

    [ObservableProperty]
    private bool _skipPrivacyQuestions;

    [ObservableProperty]
    private bool _disableBitLocker;

    /// <summary>Whether the image is Windows setup media; the Windows options only matter then.</summary>
    [ObservableProperty]
    private bool _isWindowsImage;

    public JobSpec ToSpec(bool verify) => new()
    {
        Kind = JobKind.WriteImage,
        Target = new TargetOptions
        {
            Mode = Mode,
            Scheme = Scheme,
            Firmware = Firmware,
            FileSystem = FileSystem,
            Label = string.IsNullOrWhiteSpace(Label) ? null : Label.Trim(),
            LegacyBiosFixes = LegacyBios,
            PersistenceMegabytes = Math.Max(0, PersistenceMegabytes),
        },
        Windows = new WindowsSetupOptions
        {
            BypassTpm = BypassTpm,
            BypassSecureBoot = BypassSecureBoot,
            BypassRam = BypassRam,
            BypassCpu = BypassCpu,
            BypassStorage = BypassStorage,
            LocalAccountName = string.IsNullOrWhiteSpace(LocalAccountName) ? null : LocalAccountName.Trim(),
            SkipPrivacyQuestions = SkipPrivacyQuestions,
            DisableBitLocker = DisableBitLocker,
        },
        Verify = new VerifyOptions { ReadBack = verify },
    };

    private void BuildLists()
    {
        string T(string key) => _localizer.Get(key);

        Modes =
        [
            new(WriteMode.Auto, T("Opt.Mode.Auto")),
            new(WriteMode.RawCopy, T("Opt.Mode.RawCopy")),
            new(WriteMode.Extract, T("Opt.Mode.Extract")),
        ];
        Schemes =
        [
            new(PartitionScheme.Auto, T("Opt.Scheme.Auto")),
            new(PartitionScheme.Mbr, T("Opt.Scheme.Mbr")),
            new(PartitionScheme.Gpt, T("Opt.Scheme.Gpt")),
        ];
        Firmwares =
        [
            new(TargetFirmware.Auto, T("Opt.Firmware.Auto")),
            new(TargetFirmware.Bios, T("Opt.Firmware.Bios")),
            new(TargetFirmware.Uefi, T("Opt.Firmware.Uefi")),
            new(TargetFirmware.BiosAndUefi, T("Opt.Firmware.BiosAndUefi")),
        ];
        FileSystems =
        [
            new(FileSystemKind.Auto, T("Opt.FileSystem.Auto")),
            new(FileSystemKind.Fat32, T("Opt.FileSystem.Fat32")),
            new(FileSystemKind.ExFat, T("Opt.FileSystem.ExFat")),
            new(FileSystemKind.Ntfs, T("Opt.FileSystem.Ntfs")),
        ];
    }
}
