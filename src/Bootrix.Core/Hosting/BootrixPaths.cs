// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Hosting;

/// <summary>Where Bootrix keeps its files. In portable mode everything lives next to the program.</summary>
public sealed record BootrixPaths
{
    public required string DataDirectory { get; init; }

    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    public string JournalDirectory => Path.Combine(DataDirectory, "jobs");

    public string ProfileDirectory => Path.Combine(DataDirectory, "profiles");

    public string CacheDirectory => Path.Combine(DataDirectory, "cache");

    public string WorkDirectory => Path.Combine(DataDirectory, "work");

    public static BootrixPaths ForInstalled() => new()
    {
        DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Bootrix"),
    };

    /// <summary>Settings and logs of the unprivileged window belong to the user, not to every account on the machine.</summary>
    public static BootrixPaths ForCurrentUser() => new()
    {
        DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bootrix"),
    };

    public static BootrixPaths ForPortable(string programDirectory) => new() { DataDirectory = Path.Combine(programDirectory, "data") };

    /// <summary>A marker file next to the program switches to portable mode; nothing is written to the registry or to ProgramData then.</summary>
    public static BootrixPaths Detect(string programDirectory, bool perUser = false) =>
        File.Exists(Path.Combine(programDirectory, "portable.marker"))
            ? ForPortable(programDirectory)
            : perUser ? ForCurrentUser() : ForInstalled();
}
