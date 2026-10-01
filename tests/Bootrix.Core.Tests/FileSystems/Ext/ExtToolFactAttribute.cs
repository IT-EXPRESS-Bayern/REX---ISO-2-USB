// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Tests.FileSystems.Ext;

/// <summary>A fact that is skipped on machines without e2fsprogs (for example the Windows CI runner).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ExtToolFactAttribute : FactAttribute
{
    public ExtToolFactAttribute(params string[] additionalTools)
    {
        Skip = ExtTools.SkipReason(additionalTools);
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class ExtToolTheoryAttribute : TheoryAttribute
{
    public ExtToolTheoryAttribute(params string[] additionalTools)
    {
        Skip = ExtTools.SkipReason(additionalTools);
    }
}

/// <summary>Needs root, the mount tools and a loop device; skipped everywhere else.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class ExtMountFactAttribute : FactAttribute
{
    public ExtMountFactAttribute()
    {
        Skip = ExtTools.SkipReason("mount", "umount") ??
            (OperatingSystem.IsLinux() && Environment.IsPrivilegedProcess && File.Exists("/dev/loop-control")
                ? null
                : "Mounting needs Linux, root privileges and a loop device.");
    }
}
