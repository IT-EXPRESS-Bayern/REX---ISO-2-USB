// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Tests.Images.Support;

/// <summary>A theory that is reported as skipped when one of the reference tools is not installed.</summary>
public sealed class ToolTheoryAttribute : TheoryAttribute
{
    public ToolTheoryAttribute(params string[] tools)
    {
        var missing = tools.Where(tool => !ExternalTool.Exists(tool)).ToArray();
        if (missing.Length > 0)
        {
            Skip = "Reference tool not installed: " + string.Join(", ", missing);
        }
    }
}
