// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Model;
using Bootrix.Core.Presentation;
using Bootrix.Core.Profiles;

namespace Bootrix.Core.Tests.Presentation;

public class CliCommandBuilderTests
{
    [Fact]
    public void DefaultsProduceOnlyTheEssentials()
    {
        var line = CliCommandBuilder.Write(new JobSpec(), @"C:\iso\ubuntu.iso", ["3"]);

        Assert.Equal(@"bootrix-cli write C:\iso\ubuntu.iso -d 3", line);
    }

    [Fact]
    public void OnlyWhatDiffersFromTheDefaultsIsWritten()
    {
        var spec = new JobSpec
        {
            Target = new TargetOptions { Scheme = PartitionScheme.Gpt, FileSystem = FileSystemKind.Ntfs, Label = "MEIN STICK", PersistenceMegabytes = 4096 },
            Windows = new WindowsSetupOptions { BypassTpm = true, LocalAccountName = "Anna", DriverFolders = [@"C:\Treiber 1", @"D:\drv"] },
            Verify = new VerifyOptions { ReadBack = false },
        };

        var line = CliCommandBuilder.Write(spec, @"C:\Meine ISOs\win11.iso", ["disk3", "SN 123"]);

        Assert.Equal(
            "bootrix-cli write \"C:\\Meine ISOs\\win11.iso\" -d disk3 -d \"SN 123\" --scheme gpt --fs ntfs --label \"MEIN STICK\" --persistence 4096 --bypass-tpm --local-account Anna --drivers \"C:\\Treiber 1\" D:\\drv --no-verify",
            line);
    }

    [Fact]
    public void QuotesInValuesAreEscaped()
    {
        var line = CliCommandBuilder.Write(new JobSpec { Target = new TargetOptions { Label = "A\"B" } }, null, []);

        Assert.Equal("bootrix-cli write --label \"A\\\"B\"", line);
    }
}
