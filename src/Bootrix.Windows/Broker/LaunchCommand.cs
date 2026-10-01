// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;

namespace Bootrix.Windows.Broker;

internal static class LaunchCommand
{
    /// <summary>
    /// The start request for the elevated copy of this program. Started through the shell with "runas" (that is
    /// what makes Windows ask for consent), without a window. When the program runs under the dotnet host, as it does during development,
    /// the host is started with the program's assembly as its first argument.
    /// </summary>
    public static ProcessStartInfo Build(string? processPath, string? entryAssemblyPath, string arguments)
    {
        if (string.IsNullOrEmpty(processPath))
        {
            throw new InvalidOperationException("The path of the running program is unknown.");
        }

        var underHost = string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase);
        if (underHost && string.IsNullOrEmpty(entryAssemblyPath))
        {
            throw new InvalidOperationException("The assembly of the running program is unknown.");
        }

        return new ProcessStartInfo
        {
            FileName = processPath,
            Arguments = underHost ? $"{BrokerOptions.Quote(entryAssemblyPath!)} {arguments}" : arguments,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(processPath) ?? "",
        };
    }
}
