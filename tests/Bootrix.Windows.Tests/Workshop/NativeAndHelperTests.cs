// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Bootrix.Core.Workshop.Hardware;
using Bootrix.Windows.Workshop;

namespace Bootrix.Windows.Tests.Workshop;

public class NativeAndHelperTests
{
    [Fact]
    public void MemoryStatusEx_HasTheSizeWindowsExpects()
    {
        Assert.Equal(64, Marshal.SizeOf<FirmwareNative.MemoryStatusEx>());
    }

    [Fact]
    public void TbsDeviceInfo_HasTheSizeOfTBS_DEVICE_INFO()
    {
        Assert.Equal(16, Marshal.SizeOf<TbsNative.DeviceInfo>());
    }

    [Fact]
    public void FirmwareTableSignatures_UseTheByteOrderTheApiExpects()
    {
        // Provider: the characters as a multi-character constant, first character in the high byte.
        Assert.Equal(0x41435049u, FirmwareTables.AcpiProvider);
        Assert.Equal(0x52534D42u, FirmwareTables.SmbiosProvider);

        // Table ID: the signature bytes as they lie in memory, first character in the low byte.
        Assert.Equal(0x4D44534Du, FirmwareTables.AcpiTableId("MSDM"));
        Assert.Equal(0x50434146u, FirmwareTables.AcpiTableId("FACP"));
    }

    [Theory]
    [InlineData("00000407", "0407:00000407")]
    [InlineData("00010409", "0409:00010409")]
    [InlineData("D0010409", "0409:D0010409")]
    [InlineData("0407", "0407:0407")]
    [InlineData("407", "407")]
    public void InputLocale_PrefixesTheLanguageIdOfTheLayout(string layoutId, string expected)
    {
        Assert.Equal(expected, RunningSystemReader.InputLocale(layoutId));
    }

    [Theory]
    [InlineData("20230315000000.000000+000", 2023, 3, 15)]
    [InlineData("20190621000000.******+***", 2019, 6, 21)]
    [InlineData("20061101", 2006, 11, 1)]
    public void ParseCimDate_ReadsTheDayOfACimDateTime(string text, int year, int month, int day)
    {
        Assert.Equal(new DateOnly(year, month, day), PnpDeviceReader.ParseCimDate(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2023")]
    [InlineData("notadate")]
    public void ParseCimDate_Garbage_IsNull(string? text)
    {
        Assert.Null(PnpDeviceReader.ParseCimDate(text));
    }

    [Theory]
    [InlineData(Architecture.X64, CpuArchitecture.X64)]
    [InlineData(Architecture.X86, CpuArchitecture.X86)]
    [InlineData(Architecture.Arm64, CpuArchitecture.Arm64)]
    [InlineData(Architecture.Arm, CpuArchitecture.Arm)]
    [InlineData(Architecture.Wasm, CpuArchitecture.Unknown)]
    public void ArchitectureOf_MapsTheRuntimeEnumeration(Architecture runtime, CpuArchitecture expected)
    {
        Assert.Equal(expected, CpuReader.ArchitectureOf(runtime));
    }

    [WindowsFact]
    public async Task SystemTool_RunsAProgramFromSystem32AndReturnsItsOutput()
    {
        var (exitCode, output) = await SystemTool.RunAsync("cmd.exe", ["/c", "echo", "bootrix"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("bootrix", output, StringComparison.Ordinal);
    }

    [WindowsFact]
    public async Task SystemTool_MissingProgram_Throws()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => SystemTool.RunAsync("bootrix-no-such-tool.exe", [], CancellationToken.None));
    }

    [WindowsFact]
    public async Task SystemTool_Cancellation_StopsTheProcess()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SystemTool.RunAsync("ping.exe", ["-n", "30", "127.0.0.1"], cancellation.Token));
    }
}
