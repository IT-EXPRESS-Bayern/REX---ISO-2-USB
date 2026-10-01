// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections;
using System.Globalization;
using System.Resources;
using Bootrix.Core.Localization;
using Bootrix.Core.Storage;

namespace Bootrix.Core.Tests.Localization;

public class UiStringsTests
{
    private static readonly ResourceManager Manager = new("Bootrix.Core.Resources.Ui", typeof(Localizer).Assembly);

    private static HashSet<string> Keys(CultureInfo culture)
    {
        var set = Manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false)!;
        return [.. set.Cast<DictionaryEntry>().Select(e => (string)e.Key)];
    }

    [Fact]
    public void EveryKeyExistsInGermanAndEnglish()
    {
        var german = Keys(CultureInfo.InvariantCulture);
        var english = Keys(CultureInfo.GetCultureInfo("en"));

        Assert.Empty(german.Except(english));
        Assert.Empty(english.Except(german));
    }

    [Fact]
    public void NoTextIsEmpty()
    {
        foreach (var culture in new[] { CultureInfo.InvariantCulture, CultureInfo.GetCultureInfo("en") })
        {
            var set = Manager.GetResourceSet(culture, true, false)!;
            Assert.All(set.Cast<DictionaryEntry>(), e => Assert.False(string.IsNullOrWhiteSpace((string?)e.Value), (string)e.Key));
        }
    }

    [Fact]
    public void PlaceholdersMatchBetweenTheLanguages()
    {
        var german = Manager.GetResourceSet(CultureInfo.InvariantCulture, true, false)!.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
        var english = Manager.GetResourceSet(CultureInfo.GetCultureInfo("en"), true, false)!.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);

        foreach (var (key, text) in german)
        {
            Assert.Equal(Placeholders(text), Placeholders(english[key]));
        }
    }

    [Fact]
    public void EveryJobStepHasATitle()
    {
        // Keys of the steps that jobs report; a new step needs a title here and in both resx files.
        string[] steps =
        [
            "Raw.CheckTargets", "Raw.Prepare", "Raw.Write", "Raw.Finish",
            "Raw.Persistence", "Raw.RelocateGpt",
            "Restore.Check", "Restore.Wipe", "Restore.Format", "Restore.Finish", "Verify.Check", "Verify.Compare",
            "Write.Customize", "Write.Windows.Source", "Write.Windows.Copy", "Write.Windows.BootCode", "Write.Windows.Verify",
            "Tiny.FetchSource", "Tiny.DeliverImage", "Tiny.Prepare", "Tiny.CopyMedia", "Tiny.ExportEdition", "Tiny.Mount", "Tiny.RemoveApps", "Tiny.RemoveComponents",
            "Tiny.RemoveFiles", "Tiny.Registry", "Tiny.CleanupStore", "Tiny.Unmount", "Tiny.Compress", "Tiny.PatchBootImage",
            "Tiny.Unattend", "Tiny.CreateIso",
            "Write.CheckTargets", "Write.Prepare", "Write.Finish", "Write.Linux.Copy", "Write.Linux.Bootloader", "Write.Linux.Verify",
            "Burn.CheckMedia", "Burn.HashSource", "Burn.Write", "Burn.ReadBack", "Burn.Finish", "Folder.Scan",
            "Erase.Check", "Erase.Run", "Erase.Finish", "Rip.Check", "Rip.Read", "Rip.Finish",
            "Dos.PrepareSystem", "Dos.CopyFiles", "Dos.WriteMbr", "Dos.Superfloppy",
        ];

        Assert.All(steps, step => Assert.True(Localizer.Default.Has("Step." + step), step));
    }

    [Fact]
    public void EveryBlockingReasonHasAText()
    {
        var reasons = Enum.GetValues<DeviceProtection>().Where(p => p != DeviceProtection.None && p != DeviceProtection.HardBlock);

        Assert.All(reasons, reason => Assert.True(Localizer.Default.Has("Protect." + reason), reason.ToString()));
    }

    private static string Placeholders(string text) =>
        string.Join(',', System.Text.RegularExpressions.Regex.Matches(text, @"\{\d+\}").Select(m => m.Value).Order(StringComparer.Ordinal));
}
