// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Engine;

internal static class CollectLogsRequestValidation
{
    public static void Validate(CollectLogsJobRequest request, List<string> problems)
    {
        if (!EnginePathRules.IsAbsoluteFilePath(request.OutputPath) || !request.OutputPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add("output is not an absolute path to a .zip file");
        }
    }
}
