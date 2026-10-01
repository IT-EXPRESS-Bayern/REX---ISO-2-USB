// SPDX-License-Identifier: GPL-3.0-or-later
using System.CommandLine;
using System.Text.Json;
using Bootrix.Cli.Output;
using Bootrix.Core.Engine;
using Bootrix.Core.Errors;
using Bootrix.Core.Jobs;
using Bootrix.Core.Json;
using Bootrix.Core.Localization;
using Bootrix.Core.Presentation;
using Bootrix.Core.Workshop.Capture;

namespace Bootrix.Cli.Commands;

/// <summary>Takes the inventory of this PC into an encrypted customer sheet, and reads such a sheet.</summary>
internal static class CustomerCommand
{
    public static Command Create(Lazy<IEngine> engine)
    {
        return new Command("customer", "Capture the inventory of this PC before a reinstall into an encrypted customer sheet.")
        {
            CreateCapture(engine),
            CreateShow(),
        };
    }

    private static Command CreateCapture(Lazy<IEngine> engine)
    {
        var output = new Option<FileInfo>("--output", "-o") { Description = $"Customer sheet to write ({CustomerSheetFile.Extension}).", Required = true };
        var password = PasswordOption();
        var noPrograms = new Option<bool>("--no-programs") { Description = "Leave out the list of installed programs." };
        var noDrivers = new Option<bool>("--no-drivers") { Description = "Leave out the additional drivers." };
        var noWlan = new Option<bool>("--no-wlan") { Description = "Leave out the names of the Wi-Fi networks." };
        var wlanKeys = new Option<bool>("--wlan-keys") { Description = "Also read the Wi-Fi keys in clear text (needs administrator rights)." };
        var bitLocker = new Option<bool>("--bitlocker-keys") { Description = "Also read the BitLocker recovery passwords (needs administrator rights)." };
        var productKey = new Option<bool>("--product-key") { Description = "Also decode the Windows product key." };
        var json = new Option<bool>("--json") { Description = "Machine readable output." };

        var command = new Command("capture", "Capture this PC; secrets are only read when asked for, and the sheet is always encrypted.")
        {
            output, password, noPrograms, noDrivers, noWlan, wlanKeys, bitLocker, productKey, json,
        };
        command.SetAction(async (parse, cancellationToken) =>
        {
            var writer = new ConsoleWriter(parse.GetValue(json));
            try
            {
                var secret = ReadPassword(parse.GetValue(password));
                var result = await engine.Value.RunJobAsync(
                    new CaptureCustomerPcJobRequest
                    {
                        IncludeInstalledPrograms = !parse.GetValue(noPrograms),
                        IncludeThirdPartyDrivers = !parse.GetValue(noDrivers),
                        ListWlanProfiles = !parse.GetValue(noWlan),
                        IncludeWlanKeys = parse.GetValue(wlanKeys),
                        IncludeBitLockerRecoveryKeys = parse.GetValue(bitLocker),
                        IncludeWindowsProductKey = parse.GetValue(productKey),
                    },
                    new Progress<ProgressReport>(_ => { }),
                    cancellationToken).ConfigureAwait(false);
                if (!result.Succeeded)
                {
                    var error = result.ToException()!;
                    writer.WriteError(error);
                    return ExitCodes.For(error);
                }

                var capture = JsonSerializer.Deserialize<CustomerPcCapture>(result.ReportJson ?? "{}", CoreJson.Options)
                    ?? throw new BootrixException(ErrorCode.Unknown, "empty capture");
                await using (var file = File.Create(parse.GetValue(output)!.FullName))
                {
                    CustomerSheetFile.Write(file, capture, secret);
                }

                writer.WriteLine(writer.Json ? string.Empty : $"Customer sheet written to {parse.GetValue(output)!.FullName}");
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

    private static Command CreateShow()
    {
        var file = new Argument<FileInfo>("sheet") { Description = "A customer sheet." };
        var password = PasswordOption();
        var command = new Command("show", "Print a customer sheet as text.") { file, password };
        command.SetAction(parse =>
        {
            var writer = new ConsoleWriter(json: false);
            try
            {
                using var stream = File.OpenRead(parse.GetValue(file)!.FullName);
                var capture = CustomerSheetFile.Read(stream, ReadPassword(parse.GetValue(password)));
                writer.WriteLine(CustomerSheetView.ToText(capture, Localizer.Default));
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

    private static Option<string> PasswordOption() => new("--password-env")
    {
        Description = "Name of an environment variable that holds the password (never put the password on the command line). Asked for when omitted.",
    };

    private static string ReadPassword(string? environmentVariable)
    {
        if (environmentVariable is { Length: > 0 } name)
        {
            return Environment.GetEnvironmentVariable(name)
                ?? throw new BootrixException(ErrorCode.InvalidSpec, "password variable") { Arguments = [$"The environment variable {name} is not set."] };
        }

        if (Console.IsInputRedirected)
        {
            throw new BootrixException(ErrorCode.InvalidSpec, "password") { Arguments = ["Give the password with --password-env when input is not interactive."] };
        }

        Console.Write("Password: ");
        var typed = new System.Text.StringBuilder();
        while (Console.ReadKey(intercept: true) is { Key: not ConsoleKey.Enter } key)
        {
            if (key.Key == ConsoleKey.Backspace)
            {
                if (typed.Length > 0)
                {
                    typed.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                typed.Append(key.KeyChar);
            }
        }

        Console.WriteLine();
        return typed.ToString();
    }
}
