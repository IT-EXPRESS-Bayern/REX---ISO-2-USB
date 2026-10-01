// SPDX-License-Identifier: GPL-3.0-or-later
using System.CommandLine;
using Bootrix.Cli.Output;
using Bootrix.Core.Storage;

namespace Bootrix.Cli.Commands;

internal static class DisksCommand
{
    public static Command Create(IDiskService disks)
    {
        var all = new Option<bool>("--all") { Description = "Also list internal, virtual and blocked disks." };
        var usbHdd = new Option<bool>("--usb-hdd") { Description = "Also list USB hard disks and SSDs." };
        var json = new Option<bool>("--json") { Description = "Machine readable output." };

        var command = new Command("disks", "List the drives Bootrix can write to.") { all, usbHdd, json };
        command.SetAction(parse =>
        {
            var writer = new ConsoleWriter(parse.GetValue(json));
            try
            {
                var filter = parse.GetValue(all)
                    ? DeviceSelector.ListAll
                    : new DiskFilter { IncludeUsbHardDisks = parse.GetValue(usbHdd), IncludeBlocked = false };
                var devices = disks.Enumerate(filter);

                if (writer.Json)
                {
                    foreach (var device in devices)
                    {
                        writer.WriteObject(Describe(device));
                    }
                }
                else
                {
                    writer.WriteTable(
                        ["Disk", "Letters", "Size", "Bus", "Name", "Serial", "Style", "Notes"],
                        [.. devices.Select(d => (IReadOnlyList<string>)
                        [
                            d.DiskNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            d.DriveLetters,
                            ConsoleWriter.FormatSize(d.SizeBytes),
                            d.Bus.ToString(),
                            d.DisplayName,
                            d.Serial ?? "",
                            d.PartitionStyle.ToString(),
                            Notes(d),
                        ])]);
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

    private static object Describe(StorageDevice d) => new
    {
        disk = d.DiskNumber,
        path = d.DevicePath,
        name = d.DisplayName,
        serial = d.Serial,
        bus = d.Bus.ToString(),
        sizeBytes = d.SizeBytes,
        sectorSize = d.LogicalSectorSize,
        removable = d.IsRemovableMedia,
        style = d.PartitionStyle.ToString(),
        letters = d.DriveLetters,
        blocked = d.IsBlocked,
        protection = d.Protection.ToString(),
        partitions = d.Partitions.Count,
    };

    private static string Notes(StorageDevice d)
    {
        var notes = new List<string>();
        if (d.IsBlocked)
        {
            notes.Add("PROTECTED: " + d.Protection);
        }
        else if ((d.Protection & DeviceProtection.WriteProtected) != 0)
        {
            notes.Add("write protected");
        }
        else if ((d.Protection & DeviceProtection.InternalFixedDisk) != 0)
        {
            notes.Add("internal");
        }

        return string.Join(", ", notes);
    }
}
