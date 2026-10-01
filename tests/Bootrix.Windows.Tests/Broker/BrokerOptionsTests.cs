// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using Bootrix.Windows.Broker;

namespace Bootrix.Windows.Tests.Broker;

public class BrokerOptionsTests
{
    internal const string Sid = "S-1-5-21-1004336348-1177238915-682003330-1001";
    internal const string Pipe = "bootrix-broker-" + Sid + "-0123456789abcdef0123456789abcdef";
    internal const string SecretFile = @"C:\Users\Max Muster\AppData\Local\Temp\bootrix-broker-0123456789abcdef0123456789abcdef.key";

    private static string[] Valid() =>
        ["--broker", "--pipe", Pipe, "--user-sid", Sid, "--parent-pid", "4242", "--secret-file", SecretFile];

    private static BrokerOptions Parse(params string[] args)
    {
        Assert.True(BrokerOptions.TryParse(args, out var options, out var error), error);
        return options!;
    }

    private static string Error(params string[] args)
    {
        Assert.False(BrokerOptions.TryParse(args, out var options, out var error));
        Assert.Null(options);
        return error;
    }

    [Fact]
    public void ValidArguments_AreParsed()
    {
        var options = Parse(Valid());

        Assert.Equal(Pipe, options.PipeName);
        Assert.Equal(Sid, options.UserSid);
        Assert.Equal(4242, options.ParentProcessId);
        Assert.Equal(SecretFile, options.SecretFile);
    }

    [Fact]
    public void ArgumentOrder_DoesNotMatter()
    {
        var options = Parse("--parent-pid", "7", "--secret-file", SecretFile, "--user-sid", Sid, "--pipe", Pipe, "--broker");

        Assert.Equal(7, options.ParentProcessId);
    }

    [Fact]
    public void ToArguments_ParsesBackToTheSameOptions()
    {
        var options = new BrokerOptions(Pipe, Sid, 31337, SecretFile);

        Assert.Equal(options, Parse([.. options.ToArguments()]));
    }

