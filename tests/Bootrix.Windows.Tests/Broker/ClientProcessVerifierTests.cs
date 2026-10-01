// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Windows.Broker;

namespace Bootrix.Windows.Tests.Broker;

public class ClientProcessVerifierTests
{
    private const string Own = @"C:\Program Files\Bootrix\Bootrix.exe";

    private static ClientProcessVerifier Create(int parent, params (int Pid, string? Path)[] processes) =>
        new(Own, parent, pid => processes.Where(p => p.Pid == pid).Select(p => p.Path).FirstOrDefault());

    [Fact]
    public void TheProcessThatStartedTheBroker_IsTrusted()
    {
        Assert.True(Create(100, (100, Own)).IsTrusted(100));
    }

    [Fact]
    public void ThePathIsComparedWithoutRegardToCase()
    {
        Assert.True(Create(100, (100, Own.ToUpperInvariant())).IsTrusted(100));
    }

    [Fact]
    public void AnotherProcessOfTheSameProgram_IsNotTrusted()
    {
        Assert.False(Create(100, (100, Own), (200, Own)).IsTrusted(200));
    }

    [Fact]
    public void TheRightProcessIdRunningAnotherProgram_IsNotTrusted()
    {
        Assert.False(Create(100, (100, @"C:\Users\Max\Downloads\totally-bootrix.exe")).IsTrusted(100));
    }

    [Fact]
    public void ACopyOfTheProgramInAnotherFolder_IsNotTrusted()
    {
        Assert.False(Create(100, (100, @"C:\Users\Max\AppData\Local\Temp\Bootrix.exe")).IsTrusted(100));
    }

    [Fact]
    public void AProcessThatCannotBeInspected_IsNotTrusted()
    {
        Assert.False(Create(100, (100, null)).IsTrusted(100));
        Assert.False(Create(100).IsTrusted(100));
    }

    [Fact]
    public void AnImagePathOfTheRightProgramWithAnExtraSuffix_IsNotTrusted()
    {
        Assert.False(Create(100, (100, Own + ".bak")).IsTrusted(100));
        Assert.False(Create(100, (100, Own + " ")).IsTrusted(100));
    }
}
