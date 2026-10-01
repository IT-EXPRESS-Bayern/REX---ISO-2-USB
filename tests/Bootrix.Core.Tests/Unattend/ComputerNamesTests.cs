// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Core.Unattend;

namespace Bootrix.Core.Tests.Unattend;

public class ComputerNamesTests
{
    [Fact]
    public void ResolvesTokens()
    {
        var context = new ComputerNameContext("4C530001220125117313", 7, new DateOnly(2026, 10, 1), new Random(1));

        Assert.Equal("PC-117313", ComputerNames.Resolve("PC-{serial}", context));
        Assert.Equal("KUNDE-7", ComputerNames.Resolve("kunde-{n}", context));
        Assert.Equal("KUNDE-007", ComputerNames.Resolve("kunde-{n3}", context));
        Assert.Equal("W-261001", ComputerNames.Resolve("w-{date}", context));
    }

    [Fact]
    public void RandomTokensProduceValidNames()
    {
        var random = new Random(3);
        for (var i = 0; i < 200; i++)
        {
            var name = ComputerNames.Resolve("PC-{rand}{rand6}", new ComputerNameContext(Random: random));

            Assert.Empty(UnattendValidator.ValidateComputerName(name));
        }
    }

    [Fact]
    public void UnknownTokensStayAsTheyAreAndAreCaughtByValidation()
    {
        var name = ComputerNames.Resolve("PC-{bogus}");

        Assert.NotEmpty(UnattendValidator.ValidateComputerName(name));
    }

    [Fact]
    public void SerialWithSymbolsIsCleaned()
    {
        Assert.Equal("ABC123", ComputerNames.Resolve("{serial}", new ComputerNameContext("AB-C 1_23")));
    }
}
