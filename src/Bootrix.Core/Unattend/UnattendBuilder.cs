// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Bootrix.Core.Errors;
using Bootrix.Core.Images;

namespace Bootrix.Core.Unattend;

/// <summary>
/// Builds the autounattend.xml that Windows Setup reads from the root of the installation media.
/// The file is assembled as an XML tree, never by string concatenation, so account names and
/// commands are escaped correctly whatever they contain.
/// </summary>
public static class UnattendBuilder
{
    private static readonly XNamespace Unattend = "urn:schemas-microsoft-com:unattend";
    private static readonly XNamespace Wcm = "http://schemas.microsoft.com/WMIConfig/2002/State";

    private const string PublicKeyToken = "31bf3856ad364e35";

    public static XDocument Build(UnattendOptions options)
    {
        ValidateOrThrow(options);

        var root = new XElement(
            Unattend + "unattend",
            new XAttribute(XNamespace.Xmlns + "wcm", Wcm.NamespaceName));

        AddIfAny(root, WindowsPePass(options));
        AddIfAny(root, SpecializePass(options));
        AddIfAny(root, OobePass(options));

        return new XDocument(new XDeclaration("1.0", "utf-8", null), root);
    }

    public static string ToXml(UnattendOptions options) => ToXml(Build(options));

    public static string ToXml(XDocument document)
    {
        using var buffer = new MemoryStream();
        Write(document, buffer);
        return new UTF8Encoding(false).GetString(buffer.ToArray());
    }

    public static byte[] ToBytes(UnattendOptions options)
    {
        using var buffer = new MemoryStream();
        Write(Build(options), buffer);
        return buffer.ToArray();
    }

