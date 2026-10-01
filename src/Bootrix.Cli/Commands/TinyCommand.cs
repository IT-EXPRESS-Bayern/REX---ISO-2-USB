// SPDX-License-Identifier: GPL-3.0-or-later
using System.CommandLine;
using Bootrix.Cli.Output;
using Bootrix.Core.Errors;
using Bootrix.Core.Jobs;
using Bootrix.Core.Profiles;
using Bootrix.Core.Tiny;
using Bootrix.Core.Unattend;
using Bootrix.Windows.Images;

namespace Bootrix.Cli.Commands;

internal static class TinyCommand
{
    public static Command Create(TinyBuilder builder, IInstallImageTools tools, JobRunner runner, string defaultWorkDirectory)
    {
        var command = new Command("tiny", "Build a slimmed-down Windows medium (Tiny11, Tiny11 Core, Tiny10) from an original ISO.")
        {
            CreateProfiles(),
            CreateEditions(tools),
            CreateBuild(builder, tools, runner, defaultWorkDirectory),
        };
        return command;
    }

    private static Command CreateProfiles()
    {
        var json = new Option<bool>("--json") { Description = "Machine readable output." };
        var command = new Command("profiles", "List the built-in profiles and their option groups.") { json };
        command.SetAction(parse =>
        {
            var writer = new ConsoleWriter(parse.GetValue(json));
            foreach (var id in TinyProfiles.BuiltInIds)
            {
                var profile = TinyProfiles.Load(id);
                if (writer.Json)
                {
                    writer.WriteObject(new
                    {
                        id = profile.Id,
                        name = profile.Name,
                        windows = profile.WindowsFamily,
                        servicing = !profile.BreaksServicing,
                        groups = profile.Groups.Select(g => new { id = g.Id, enabled = g.Default }),
                    });
                    continue;
                }

                writer.WriteLine($"{profile.Id,-11} {profile.Name}{(profile.BreaksServicing ? "  (cannot be serviced afterwards)" : "")}");
                writer.WriteLine($"            groups: {string.Join(", ", profile.Groups.Select(g => g.Default ? g.Id : $"{g.Id} (off)"))}");
                if (profile.Description is { } text)
                {
                    writer.WriteLine($"            {text}");
                }
            }

            return ExitCodes.Success;
        });
        return command;
    }

    private static Command CreateEditions(IInstallImageTools tools)
    {
        var iso = new Argument<FileInfo>("iso") { Description = "Original Windows ISO." };
        var json = new Option<bool>("--json") { Description = "Machine readable output." };
        var command = new Command("editions", "List the editions inside a Windows ISO.") { iso, json };
        command.SetAction(async (parse, cancellationToken) =>
        {
            var writer = new ConsoleWriter(parse.GetValue(json));
            try
            {
                using var mounted = Mount(parse.GetValue(iso)!, cancellationToken);
                var editions = await tools.GetEditionsAsync(InstallImageIn(mounted.RootPath!), cancellationToken).ConfigureAwait(false);
                foreach (var edition in editions)
                {
                    if (writer.Json)
                    {
                        writer.WriteObject(new { index = edition.Index, name = edition.Name, sizeBytes = edition.TotalBytes });
                    }
                    else
                    {
                        writer.WriteLine($"{edition.Index,3}  {edition.Name}  ({ConsoleWriter.FormatSize(edition.TotalBytes)})");
                    }
                }

                return ExitCodes.Success;
            }
            catch (Exception ex)
            {
                writer.WriteError(ex);
                return ExitCodes.For(ex);
            }
        });
        return command;
    }

