// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Workshop.Hardware;

namespace Bootrix.Core.Tests.Workshop;

public class CpuNameHeuristicsTests
{
    [Theory]
    [InlineData("Intel(R) Core(TM) i7-7700K CPU @ 4.20GHz")]
    [InlineData("Intel(R) Core(TM) i5-6200U CPU @ 2.30GHz")]
    [InlineData("Intel(R) Core(TM) i7-4790 CPU @ 3.60GHz")]
    [InlineData("Intel(R) Core(TM) i3-3220 CPU @ 3.30GHz")]
    [InlineData("Intel(R) Core(TM) i5-2400 CPU @ 3.10GHz")]
    [InlineData("Intel(R) Core(TM) i7-920 CPU @ 2.67GHz")]
    [InlineData("Intel(R) Core(TM)2 Duo CPU E8400 @ 3.00GHz")]
    [InlineData("Intel(R) Core(TM)2 Quad CPU Q6600 @ 2.40GHz")]
    [InlineData("Intel(R) Pentium(R) 4 CPU 3.00GHz")]
    [InlineData("Intel(R) Pentium(R) Dual-Core CPU E5300 @ 2.60GHz")]
    [InlineData("Intel(R) Atom(TM) CPU N270 @ 1.60GHz")]
    [InlineData("AMD Ryzen 5 1600 Six-Core Processor")]
    [InlineData("AMD Ryzen 7 PRO 1700 Eight-Core Processor")]
    [InlineData("AMD FX(tm)-8350 Eight-Core Processor")]
    [InlineData("AMD Phenom(tm) II X4 955 Processor")]
    [InlineData("AMD Athlon(tm) II X2 250 Processor")]
    [InlineData("AMD A10-7800 Radeon R7, 12 Compute Cores 4C+8G")]
    [InlineData("AMD Athlon(tm) 64 X2 Dual Core Processor 5000+")]
    public void LooksUnsupportedByWindows11_RecognisesClearlyOldProcessors(string name)
    {
        Assert.True(CpuNameHeuristics.LooksUnsupportedByWindows11(name));
    }

    [Theory]
    [InlineData("Intel(R) Core(TM) i7-8550U CPU @ 1.80GHz")]
    [InlineData("Intel(R) Core(TM) i5-8250U CPU @ 1.60GHz")]
    [InlineData("Intel(R) Core(TM) i3-9100 CPU @ 3.60GHz")]
    [InlineData("Intel(R) Core(TM) i7-10710U CPU @ 1.10GHz")]
    [InlineData("11th Gen Intel(R) Core(TM) i7-1165G7 @ 2.80GHz")]
    [InlineData("12th Gen Intel(R) Core(TM) i5-1235U")]
    [InlineData("13th Gen Intel(R) Core(TM) i7-13700H")]
    [InlineData("Intel(R) Core(TM) i7-1065G7 CPU @ 1.30GHz")]
    [InlineData("Intel(R) Core(TM) Ultra 7 155H")]
    [InlineData("Intel(R) Core(TM) i3-N305")]
    [InlineData("Intel(R) N100")]
    [InlineData("AMD Ryzen 5 2600 Six-Core Processor")]
    [InlineData("AMD Ryzen 5 3600 6-Core Processor")]
    [InlineData("AMD Ryzen 7 5800X 8-Core Processor")]
    [InlineData("AMD Ryzen 5 PRO 4650U with Radeon Graphics")]
    [InlineData("AMD Ryzen 9 7940HS w/ Radeon 780M Graphics")]
    [InlineData("AMD Ryzen AI 9 HX 370 w/ Radeon 890M")]
    [InlineData("AMD Athlon Silver 3050U with Radeon Graphics")]
    [InlineData("AMD Ryzen Threadripper 1950X 16-Core Processor")]
    [InlineData("Snapdragon(R) X Elite - X1E78100 - Qualcomm(R) Oryon(TM) CPU")]
    [InlineData("Intel(R) Xeon(R) Gold 6130 CPU @ 2.10GHz")]
    [InlineData("")]
    [InlineData(null)]
    public void LooksUnsupportedByWindows11_LeavesEverythingElseUnknown(string? name)
    {
        Assert.False(CpuNameHeuristics.LooksUnsupportedByWindows11(name));
    }

    [Theory]
    [InlineData("Snapdragon(R) X2 Elite Extreme - X2E-96-100", true)]
    [InlineData("Snapdragon(R) X2 Elite - X2E-88-100", true)]
    [InlineData("Snapdragon X2 Plus X2P-64-100", true)]
    [InlineData("NVIDIA N1X", true)]
    [InlineData("Snapdragon(R) X Elite - X1E78100 - Qualcomm(R) Oryon(TM) CPU", false)]
    [InlineData("Snapdragon(R) X Plus - X1P64100 - Qualcomm(R) Oryon(TM) CPU", false)]
    [InlineData("Intel(R) Core(TM) Ultra 7 155H", false)]
    [InlineData(null, false)]
    public void IsNewArmPlatform_MatchesTheNamedChipFamilies(string? name, bool expected)
    {
        Assert.Equal(expected, CpuNameHeuristics.IsNewArmPlatform(name));
    }
}
