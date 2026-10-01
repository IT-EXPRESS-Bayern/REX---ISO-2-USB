// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using Bootrix.Windows.Broker;

namespace Bootrix.Windows.Tests.Broker;

public class LaunchCommandTests
{
    private static readonly string Exe = Path.Combine(Path.GetTempPath(), "bootrix", "Bootrix.exe");

    [Fact]
    public void StartsTheProgramElevatedThroughTheShellWithoutAWindow()
    {
        var info = LaunchCommand.Build(Exe, null, "--broker --pipe x");

        Assert.Equal(Exe, info.FileName);
        Assert.Equal("--broker --pipe x", info.Arguments);
        Assert.True(info.UseShellExecute);
        Assert.Equal("runas", info.Verb);
        Assert.Equal(ProcessWindowStyle.Hidden, info.WindowStyle);
        Assert.Equal(Path.GetDirectoryName(Exe), info.WorkingDirectory);
    }

    [Fact]
    public void UnderTheDotnetHost_TheAssemblyIsTheFirstArgument()
    {
        var host = Path.Combine(Path.GetTempPath(), "dotnet", "dotnet.exe");
        var assembly = Path.Combine(Path.GetTempPath(), "my build", "Bootrix.dll");

        var info = LaunchCommand.Build(host, assembly, "--broker");

        Assert.Equal(host, info.FileName);
        Assert.Equal($"\"{assembly}\" --broker", info.Arguments);
    }

    [Fact]
    public void HostWithoutAssembly_IsRefused()
    {
        Assert.Throws<InvalidOperationException>(() => LaunchCommand.Build(Path.Combine(Path.GetTempPath(), "dotnet"), null, "--broker"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void UnknownProgramPath_IsRefused(string? path)
    {
        Assert.Throws<InvalidOperationException>(() => LaunchCommand.Build(path, null, "--broker"));
    }
}