    private static Command CreateBuild(TinyBuilder builder, IInstallImageTools tools, JobRunner runner, string defaultWorkDirectory)
    {
        var iso = new Argument<FileInfo>("iso") { Description = "Original Windows ISO." };
        var profile = new Option<string>("--profile", "-p") { Description = "tiny11, tiny11core or tiny10.", DefaultValueFactory = _ => "tiny11" };
        var edition = new Option<string>("--edition", "-e") { Description = "Edition index or part of its name (\"Pro\"). Needed when the ISO holds several." };
        var output = new Option<FileInfo>("--output", "-o") { Description = "ISO file to create.", Required = true };
        var keep = new Option<string[]>("--keep") { Description = "Option groups to leave untouched, e.g. edge or onedrive.", AllowMultipleArgumentsPerToken = true };
        var include = new Option<string[]>("--include") { Description = "Option groups that are off by default and should be applied, e.g. tools.", AllowMultipleArgumentsPerToken = true };
        var work = new Option<DirectoryInfo>("--work") { Description = "Scratch folder; needs about three times the size of the install image.", DefaultValueFactory = _ => new DirectoryInfo(defaultWorkDirectory) };
        var label = new Option<string>("--label") { Description = "Volume label of the ISO.", DefaultValueFactory = _ => "TINY" };
        var noBypass = new Option<bool>("--no-bypass") { Description = "Keep the TPM, Secure Boot, RAM and CPU checks of Setup." };
        var fat32 = new Option<bool>("--fat32") { Description = "Compress so the install image can be split for FAT32 sticks (larger result)." };
        var acknowledge = new Option<bool>("--accept-no-servicing") { Description = "Confirm that the result cannot be updated or serviced (required for tiny11core)." };
        var keepWork = new Option<bool>("--keep-work") { Description = "Do not delete the scratch folder afterwards." };
        var account = new Option<string>("--local-account") { Description = "Create this local account during setup (no Microsoft account needed)." };
        var language = new Option<string>("--language") { Description = "Setup and system language, e.g. de-DE." };
        var timeZone = new Option<string>("--time-zone") { Description = "Windows time zone id, e.g. \"W. Europe Standard Time\"." };
        var skipPrivacy = new Option<bool>("--skip-privacy") { Description = "Skip the privacy questions of the first sign-in." };
        var json = new Option<bool>("--json") { Description = "Machine readable output (one JSON object per line)." };

        var command = new Command("build", "Build a Tiny ISO.")
        {
            iso, profile, edition, output, keep, include, work, label, noBypass, fat32, acknowledge, keepWork, account, language, timeZone, skipPrivacy, json,
        };

        command.SetAction(async (parse, cancellationToken) =>
        {
            var writer = new ConsoleWriter(parse.GetValue(json));
            try
            {
                var source = parse.GetValue(iso)!;
                using var mounted = Mount(source, cancellationToken);
                var root = mounted.RootPath!;

                var editions = await tools.GetEditionsAsync(InstallImageIn(root), cancellationToken).ConfigureAwait(false);
                var index = EditionPicker.Pick(editions, parse.GetValue(edition));

                var tinyProfile = TinyProfiles.Load(parse.GetValue(profile)!.ToLowerInvariant());
                var unattend = BuildUnattend(parse.GetValue(account), parse.GetValue(language), parse.GetValue(timeZone), parse.GetValue(skipPrivacy));
                var options = new TinyBuildOptions
                {
                    SourceRoot = root,
                    WorkDirectory = Path.Combine(parse.GetValue(work)!.FullName, "tiny-" + Guid.NewGuid().ToString("N")[..8]),
                    ImageIndex = index,
                    ProfileId = tinyProfile.Id,
                    DisabledGroups = TinyProfiles.DisabledGroups(tinyProfile, parse.GetValue(keep) ?? [], parse.GetValue(include) ?? []),
                    IsoPath = parse.GetValue(output)!.FullName,
                    VolumeLabel = parse.GetValue(label)!,
                    Compression = parse.GetValue(fat32) ? InstallImageCompression.Maximum : InstallImageCompression.Recovery,
                    BypassHardwareChecks = !parse.GetValue(noBypass),
                    Unattend = unattend,
                    AcknowledgeNoServicing = parse.GetValue(acknowledge),
                    KeepWorkDirectory = parse.GetValue(keepWork),
                };

                var result = await runner.RunAsync(
                    builder.CreateJob(options),
                    new DelegateProgressSink(writer.WriteProgress),
                    cancellationToken).ConfigureAwait(false);
                writer.EndProgress();

                if (result.Succeeded)
                {
                    writer.WriteLine(writer.Json ? string.Empty : $"Done in {result.Duration:hh\\:mm\\:ss}: {options.IsoPath}");
                    return ExitCodes.Success;
                }

                writer.WriteError(result.Error!);
                return ExitCodes.For(result.Error!);
            }
            catch (Exception ex)
            {
                writer.EndProgress();
                writer.WriteError(ex);
                return ExitCodes.For(ex);
            }
        });
        return command;
    }

    private static MountedImage Mount(FileInfo iso, CancellationToken cancellationToken)
    {
        if (!iso.Exists)
        {
            throw new BootrixException(ErrorCode.ImageUnreadable, iso.FullName) { Arguments = [iso.FullName] };
        }

        var mounted = VirtualDiskMounter.MountIso(iso.FullName, cancellationToken);
        if (mounted.RootPath is null)
        {
            mounted.Dispose();
            throw new BootrixException(ErrorCode.ImageMountFailed, iso.FullName);
        }

        return mounted;
    }

    /// <summary>Retail media carry install.wim; media from the Media Creation Tool carry install.esd instead.</summary>
    private static string InstallImageIn(string root)
    {
        var sources = Path.Combine(root, "sources");
        foreach (var name in new[] { "install.wim", "install.esd" })
        {
            var candidate = Path.Combine(sources, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new BootrixException(ErrorCode.ImageUnsupported, root) { Arguments = [root] };
    }

    private static UnattendOptions? BuildUnattend(string? account, string? language, string? timeZone, bool skipPrivacy)
    {
        if (account is null && language is null && timeZone is null && !skipPrivacy)
        {
            return null;
        }

        return new UnattendOptions
        {
            Windows = new WindowsSetupOptions
            {
                LocalAccountName = account,
                UiLanguage = language,
                TimeZone = timeZone,
                SkipPrivacyQuestions = skipPrivacy,
            },
        };
    }
}
