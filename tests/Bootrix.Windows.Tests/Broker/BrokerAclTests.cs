// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Security.AccessControl;
using System.Text.RegularExpressions;
using Bootrix.Windows.Broker;

namespace Bootrix.Windows.Tests.Broker;

public partial class BrokerAclTests
{
    private const string User = "S-1-5-21-1004336348-1177238915-682003330-1001";
    private const string Admin = "S-1-5-21-1004336348-1177238915-682003330-500";
    private const string LocalSystem = "S-1-5-18";
    private const string Administrators = "S-1-5-32-544";
    private const int FileAllAccess = 0x1f01ff;

    private sealed record Rule(string Type, string Sid, int Mask);

    /// <summary>
    /// Reads the DACL part of an SDDL string without the Windows ACL classes, which do not exist on other systems,
    /// so that what the broker would put on its pipe can be checked everywhere. Only the forms the broker writes are understood.
    /// </summary>
    private static (bool Protected, List<Rule> Rules) Parse(string sddl)
    {
        Assert.StartsWith("D:", sddl, StringComparison.Ordinal);
        var body = sddl[2..];
        var isProtected = body.StartsWith('P');
        if (isProtected)
        {
            body = body[1..];
        }

        var rules = new List<Rule>();
        var consumed = 0;
        foreach (Match match in AceText().Matches(body))
        {
            Assert.Equal(consumed, match.Index);
            consumed += match.Length;
            rules.Add(new Rule(match.Groups["type"].Value, ResolveSid(match.Groups["sid"].Value), ResolveMask(match.Groups["mask"].Value)));
        }

        Assert.Equal(body.Length, consumed);
        return (isProtected, rules);
    }

    private static string ResolveSid(string text) => text switch
    {
        "SY" => LocalSystem,
        "BA" => Administrators,
        _ => text,
    };

    private static int ResolveMask(string text) => text switch
    {
        "FA" => FileAllAccess,
        _ when text.StartsWith("0x", StringComparison.Ordinal) => int.Parse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
        _ => throw new FormatException($"Unexpected rights '{text}'"),
    };

    [GeneratedRegex(@"\((?<type>[A-Z]);;(?<mask>[A-Za-z0-9]+);;;(?<sid>[A-Z0-9\-]+)\)")]
    private static partial Regex AceText();

    [Fact]
    public void PipeSddl_NamesTheUserTheBrokerAndSystemAndNobodyElse()
    {
        Assert.Equal($"D:P(A;;FA;;;SY)(A;;FA;;;{Admin})(A;;0x12019f;;;{User})", BrokerAcl.PipeSddl(User, Admin));
    }

    [Fact]
    public void PipeSddl_GrantsExactlyThreeRulesAndInheritsNothing()
    {
        var (isProtected, rules) = Parse(BrokerAcl.PipeSddl(User, Admin));

        Assert.True(isProtected);
        Assert.Equal(3, rules.Count);
        Assert.All(rules, r => Assert.Equal("A", r.Type));
        Assert.Equal([LocalSystem, Admin, User], rules.Select(r => r.Sid));
        Assert.Equal([FileAllAccess, FileAllAccess, 0x12019f], rules.Select(r => r.Mask));
    }

    [Fact]
    public void PipeSddl_GivesTheUserWhatAnInOutPipeClientAsksFor()
    {
        // A .NET client opens the pipe with GENERIC_READ | GENERIC_WRITE, which the system maps to FILE_GENERIC_READ |
        // FILE_GENERIC_WRITE. A narrower mask would lock the client out; FILE_CREATE_PIPE_INSTANCE shares its bit
        // with FILE_APPEND_DATA and is part of that, so what stops the user from creating more instances is the
        // instance limit of the pipe. Pinned so that nobody "tightens" the mask into a connect failure.
        const int fileGenericRead = 0x120089;
        const int fileGenericWrite = 0x120116;
        var (_, rules) = Parse(BrokerAcl.PipeSddl(User, Admin));

        Assert.Equal(fileGenericRead | fileGenericWrite, rules.Single(r => r.Sid == User).Mask);
    }

    [Fact]
    public void PipeSddl_WhenTheUserStartedTheBrokerHerself_ListsHerOnceWithFullControl()
    {
        var (_, rules) = Parse(BrokerAcl.PipeSddl(User, User));

        Assert.Equal([LocalSystem, User], rules.Select(r => r.Sid));
        Assert.All(rules, r => Assert.Equal(FileAllAccess, r.Mask));
    }

    [Fact]
    public void PipeSddl_NeverMentionsEveryoneAnonymousOrAuthenticatedUsers()
    {
        var sddl = BrokerAcl.PipeSddl(User, Admin);

        foreach (var forbidden in new[] { "WD", "AN", "AU", "BU", "S-1-1-0", "S-1-5-7", "S-1-5-11", "S-1-5-32-545" })
        {
            Assert.DoesNotContain(";" + forbidden + ")", sddl, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SecretFileSddl_AllowsTheUserSystemAndAdministrators()
    {
        var sddl = BrokerAcl.SecretFileSddl(User);
        var (isProtected, rules) = Parse(sddl);

        Assert.Equal($"D:P(A;;FA;;;{User})(A;;FA;;;SY)(A;;FA;;;BA)", sddl);
        Assert.True(isProtected);
        Assert.Equal([User, LocalSystem, Administrators], rules.Select(r => r.Sid));
        Assert.All(rules, r => Assert.Equal(FileAllAccess, r.Mask));
    }

    [Fact]
    public void Descriptors_AreAcceptedByWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        foreach (var sddl in new[] { BrokerAcl.PipeSddl(User, Admin), BrokerAcl.PipeSddl(User, User), BrokerAcl.SecretFileSddl(User) })
        {
            var descriptor = new RawSecurityDescriptor(sddl);

            Assert.True(descriptor.ControlFlags.HasFlag(ControlFlags.DiscretionaryAclProtected));
            Assert.Equal(Parse(sddl).Rules.Select(r => (r.Sid, r.Mask)), descriptor.DiscretionaryAcl!.Cast<CommonAce>().Select(a => (a.SecurityIdentifier.Value, a.AccessMask)));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("S-1-5")]
    [InlineData("Everyone")]
    [InlineData("S-1-5-18)(A;;GA;;;WD")]
    [InlineData("S-1-5-21-1-2-3-4 ")]
    [InlineData("S-1-5-21-1-2-3-4\n")]
    public void NothingThatIsNoSid_CanBeWrittenIntoTheDescriptor(string sid)
    {
        Assert.Throws<ArgumentException>(() => BrokerAcl.PipeSddl(sid, Admin));
        Assert.Throws<ArgumentException>(() => BrokerAcl.PipeSddl(User, sid));
        Assert.Throws<ArgumentException>(() => BrokerAcl.SecretFileSddl(sid));
    }

    [Theory]
    [InlineData("S-1-5-18", true)]
    [InlineData("S-1-5-21-1-2-3-1001", true)]
    [InlineData("s-1-5-18", false)]
    [InlineData("S-2-5-18", false)]
    [InlineData("S-1-5", false)]
    [InlineData(null, false)]
    public void IsSid_RecognisesTheTextForm(string? value, bool expected)
    {
        Assert.Equal(expected, BrokerAcl.IsSid(value));
    }
}
