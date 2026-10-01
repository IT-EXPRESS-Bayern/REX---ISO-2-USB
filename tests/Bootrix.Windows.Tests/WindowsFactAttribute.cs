// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Windows.Tests;

/// <summary>A test that needs a real Windows system; elsewhere it is skipped instead of failing.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "needs Windows";
        }
    }
}

/// <summary>A theory that needs a real Windows system; elsewhere it is skipped instead of failing.</summary>
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "needs Windows";
        }
    }
}
