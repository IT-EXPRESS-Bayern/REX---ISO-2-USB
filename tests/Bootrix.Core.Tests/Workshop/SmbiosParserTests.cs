// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using Bootrix.Core.Workshop.Firmware;

namespace Bootrix.Core.Tests.Workshop;

public class SmbiosParserTests
{
    private static readonly string[] DmidecodePaths = ["/usr/sbin/dmidecode", "/usr/bin/dmidecode"];

    private static readonly byte[] SequentialUuid = [.. Enumerable.Range(0x10, 16).Select(i => (byte)i)];

    private static SmbiosTableBuilder FullTable() => new SmbiosTableBuilder()
        .AddBios("AcmeBIOS", "1.2.3", "04/21/2019")
        .AddSystem("Acme Corp", "Roadrunner 9000", "v2", "SN-12345", SequentialUuid, "SKU-77", "Looney")
        .AddBaseBoard("Acme Board Co", "RB-9", "rev A", "BSN-1")
        .AddChassis(0x80 | 0x09)
        .AddEndOfTable();

    [Fact]
    public void ParseStructureTable_ReadsTypes0To3AtTheirOffsets()
    {
        var data = SmbiosParser.ParseStructureTable(FullTable().Build(), 2, 8);

        Assert.Equal("AcmeBIOS", data.BiosVendor);
        Assert.Equal("1.2.3", data.BiosVersion);
        Assert.Equal(new DateOnly(2019, 4, 21), data.BiosReleaseDate);
        Assert.Equal("Acme Corp", data.SystemManufacturer);
        Assert.Equal("Roadrunner 9000", data.SystemProductName);
        Assert.Equal("v2", data.SystemVersion);
        Assert.Equal("SN-12345", data.SystemSerialNumber);
        Assert.Equal("SKU-77", data.SystemSku);
        Assert.Equal("Looney", data.SystemFamily);
        Assert.Equal("Acme Board Co", data.BoardManufacturer);
        Assert.Equal("RB-9", data.BoardProduct);
        Assert.Equal("rev A", data.BoardVersion);
        Assert.Equal("BSN-1", data.BoardSerialNumber);
        Assert.Equal((byte)9, data.ChassisType);
        Assert.Equal("2.8", data.Version);
    }

    [Fact]
    public void ParseRawFirmwareTable_SkipsTheEightByteHeaderAndReadsVersion()
    {
        var raw = FullTable().BuildRaw(3, 4);

        var data = SmbiosParser.ParseRawFirmwareTable(raw);

        Assert.NotNull(data);
        Assert.Equal("3.4", data.Version);
        Assert.Equal("Acme Corp", data.SystemManufacturer);
    }

    [Fact]
    public void ParseRawFirmwareTable_ClampsDeclaredLengthToTheBuffer()
    {
        var raw = FullTable().BuildRaw(3, 0);
        // The header claims more data than the buffer holds; the parser must not read past it.
        raw[4] = 0xFF;
        raw[5] = 0xFF;

        var data = SmbiosParser.ParseRawFirmwareTable(raw);

        Assert.Equal("Acme Corp", data!.SystemManufacturer);
    }

    [Fact]
    public void ParseRawFirmwareTable_ShortBuffer_ReturnsNull()
    {
        Assert.Null(SmbiosParser.ParseRawFirmwareTable(new byte[7]));
    }

    [Fact]
    public void Uuid_FromSmbios26_UsesLittleEndianFields()
    {
        var table = new SmbiosTableBuilder().AddSystem("A", "B", "C", "D", SequentialUuid).Build();

        var data = SmbiosParser.ParseStructureTable(table, 2, 6);

        Assert.Equal(Guid.Parse("13121110-1514-1716-1819-1a1b1c1d1e1f"), data.SystemUuid);
    }

    [Fact]
    public void Uuid_BeforeSmbios26_KeepsByteOrder()
    {
        var table = new SmbiosTableBuilder().AddSystem("A", "B", "C", "D", SequentialUuid).Build();

        var data = SmbiosParser.ParseStructureTable(table, 2, 5);

        Assert.Equal(Guid.Parse("10111213-1415-1617-1819-1a1b1c1d1e1f"), data.SystemUuid);
    }

    [Theory]
    [InlineData(0x00)]
    [InlineData(0xFF)]
    public void Uuid_AllZeroOrAllOnes_IsNotReported(byte fill)
    {
        var table = new SmbiosTableBuilder().AddSystem("A", "B", "C", "D", [.. Enumerable.Repeat(fill, 16)]).Build();

        Assert.Null(SmbiosParser.ParseStructureTable(table, 3, 0).SystemUuid);
    }

    [Fact]
    public void OemPlaceholders_AreDroppedInsteadOfReportedAsFacts()
    {
        var table = new SmbiosTableBuilder()
            .AddSystem("To Be Filled By O.E.M.", "Default string", "System Version", "Not Specified")
            .AddBaseBoard("ASUSTeK COMPUTER INC.", "PRIME B450M-A", "Rev X.0x", "To be filled by O.E.M.")
            .Build();

        var data = SmbiosParser.ParseStructureTable(table, 2, 8);

        Assert.Null(data.SystemManufacturer);
        Assert.Null(data.SystemProductName);
        Assert.Null(data.SystemVersion);
        Assert.Null(data.SystemSerialNumber);
        Assert.Equal("PRIME B450M-A", data.BoardProduct);
        Assert.Null(data.BoardSerialNumber);
    }

