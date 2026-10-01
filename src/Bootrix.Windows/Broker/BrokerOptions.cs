// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace Bootrix.Windows.Broker;

/// <summary>
/// What the GUI tells the elevated process on its command line: bootrix.exe --broker --pipe NAME
/// --user-sid SID --parent-pid PID --secret-file FILE. The secret itself is not among the arguments,
/// because the command line of a process can be read by others; only the file it is in is named.
/// </summary>
public sealed record BrokerOptions(string PipeName, string UserSid, int ParentProcessId, string SecretFile)
{
    public const string BrokerFlag = "--broker";

    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal) { "--pipe", "--user-sid", "--parent-pid", "--secret-file" };

    /// <summary>True when the arguments ask for the broker, whether or not they are valid.</summary>
    public static bool IsBrokerRequest(IReadOnlyList<string> args) => args.Contains(BrokerFlag, StringComparer.Ordinal);

    /// <summary>
    /// Reads the arguments strictly: every option exactly once, nothing unknown, and values that look
    /// the way the launcher writes them. The broker is started with elevated rights, so it does not guess.
    /// </summary>
    public static bool TryParse(IReadOnlyList<string> args, out BrokerOptions? options, out string error)
    {
        options = null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var broker = false;

        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i];
            if (name == BrokerFlag)
            {
                if (broker)
                {
                    error = $"{BrokerFlag} appears twice";
                    return false;
                }

                broker = true;
                continue;
            }

            if (!ValueOptions.Contains(name))
            {
                error = $"unknown argument {name}";
                return false;
            }

            if (++i >= args.Count)
            {
                error = $"{name} needs a value";
                return false;
            }

            if (!values.TryAdd(name, args[i]))
            {
                error = $"{name} appears twice";
                return false;
            }
        }

        values.TryGetValue("--pipe", out var pipe);
        values.TryGetValue("--user-sid", out var sid);
        values.TryGetValue("--parent-pid", out var parent);
        values.TryGetValue("--secret-file", out var secretFile);

        if (!broker)
        {
            error = $"{BrokerFlag} is missing";
            return false;
        }

        if (!BrokerPipeName.TryParse(pipe, out var pipeSid))
        {
            error = "--pipe is not a broker pipe name";
            return false;
        }

        if (!BrokerAcl.IsSid(sid) || !string.Equals(sid, pipeSid, StringComparison.OrdinalIgnoreCase))
        {
            error = "--user-sid is missing or does not belong to the pipe name";
            return false;
        }

        if (!int.TryParse(parent, NumberStyles.None, CultureInfo.InvariantCulture, out var parentId) || parentId <= 0)
        {
            error = "--parent-pid is not a process id";
            return false;
        }

        if (secretFile is null || !BrokerSecretFile.IsAcceptablePath(secretFile))
        {
            error = "--secret-file is missing or not a secret file path";
            return false;
        }

        options = new BrokerOptions(pipe!, sid!, parentId, secretFile);
        error = "";
        return true;
    }

    /// <summary>The arguments in the order the launcher passes them, without quoting.</summary>
    public IReadOnlyList<string> ToArguments() =>
    [
        BrokerFlag,
        "--pipe", PipeName,
        "--user-sid", UserSid,
        "--parent-pid", ParentProcessId.ToString(CultureInfo.InvariantCulture),
        "--secret-file", SecretFile,
    ];

    /// <summary>The arguments as one command line, quoted the way CommandLineToArgvW takes them apart again.</summary>
    public string ToCommandLine() => string.Join(' ', ToArguments().Select(Quote));

    internal static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.All(c => !char.IsWhiteSpace(c) && c != '"'))
        {
            return argument;
        }

        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            // Backslashes in front of a quote have to be doubled, and the quote itself escaped.
            quoted.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes).Append(c);
            backslashes = 0;
        }

        // Backslashes at the end would escape the closing quote.
        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
