// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Windows.Writing.Windows;

/// <summary>The customizers that run, in this order, after the setup files have been copied.</summary>
public static class WindowsCustomizers
{
    public static IReadOnlyList<IWindowsMediaCustomizer> CreateDefault(WriteServices services) => [];
}