    private static void Write(XDocument document, Stream output)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            Encoding = new UTF8Encoding(false),
            NewLineHandling = NewLineHandling.Replace,
            NewLineChars = "\r\n",
        };

        using var writer = XmlWriter.Create(output, settings);
        document.Save(writer);
    }

    private static void ValidateOrThrow(UnattendOptions options)
    {
        var issues = new List<ValidationIssue>();
        if (options.Windows.LocalAccountName is { } account)
        {
            issues.AddRange(UnattendValidator.ValidateAccountName(account, options.ComputerName));
        }

        if (options.ComputerName is not null)
        {
            issues.AddRange(UnattendValidator.ValidateComputerName(options.ComputerName));
        }

        if (issues.Any(i => i.IsError))
        {
            var first = issues.First(i => i.IsError);
            throw new BootrixException(ErrorCode.InvalidSpec, first.Key) { Arguments = [first.Describe()] };
        }
    }

    private static void AddIfAny(XElement root, XElement pass)
    {
        if (pass.HasElements)
        {
            root.Add(pass);
        }
    }

    private static string Architecture(WindowsArch arch) => arch switch
    {
        WindowsArch.Arm64 => "arm64",
        WindowsArch.X86 => "x86",
        _ => "amd64",
    };

    private static XElement Component(string name, UnattendOptions options) => new(
        Unattend + "component",
        new XAttribute("name", name),
        new XAttribute("processorArchitecture", Architecture(options.Arch)),
        new XAttribute("publicKeyToken", PublicKeyToken),
        new XAttribute("language", "neutral"),
        new XAttribute("versionScope", "nonSxS"));

    private static XElement Pass(string name) => new(Unattend + "settings", new XAttribute("pass", name));

    private static XElement Command(string element, int order, string commandLine, string pathElement) => new(
        Unattend + element,
        new XAttribute(Wcm + "action", "add"),
        new XElement(Unattend + "Order", order),
        new XElement(Unattend + pathElement, commandLine));

    private static XElement WindowsPePass(UnattendOptions options)
    {
        var pass = Pass("windowsPE");
        var setup = Component("Microsoft-Windows-Setup", options);

        var bypass = BypassCommands(options).ToList();
        if (bypass.Count > 0)
        {
            var run = new XElement(Unattend + "RunSynchronous");
            for (var i = 0; i < bypass.Count; i++)
            {
                run.Add(Command("RunSynchronousCommand", i + 1, bypass[i], "Path"));
            }

            setup.Add(run);
        }

        var userData = new XElement(Unattend + "UserData", new XElement(Unattend + "AcceptEula", "true"));
        if (!string.IsNullOrWhiteSpace(options.ProductKey))
        {
            userData.Add(new XElement(
                Unattend + "ProductKey",
                new XElement(Unattend + "Key", options.ProductKey.Trim()),
                new XElement(Unattend + "WillShowUI", "OnError")));
        }

        setup.Add(userData);

        var image = ImageSelection(options);
        if (image is not null)
        {
            setup.Add(image);
        }

        pass.Add(setup);

        if (options.Windows.UiLanguage is { Length: > 0 } language)
        {
            var intl = Component("Microsoft-Windows-International-Core-WinPE", options);
            intl.Add(
                new XElement(Unattend + "SetupUILanguage", new XElement(Unattend + "UILanguage", language)),
                new XElement(Unattend + "InputLocale", language),
                new XElement(Unattend + "SystemLocale", language),
                new XElement(Unattend + "UILanguage", language),
                new XElement(Unattend + "UserLocale", language));
            pass.Add(intl);
        }

        return pass;
    }

    private static IEnumerable<string> BypassCommands(UnattendOptions options)
    {
        var windows = options.Windows;
        const string key = @"HKLM\SYSTEM\Setup\LabConfig";
        if (windows.BypassTpm)
        {
            yield return $"reg add {key} /v BypassTPMCheck /t REG_DWORD /d 1 /f";
        }

        if (windows.BypassSecureBoot)
        {
            yield return $"reg add {key} /v BypassSecureBootCheck /t REG_DWORD /d 1 /f";
        }

        if (windows.BypassRam)
        {
            yield return $"reg add {key} /v BypassRAMCheck /t REG_DWORD /d 1 /f";
        }

        if (windows.BypassCpu)
        {
            yield return $"reg add {key} /v BypassCPUCheck /t REG_DWORD /d 1 /f";
        }

        if (windows.BypassStorage)
        {
            yield return $"reg add {key} /v BypassStorageCheck /t REG_DWORD /d 1 /f";
        }
    }

    private static XElement? ImageSelection(UnattendOptions options)
    {
        string? key = null;
        string? value = null;
        if (options.ImageIndex is { } index)
        {
            key = "/IMAGE/INDEX";
            value = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        else if (!string.IsNullOrWhiteSpace(options.ImageName ?? options.Windows.Edition))
        {
            key = "/IMAGE/NAME";
            value = (options.ImageName ?? options.Windows.Edition)!.Trim();
        }

        if (key is null)
        {
            return null;
        }

        return new XElement(
            Unattend + "ImageInstall",
            new XElement(
                Unattend + "OSImage",
                new XElement(
                    Unattend + "InstallFrom",
                    new XElement(
                        Unattend + "MetaData",
                        new XAttribute(Wcm + "action", "add"),
                        new XElement(Unattend + "Key", key),
                        new XElement(Unattend + "Value", value)))));
    }

    private static XElement SpecializePass(UnattendOptions options)
    {
        var pass = Pass("specialize");
        var shell = Component("Microsoft-Windows-Shell-Setup", options);

        if (options.ComputerName is { Length: > 0 } name)
        {
            shell.Add(new XElement(Unattend + "ComputerName", name));
        }

        if (!string.IsNullOrEmpty(options.Windows.TimeZone))
        {
            shell.Add(new XElement(Unattend + "TimeZone", options.Windows.TimeZone));
        }

        if (options.Branding is { IsEmpty: false } branding)
        {
            shell.Add(OemInformation(branding));
        }

        if (shell.HasElements)
        {
            pass.Add(shell);
        }

        var commands = new List<string>();
        if (options.Windows.DisableBitLocker)
        {
            // Windows 11 encrypts the system drive automatically on capable hardware unless this value is set.
            commands.Add(@"reg add HKLM\SYSTEM\CurrentControlSet\Control\BitLocker /v PreventDeviceEncryption /t REG_DWORD /d 1 /f");
        }

        commands.AddRange(options.SpecializeCommands);
        if (commands.Count > 0)
        {
            var deployment = Component("Microsoft-Windows-Deployment", options);
            var run = new XElement(Unattend + "RunSynchronous");
            for (var i = 0; i < commands.Count; i++)
            {
                run.Add(Command("RunSynchronousCommand", i + 1, commands[i], "Path"));
            }

            deployment.Add(run);
            pass.Add(deployment);
        }

        return pass;
    }

    private static XElement OemInformation(OemBranding branding)
    {
        var info = new XElement(Unattend + "OEMInformation");
        AddText(info, "Manufacturer", branding.Manufacturer);
        AddText(info, "Model", branding.Model);
        AddText(info, "SupportProvider", branding.SupportProvider);
        AddText(info, "SupportURL", branding.SupportUrl);
        AddText(info, "SupportPhone", branding.SupportPhone);
        AddText(info, "SupportHours", branding.SupportHours);
        return info;
    }

    private static void AddText(XElement parent, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parent.Add(new XElement(Unattend + name, value));
        }
    }

    private static XElement OobePass(UnattendOptions options)
    {
        var pass = Pass("oobeSystem");
        var shell = Component("Microsoft-Windows-Shell-Setup", options);
        var windows = options.Windows;

        var oobe = new XElement(Unattend + "OOBE");
        if (windows.LocalAccountName is not null || windows.SkipPrivacyQuestions)
        {
            oobe.Add(
                new XElement(Unattend + "HideEULAPage", "true"),
                new XElement(Unattend + "HideOEMRegistrationScreen", "true"));
        }

        if (windows.LocalAccountName is not null)
        {
            // A local account in the answer file replaces the removed BypassNRO switch.
            oobe.Add(
                new XElement(Unattend + "HideOnlineAccountScreens", "true"),
                new XElement(Unattend + "HideWirelessSetupInOOBE", "true"));
        }

        if (windows.SkipPrivacyQuestions)
        {
            oobe.Add(new XElement(Unattend + "ProtectYourPC", "3"));
        }

        if (oobe.HasElements)
        {
            shell.Add(oobe);
        }

        if (windows.LocalAccountName is { } account)
        {
            shell.Add(LocalAccount(UnattendValidator.NormalizeAccountName(account), options));
            if (options.AutoLogonOnce)
            {
                shell.Add(AutoLogon(UnattendValidator.NormalizeAccountName(account), options));
            }
        }

        if (options.FirstLogonCommands.Count > 0)
        {
            var commands = new XElement(Unattend + "FirstLogonCommands");
            for (var i = 0; i < options.FirstLogonCommands.Count; i++)
            {
                commands.Add(Command("SynchronousCommand", i + 1, options.FirstLogonCommands[i], "CommandLine"));
            }

            shell.Add(commands);
        }

        if (!string.IsNullOrEmpty(windows.TimeZone))
        {
            shell.Add(new XElement(Unattend + "TimeZone", windows.TimeZone));
        }

        if (shell.HasElements)
        {
            pass.Add(shell);
        }

        if (windows.UiLanguage is { Length: > 0 } language)
        {
            var intl = Component("Microsoft-Windows-International-Core", options);
            intl.Add(
                new XElement(Unattend + "InputLocale", language),
                new XElement(Unattend + "SystemLocale", language),
                new XElement(Unattend + "UILanguage", language),
                new XElement(Unattend + "UserLocale", language));
            pass.Add(intl);
        }

        return pass;
    }

    private static XElement LocalAccount(string name, UnattendOptions options)
    {
        var account = new XElement(
            Unattend + "LocalAccount",
            new XAttribute(Wcm + "action", "add"),
            new XElement(Unattend + "Name", name),
            new XElement(Unattend + "Group", "Administrators"),
            new XElement(Unattend + "DisplayName", name));

        if (!string.IsNullOrEmpty(options.LocalAccountPassword))
        {
            // PlainText=false is only Base64, not encryption; the file is treated as sensitive either way.
            account.Add(new XElement(
                Unattend + "Password",
                new XElement(Unattend + "Value", options.LocalAccountPassword),
                new XElement(Unattend + "PlainText", "true")));
        }

        return new XElement(Unattend + "UserAccounts", new XElement(Unattend + "LocalAccounts", account));
    }

    private static XElement AutoLogon(string name, UnattendOptions options)
    {
        var logon = new XElement(
            Unattend + "AutoLogon",
            new XElement(Unattend + "Enabled", "true"),
            new XElement(Unattend + "LogonCount", "1"),
            new XElement(Unattend + "Username", name));

        if (!string.IsNullOrEmpty(options.LocalAccountPassword))
        {
            logon.Add(new XElement(
                Unattend + "Password",
                new XElement(Unattend + "Value", options.LocalAccountPassword),
                new XElement(Unattend + "PlainText", "true")));
        }

        return logon;
    }
}