    [Fact]
    public void ToCommandLine_QuotesWhatNeedsIt()
    {
        var line = new BrokerOptions(Pipe, Sid, 5, SecretFile).ToCommandLine();

        Assert.Equal($"--broker --pipe {Pipe} --user-sid {Sid} --parent-pid 5 --secret-file \"{SecretFile}\"", line);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("two words", "\"two words\"")]
    [InlineData("", "\"\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\dir with space\", "\"C:\\dir with space\\\\\"")]
    public void Quote_FollowsTheWindowsRules(string argument, string expected)
    {
        Assert.Equal(expected, BrokerOptions.Quote(argument));
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("two words")]
    [InlineData("")]
    [InlineData("say \"hi\"")]
    [InlineData(@"C:\dir with space\")]
    [InlineData(@"a\\""b c")]
    [InlineData(@"\\server\share\x y\")]
    [InlineData("tab\tseparated")]
    public void Quote_IsUndoneByTheWindowsCommandLineParser(string argument)
    {
        var line = $"first {BrokerOptions.Quote(argument)} last";

        Assert.Equal(["first", argument, "last"], SplitLikeWindows(line));
    }

    [Fact]
    public void IsBrokerRequest_LooksOnlyForTheFlag()
    {
        Assert.True(BrokerOptions.IsBrokerRequest(["x", "--broker"]));
        Assert.True(BrokerOptions.IsBrokerRequest(["--broker", "garbage"]));
        Assert.False(BrokerOptions.IsBrokerRequest([]));
        Assert.False(BrokerOptions.IsBrokerRequest(["--Broker", "broker", "--brokers", @"C:\--broker"]));
    }

    [Fact]
    public void MissingBrokerFlag_IsRefused()
    {
        Assert.Contains("--broker", Error(Valid().Skip(1).ToArray()), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--pipe")]
    [InlineData("--user-sid")]
    [InlineData("--parent-pid")]
    [InlineData("--secret-file")]
    public void EachOptionIsRequired(string name)
    {
        var args = Valid().ToList();
        var index = args.IndexOf(name);
        args.RemoveRange(index, 2);

        Assert.False(BrokerOptions.TryParse(args, out _, out _));
    }

    [Theory]
    [InlineData("--pipe")]
    [InlineData("--user-sid")]
    [InlineData("--parent-pid")]
    [InlineData("--secret-file")]
    [InlineData("--broker")]
    public void OptionsGivenTwice_AreRefused(string name)
    {
        var args = Valid().ToList();
        var index = args.IndexOf(name);
        args.AddRange(name == "--broker" ? new[] { name } : args.Skip(index).Take(2).ToArray());

        Assert.Contains("twice", Error([.. args]), StringComparison.Ordinal);
    }

    [Fact]
    public void OptionWithoutValue_IsRefused()
    {
        Assert.Contains("needs a value", Error("--broker", "--pipe", Pipe, "--user-sid", Sid, "--parent-pid", "4", "--secret-file"), StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownArgument_IsRefused()
    {
        Assert.Contains("unknown", Error([.. Valid(), "--elevate-everything"]), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("bootrix-broker-" + "S-1-5-21-1-2-3-1001-0123456789abcdef0123456789abcde")]
    [InlineData("bootrix-broker-" + "S-1-5-21-1-2-3-1001-0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("evil-S-1-5-21-1-2-3-1001-0123456789abcdef0123456789abcdef")]
    [InlineData(@"..\..\bootrix-broker-S-1-5-21-1-2-3-1001-0123456789abcdef0123456789abcdef")]
    [InlineData("bootrix-broker-S-1-5-21-1-2-3-1001-0123456789abcdef0123456789abcdef\\x")]
    [InlineData("bootrix-broker-notasid-0123456789abcdef0123456789abcdef")]
    [InlineData("")]
    public void PipeNameThatIsNotOurs_IsRefused(string pipe)
    {
        Assert.Contains("--pipe", Error("--broker", "--pipe", pipe, "--user-sid", Sid, "--parent-pid", "4", "--secret-file", SecretFile), StringComparison.Ordinal);
    }

    [Fact]
    public void UserSidThatDiffersFromThePipeName_IsRefused()
    {
        var other = "S-1-5-21-1004336348-1177238915-682003330-1002";

        Assert.Contains("--user-sid", Error("--broker", "--pipe", Pipe, "--user-sid", other, "--parent-pid", "4", "--secret-file", SecretFile), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("99999999999")]
    [InlineData("+7")]
    [InlineData("1 2")]
    public void ParentPidThatIsNoProcessId_IsRefused(string pid)
    {
        Assert.Contains("--parent-pid", Error("--broker", "--pipe", Pipe, "--user-sid", Sid, "--parent-pid", pid, "--secret-file", SecretFile), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"relative\bootrix-broker-0123456789abcdef0123456789abcdef.key")]
    [InlineData(@"C:\Windows\System32\config\SAM")]
    [InlineData(@"C:\Users\x\..\..\Windows\bootrix-broker-0123456789abcdef0123456789abcdef.key")]
    public void SecretFileThatIsNotOurs_IsRefused(string file)
    {
        Assert.Contains("--secret-file", Error("--broker", "--pipe", Pipe, "--user-sid", Sid, "--parent-pid", "4", "--secret-file", file), StringComparison.Ordinal);
    }

    /// <summary>CommandLineToArgvW for the cases that matter here: spaces, quotes and backslashes in front of quotes.</summary>
    private static List<string> SplitLikeWindows(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var started = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\')
            {
                var count = 0;
                while (i < line.Length && line[i] == '\\')
                {
                    count++;
                    i++;
                }

                if (i < line.Length && line[i] == '"')
                {
                    current.Append('\\', count / 2);
                    if (count % 2 == 1)
                    {
                        current.Append('"');
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else
                {
                    current.Append('\\', count);
                    i--;
                }

                started = true;
            }
            else if (c == '"')
            {
                inQuotes = !inQuotes;
                started = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (started)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    started = false;
                }
            }
            else
            {
                current.Append(c);
                started = true;
            }
        }

        if (started)
        {
            result.Add(current.ToString());
        }

        return result;
    }
}
