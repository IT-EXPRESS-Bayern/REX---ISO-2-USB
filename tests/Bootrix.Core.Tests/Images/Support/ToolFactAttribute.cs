// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Tests.Images.Support;

/// <summary>A fact that is reported as skipped when one of the reference tools is not installed.</summary>
public sealed class ToolFactAttribute : FactAttribute
{
    public ToolFactAttribute(params string[] tools)
    {
        var missing = tools.Where(tool => !ReferenceTool.Exists(tool)).ToArray();
        if (missing.Length > 0)
        {
            Skip = "Reference tool not installed: " + string.Join(", ", missing);
        }
    }
}
