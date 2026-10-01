// SPDX-License-Identifier: GPL-3.0-or-later
using System.Reflection;

namespace Bootrix.Core;

public static class AppInfo
{
    public const string Name = "Bootrix";

    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0] ?? "0.0.0";
}
