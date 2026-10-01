// SPDX-License-Identifier: GPL-3.0-or-later
using System.CommandLine;
using Bootrix.Core.Model;
using Bootrix.Core.Profiles;

namespace Bootrix.Cli.Commands;

/// <summary>The options that describe what a medium should look like. <c>write</c> and <c>plan</c> share them so a plan shows exactly what a write would do.</summary>
internal sealed class SpecOptions
{
    private Option<WriteMode> _mode = null!;
    private Option<PartitionScheme> _scheme = null!;
    private Option<TargetFirmware> _firmware = null!;
    private Option<FileSystemKind> _fileSystem = null!;
    private Option<string> _label = null!;
    private Option<bool> _legacy = null!;
    private Option<int> _persistence = null!;
    private Option<bool> _bypassTpm = null!;
    private Option<bool> _bypassSecureBoot = null!;
    private Option<bool> _bypassRam = null!;
    private Option<bool> _bypassCpu = null!;
    private Option<bool> _bypassStorage = null!;
    private Option<string> _account = null!;
    private Option<bool> _skipPrivacy = null!;
    private Option<bool> _noBitLocker = null!;
    private Option<string> _language = null!;
    private Option<string> _timeZone = null!;
    private Option<string[]> _drivers = null!;
    private Option<BootCertificate> _certificate = null!;
    private Option<string> _passwordEnvironment = null!;

    public static SpecOptions Create() => new()
    {
        _mode = new Option<WriteMode>("--mode") { Description = "auto, rawcopy (byte for byte) or extract (copy the files).", DefaultValueFactory = _ => WriteMode.Auto },
        _scheme = new Option<PartitionScheme>("--scheme") { Description = "auto, mbr or gpt.", DefaultValueFactory = _ => PartitionScheme.Auto },
        _firmware = new Option<TargetFirmware>("--firmware") { Description = "auto, bios, uefi or biosanduefi.", DefaultValueFactory = _ => TargetFirmware.Auto },
        _fileSystem = new Option<FileSystemKind>("--fs") { Description = "auto, fat32, exfat or ntfs.", DefaultValueFactory = _ => FileSystemKind.Auto },
        _label = new Option<string>("--label") { Description = "Volume label." },
        _legacy = new Option<bool>("--legacy-bios") { Description = "Workarounds for old BIOS machines (classic partition offset, CHS values)." },
        _persistence = new Option<int>("--persistence") { Description = "Persistence partition size in MB for Linux live media (0 = none).", DefaultValueFactory = _ => 0 },
        _bypassTpm = new Option<bool>("--bypass-tpm") { Description = "Windows setup: do not require TPM 2.0." },
        _bypassSecureBoot = new Option<bool>("--bypass-secure-boot") { Description = "Windows setup: do not require Secure Boot." },
        _bypassRam = new Option<bool>("--bypass-ram") { Description = "Windows setup: skip the RAM check." },
        _bypassCpu = new Option<bool>("--bypass-cpu") { Description = "Windows setup: skip the CPU check." },
        _bypassStorage = new Option<bool>("--bypass-storage") { Description = "Windows setup: skip the storage check." },
        _account = new Option<string>("--local-account") { Description = "Windows setup: create this local account instead of asking for a Microsoft account." },
        _skipPrivacy = new Option<bool>("--skip-privacy") { Description = "Windows setup: skip the privacy questions." },
        _noBitLocker = new Option<bool>("--no-device-encryption") { Description = "Windows setup: turn off automatic device encryption." },
        _language = new Option<string>("--language") { Description = "Windows setup language, e.g. de-DE." },
        _timeZone = new Option<string>("--time-zone") { Description = "Windows time zone id, e.g. \"W. Europe Standard Time\"." },
        _drivers = new Option<string[]>("--drivers") { Description = "Windows setup: folders with drivers to add.", AllowMultipleArgumentsPerToken = true },
        _certificate = new Option<BootCertificate>("--boot-certificate") { Description = "auto, windows2011 or windows2023 (boot manager signed with the 2023 CA).", DefaultValueFactory = _ => BootCertificate.Auto },
        _passwordEnvironment = new Option<string>("--account-password-env") { Description = "Name of an environment variable that holds the password of the local account (never put the password on the command line)." },
    };

    public void AddTo(Command command)
    {
        foreach (var option in new Option[]
        {
            _mode, _scheme, _firmware, _fileSystem, _label, _legacy, _persistence, _bypassTpm, _bypassSecureBoot, _bypassRam,
            _bypassCpu, _bypassStorage, _account, _skipPrivacy, _noBitLocker, _language, _timeZone, _drivers, _certificate,
            _passwordEnvironment,
        })
        {
            command.Add(option);
        }
    }

    public JobSpec Build(ParseResult parse, bool verify) => new()
    {
        Kind = JobKind.WriteImage,
        Target = new TargetOptions
        {
            Mode = parse.GetValue(_mode),
            Scheme = parse.GetValue(_scheme),
            Firmware = parse.GetValue(_firmware),
            FileSystem = parse.GetValue(_fileSystem),
            Label = parse.GetValue(_label),
            LegacyBiosFixes = parse.GetValue(_legacy),
            PersistenceMegabytes = parse.GetValue(_persistence),
        },
        Windows = new WindowsSetupOptions
        {
            BypassTpm = parse.GetValue(_bypassTpm),
            BypassSecureBoot = parse.GetValue(_bypassSecureBoot),
            BypassRam = parse.GetValue(_bypassRam),
            BypassCpu = parse.GetValue(_bypassCpu),
            BypassStorage = parse.GetValue(_bypassStorage),
            LocalAccountName = parse.GetValue(_account),
            SkipPrivacyQuestions = parse.GetValue(_skipPrivacy),
            DisableBitLocker = parse.GetValue(_noBitLocker),
            UiLanguage = parse.GetValue(_language),
            TimeZone = parse.GetValue(_timeZone),
            DriverFolders = [.. (parse.GetValue(_drivers) ?? []).Select(Path.GetFullPath)],
            BootCertificate = parse.GetValue(_certificate),
        },
        Verify = new VerifyOptions { ReadBack = verify },
    };

    public string? Password(ParseResult parse) =>
        parse.GetValue(_passwordEnvironment) is { Length: > 0 } name ? Environment.GetEnvironmentVariable(name) : null;
}
