// SPDX-License-Identifier: GPL-3.0-or-later
using Bootrix.Windows.Broker;

namespace Bootrix.Windows.Tests.Broker;

public class BrokerPipeNameTests
{
    [Fact]
    public void Name_ContainsTheSidAndTheRandomPartInHex()
    {
        var random = Convert.FromHexString("00112233445566778899aabbccddeeff");

        var name = BrokerPipeName.Create(BrokerOptionsTests.Sid, random);

        Assert.Equal("bootrix-broker-" + BrokerOptionsTests.Sid + "-00112233445566778899aabbccddeeff", name);
    }

    [Fact]
    public void Name_IsDifferentEveryTime()
    {
        var names = Enumerable.Range(0, 50).Select(_ => BrokerPipeName.Create(BrokerOptionsTests.Sid)).ToHashSet();

        Assert.Equal(50, names.Count);
    }

    [Fact]
    public void CreatedNames_AreRecognisedAndYieldTheirSid()
    {
        var name = BrokerPipeName.Create(BrokerOptionsTests.Sid);

        Assert.True(BrokerPipeName.TryParse(name, out var sid));
        Assert.Equal(BrokerOptionsTests.Sid, sid);
    }

    [Fact]
    public void FullPath_IsTheNameUnderThePipeRoot()
    {
        Assert.Equal(@"\\.\pipe\bootrix-broker-x", BrokerPipeName.FullPath("bootrix-broker-x"));
    }

    [Theory]
    [InlineData("not-a-sid")]
    [InlineData("S-1-5")]
    [InlineData("S-1-5-21-1-2-3\\..\\x")]
    [InlineData("")]
    public void Create_RefusesWhatIsNoSid(string sid)
    {
        Assert.Throws<ArgumentException>(() => BrokerPipeName.Create(sid, new byte[16]));
    }

    [Fact]
    public void Create_RefusesARandomPartOfTheWrongSize()
    {
        Assert.Throws<ArgumentException>(() => BrokerPipeName.Create(BrokerOptionsTests.Sid, new byte[8]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bootrix-broker-")]
    [InlineData("bootrix-broker-S-1-5-21-1-2-3-4")]
    [InlineData("bootrix-broker-S-1-5-18-0123456789abcdef0123456789abcdeg")]
    [InlineData(" bootrix-broker-S-1-5-18-0123456789abcdef0123456789abcdef")]
    [InlineData("bootrix-broker-S-1-5-18-0123456789abcdef0123456789abcdef ")]
    [InlineData("bootrix-broker-S-1-5-18-0123456789abcdef0123456789abcdef\n")]
    public void TryParse_RefusesOtherNames(string? name)
    {
        Assert.False(BrokerPipeName.TryParse(name, out var sid));
        Assert.Equal("", sid);
    }

    [Theory]
    [InlineData("S-1-5-18")]
    [InlineData("S-1-5-21-1004336348-1177238915-682003330-512")]
    [InlineData("S-1-12-1-1234567890-1234567890-1234567890-1234567890")]
    public void Names_AcceptRealWorldSids(string sid)
    {
        Assert.True(BrokerPipeName.TryParse(BrokerPipeName.Create(sid, new byte[16]), out var parsed));
        Assert.Equal(sid, parsed);
    }
}
