// SPDX-License-Identifier: GPL-3.0-or-later
namespace Bootrix.Core.Engine;

internal static class StickTestRequestValidation
{
    public static void Validate(StickTestJobRequest request, List<string> problems)
    {
        EngineRequestValidator.ValidateTargets(request.Targets, problems);
        if (!Enum.IsDefined(request.Mode))
        {
            problems.Add("test mode is not known");
        }
    }
}