    [Fact]
    public void UnknownStructuresWithStrings_AreSkippedCleanly()
    {
        var table = new SmbiosTableBuilder()
            .AddOther(4, "Socket 1", "Intel", "Core")
            .AddOther(9)
            .AddSystem("Acme", "Box", "1", "S")
            .Build();

        Assert.Equal("Box", SmbiosParser.ParseStructureTable(table, 2, 8).SystemProductName);
    }

    [Fact]
    public void OnlyTheFirstBoardAndChassisCount()
    {
        var table = new SmbiosTableBuilder()
            .AddBaseBoard("First", "One", "1", "1")
            .AddBaseBoard("Second", "Two", "2", "2")
            .AddChassis(3)
            .AddChassis(23)
            .Build();

        var data = SmbiosParser.ParseStructureTable(table, 2, 8);

        Assert.Equal("First", data.BoardManufacturer);
        Assert.Equal((byte)3, data.ChassisType);
    }

    [Fact]
    public void StringIndexBeyondTheStringSet_YieldsNull()
    {
        var table = new SmbiosTableBuilder().AddBios("OnlyVendor", "v", "01/02/2003").Build();
        // Point the release date at string 9, which does not exist.
        table[0x08] = 9;

        var data = SmbiosParser.ParseStructureTable(table, 2, 8);

        Assert.Equal("OnlyVendor", data.BiosVendor);
        Assert.Null(data.BiosReleaseDate);
    }

    [Fact]
    public void TwoDigitYear_UsesThePivotOfTheCalendar()
    {
        var table = new SmbiosTableBuilder().AddBios("V", "1", "04/21/98").Build();

        Assert.Equal(new DateOnly(1998, 4, 21), SmbiosParser.ParseStructureTable(table, 2, 4).BiosReleaseDate);
    }

    [Fact]
    public void TruncatedTable_ReturnsWhatWasReadBeforeTheDamage()
    {
        var table = FullTable().Build();
        var cut = table[..(table.Length - 40)];

        var data = SmbiosParser.ParseStructureTable(cut, 2, 8);

        Assert.Equal("AcmeBIOS", data.BiosVendor);
        Assert.Equal("Acme Corp", data.SystemManufacturer);
    }

    [Fact]
    public void StructureWithImpossibleLength_StopsTheWalk()
    {
        var table = FullTable().Build();
        table[1] = 2;

        var data = SmbiosParser.ParseStructureTable(table, 2, 8);

        Assert.Null(data.BiosVendor);
    }

    [Fact]
    public void ShortSystemStructure_LeavesLaterFieldsNull()
    {
        // SMBIOS 2.0 type 1 ends after the UUID and wake-up type, without SKU and family.
        var table = new SmbiosTableBuilder().AddSystem("A", "B", "C", "D").Build();
        table[1] = 0x19;
        // Move the string set up behind the shortened formatted area.
        var shortened = table.Take(0x19).Concat(table.Skip(0x1B)).ToArray();

        var data = SmbiosParser.ParseStructureTable(shortened, 2, 0);

        Assert.Equal("B", data.SystemProductName);
        Assert.Null(data.SystemSku);
        Assert.Null(data.SystemFamily);
    }

    /// <summary>dmidecode is an independent implementation; the same bytes must yield the same strings.</summary>
    [Fact]
    public void Dump_AgreesWithDmidecode()
    {
        var dmidecode = DmidecodePaths.FirstOrDefault(File.Exists);
        if (dmidecode is null)
        {
            return;
        }

        var builder = FullTable();
        var path = Path.Combine(Path.GetTempPath(), $"bootrix-smbios-{Guid.NewGuid():N}.dmp");
        File.WriteAllBytes(path, builder.BuildDmidecodeDump(2, 8));
        try
        {
            var data = SmbiosParser.ParseStructureTable(builder.Build(), 2, 8);

            Assert.Equal(data.BiosVendor, Ask(dmidecode, path, "bios-vendor"));
            Assert.Equal(data.BiosVersion, Ask(dmidecode, path, "bios-version"));
            Assert.Equal(data.BiosReleaseDate!.Value.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture), Ask(dmidecode, path, "bios-release-date"));
            Assert.Equal(data.SystemManufacturer, Ask(dmidecode, path, "system-manufacturer"));
            Assert.Equal(data.SystemProductName, Ask(dmidecode, path, "system-product-name"));
            Assert.Equal(data.SystemVersion, Ask(dmidecode, path, "system-version"));
            Assert.Equal(data.SystemSerialNumber, Ask(dmidecode, path, "system-serial-number"));
            Assert.Equal(data.SystemUuid!.Value.ToString(), Ask(dmidecode, path, "system-uuid"), ignoreCase: true);
            Assert.Equal(data.BoardManufacturer, Ask(dmidecode, path, "baseboard-manufacturer"));
            Assert.Equal(data.BoardProduct, Ask(dmidecode, path, "baseboard-product-name"));
            Assert.Equal(data.BoardVersion, Ask(dmidecode, path, "baseboard-version"));
            Assert.Equal(data.BoardSerialNumber, Ask(dmidecode, path, "baseboard-serial-number"));
            Assert.Equal("Laptop", Ask(dmidecode, path, "chassis-type"));
            Assert.Equal((byte)9, data.ChassisType);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string Ask(string dmidecode, string dump, string keyword)
    {
        var start = new ProcessStartInfo(dmidecode) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add("--from-dump");
        start.ArgumentList.Add(dump);
        start.ArgumentList.Add("-s");
        start.ArgumentList.Add(keyword);

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        // Comment lines (starting with '#') carry the tool's banner, the value is the first other line.
        return output.Split('\n').First(l => l.Length > 0 && l[0] != '#').Trim();
    }
}
